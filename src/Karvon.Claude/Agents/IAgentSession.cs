using System.Text.Json;

namespace Karvon.Claude;

/// <summary>
/// One running conversation with a coding agent: Claude Code (<see cref="ClaudeSession"/>), Codex, a model Karvon drives
/// itself, or an agent that speaks the Agent Client Protocol. Each tells the panel what happens in the same words
/// (<see cref="ClaudeEvent"/>s: streamed text, tool calls and their results, questions for the user, the end of a turn), so
/// the panel shows them all alike.
/// </summary>
public interface IAgentSession : IAsyncDisposable
{
    /// <summary>Something happened (on any thread): text streamed, a tool ran, the agent asks, the turn ended...</summary>
    event Action<ClaudeEvent>? Event;

    /// <summary>The conversation's id (to go on with it later), once the agent gave one.</summary>
    string? SessionId { get; }

    /// <summary>The folder the agent works in.</summary>
    string? WorkingDirectory { get; }

    bool HasExited { get; }

    /// <summary>
    /// What the agent said as it started, in Claude Code's shape: its models ({ models: [{ value, displayName, description }] }),
    /// the one it answers with now (currentModel), its commands, its account.
    /// </summary>
    JsonElement Initialization { get; }

    /// <summary>The variables of Karvon's environment that hold a secret, left out of the agent's.</summary>
    IReadOnlyList<string> HiddenVariables { get; }

    /// <summary>Where Karvon writes what the agent did with its tools; null when it does not.</summary>
    ClaudeAudit? Audit { get; }

    Task SendUserMessageAsync(string text, IEnumerable<string>? extraBlocks = null, IEnumerable<ClaudeImage>? images = null);

    /// <summary>A slash command (/compact...): the agent's own, or a prompt where it has none.</summary>
    Task SendCommandAsync(string command) => SendUserMessageAsync(command);

    /// <summary>Answers a <see cref="ClaudePermissionRequest"/> the agent sent.</summary>
    Task RespondToPermissionAsync(string requestId, ClaudePermissionDecision decision, string by = "the user");

    /// <summary>Stops the turn that runs.</summary>
    Task InterruptAsync();

    /// <summary>"default", "acceptEdits", "plan", "auto" or "bypassPermissions": as the agent can have it.</summary>
    Task SetPermissionModeAsync(string mode);

    Task SetModelAsync(string? model);

    /// <summary>How hard the model thinks from the next turn ("low" ... "max"); an agent that cannot be told ignores it.</summary>
    Task SetEffortAsync(string? effort) => Task.CompletedTask;
}
