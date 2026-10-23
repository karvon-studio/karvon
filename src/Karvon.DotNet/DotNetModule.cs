using Avalonia.Controls.Templates;
using Avalonia.Threading;
using Karvon.Build;
using Karvon.DotNet.Lsp;
using Karvon.DotNet.NuGet;
using Karvon.DotNet.Templates;
using Karvon.Modules;
using Karvon.Templates;
using Karvon.ViewModels;
using Karvon.Workspace;

namespace Karvon.DotNet;

/// <summary>
/// .NET: the SDK's project templates in New Project, C#'s in Add New Item, Test Explorer's xUnit, NUnit and MSTest
/// tests (vstest), the NuGet Package Manager (a project's packages, or the solution's), Solution Explorer's NuGet
/// commands and C#'s language server and debugger as its components. The rest of .NET (solutions, builds, the C# editor,
/// F5) is still Karvon's own, and comes here one part at a time.
/// </summary>
public sealed partial class DotNetModule : KarvonModule
{
    public override string Id => "dotnet";

    public override string Name => ".NET";


    public override string Icon => "csharp";

    public override string Description =>
        "C#, ASP.NET Core and Blazor. The SDK's project templates in New Project, C#'s and Razor's in Add New Item, Test "
        + "Explorer's xUnit, NUnit and MSTest tests, the NuGet Package Manager (Browse, Installed, Updates; a project's "
        + "packages or the solution's) and Solution Explorer's NuGet commands are the module's; solutions, builds, the C# "
        + "editor and F5 are still part of Karvon Studio itself, and stay when the module is off.";

    public override IReadOnlyList<ModuleComponent> Components =>
    [
        new("roslyn", "C# and Razor language server", RoslynServerLocator.Version, RoslynServerLocator.InstallRoot,
            () => RoslynServerLocator.Find() is not null, RoslynServerLocator.EnsureInstalledAsync)
        {
            ApproximateSize = 311L << 20,
            Description = "roslyn-language-server (MIT), Visual Studio's C# engine, from nuget.org.",
        },
        new("netcoredbg", ".NET debugger", Karvon.DotNet.Debugging.NetcoredbgLocator.Version, Karvon.DotNet.Debugging.NetcoredbgLocator.InstallRoot,
            () => Karvon.DotNet.Debugging.NetcoredbgLocator.Find() is not null, Karvon.DotNet.Debugging.NetcoredbgLocator.EnsureInstalledAsync)
        {
            ApproximateSize = 10L << 20,
            Description = "Samsung's netcoredbg (MIT), from its GitHub release.",
        },
    ];

    /// <summary>
    /// Roslyn, Visual Studio's C# engine, for C# and Razor: it starts with a solution or a project (not with a folder),
    /// loads its projects, and serves their files from then on.
    /// </summary>
    public override IReadOnlyList<LanguageServerInfo> LanguageServers =>
        [new("roslyn", "C# and Razor language server (Roslyn)", RoslynClient.Serves, StartRoslynAsync) { ForWorkspace = solution => !solution.IsFolder || DotNetFolder.Has(solution.Directory) }];

    /// <summary>The running Roslyn, and the project files it was told of.</summary>
    RoslynClient? _roslyn;
    HashSet<string> _roslynProjects = [];

    async Task<Karvon.Lsp.LspClient> StartRoslynAsync(LanguageServerStart start)
    {
        var solution = Workbench.Solution ?? throw new InvalidOperationException("No solution is open.");
        var install = await RoslynServerLocator.EnsureInstalledAsync(start.Log, start.Cancellation);
        // A folder: its one solution, else its projects.
        var (target, projects) = (solution.FilePath, solution.Projects.Select(p => p.FilePath).ToList());
        if (solution.IsFolder)
        {
            var (solutions, found) = DotNetFolder.Find(solution.Directory);
            projects = [.. found.Select(p => p.FilePath)];
            target = solutions.Count == 1 || projects.Count == 0 ? solutions[0] : projects[0];
        }
        var lsp = await RoslynClient.StartAsync(install, target, Path.Combine(Path.GetTempPath(), "Karvon", "roslyn-logs"), start.Cancellation, solution.Directory);
        if (solution.IsFolder && SolutionLoader.IsProjectFile(target) && projects.Count > 1)
            await lsp.OpenProjectsAsync(projects.Skip(1));
        _roslyn = lsp;
        _roslynProjects = [.. projects];
        lsp.RestoreRequested += projects => Dispatcher.UIThread.Post(() => _ = RestoreForRoslynAsync(projects));
        lsp.ProjectsLoaded += () => _ = WarmUpRazorAsync(lsp);
        return lsp;
    }

