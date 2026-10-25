
namespace Karvon.Claude;

/// <summary>
/// The tools Karvon gives Claude Code: the core's (the build, the tests, the debugger and the editor) and those of the modules
/// that are on (the Database Explorer's data sources, the Docker engine's containers, the SSH servers, the Jira sites, the
/// forwarded ports, the repository's CI and its pull requests).
/// </summary>
public static class KarvonTools
{
    const string Instructions =
        "Tools from Karvon, the IDE the user works in. They use Karvon's own build, Test Explorer, debugger and editor, the user's Database "
        + "Explorer data sources, Docker engine, SSH servers, Jira sites, tunnel service (Ports), the repository's CI (GitHub Actions, GitLab CI) and its pull requests (GitHub's, GitLab's merge requests), so you need no credentials, "
        + "and they hide secrets. Prefer them to the shell (dotnet build, dotnet test, dotnet run, npm run build, npm test, psql, docker, ssh, curl to Jira's API, jprq, "
        + "ngrok or cloudflared, gh run, glab ci, gh pr, glab mr) for what they cover: the user sees "
        + "their results in Karvon; in a folder opened as it is, they run its tasks (tasks.json, package.json scripts) and its launch.json configurations. "
        + "To run the app, use app_control (or debug_control to debug it), never dotnet run, npm start or a background process in the shell, "
        + "also when a skill or a habit suggests one: Karvon runs it, shows its output and stops it. Reading, building, testing and "
        + "stepping run at once; a change (SQL that writes, a restart, compose up, a removal, a command on a server, a Jira issue made, moved, commented or assigned, a port made public, a CI run re-run or canceled, a pull request made, commented, reviewed or merged, starting or "
        + "stopping the app or the debugger) asks the user first unless they chose to bypass permissions, read-only data sources "
        + "refuse changes, and production servers ask for every command. When a call is refused or denied, do not retry it another "
        + "way: tell the user what you wanted to do.";

    /// <summary>
    /// What each session's system prompt gets (as Claude Code's own rules are): in Karvon, its tools before the shell. The
    /// MCP server's instructions say the same at more length; a Bash call they cover is sent to them besides (when the user
    /// keeps that on).
    /// </summary>
    public const string SystemPrompt =
        "You run inside Karvon Studio, the IDE the user works in, which gives you its tools as mcp__karvon__*. Use them, not "
        + "Bash, for what they do: building, testing, running and debugging the workspace (run_build, run_tests, app_control, "
        + "debug_control), its databases (db_query and the other db_ tools), Docker (container_ tools), the user's SSH servers "
        + "(server_exec, server_logs), Jira (jira_ tools), a local port at a public address (tunnel_open), the repository's CI "
        + "runs and their logs (pipeline_ tools) and its pull requests (pr_ tools). They sign in with the "
        + "user's own saved connections, so you need no "
        + "credentials, never read or ask for tokens or passwords for them, and the user sees what they do in Karvon. Use Bash for "
        + "the rest.";

    /// <summary>
    /// The tools of Karvon's core (the IDE's) and of its modules, as one MCP server; the shell commands they do, which they
    /// come before while <paramref name="toolsFirst"/> says so (always by default).
    /// </summary>
    public static ClaudeToolServer Create(IEnumerable<IClaudeTool> tools, IEnumerable<ClaudeShellRule>? shellRules = null, Func<bool>? toolsFirst = null) =>
        new([.. tools], Instructions, SystemPrompt, [.. shellRules ?? []], toolsFirst);
}
