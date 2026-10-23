using Karvon.Modules;

namespace Karvon.TypeScript;

/// <summary>
/// TypeScript and JavaScript: TypeScript 7's own language server in the editor of their files (errors as you type,
/// completion, hover, go to definition, references, rename, formatting), installed the first time one of them opens; a
/// folder's package.json scripts as its tasks (build, test, the app); and js-debug for F5.
/// </summary>
public sealed class TypeScriptModule : KarvonModule
{
    public override string Id => "typescript";

    public override string Name => "TypeScript and JavaScript";

    public override string Description =>
        "TypeScript 7's language server for .ts, .tsx, .js and .jsx files: errors as you type, completion, hover, go to definition, "
        + "references, rename and formatting, installed the first time one of them opens (no Node.js needed). In a folder, the "
        + "package.json scripts build, run and test it (with npm, pnpm, yarn or bun), and F5 debugs the app or a launch.json "
        + "configuration with js-debug, the JavaScript debugger of VS Code (installed the first time; it runs on Node.js).";

    public override IReadOnlyList<LanguageServerInfo> LanguageServers =>
        [new("typescript", "TypeScript language server", TypeScriptServer.Serves, StartAsync)];

    /// <summary>The package.json scripts: build, test, the app (dev or start), and the rest in Build > Run Task.</summary>
    public override IReadOnlyList<WorkspaceTask> WorkspaceTasks(string workspace) => PackageScripts.Read(workspace);

    public override IReadOnlyList<string> WorkspaceTaskFiles => ["package.json"];

    /// <summary>A solution's frontends: the package.json folders beside its projects.</summary>
    public override IReadOnlyList<WorkspaceProject> WorkspaceProjects(string workspace) => PackageScripts.Projects(workspace);

    public override IReadOnlyList<ModuleComponent> Components =>
    [
        new("typescript", "TypeScript language server", TypeScriptServerLocator.Version, TypeScriptServerLocator.InstallRoot,
            () => TypeScriptServerLocator.Find() is not null, TypeScriptServerLocator.EnsureInstalledAsync)
        {
            Description = "TypeScript 7's native compiler, its own language server (Apache-2.0), from the npm registry.",
        },
        new("js-debug", "JavaScript debugger", JsDebugLocator.Version, JsDebugLocator.InstallRoot,
            () => JsDebugLocator.Find() is not null, JsDebugLocator.EnsureInstalledAsync)
        {
            Description = "js-debug, the JavaScript debugger of VS Code (MIT), from its GitHub release; it runs on Node.js.",
        },
    ];

    /// <summary>F5: js-debug, for launch.json's node, chrome and msedge configurations and the package.json scripts.</summary>
    public override IReadOnlyList<DebuggerInfo> Debuggers =>
        [new("js-debug", $"the JavaScript debugger (js-debug {JsDebugLocator.Version})", JsDebugger.Types, StartDebuggerAsync)];

    /// <summary>Node.js's folder when Karvon's PATH has none (started from the desktop, with Node from nvm).</summary>
    public override IEnumerable<string> ToolDirectories => NodeLocator.DirectoryOffPath() is { } node ? [node] : [];

    async Task<Debugging.DebugAdapter> StartDebuggerAsync(DebuggerStart start)
    {
        var installing = JsDebugLocator.Find() is null;
        if (installing)
            Workbench.Status($"Installing the JavaScript debugger (js-debug {JsDebugLocator.Version})...");
        var adapter = await JsDebugger.StartAsync(start);
        if (installing)
            Workbench.Status($"js-debug {JsDebugLocator.Version} installed.");
        return adapter;
    }

    async Task<Lsp.LspClient> StartAsync(LanguageServerStart start)
    {
        var installing = TypeScriptServerLocator.Find() is null;
        if (installing)
            Workbench.Status($"Installing TypeScript {TypeScriptServerLocator.Version}...");
        var server = await TypeScriptServer.StartAsync(start);
        if (installing)
            Workbench.Status($"TypeScript {TypeScriptServerLocator.Version} installed.");
        return server;
    }
}
