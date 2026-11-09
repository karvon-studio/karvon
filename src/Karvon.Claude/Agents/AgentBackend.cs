namespace Karvon.Claude;

/// <summary>How the panel starts a conversation with an agent other than Claude Code.</summary>
/// <param name="Executable">The agent's program (codex, gemini...); for a model Karvon drives itself, its provider's URL.</param>
/// <param name="Model">The model asked for; null for the agent's own choice.</param>
/// <param name="PermissionMode">"default", "acceptEdits", "plan" or "bypassPermissions", as the panel's modes say.</param>
/// <param name="Resume">The conversation to go on with; null for a new one.</param>
/// <param name="Tools">Karvon's own tools, given to the agent; null for none.</param>
/// <param name="GuardSecrets">Whether Karvon keeps the agent from the files and commands that give secrets (<see cref="SecretGuard"/>).</param>
/// <param name="Trusted">Whether the folder is trusted (the user trusts it).</param>
/// <param name="AuditDirectory">Where what the agent did with Karvon's tools is written; null for nowhere.</param>
/// <param name="Effort">How hard the model thinks ("low" ... "max"), when it can be told; null for its own default.</param>
public sealed record AgentStartOptions(
    string Executable,
    string WorkingDirectory,
    string? Model = null,
    string PermissionMode = "default",
    string? Resume = null,
    ClaudeToolServer? Tools = null,
    bool GuardSecrets = true,
    bool Trusted = true,
    string? AuditDirectory = null,
    string? Effort = null);

/// <summary>
/// An agent the panel can talk to besides Claude Code: how to find it, what to tell the user when it is not there, and how to
/// start a conversation with it.
/// </summary>
public interface IAgentBackend
{
    /// <summary>"codex", "openrouter", "ollama", "acp:gemini"...: what the settings keep.</summary>
    string Id { get; }

    string Name { get; }

    string Description { get; }

    /// <summary>The program (or the provider) to start it with; null when it is not there.</summary>
    string? Find();

    /// <summary>What the panel says when <see cref="Find"/> found nothing.</summary>
    string MissingTitle { get; }

    string MissingHint { get; }

    /// <summary>The command that installs it, shown to copy; null for none.</summary>
    string? InstallCommand { get; }

    /// <summary>The panel's modes it has ("default", "acceptEdits", "plan", "auto", "bypassPermissions").</summary>
    IReadOnlyList<string> Modes { get; }

    /// <summary>It needs a key the user gives (an API key): the panel asks for it when <see cref="Find"/> found none.</summary>
    bool NeedsKey => false;

    /// <summary>The key the user gave: whether it was taken.</summary>
    bool SetKey(string key) => false;

    Task<IAgentSession> StartAsync(AgentStartOptions options, CancellationToken cancellationToken = default);
}