    /// <summary>
    /// Razor discovers components in the background after the projects load (~30 s on a large Blazor solution) without
    /// sending workspace/diagnostic/refresh, and early pulls report RZ10012 "unexpected markup element": the open
    /// documents are checked again while that warms up.
    /// </summary>
    static async Task WarmUpRazorAsync(RoslynClient lsp)
    {
        for (var i = 0; i < 18 && !lsp.IsDisposed; i++)
        {
            await Task.Delay(i == 0 ? 100 : 5000);
            if (lsp.IsDisposed)
                return;
            lsp.RefreshDiagnostics();
            if (i == 0)
                lsp.RefreshSemanticTokens();
        }
    }

    /// <summary>The solution read again: Roslyn loads the projects it does not have yet (added, or a project file renamed).</summary>
    void OnSolutionReloaded()
    {
        if (_roslyn is not { IsDisposed: false } lsp || Workbench.Solution is not { IsFolder: false } solution)
            return;
        var added = solution.Projects.Select(p => p.FilePath).Where(p => !_roslynProjects.Contains(p)).ToList();
        _roslynProjects = [.. solution.Projects.Select(p => p.FilePath)];
        if (added.Count > 0)
            _ = lsp.OpenProjectsAsync(added);
    }

    /// <summary>A project Roslyn loads needs its packages: dotnet restore, in Output (Build).</summary>
    async Task RestoreForRoslynAsync(IReadOnlyList<string> projects)
    {
        if (Workbench.Solution is not { IsFolder: false } solution)
            return;
        var output = Workbench.Output("Build");
        var nuget = new NuGetService(solution.Directory, output.Append);
        foreach (var project in projects.Count > 0 ? projects : [solution.FilePath])
        {
            output.Append($"Restoring packages for {project}...");
            await nuget.RestoreAsync(project, CancellationToken.None);
        }
    }

    /// <summary>The SDK's project templates (dotnet new's), as Visual Studio lists them.</summary>
    public override Task<IReadOnlyList<ProjectTemplate>> LoadProjectTemplatesAsync(CancellationToken ct) => TemplateCatalog.LoadAsync(ct);

    public override bool OpensWorkspace(string path) => SolutionReader.Opens(path);

    /// <summary>F5 on a solution's startup project: netcoredbg's "dotnet" launch configuration of it.</summary>
    public override DebugLaunch? StartupLaunch(ProjectInfo project, string? launchProfile, string configuration)
    {
        if (!SolutionLoader.IsProjectFile(project.FilePath))
            return null;
        var launch = new System.Text.Json.Nodes.JsonObject
        {
            ["name"] = project.Name,
            ["type"] = "dotnet",
            ["request"] = "launch",
            ["projectPath"] = project.FilePath,
            ["configuration"] = configuration,
        };
        if (launchProfile is not null)
            launch["launchProfile"] = launchProfile;
        return new DebugLaunch(project.Name, launch);
    }

    /// <summary>A folder opened as it is with .NET in it: dotnet build, test and run of its solution and projects.</summary>
    public override IReadOnlyList<WorkspaceTask> WorkspaceTasks(string workspace) =>
        Workbench.Solution is { IsFolder: true } ? DotNetFolder.Tasks(workspace) : [];

    public override Task<SolutionInfo> OpenWorkspaceAsync(string path, CancellationToken ct) => SolutionReader.LoadAsync(path, ct);

    /// <summary>dotnet build (clean, pack, publish) of the solution or of one of its projects, as Visual Studio builds it.</summary>
    public override BuildCommand? BuildSolution(SolutionInfo solution, BuildRequest request)
    {
        if (solution.IsFolder || request.Verb is not ("build" or "rebuild" or "clean" or "pack" or "publish"))
            return null;
        _usedBuildServers = true;
        List<string> args = [request.Verb == "rebuild" ? "build" : request.Verb, request.Target, "-c", request.Configuration, "-nologo", "-v:minimal", "-clp:NoSummary;ForceNoAlign"];
        if (request.Verb == "rebuild")
            args.Add("--no-incremental");
        return new BuildCommand(DotnetCli.Executable, args) { ProjectBuilt = line => line.Contains(" -> ", StringComparison.Ordinal) };
    }

    /// <summary>
    /// Whether it built. dotnet build leaves the C# compiler server (VBCSCompiler) and MSBuild nodes running for the next
    /// build, about a gigabyte together, and they only exit after several idle minutes: Karvon keeps them while it is
    /// open and shuts them down when it exits, if it built anything.
    /// </summary>
    bool _usedBuildServers;

