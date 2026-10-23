using Avalonia.Controls.Templates;
using Karvon.DotNet.NuGet;
using Karvon.DotNet.Templates;
using Karvon.Modules;
using Karvon.Templates;
using Karvon.ViewModels;
using Karvon.Workspace;

namespace Karvon.DotNet;

/// <summary>
/// .NET: the SDK's project templates in New Project, the NuGet Package Manager (a project's packages, or the
/// solution's), Solution Explorer's NuGet commands and C#'s language server and debugger as its components. The rest of .NET (solutions, builds, the C# editor, F5, tests)
/// is still Karvon's own, and comes here one part at a time.
/// </summary>
public sealed class DotNetModule : KarvonModule
{
    public override string Id => "dotnet";

    public override string Name => ".NET";

    public override string Description =>
        "C#, ASP.NET Core and Blazor. The SDK's project templates in New Project, the NuGet Package Manager (Browse, "
        + "Installed, Updates; a project's packages or the solution's) and Solution Explorer's NuGet commands are the "
        + "module's; solutions, builds, the C# editor, F5 and tests are still part of Karvon Studio itself, and stay when "
        + "the module is off.";

    public override IReadOnlyList<ModuleComponent> Components =>
    [
        new("roslyn", "C# and Razor language server", Lsp.RoslynServerLocator.Version, Lsp.RoslynServerLocator.InstallRoot,
            () => Lsp.RoslynServerLocator.Find() is not null, Lsp.RoslynServerLocator.EnsureInstalledAsync)
        {
            Description = "roslyn-language-server (MIT), Visual Studio's C# engine, from nuget.org.",
        },
        new("netcoredbg", ".NET debugger", Debugging.NetcoredbgLocator.Version, Debugging.NetcoredbgLocator.InstallRoot,
            () => Debugging.NetcoredbgLocator.Find() is not null, Debugging.NetcoredbgLocator.EnsureInstalledAsync)
        {
            Description = "Samsung's netcoredbg (MIT), from its GitHub release.",
        },
    ];

    /// <summary>The SDK's project templates (dotnet new's), as Visual Studio lists them.</summary>
    public override Task<IReadOnlyList<ProjectTemplate>> LoadProjectTemplatesAsync(CancellationToken ct) => TemplateCatalog.LoadAsync(ct);

    public override IReadOnlyList<ItemTemplate> ItemTemplates => CSharpItemTemplates.All;

    public override IReadOnlyList<IDataTemplate> DocumentTemplates => [Document<NuGetViewModel>(_ => new NuGetView())];

    public override IReadOnlyList<ModuleSubmenu> MainMenus =>
        [new("Tools", "_NuGet Package Manager", [new("Manage NuGet Packages for _Solution...", () => ManagePackagesAsync(null))])];

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
                return [.. AddItems(node), new("Manage NuGet Packages...", () => ManagePackagesAsync(project)) { After = "Add" }];
            // A folder of a project.
            case NodeKind.Folder when Workbench.ProjectOf(node) is not null:
                return AddItems(node);
            case NodeKind.Dependencies or NodeKind.DependencyGroup:
            {
                var owner = Workbench.ProjectOf(node);
                return [new("Manage NuGet Packages...", () => ManagePackagesAsync(owner)) { IsEnabled = owner is not null }];
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

    protected override void Started() => _ = Output;

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
