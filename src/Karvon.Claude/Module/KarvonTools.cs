
namespace Karvon.Claude;

/// <summary>
/// The tools Karvon gives Claude Code: the core's (the build, the tests, the debugger, the editor and code navigation) and those of the modules
/// that are on (the Database Explorer's data sources, the Docker engine's containers, the SSH servers, the Jira sites, the
/// forwarded ports, the repository's CI and its pull requests, the user's Kubernetes clusters, the solution's HTTP endpoints).
/// </summary>
public static class KarvonTools
{
    const string Instructions =
        "Karvon's tools work through the IDE: its build, Test Explorer, debugger and language servers, and the user's saved "
        + "connections (data sources, Docker, SSH servers, Jira sites, CI, pull requests, Kubernetes), so they need no credentials and "
        + "hide secrets; the user sees what they do in Karvon. Run the app with app_control (or debug_control), never with dotnet "
        + "run, npm start or a background process in the shell. Reading, building, testing and stepping run at once; a change (SQL "
        + "that writes, compose up, a command on a server, a restart, a merge...) asks the user first unless they bypass "
        + "permissions. When a call is refused or denied, do not retry it another way: say what you wanted to do.";

    /// <summary>
    /// What each session's system prompt gets (as Claude Code's own rules are): in Karvon, its tools before the shell. The
    /// MCP server's instructions say the same at more length; a Bash call they cover is sent to them besides (when the user
    /// keeps that on).
    /// </summary>
    public const string SystemPrompt =
        "You run inside Karvon Studio, the user's IDE; its tools are mcp__karvon__*. Use them, not Bash, for what they do: build "
        + "and test (run_build, run_tests), run and debug the app (app_control, debug_control), find and read code by what it means "
        + "rather than grep, sed and cat on source files (code_symbols, code_read_symbol, code_references, code_definition, "
        + "code_implementations, code_outline), and the user's databases, Docker, SSH servers, Jira, CI runs, pull requests, "
        + "Kubernetes and HTTP requests to the app (db_, container_, server_, jira_, pipeline_, pr_, k8s_ and http_ tools). Use Bash "
        + "for the rest.";

    /// <summary>
    /// The tools of Karvon's core (the IDE's) and of its modules, as one MCP server; the shell commands they do, which they
    /// come before while <paramref name="toolsFirst"/> says so (always by default).
    /// </summary>
    public static ClaudeToolServer Create(IEnumerable<IClaudeTool> tools, IEnumerable<ClaudeShellRule>? shellRules = null, Func<bool>? toolsFirst = null) =>
        new([.. tools], Instructions, SystemPrompt, [.. shellRules ?? []], toolsFirst, AlwaysLoaded);

    /// <summary>
    /// The tools most sessions use (building, testing, reading code by what it means): in the prompt from the start. Claude
    /// Code defers an MCP server's tools behind its tool search, which costs a turn of its own before the first call; the
    /// rest of Karvon's stay deferred, so the prompt stays small.
    /// </summary>
    public static readonly IReadOnlySet<string> AlwaysLoaded =
        new HashSet<string>(StringComparer.Ordinal) { "run_build", "run_tests", "code_symbols", "code_read_symbol", "code_references", "find_in_files" };
}