    /// <summary>`dotnet build-server shutdown` (replaced in tests, whose builds share the servers with other tests).</summary>
    public Func<Task> ShutdownBuildServers { get; set; } = ShutdownBuildServersCoreAsync;

    public override async ValueTask DisposeAsync()
    {
        if (_usedBuildServers)
        {
            _usedBuildServers = false;
            await ShutdownBuildServers();
        }
        await base.DisposeAsync();
    }

    static async Task ShutdownBuildServersCoreAsync()
    {
        try
        {
            using var runner = ProcessRunner.Start(DotnetCli.Executable, ["build-server", "shutdown"],
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), _ => { });
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await runner.WaitAsync(cts.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            // Leaving them to exit on their own is fine.
        }
    }

    public override IReadOnlyList<ItemTemplate> ItemTemplates => CSharpItemTemplates.All;

    /// <summary>netcoredbg, for "coreclr" configurations and "dotnet" ones (a project and its launch profile: F5 on the startup project).</summary>
    public override IReadOnlyList<DebuggerInfo> Debuggers => [Debugging.DotNetDebugger.Info(Workbench, OfferDevCertificateTrustAsync)];

    /// <summary>The solution's xUnit, NUnit and MSTest tests, through the SDK's vstest; in Test Explorer once it has a test project.</summary>
    public override IReadOnlyList<Karvon.Testing.TestFramework> TestFrameworks(TestFrameworkStart start) => [new Testing.DotNetTests(Workbench, start)];

    public override IReadOnlyList<IDataTemplate> DocumentTemplates => [Document<NuGetViewModel>(_ => new NuGetView())];

    public override IReadOnlyList<ModuleMainMenuItem> MainMenus =>
    [
        new("Tools", new("_NuGet Package Manager", null) { Items = [new("Manage NuGet Packages for _Solution...", () => ManagePackagesAsync(null))] }),
        new("Tools", new("_Trust ASP.NET Core Development Certificate", TrustDevCertificateAsync) { After = "Restart Language Server" }),
    ];

    public override IReadOnlyList<ModuleMenuItem> ExplorerMenu(ExplorerNode node)
    {
        switch (node.Kind)
        {
            // After Build, Rebuild, Clean and Run Tests, as Visual Studio has them.
            case NodeKind.Solution when !node.IsFolderRoot:
                return
                [
                    ModuleMenuItem.Separator with { After = "Run Tests" },
                    new("Manage NuGet Packages for Solution...", () => ManagePackagesAsync(null)) { After = "Run Tests" },
                    new("Restore NuGet Packages", () => RestorePackagesAsync()) { After = "Run Tests" },
                ];
            case NodeKind.Project when node.Project is { } project:
                return
                [
                    // Add > New EditorConfig and Project Reference... (the Reference Manager), before Add's items of the project's kind.
                    EditorConfigItem(project.Directory),
                    ModuleMenuItem.Separator with { In = "Add" },
                    new("Project Reference...", () => EditProjectReferencesAsync(project)) { In = "Add" },
                    // Pack (dotnet pack: its NuGet package in bin/<configuration>) and Publish to a folder (dotnet publish, as
                    // VS's folder profile), after Clean (and Run Tests), as Visual Studio has them.
                    new("Pack", () => Workbench.BuildAsync("pack", project)) { After = project.IsTestProject ? "Run Tests" : "Clean" },
                    new("Publish to Folder", () => Workbench.BuildAsync("publish", project)) { After = project.IsTestProject ? "Run Tests" : "Clean" },
                    .. AddItems(node),
                    new("Manage NuGet Packages...", () => ManagePackagesAsync(project)) { After = "Add" },
                    .. project.IsRunnable ? [new ModuleMenuItem("Manage User Secrets", () => ManageUserSecretsAsync(project)) { After = "Add" }] : Array.Empty<ModuleMenuItem>(),
                ];
            // A folder of a project.
            case NodeKind.Folder when Workbench.ProjectOf(node) is not null:
                return [EditorConfigItem(node.Path!), .. AddItems(node)];
            // A folder opened as it is with .NET in it: its folders' New EditorConfig.
            case NodeKind.Folder or NodeKind.Solution when node.Path is { } folder && (node.Kind == NodeKind.Folder || node.IsFolderRoot)
                && Workbench.Solution is { IsFolder: true } opened && DotNetFolder.Has(opened.Directory):
                return [EditorConfigItem(folder)];
            // A language's Dependencies (a package.json folder's) are not .NET's.
            case NodeKind.Dependencies or NodeKind.DependencyGroup or NodeKind.Package when node.Packages is not null:
                return [];
            case NodeKind.Dependencies or NodeKind.DependencyGroup:
            {
                var owner = Workbench.ProjectOf(node);
                return
                [
                    .. node.Kind == NodeKind.Dependencies || node.Name == "Projects"
                        ? [new ModuleMenuItem("Add Project Reference...", () => EditProjectReferencesAsync(owner)) { IsEnabled = owner is not null }]
                        : Array.Empty<ModuleMenuItem>(),
                    new("Manage NuGet Packages...", () => ManagePackagesAsync(owner)) { IsEnabled = owner is not null },
                ];
            }
            case NodeKind.ProjectReference when node.Path is { } reference:
            {
                var owner = Workbench.ProjectOf(node);
                return
                [
                    new("Open Project File", () =>
                    {
                        Workbench.OpenFile(reference);
                        return Task.CompletedTask;
                    }),
                    ModuleMenuItem.Separator,
                    new("Remove", () => RemoveProjectReferenceAsync(owner!, reference)) { IsEnabled = owner is not null },
                ];
            }
            case NodeKind.Package:
            {
                var owner = Workbench.ProjectOf(node);
                return
                [
                    new("Manage NuGet Packages...", () => ManagePackagesAsync(owner)) { IsEnabled = owner is not null },
                    ModuleMenuItem.Separator,
                    new("Remove", () => RemovePackageAsync(owner!, node.Name)) { IsEnabled = owner is not null },
                ];
            }
            default:
                return [];
        }
    }

