using CommunityToolkit.Mvvm.Input;
using Karvon.Claude;
using Karvon.Services;

namespace Karvon.ViewModels;

// The Agents panel: the same conversation, with another agent (Codex...) that tells it what happens in Claude Code's words.
// Claude Code's own panel has no backends and is Claude Code's alone.
public sealed partial class ClaudeViewModel
{
    IReadOnlyList<IAgentBackend> _backends = [];
    IAgentBackend? _backend;

    /// <summary>Karvon's transcript of each agent conversation (Claude Code keeps its own).</summary>
    readonly System.Runtime.CompilerServices.ConditionalWeakTable<IAgentSession, AgentTranscript> _transcripts = [];

    /// <summary>Where Karvon keeps the agents' conversations (transcripts, and what an agent needs to go on with one).</summary>
    public static string AgentsDirectory => Path.Combine(Settings.ConfigDirectory, "agents");

    /// <summary>
    /// The agents the Agents panel talks to (Codex, a model Karvon drives itself, an Agent Client Protocol agent): the one
    /// the settings name answers, else the first. None: the panel is Claude Code's.
    /// </summary>
    public IReadOnlyList<IAgentBackend> Backends
    {
        get => _backends;
        set
        {
            _backends = value;
            if ((value.FirstOrDefault(b => b.Id == _settings.AgentBackend) ?? value.FirstOrDefault()) is { } backend)
                UseAgent(backend);
        }
    }

    /// <summary>The agent the panel talks to; null for Claude Code.</summary>
    public IAgentBackend? Backend => _backend;

    /// <summary>The panel talks to Claude Code (what only it has shows: its accounts, Remote Control, its MCP servers, past conversations).</summary>
    public bool IsClaude => _backend is null;

    /// <summary>Who the cards ask for ("Allow Codex to run this command?").</summary>
    public string AgentName => _backend?.Name ?? "Claude";

    /// <summary>The panel's title: Claude Code, or the agent's name.</summary>
    public string HeaderTitle => _backend?.Name ?? "Claude Code";

    /// <summary>The panel's icon: Claude's, or the agents'.</summary>
    public string HeaderIcon => _backend is null ? "Claude" : "Sparkle";

    /// <summary>Claude's words for this panel's agent ("Ask Codex to..."): translated as Claude's (<see cref="ClaudeLanguage.AddAgentName"/>).</summary>
    internal string Say(string text) =>
        _backend is null ? text : text.Replace("Claude Code", AgentName, StringComparison.Ordinal).Replace("Claude", AgentName, StringComparison.Ordinal);

    public string PromptPlaceholder => Say("Ask Claude to edit, fix or explain... (/ for commands, @ for files)");

    public string ContextTip => Say("Claude sees this file and your selection. Click to include or leave it out.");

    public string ModeTip => Say("How much Claude may do on its own, and how hard it thinks (Shift+Tab switches mode)");

    public string StopTip => Say("Stop Claude (Esc)");





    public string EmptyText => Say("Claude Code works in your solution folder: it can read and edit files, run commands and explain code. You approve each edit unless you choose to edit automatically.");

    public string MissingTitle => _backend?.MissingTitle ?? "Claude Code is not installed";

    public string MissingHint => _backend?.MissingHint ?? "Install the Claude Code CLI, sign in once by running claude in a terminal, then retry.";

    public string? InstallCommand => _backend is null ? "curl -fsSL https://claude.ai/install.sh | bash" : _backend.InstallCommand;

    /// <summary>The panel's mode, kept apart for each panel (Claude Code's, the agents').</summary>
    string? SavedPermissionMode
    {
        get => _backend is null ? _settings.ClaudePermissionMode : _settings.AgentPermissionMode;
        set
        {
            if (_backend is null)
                _settings.ClaudePermissionMode = value;
            else
                _settings.AgentPermissionMode = value;
        }
    }

    string? SavedEffort
    {
        get => _backend is null ? _settings.ClaudeEffort : _settings.AgentEffort;
        set
        {
            if (_backend is null)
                _settings.ClaudeEffort = value;
            else
                _settings.AgentEffort = value;
        }
    }

