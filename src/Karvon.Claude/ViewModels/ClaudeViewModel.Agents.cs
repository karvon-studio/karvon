using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Karvon.Claude;
using Karvon.Services;

namespace Karvon.ViewModels;

/// <summary>An agent the panel can talk to: Claude Code, or one of its <see cref="IAgentBackend"/>s.</summary>
public sealed record AgentChoice(string Id, string Name, string Description)
{
    public const string ClaudeId = "claude";
}

// The agent the panel talks to: Claude Code, or another (Codex...) that tells it what happens in Claude Code's words.
public sealed partial class ClaudeViewModel
{
    IReadOnlyList<IAgentBackend> _backends = [];
    IAgentBackend? _backend;
    bool _switchingAgent;

    public ObservableCollection<AgentChoice> Agents { get; } = [new(AgentChoice.ClaudeId, "Claude Code", "Anthropic's Claude Code CLI")];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsClaude), nameof(AgentName), nameof(MissingTitle), nameof(MissingHint), nameof(InstallCommand), nameof(HasManyAgents))]
    public partial AgentChoice? SelectedAgent { get; set; }

    /// <summary>
    /// The agents besides Claude Code (Codex, a model Karvon drives itself, an Agent Client Protocol agent): the one the
    /// settings name is chosen.
    /// </summary>
    public IReadOnlyList<IAgentBackend> Backends
    {
        get => _backends;
        set
        {
            _backends = value;
            while (Agents.Count > 1)
                Agents.RemoveAt(1);
            foreach (var backend in value)
                Agents.Add(new AgentChoice(backend.Id, backend.Name, backend.Description));
            OnPropertyChanged(nameof(HasManyAgents));
            _switchingAgent = true;
            SelectedAgent = Agents.FirstOrDefault(a => a.Id == _settings.AgentBackend) ?? Agents[0];
            _switchingAgent = false;
            if (SelectedAgent.Id != AgentChoice.ClaudeId)
                UseAgent(SelectedAgent);
        }
    }

    public bool HasManyAgents => Agents.Count > 1;

    /// <summary>The panel talks to Claude Code (what only it has shows: its accounts, Remote Control, its MCP servers, past conversations).</summary>
    public bool IsClaude => _backend is null;

    /// <summary>Who the cards ask for ("Allow Codex to run this command?").</summary>
    public string AgentName => _backend?.Name ?? "Claude";

    public string MissingTitle => _backend?.MissingTitle ?? "Claude Code is not installed";

    public string MissingHint => _backend?.MissingHint ?? "Install the Claude Code CLI, sign in once by running claude in a terminal, then retry.";

    public string? InstallCommand => _backend is null ? "curl -fsSL https://claude.ai/install.sh | bash" : _backend.InstallCommand;

    /// <summary>The agent needs a key the user gives (OpenRouter's): the panel asks for it.</summary>
    public bool NeedsKey => _backend?.NeedsKey == true;

    /// <summary>The key typed for the agent; never kept here once given.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveAgentKeyCommand))]
    public partial string AgentKey { get; set; } = "";

    bool CanSaveAgentKey => AgentKey.Trim().Length > 0;

    /// <summary>The key goes to the agent (and Karvon's vault); the panel is ready once it has one.</summary>
    [RelayCommand(CanExecute = nameof(CanSaveAgentKey))]
    void SaveAgentKey()
    {
        if (_backend?.SetKey(AgentKey) == true)
        {
            AgentKey = "";
            CheckCli();
            WarmUp();
        }
    }

    partial void OnSelectedAgentChanged(AgentChoice? value)
    {
        if (value is null || _switchingAgent)
            return;
        _settings.AgentBackend = value.Id == AgentChoice.ClaudeId ? null : value.Id;
        _settings.Save();
        _ = SwitchAgentAsync(value);
    }

    /// <summary>Another agent: the conversation starts afresh with it (one Claude was working on goes on in the background).</summary>
    async Task SwitchAgentAsync(AgentChoice agent)
    {
        await ResetAsync();
        UseAgent(agent);
        WarmUp();
    }

    void UseAgent(AgentChoice agent)
    {
        _backend = _backends.FirstOrDefault(b => b.Id == agent.Id);
        Commands.Clear();
        Account = null;
        _changingModeFromCli = true;
        Models.Clear();
        if (_backend is null)
        {
            foreach (var model in DefaultModels)
                Models.Add(model);
            SelectedModel = Models.FirstOrDefault(m => m.Value == _settings.ClaudeModel) ?? Models[0];
        }
        else
        {
            if (_settings.AgentModels.TryGetValue(_backend.Id, out var saved))
                Models.Add(new ClaudeModelChoice(saved, saved, null));
            SelectedModel = Models.FirstOrDefault();
        }
        if (_backend is { } backend && !backend.Modes.Contains(PermissionMode))
            PermissionMode = "default";
        _changingModeFromCli = false;
        RefreshModes();
        RefreshEffortSteps();
        CheckCli();
        OnPropertyChanged(nameof(IsClaude));
        OnPropertyChanged(nameof(AgentName));
        OnPropertyChanged(nameof(MissingTitle));
        OnPropertyChanged(nameof(MissingHint));
        OnPropertyChanged(nameof(InstallCommand));
        OnPropertyChanged(nameof(NeedsKey));
    }

    /// <summary>A conversation with the agent chosen (not Claude Code), in <paramref name="cwd"/>.</summary>
    async Task<IAgentSession> StartAgentAsync(IAgentBackend backend, string cwd)
    {
        await StopSessionAsync();
        var session = await backend.StartAsync(new AgentStartOptions(CliPath!, cwd, SelectedModel?.Value, PermissionMode, _resumeId, Tools,
            _settings.ClaudeKeepSecretsAway, WorkspaceTrust.IsTrusted(_settings.TrustedFolders, cwd), Path.Combine(Settings.ConfigDirectory, "audit"), Effort));
        session.Event += ev => Avalonia.Threading.Dispatcher.UIThread.Post(() => Handle(session, ev));
        _session = session;
        _sessionCwd = cwd;
        OnPropertyChanged(nameof(IsFolderTrusted));
        OnPropertyChanged(nameof(AuditFile));
        if (session.HiddenVariables.Count > 0)
            VariablesHidden?.Invoke(session.HiddenVariables);
        ApplyInitialization(session.Initialization);
        return session;
    }
}
