using Karvon.Modules;

namespace Karvon.Go;

/// <summary>
/// Go: gopls in the editor of .go files, Delve for F5, a module's go build, test, vet and run as its tasks (their errors
/// in the Error List), and go test's tests in Test Explorer, with the Go the user has.
/// </summary>
public sealed class GoModule : KarvonModule
{
    public override string Id => "go";

    public override string Name => "Go";


    public override string Icon => "go";

    public override string Description =>
        "gopls in the editor of .go files (errors as you type, completion, hover, go to definition, references, rename), Delve "
        + "for F5, a module's go build, test, vet and run as its tasks, and go test's tests in Test Explorer. gopls and Delve are "
        + "installed with go install the first time they are needed (Go itself is the user's).";

    public override IReadOnlyList<LanguageServerInfo> LanguageServers =>
        [new("gopls", "Go language server (gopls)", GoplsServer.Serves, StartAsync)];

    public override IReadOnlyList<WorkspaceTask> WorkspaceTasks(string workspace) => GoTasks.Read(workspace);

    /// <summary>A Go module's Dependencies in Solution Explorer.</summary>
    public override Packages.PackageSource? PackagesIn(string folder) => File.Exists(Path.Combine(folder, "go.mod")) ? new GoPackages(folder) : null;

    /// <summary>A Go module's Manage Go Modules...: pkg.go.dev's packages, by go get.</summary>
    public override IReadOnlyList<ModuleMenuItem> ExplorerMenu(Workspace.ExplorerNode node) =>
        FolderWith(node, "go.mod") is { } dir
            ? [new("Manage Go Modules...", () => Workbench.ManagePackagesAsync(new GoPackages(dir))) { After = "Add" }]
            : [];

    public override IReadOnlyList<Templates.ProjectTemplate> ProjectTemplates => GoTemplates.All;

    public override IReadOnlyList<string> WorkspaceTaskFiles => ["go.mod"];

    /// <summary>Go's folder when Karvon's PATH has none (Karvon started from the desktop).</summary>
    public override IEnumerable<string> ToolDirectories => GoTools.DirectoryOffPath() is { } go ? [go] : [];

    public override IReadOnlyList<Testing.TestFramework> TestFrameworks(TestFrameworkStart start) =>
        GoTestFramework.IsUsedIn(start.Workspace) ? [new GoTestFramework(start)] : [];

    public override IReadOnlyList<DebuggerInfo> Debuggers =>
        [new("dlv", $"Delve {GoTools.DelveVersion}", GoDebugger.Types, GoDebugger.StartAsync)];

    public override IReadOnlyList<ModuleComponent> Components =>
    [
        new("gopls", "Go language server (gopls)", GoTools.GoplsVersion, GoTools.GoplsRoot, () => GoTools.FindGopls() is not null, GoTools.EnsureGoplsAsync)
        {
            ApproximateSize = 43L << 20,
            Description = "gopls (BSD-3-Clause), the Go team's language server, built with go install.",
        },
        new("dlv", "Go debugger (Delve)", GoTools.DelveVersion, GoTools.DelveRoot, () => GoTools.FindDelve() is not null, GoTools.EnsureDelveAsync)
        {
            ApproximateSize = 23L << 20,
            Description = "Delve (MIT), Go's debugger, built with go install.",
        },
    ];

    async Task<Lsp.LspClient> StartAsync(LanguageServerStart start)
    {
        var installing = GoTools.FindGopls() is null;
        if (installing)
            Workbench.Status($"Installing gopls {GoTools.GoplsVersion} (go install)...");
        var server = await GoplsServer.StartAsync(start);
        if (installing)
            Workbench.Status($"gopls {GoTools.GoplsVersion} installed.");
        return server;
    }
}