    /// <summary>Add > Class... and Razor Component..., as Visual Studio has them in a project and its folders.</summary>
    IReadOnlyList<ModuleMenuItem> AddItems(ExplorerNode node) =>
    [
        ModuleMenuItem.Separator with { In = "Add" },
        new("Class...", () => Workbench.AddNewItemAsync(node, CSharpItemTemplates.Class)) { In = "Add" },
        new("Razor Component...", () => Workbench.AddNewItemAsync(node, CSharpItemTemplates.RazorComponent)) { In = "Add" },
    ];

    /// <summary>The Output window's Package Manager, as Visual Studio's: what NuGet's commands say.</summary>
    OutputChannel Output => Workbench.Output("Package Manager");

    protected override void Started()
    {
        _ = Output;
        Workbench.SolutionReloaded += OnSolutionReloaded;
    }

    /// <summary>The NuGet Package Manager of a project, or of the whole solution when null: the one open, else a new one.</summary>
    internal async Task ManagePackagesAsync(ProjectInfo? project)
    {
        if (Workbench.Solution is not { } solution)
            return;
        var key = project is null ? "nuget:solution" : "nuget:" + project.FilePath;
        if (Workbench.Documents.FirstOrDefault(d => d.Key == key) is { } open)
        {
            Workbench.ShowDocument(open);
            return;
        }
        // After an install or an uninstall the projects are read again, for Solution Explorer's Dependencies.
        var manager = new NuGetViewModel(solution, project, Output.Append, Workbench.ReloadSolutionAsync);
        Workbench.ShowDocument(manager);
        await manager.InitializeAsync();
    }

    /// <summary>Remove on a package in Dependencies: dotnet remove package, then the projects read again.</summary>
    internal async Task<bool> RemovePackageAsync(ProjectInfo project, string package)
    {
        if (Workbench.Solution is not { } solution)
            return false;
        var (ok, output) = await new NuGetService(solution.Directory, Output.Append).UninstallAsync(project.FilePath, package, CancellationToken.None);
        if (!ok)
            Workbench.Status(LastLine(output) ?? $"{package} was not removed.");
        await Workbench.ReloadSolutionAsync();
        return ok;
    }

    /// <summary>Restore NuGet Packages: dotnet restore of the solution, in Output (Package Manager), as Visual Studio does.</summary>
    internal async Task<bool> RestorePackagesAsync()
    {
        if (Workbench.Solution is not { IsFolder: false } solution)
            return false;
        Workbench.ShowOutput(Output);
        Workbench.Status("Restoring NuGet packages...");
        var (ok, output) = await new NuGetService(solution.Directory, Output.Append).RestoreAsync(solution.FilePath, CancellationToken.None);
        Workbench.Status(ok ? "Restore succeeded" : LastLine(output) ?? "Restore failed");
        return ok;
    }

    static string? LastLine(string output) => output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
}
