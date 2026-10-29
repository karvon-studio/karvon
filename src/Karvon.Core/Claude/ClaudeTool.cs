using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

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

/// <summary>
/// A shell command one of Karvon's tools does (dotnet build, docker ps, psql, ssh to a server, Jira's API by curl): Claude's
/// Bash call of it does not run, and Claude is told the tool to use instead (a PreToolUse hook Karvon answers), as long as
/// the user keeps that setting on.
/// </summary>
/// <param name="Name">What it is ("dotnet build"), for Karvon's log.</param>
/// <param name="Matches">Whether a Bash command (all of it, its lines and pipelines) runs it.</param>
/// <param name="Use">What Claude is told instead: the tool, and why.</param>
public sealed record ClaudeShellRule(string Name, Func<string, bool> Matches, string Use)
{
    /// <summary>
    /// The workspace's own build, tests or run: a command that does it in another folder (cd went there, or it names a
    /// project there) is not the rule's, as Karvon's tools work only on the workspace.
    /// </summary>
    public bool InWorkspace { get; init; }

    /// <summary>Whether <paramref name="command"/> is the rule's, run in <paramref name="workspace"/> (null: wherever).</summary>
    public bool Applies(string command, string? workspace) =>
        Matches(command) && !(InWorkspace && workspace is not null && ShellCommand.RunsElsewhere(command, workspace, Matches));

    /// <summary>
    /// A rule for a program run as a command: at a line's start, after ; &amp;&amp; || | ( ` $( or a newline, after sudo and
    /// variables set before it (A=1 dotnet build). <paramref name="pattern"/> is the program and what follows it.
    /// </summary>
    public static ClaudeShellRule Command(string name, string pattern, string use)
    {
        var regex = new Regex(@"(?:^|[;&|(\n`]|\$\()\s*(?:sudo\s+)?(?:[A-Za-z_][A-Za-z0-9_]*=\S*\s+)*(?:" + pattern + ")",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
        return new ClaudeShellRule(name, command =>
        {
            try
            {
                return regex.IsMatch(command);
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }, use);
    }
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
