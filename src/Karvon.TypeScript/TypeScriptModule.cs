using Karvon.Modules;

namespace Karvon.TypeScript;

/// <summary>
/// TypeScript and JavaScript: TypeScript 7's own language server in the editor of their files (errors as you type,
/// completion, hover, go to definition, references, rename, formatting), installed the first time one of them opens; a
/// folder's package.json scripts as its tasks (build, test, the app); js-debug for F5; and ESLint's diagnostics beside
/// TypeScript's, where the user lets it run the folder's ESLint.
/// </summary>
public sealed class TypeScriptModule : KarvonModule
{
    public override string Id => "typescript";

    public override string Name => "TypeScript and JavaScript";


    public override string Icon => "typescript";

    public override string Description =>
        "TypeScript 7's language server for .ts, .tsx, .js and .jsx files: errors as you type, completion, hover, go to definition, "
        + "references, rename and formatting, installed the first time one of them opens (no Node.js needed). In a folder, the "
        + "package.json scripts build, run and test it (with npm, pnpm, yarn or bun), and F5 debugs the app or a launch.json "
        + "configuration with js-debug, the JavaScript debugger of VS Code (installed the first time; it runs on Node.js). "
        + "Test Explorer runs and debugs a package's Vitest or Jest tests. VS Code's HTML, CSS (with SCSS and Less) and JSON "
        + "language servers serve those files (installed the first time one of them opens; they run on Node.js). In a folder with "
        + "its own ESLint, ESLint checks the JavaScript and TypeScript files as you type, once you let it run there.";

    public override IReadOnlyList<LanguageServerInfo> LanguageServers =>
    [
        new("typescript", "TypeScript language server", TypeScriptServer.Serves, StartAsync),
        WebServer("html", "HTML language server"),
        WebServer("css", "CSS language server"),
        WebServer("json", "JSON language server"),
        new("eslint", "ESLint", EslintServer.Serves, StartEslintAsync) { Lints = true, MayStartAsync = MayRunEslintAsync },
    ];

    /// <summary>
    /// A package.json folder's Manage npm Packages... (or pnpm's, yarn's, bun's), as a .NET project's Manage NuGet
    /// Packages...: on the folder (a frontend beside a solution, the folder opened), and on its package.json.
    /// </summary>
    /// <summary>A package.json folder's Dependencies in Solution Explorer.</summary>
    public override Packages.PackageSource? PackagesIn(string folder) => File.Exists(Path.Combine(folder, "package.json")) ? new NpmPackages(folder) : null;

    /// <summary>A package.json's packages installed by the folder's package manager (npm, pnpm, yarn or bun install); missing with no node_modules.</summary>
    public override PackageRestore? PackageRestoreIn(string folder)
    {
        if (!File.Exists(Path.Combine(folder, "package.json")) || new NpmPackages(folder).Dependencies().Count == 0)
            return null;
        var manager = PackageScripts.ManagerOf(folder);
        return new($"Install the packages ({manager} install)", !Directory.Exists(Path.Combine(folder, "node_modules")),
            [new($"{manager}: install", WorkspaceTaskKind.Other, manager, ["install"], folder) { Source = PackageScripts.Source }]);
    }

    /// <summary>TypeScript's "Cannot find module" (TS2307): a package not installed.</summary>
    public override bool IsUnresolvedImport(Lsp.LspDiagnostic diagnostic) => diagnostic.Code == "2307";

    public override IReadOnlyList<ModuleMenuItem> ExplorerMenu(Workspace.ExplorerNode node) =>
        FolderWith(node, "package.json") is { } dir
            ? [new($"Manage {PackageScripts.ManagerOf(dir)} Packages...", () => Workbench.ManagePackagesAsync(new NpmPackages(dir))) { After = "Add" }]
            : [];

    /// <summary>Tools > ESLint in This Folder: whether ESLint checks the open folder's files, asked again.</summary>
    public override IReadOnlyList<ModuleMainMenuItem> MainMenus =>
        [new("Tools", new("_ESLint in This Folder...", ChooseEslintAsync) { After = "Restart Language Server" })];

