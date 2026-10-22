using System.Text.Json;
using System.Text.Json.Nodes;

namespace Karvon.Claude;

/// <summary>
/// How much a tool call may change: reads and runs (a build, the tests, a debugger step: nothing of the user's changes)
/// go at once; writes ask; destructive changes ask and are never remembered.
/// </summary>
public enum ToolAccess
{
    Read,
    Run,
    Write,
    Destructive,
}

/// <summary>
/// What a call will do, before it runs: its access, its row in the conversation ("Query", "shop: SELECT ..."), and for a
/// change the approval card's question and warning.
/// </summary>
public sealed record ClaudeToolPlan(ToolAccess Access, string Title, string? Subject, string Question)
{
    /// <summary>The SQL or command, shown in full on the card and the row.</summary>
    public string? Code { get; init; }

    /// <summary>Under the question: what the change does, or why it cannot be undone.</summary>
    public string? Warning { get; init; }
}

public sealed record ClaudeToolOutput(string Text, bool IsError = false)
{
    /// <summary>What the call produced for the host to show (a query's result sets, the matches of a search); never sent to Claude.</summary>
    public object? Detail { get; init; }

    public static ClaudeToolOutput Error(string message) => new(message, true);
}

/// <summary>A tool of Karvon's own that Claude can call (mcp__karvon__name).</summary>
public interface IClaudeTool
{
    string Name { get; }

    string Description { get; }

    /// <summary>The input's JSON Schema (an object).</summary>
    JsonObject InputSchema { get; }

    /// <summary>What the call would do; computed again for the approval card and when it runs.</summary>
    ClaudeToolPlan Plan(JsonElement input);

    Task<ClaudeToolOutput> CallAsync(JsonElement input, CancellationToken cancellationToken);
}

/// <summary>The name Karvon's tools have in Claude Code: its MCP server's, and the prefix of each tool (mcp__karvon__db_query).</summary>
public static class KarvonToolNames
{
    public const string Server = "karvon";

    public const string Prefix = "mcp__" + Server + "__";
}