    /// <summary>The view shows Choose Agent and Model with this picker, then hands its choice to <see cref="UseAgentAsync"/>.</summary>
    public event Action<AgentPickerViewModel>? PickAgentRequested;

    /// <summary>Choose Agent and Model, for the agent and the model the panel has now.</summary>
    public AgentPickerViewModel CreatePicker() =>
        new(_backends, _backend, SelectedModel?.Value, _workingDirectory,
            backend => backend == _backend && _session is { HasExited: false } session && AgentModels.Of(session.Initialization) is { Count: > 0 } models ? models : null);

    [RelayCommand]
    void PickAgent()
    {
        if (_backends.Count > 0)
            PickAgentRequested?.Invoke(CreatePicker());
    }

    /// <summary>
    /// What Choose Agent and Model chose: another agent starts a fresh conversation (one it was working on goes on in the
    /// background); the same one goes on with the model chosen.
    /// </summary>
    public async Task UseAgentAsync(IAgentBackend backend, AgentModel? model)
    {
        if (model is not null)
            _settings.AgentModels[backend.Id] = model.Id;
        else
            _settings.AgentModels.Remove(backend.Id);
        _settings.AgentBackend = backend.Id;
        _settings.Save();
        if (backend == _backend)
        {
            if (model is not null)
            {
                var choice = Models.FirstOrDefault(m => m.Value == model.Id);
                if (choice is null)
                    Models.Add(choice = new ClaudeModelChoice(model.Id, model.Name, model.Description));
                SelectedModel = choice;
            }
            CheckCli();
            if (_session is null)
                WarmUp();
            return;
        }
        await ResetAsync();
        UseAgent(backend, model);
        WarmUp();
    }

    void UseAgent(IAgentBackend backend, AgentModel? model = null)
    {
        _backend = backend;
        Commands.Clear();
        Account = null;
        _changingModeFromCli = true;
        Models.Clear();
        if (model is not null)
            Models.Add(new ClaudeModelChoice(model.Id, model.Name, model.Description));
        else if (_settings.AgentModels.TryGetValue(backend.Id, out var saved))
            Models.Add(new ClaudeModelChoice(saved, saved, null));
        SelectedModel = Models.FirstOrDefault();
        PermissionMode = backend.Modes.Contains(KnownMode(SavedPermissionMode)) ? KnownMode(SavedPermissionMode) : "default";
        Effort = SavedEffort;
        _changingModeFromCli = false;
        RefreshModes();
        RefreshEffortSteps();
        CheckCli();
        ClaudeLanguage.AddAgentName(backend.Name);
        foreach (var name in new[] { nameof(Backend), nameof(IsClaude), nameof(AgentName), nameof(HeaderTitle), nameof(HeaderIcon), nameof(PromptPlaceholder),
            nameof(ContextTip), nameof(ModeTip), nameof(StopTip), nameof(EmptyText) })
            OnPropertyChanged(name);
        OnPropertyChanged(nameof(MissingTitle));
        OnPropertyChanged(nameof(MissingHint));
        OnPropertyChanged(nameof(InstallCommand));
    }

    /// <summary>A conversation with the agent chosen (not Claude Code), in <paramref name="cwd"/>.</summary>
    async Task<IAgentSession> StartAgentAsync(IAgentBackend backend, string cwd)
    {
        await StopSessionAsync();
        var session = await backend.StartAsync(new AgentStartOptions(CliPath!, cwd, SelectedModel?.Value, PermissionMode, _resumeId, Tools,
            _settings.ClaudeKeepSecretsAway, WorkspaceTrust.IsTrusted(_settings.TrustedFolders, cwd), Path.Combine(Settings.ConfigDirectory, "audit"), Effort,
            AgentTranscripts.Folder(AgentsDirectory, cwd)));
        // What the panel shows of it is kept, to be shown again from Past conversations.
        if (session.SessionId is { Length: > 0 } id)
        {
            var transcript = AgentTranscript.Open(AgentsDirectory, backend.Id, backend.Name, cwd, id);
            _transcripts.AddOrUpdate(session, transcript);
            session.Event += transcript.Record;
        }
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