    static string FolderKey(string workspace) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));

    /// <summary>
    /// Whether ESLint checks a workspace's files: only with the folder's own ESLint, and once the user let it, as that
    /// runs the folder's code (its ESLint, configuration and plugins). The answer is kept in Karvon's settings.
    /// </summary>
    async Task<bool> MayRunEslintAsync(string workspace, string file)
    {
        if (EslintOf(workspace, file) is not { } library)
            return false;
        return Workbench.Settings.EslintFolders.TryGetValue(FolderKey(workspace), out var allowed) ? allowed : await AskEslintAsync(workspace, library) == true;
    }

    /// <summary>The folder's own ESLint: the file's, else the folder's, else one of its packages' (a frontend beside a solution).</summary>
    static string? EslintOf(string workspace, string file) =>
        EslintServer.LibraryFor(file, workspace) ?? EslintServer.LibraryFor(workspace, workspace)
        ?? PackageScripts.Projects(workspace).Select(p => EslintServer.LibraryFor(p.Directory, workspace)).OfType<string>().FirstOrDefault();

    /// <summary>The question, its answer kept (the dialog closed: none, and it is asked again the next time).</summary>
    async Task<bool?> AskEslintAsync(string workspace, string library)
    {
        var answer = await Workbench.ConfirmAsync("ESLint", "Let ESLint check this folder's JavaScript and TypeScript as you type?\n\n"
            + $"It runs the folder's own ESLint ({Path.GetRelativePath(workspace, library)}) with its configuration and plugins: code of the "
            + "folder's. Let it only in a folder you trust; Tools > ESLint in This Folder changes your answer.");
        if (answer is { } yes)
        {
            Workbench.Settings.EslintFolders[FolderKey(workspace)] = yes;
            Workbench.SaveSettings();
        }
        return answer;
    }

    async Task ChooseEslintAsync()
    {
        if (Workbench.Workspace is not { } workspace)
        {
            Workbench.Status("Open a folder first: ESLint checks the files of a folder with its own ESLint.");
            return;
        }
        if (EslintOf(workspace, workspace) is not { } library)
        {
            Workbench.Status("This folder has no ESLint of its own (node_modules/eslint): npm install --save-dev eslint, then try again.");
            return;
        }
        Workbench.Settings.EslintFolders.TryGetValue(FolderKey(workspace), out var before);
        if (await AskEslintAsync(workspace, library) is { } now && now != before)
            await Workbench.RestartLanguageServersAsync();
    }

    async Task<Lsp.LspClient> StartEslintAsync(LanguageServerStart start)
    {
        var installing = EslintLocator.Find() is null;
        if (installing)
            Workbench.Status($"Installing ESLint's language server {EslintLocator.Version}...");
        var server = await EslintServer.StartAsync(start, url => Avalonia.Threading.Dispatcher.UIThread.Post(() => Workbench.OpenUrl(url)));
        if (installing)
            Workbench.Status($"ESLint's language server {EslintLocator.Version} installed.");
        return server;
    }

    /// <summary>One of VS Code's HTML, CSS and JSON servers, for its language's files.</summary>
    LanguageServerInfo WebServer(string language, string name) =>
        new(language, name, path => WebLanguageServer.LanguageOf(path) == language, async start =>
        {
            var installing = !WebLanguagesLocator.IsInstalled();
            if (installing)
                Workbench.Status($"Installing the HTML, CSS and JSON language servers {WebLanguagesLocator.Version}...");
            var server = await WebLanguageServer.StartAsync(language, start);
            if (installing)
                Workbench.Status($"The HTML, CSS and JSON language servers {WebLanguagesLocator.Version} installed.");
            return server;
        });

    /// <summary>The package.json scripts: build, test, the app (dev or start), and the rest in Build > Run Task.</summary>
    public override IReadOnlyList<WorkspaceTask> WorkspaceTasks(string workspace) => PackageScripts.Read(workspace);

    public override IReadOnlyList<string> WorkspaceTaskFiles => ["package.json"];

    public override IReadOnlyList<Templates.ProjectTemplate> ProjectTemplates => TypeScriptTemplates.All;

    /// <summary>Test Explorer: the Vitest and Jest tests of the packages that depend on them.</summary>
    public override IReadOnlyList<Testing.TestFramework> TestFrameworks(TestFrameworkStart start) =>
        [.. VitestFramework.In(start), .. JestFramework.In(start)];

    /// <summary>A solution's frontends: the package.json folders beside its projects.</summary>
    public override IReadOnlyList<WorkspaceProject> WorkspaceProjects(string workspace) => PackageScripts.Projects(workspace);

    public override IReadOnlyList<ModuleComponent> Components =>
    [
        new("typescript", "TypeScript language server", TypeScriptServerLocator.Version, TypeScriptServerLocator.InstallRoot,
            () => TypeScriptServerLocator.Find() is not null, TypeScriptServerLocator.EnsureInstalledAsync)
        {
            ApproximateSize = 27L << 20,
            Description = "TypeScript 7's native compiler, its own language server (Apache-2.0), from the npm registry.",
        },
        new("js-debug", "JavaScript debugger", JsDebugLocator.Version, JsDebugLocator.InstallRoot,
            () => JsDebugLocator.Find() is not null, JsDebugLocator.EnsureInstalledAsync)
        {
            ApproximateSize = 3L << 20,
            Description = "js-debug, the JavaScript debugger of VS Code (MIT), from its GitHub release; it runs on Node.js.",
        },
        new("web-languages", "HTML, CSS and JSON language servers", WebLanguagesLocator.Version, WebLanguagesLocator.InstallRoot,
            WebLanguagesLocator.IsInstalled, WebLanguagesLocator.EnsureInstalledAsync)
        {
            ApproximateSize = 34L << 20,
            Description = "VS Code's own (MIT), from Open VSX, with the TypeScript library (Apache-2.0) for HTML's scripts; they run on Node.js.",
        },
        new("eslint", "ESLint language server", EslintLocator.Version, EslintLocator.InstallRoot,
            () => EslintLocator.Find() is not null, EslintLocator.EnsureInstalledAsync)
        {
            ApproximateSize = 1L << 20,
            Description = "The one of VS Code's ESLint extension (MIT), from Open VSX; on Node.js, it runs a folder's own ESLint where you let it.",
        },
    ];

    /// <summary>F5: js-debug, for launch.json's node, chrome and msedge configurations and the package.json scripts.</summary>
    public override IReadOnlyList<DebuggerInfo> Debuggers =>
        [new("js-debug", $"the JavaScript debugger (js-debug {JsDebugLocator.Version})", JsDebugger.Types, StartDebuggerAsync)];

    /// <summary>Node.js's folder when Karvon's PATH has none (started from the desktop, with Node from nvm).</summary>
    public override IEnumerable<string> ToolDirectories => Build.NodeLocator.DirectoryOffPath() is { } node ? [node] : [];

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
