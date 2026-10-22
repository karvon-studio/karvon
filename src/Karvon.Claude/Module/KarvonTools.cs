
namespace Karvon.Claude;

/// <summary>
/// The tools Karvon gives Claude Code: the core's (the build, the tests, the debugger and the editor) and those of the modules
/// that are on (the Database Explorer's data sources, the Docker engine's containers, the SSH servers).
/// </summary>
public static class KarvonTools
{
    const string Instructions =
        "Tools from Karvon, the IDE the user works in. They use Karvon's own build, Test Explorer, debugger and editor, the user's Database "
        + "Explorer data sources, Docker engine and SSH servers, so you need no credentials, and they hide secrets. Prefer them to the "
        + "shell (dotnet build, dotnet test, dotnet run, psql, docker, ssh) for what they cover: the user sees their results in Karvon. "
        + "To run the app, use app_control (or debug_control to debug it), never dotnet run or a background process in the shell, "
        + "also when a skill or a habit suggests one: Karvon runs it, shows its output and stops it. Reading, building, testing and "
        + "stepping run at once; a change (SQL that writes, a restart, compose up, a removal, a command on a server, starting or "
        + "stopping the app or the debugger) asks the user first unless they chose to bypass permissions, read-only data sources "
        + "refuse changes, and production servers ask for every command. When a call is refused or denied, do not retry it another "
        + "way: tell the user what you wanted to do.";

    /// <summary>The tools of Karvon's core (the IDE's) and of its modules, as one MCP server.</summary>
    public static ClaudeToolServer Create(IEnumerable<IClaudeTool> tools) => new([.. tools], Instructions);
}
