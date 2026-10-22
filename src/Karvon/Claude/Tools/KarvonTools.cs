using Bengine.Introspection.Servers;
using Karvon.Docker;

namespace Karvon.Claude.Tools;

/// <summary>What the database tools use of Karvon: the Database Explorer's data sources and passwords.</summary>
public interface IDatabaseToolHost
{
    /// <summary>The Database Explorer's data sources: the solution's own, or Bengine Data Studio's list with no solution open.</summary>
    IReadOnlyList<ServerProfile> DataSources();

    /// <summary>The password the Database Explorer would use: typed there this session, else the saved one.</summary>
    Task<string?> PasswordForAsync(ServerProfile server);
}

/// <summary>What the container tools use of Karvon: the solution's folder and the Docker engine.</summary>
public interface IDockerToolHost
{
    /// <summary>The open solution's folder (its compose project), or null.</summary>
    string? SolutionDirectory { get; }

    DockerEngine Docker { get; }

    /// <summary>Every container of the engine, not only those of the solution's compose project.</summary>
    bool AllContainers { get; }
}

/// <summary>Both: the databases and the containers.</summary>
public interface IClaudeToolHost : IDatabaseToolHost, IDockerToolHost;

/// <summary>
/// The tools Karvon gives Claude Code: the build, the tests, the debugger and the editor (with <c>ide</c>); databases through
/// the Database Explorer; and those of the modules that are on (containers through Docker, the SSH servers).
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

    /// <summary>The IDE's, the databases', the containers' and (with <paramref name="servers"/>) the SSH servers' tools.</summary>
    public static ClaudeToolServer Create(IClaudeToolHost host, ViewModels.MainViewModel? ide = null, Func<Servers.ViewModels.ServersExplorerViewModel>? servers = null) =>
        Create(host, ide, [.. servers is null ? [] : ServerTools(servers), .. DockerTools(host)]);

    /// <summary>The IDE's and the databases' tools, then <paramref name="more"/>: the modules'.</summary>
    public static ClaudeToolServer Create(IDatabaseToolHost host, ViewModels.MainViewModel? ide, IEnumerable<IClaudeTool> more) => new(
    [
        .. ide is null ? [] : IdeTools(ide),
        new DbConnectionsTool(host),
        new DbSchemaTool(host),
        new DbQueryTool(host),
        new DbExplainTool(host),
        .. more,
    ], Instructions);

    public static IEnumerable<IClaudeTool> DockerTools(IDockerToolHost host) =>
    [
        new ContainerListTool(host),
        new ContainerLogsTool(host),
        new ContainerInspectTool(host),
        new ContainerRestartTool(host),
        new ComposeUpTool(host),
        new ContainerRemoveTool(host),
    ];

    public static IEnumerable<IClaudeTool> ServerTools(Func<Servers.ViewModels.ServersExplorerViewModel> servers) =>
        [new ServerListTool(servers), new ServerLogsTool(servers), new ServerExecTool(servers)];

    static IEnumerable<IClaudeTool> IdeTools(ViewModels.MainViewModel vm) =>
    [
        new RunBuildTool(vm),
        new BuildErrorsTool(vm),
        new RunTestsTool(vm),
        new TestFailuresTool(vm),
        new DebugStateTool(vm),
        new DebugLocalsTool(vm),
        new DebugEvaluateTool(vm),
        new DebugControlTool(vm),
        new AppControlTool(vm),
        new EditorSelectionTool(vm),
        new OpenDocumentTool(vm),
        new CodeDiagnosticsTool(vm),
        new FindInFilesTool(vm),
    ];
}
