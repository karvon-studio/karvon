using Karvon.Modules;
using Karvon.Services;
using Karvon.ViewModels;
using Karvon.Views;

namespace Karvon.Claude;

/// <summary>
/// Agents: a conversation with a coding agent other than Claude Code, in a tool window of its own (Claude Code keeps its
/// own). The agent and its model are chosen in a dialog (<see cref="AgentPickerViewModel"/>): Codex, a model Karvon drives
/// itself through OpenRouter, Ollama or LM Studio, or an Agent Client Protocol agent (Gemini CLI, and the user's own). The
/// panel is Claude Code's (<see cref="ClaudeViewModel"/>), so the agents' answers, tool calls, diffs and questions show
/// alike, and they use Karvon's tools under the same rules.
/// </summary>
public sealed class AgentsModule : AgentPanelModule
{
    public const string ToolWindowId = "Agents";

    ClaudeViewModel? _agents;

    public override string Id => "agents";

    public override string Name => "Agents";

    public override string Icon => "robot";

    public override string Description =>
        "Other coding agents beside Claude Code: Codex, any model on OpenRouter, a local model in Ollama or LM Studio, and Agent "
        + "Client Protocol agents (Gemini CLI, your own), with Karvon's tools.";

    public override IReadOnlyList<ToolWindowInfo> ToolWindows =>
        [new(ToolWindowId, "Agents", "_Agents", "Sparkle", DockSide.Right, () => new ClaudeView { DataContext = Agents }) { Show = Show }];

    /// <summary>The conversation, made the first time it is needed.</summary>
    public ClaudeViewModel Agents => _agents ??= Create();

    protected override string OutputName => "Agents";

    protected override void Started()
    {
        _ = Output;
        Workbench.ActiveDocumentChanged += _ => _agents?.RefreshContext();
    }

    /// <summary>The agents the panel can talk to: those Karvon knows, then the user's own ACP agents.</summary>
    public static IReadOnlyList<IAgentBackend> Backends(Settings settings) =>
    [
        new CodexBackend(),
        new ModelBackend(ModelProvider.OpenRouter, Keyring.System),
        new ModelBackend(ModelProvider.Ollama),
        new ModelBackend(ModelProvider.LmStudio),
        new AcpBackend(AcpAgent.GeminiCli),
        .. settings.AcpAgents.Where(a => a.Name.Length > 0 && a.Command.Length > 0).Select(a => new AcpBackend(
            new AcpAgent(a.Name.ToLowerInvariant().Replace(' ', '-'), a.Name, "An agent of yours, through the Agent Client Protocol", [a.Command], a.Arguments, [], null))),
    ];

    ClaudeViewModel Create()
    {
        var agents = new ClaudeViewModel(Workbench.Settings, WorkingDirectory, EditorContext, Files, () => null)
        {
            Tools = NewToolServer(),
            Backends = Backends(Workbench.Settings),
        };
        Wire(agents, ToolWindowId, "the agent", () => agents.AgentName, "agents");
        return agents;
    }

    /// <summary>View > Agents.</summary>
    public void Show()
    {
        Workbench.ShowToolWindow(ToolWindowId);
        Agents.RefreshContext();
        Agents.RequestFocus();
    }

    /// <summary>The conversation the solution had open, kept with it.</summary>
    const string ConversationKey = "agents.conversation";

    /// <summary>Another folder: the conversation runs there; a solution opened gets back the conversation it had.</summary>
    public override async Task WorkspaceChangedAsync()
    {
        ForgetFiles();
        if (_agents is { } agents)
            await agents.OnWorkingDirectoryChangedAsync();
        if (Workbench.WorkspaceState.GetValueOrDefault(ConversationKey) is { Length: > 0 } conversation)
            await Agents.RestoreConversationAsync(conversation);
    }

    /// <summary>The conversation open now, kept with the solution (one never opened this time keeps the one it would restore).</summary>
    public override void SaveWorkspaceState(IDictionary<string, string> state)
    {
        if (_agents is not { } agents)
            return;
        if (agents.SessionId is { } session)
            state[ConversationKey] = session;
        else
            state.Remove(ConversationKey);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_agents is { } agents)
            await agents.DisposeAsync();
    }
}
