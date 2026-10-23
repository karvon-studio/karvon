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

    public override string Description =>
        "Pyright's language server for .py files: errors as you type, completion, hover, go to definition, references and "
        + "rename, installed the first time one of them opens (it runs on Node.js); F5 debugs a launch.json python configuration "
        + "with debugpy, and Test Explorer runs and debugs pytest's tests. They use the workspace's interpreter (its .venv first).";

    public override IReadOnlyList<LanguageServerInfo> LanguageServers =>
        [new("pyright", "Python language server (Pyright)", PyrightServer.Serves, StartAsync)];

    public override IReadOnlyList<ModuleComponent> Components =>
    [
        new("pyright", "Python language server (Pyright)", PyrightLocator.Version, PyrightLocator.InstallRoot,
            () => PyrightLocator.Find() is not null, PyrightLocator.EnsureInstalledAsync)
        {
            Description = "Pyright (MIT), Microsoft's Python type checker and language server, from the npm registry; it runs on Node.js.",
        },
        new("debugpy", "Python debugger (debugpy)", DebugpyLocator.Version, DebugpyLocator.InstallRoot,
            () => DebugpyLocator.Find() is not null, DebugpyLocator.EnsureInstalledAsync)
        {
            Description = "debugpy (MIT), Microsoft's Python debugger, from PyPI; the workspace's interpreter runs it.",
        },
    ];

    /// <summary>A folder's entry point (main.py, app.py, manage.py) and the file in the editor, to run and debug.</summary>
    public override IReadOnlyList<WorkspaceTask> WorkspaceTasks(string workspace) => PythonTasks.Read(workspace);

    /// <summary>Test Explorer: pytest's tests, when the workspace tests with pytest.</summary>
    public override IReadOnlyList<Testing.TestFramework> TestFrameworks(TestFrameworkStart start) =>
        PytestFramework.IsUsedIn(start.Workspace) ? [new PytestFramework(start)] : [];

    /// <summary>F5: debugpy, for launch.json's debugpy and python configurations.</summary>
    public override IReadOnlyList<DebuggerInfo> Debuggers =>
        [new("debugpy", $"debugpy {DebugpyLocator.Version}", PythonDebugger.Types, PythonDebugger.StartAsync)];

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
