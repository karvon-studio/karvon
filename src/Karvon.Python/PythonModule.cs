using Karvon.Modules;

namespace Karvon.Python;

/// <summary>
/// Python: Pyright's language server in the editor of .py files (errors as you type, completion, hover, go to
/// definition), installed the first time one of them opens, and debugpy for F5, both with the workspace's interpreter
/// (its .venv first).
/// </summary>
public sealed class PythonModule : KarvonModule
{
    public override string Id => "python";

    public override string Name => "Python";


    public override string Icon => "python";

    public override string Description =>
        "Pyright's language server for .py files: errors as you type, completion, hover, go to definition, references and "
        + "rename, installed the first time one of them opens (it runs on Node.js); F5 debugs a launch.json python configuration "
        + "with debugpy, and Test Explorer runs and debugs pytest's tests. They use the workspace's interpreter (its .venv first). "
        + "A uv project has uv sync and its scripts as tasks.";

    public override IReadOnlyList<LanguageServerInfo> LanguageServers =>
        [new("pyright", "Python language server (Pyright)", PyrightServer.Serves, StartAsync)];

    public override IReadOnlyList<ModuleComponent> Components =>
    [
        new("pyright", "Python language server (Pyright)", PyrightLocator.Version, PyrightLocator.InstallRoot,
            () => PyrightLocator.Find() is not null, PyrightLocator.EnsureInstalledAsync)
        {
            ApproximateSize = 38L << 20,
            Description = "Pyright (MIT), Microsoft's Python type checker and language server, from the npm registry; it runs on Node.js.",
        },
        new("debugpy", "Python debugger (debugpy)", DebugpyLocator.Version, DebugpyLocator.InstallRoot,
            () => DebugpyLocator.Find() is not null, DebugpyLocator.EnsureInstalledAsync)
        {
            ApproximateSize = 31L << 20,
            Description = "debugpy (MIT), Microsoft's Python debugger, from PyPI; the workspace's interpreter runs it.",
        },
    ];

    /// <summary>A folder's entry point (main.py, app.py, manage.py) and the file in the editor, to run and debug.</summary>
    public override IReadOnlyList<WorkspaceTask> WorkspaceTasks(string workspace) => PythonTasks.Read(workspace);

    public override IReadOnlyList<Templates.ProjectTemplate> ProjectTemplates => PythonTemplates.All;

    /// <summary>A uv project's scripts are pyproject.toml's.</summary>
    public override IReadOnlyList<string> WorkspaceTaskFiles => ["pyproject.toml"];

    /// <summary>A Python project's Dependencies in Solution Explorer.</summary>
    public override Packages.PackageSource? PackagesIn(string folder) =>
        File.Exists(Path.Combine(folder, "pyproject.toml")) || File.Exists(Path.Combine(folder, "requirements.txt")) ? new PypiPackages(folder) : null;

    /// <summary>
    /// A Python project's Manage Python Packages...: PyPI's, by uv in a uv project (pyproject.toml's [project]), else by
    /// pip into its virtual environment (requirements.txt).
    /// </summary>
    public override IReadOnlyList<ModuleMenuItem> ExplorerMenu(Workspace.ExplorerNode node) =>
        (FolderWith(node, "pyproject.toml") ?? FolderWith(node, "requirements.txt")) is { } dir
            ? [new("Manage Python Packages...", () => Workbench.ManagePackagesAsync(new PypiPackages(dir))) { After = "Add" }]
            : [];

    /// <summary>Test Explorer: pytest's tests, when the workspace tests with pytest.</summary>
    public override IReadOnlyList<Testing.TestFramework> TestFrameworks(TestFrameworkStart start) =>
        PytestFramework.IsUsedIn(start.Workspace) ? [new PytestFramework(start)] : [];

    /// <summary>F5: debugpy, for launch.json's debugpy and python configurations.</summary>
    public override IReadOnlyList<DebuggerInfo> Debuggers =>
        [new("debugpy", $"debugpy {DebugpyLocator.Version}", PythonDebugger.Types, PythonDebugger.StartAsync)];

    /// <summary>
    /// A folder whose packages are nowhere (Pyright cannot resolve its imports): a uv project synced, else a virtual
    /// environment made with requirements.txt installed into it (or requirements.txt into the one it has).
    /// </summary>
    public override PackageRestore? PackageRestoreIn(string folder) => PythonEnvironmentSetup.Offer(folder) is { } title
        ? new(title, PythonEnvironmentSetup.IsMissing(folder), PythonEnvironmentSetup.Steps(folder, Environment.GetEnvironmentVariable("PATH") ?? ""))
        {
            Problem = "Python was not found: install Python 3, then try again.",
        }
        : null;

    /// <summary>Pyright's import it cannot resolve (its module, or its source beside the stubs).</summary>
    public override bool IsUnresolvedImport(Lsp.LspDiagnostic diagnostic) => diagnostic.Code is "reportMissingImports" or "reportMissingModuleSource";

    async Task<Lsp.LspClient> StartAsync(LanguageServerStart start)
    {
        var installing = PyrightLocator.Find() is null;
        if (installing)
            Workbench.Status($"Installing Pyright {PyrightLocator.Version}...");
        var server = await PyrightServer.StartAsync(start);
        if (installing)
            Workbench.Status($"Pyright {PyrightLocator.Version} installed.");
        return server;
    }
}
