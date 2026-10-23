using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Karvon.Modules;
using Karvon.Packages;
using Karvon.TypeScript;
using Karvon.Views;
using Karvon.Workspace;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>The tests' registry: two packages, the project depending on an old alpha.</summary>
sealed class FakePackages(string directory) : PackageSource
{
    public List<DependencyPackage> Installed { get; } = [new("alpha", "^1.0.0", "1.0.0", "dependencies")];

    static readonly RegistryPackage[] Known =
    [
        new("alpha", "2.0.0", "The first letter.") { Owners = "greek", Downloads = 1500, License = "MIT" },
        new("beta", "1.0.0", "The second letter.") { Owners = "greek", Downloads = 12 },
    ];

    public override string Name => "fake";

    public override string Registry => "fake.example";

    public override string Project => "letters";

    public override string Directory => directory;

    public override IReadOnlyList<string> Groups => ["dependencies", "devDependencies"];

    public override Task<IReadOnlyList<RegistryPackage>> SearchAsync(string text, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<RegistryPackage>>([.. Known.Where(p => p.Id.Contains(text, StringComparison.Ordinal))]);

    public override Task<RegistryPackage?> FindAsync(string id, CancellationToken ct) => Task.FromResult(Known.FirstOrDefault(p => p.Id == id));

    public override Task<IReadOnlyList<string>> VersionsAsync(string id, bool prerelease, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(id == "alpha" ? [.. prerelease ? ["3.0.0-rc.1"] : Array.Empty<string>(), "2.0.0", "1.0.0"] : ["1.0.0"]);

    public override IReadOnlyList<DependencyPackage> Dependencies() => [.. Installed];

    public override WorkspaceTask Add(string id, string version, string? group) => new($"add {id}", WorkspaceTaskKind.Other, "fake", ["add", $"{id}@{version}", group ?? ""], directory);

    public override WorkspaceTask Remove(DependencyPackage package) => new($"remove {package.Id}", WorkspaceTaskKind.Other, "fake", ["remove", package.Id], directory);
}

public class PackagesTests
{
    [Fact]
    public void Versions_are_ordered_as_semantic_versions()
    {
        Assert.True(PackageVersions.Compare("1.10.0", "1.9.0") > 0);
        Assert.True(PackageVersions.Compare("2.0.0-rc.1", "2.0.0") < 0);
        Assert.True(PackageVersions.Compare("^1.2.3", "1.2.3") == 0);
        Assert.True(PackageVersions.Compare("1.2", "1.2.0") == 0);
        Assert.Equal("1.5K", PackageVersions.FormatCount(1500));
    }

    [AvaloniaFact]
    public async Task The_package_manager_browses_installs_updates_and_uninstalls()
    {
        var source = new FakePackages("/w/letters");
        var ran = new List<string>();
        var log = new List<string>();
        var changed = 0;
        var vm = new PackagesViewModel(source, (task, output, _) =>
        {
            ran.Add(string.Join(' ', task.Arguments));
            output("ok");
            // What the package manager does to the project's manifest.
            switch (task.Arguments[0])
            {
                case "add":
                    var (id, version) = (task.Arguments[1].Split('@')[0], task.Arguments[1].Split('@')[1]);
                    source.Installed.RemoveAll(d => d.Id == id);
                    source.Installed.Add(new(id, "^" + version, version, task.Arguments[2]));
                    break;
                case "remove":
                    source.Installed.RemoveAll(d => d.Id == task.Arguments[1]);
                    break;
            }
            return Task.FromResult(0);
        }, log.Add, () =>
        {
            changed++;
            return Task.CompletedTask;
        });
        Assert.Equal(("fake: letters", "fake Package Manager: letters", "packages:fake:/w/letters"), (vm.Title, vm.Header, vm.Key));
        Assert.True(vm.HasGroups);
        await vm.InitializeAsync();

        // Browse: nothing until a search; then the registry's, the project's own marked installed.
        Assert.Empty(vm.Packages);
        Assert.Equal("Search fake.example for a package.", vm.Status);
        vm.SearchText = "a";
        await vm.SearchCommand.ExecuteAsync(null);
        Assert.Equal(["alpha", "beta"], vm.Packages.Select(p => p.Id));
        var alpha = vm.Packages[0];
        Assert.Equal(("1.0.0", true, true, "by greek, 1.5K downloads"), (alpha.VersionText, alpha.IsInstalled, alpha.HasUpdate, alpha.Byline));
        await WaitUntil(() => vm.Versions.Count > 0, TimeSpan.FromSeconds(5));
        Assert.Equal(["2.0.0", "1.0.0"], vm.Versions);
        Assert.Equal(("2.0.0", "Update"), (vm.SelectedVersion, vm.InstallButtonText));
        vm.IncludePrerelease = true;
        await WaitUntil(() => vm.Versions.Count == 3, TimeSpan.FromSeconds(5));
        Assert.Equal("3.0.0-rc.1", vm.SelectedVersion);

        // Updates: alpha, which Update All brings to its latest, in its group.
        Assert.Equal(1, vm.UpdateCount);
        vm.IsUpdatesTab = true;
        await WaitUntil(() => vm.Packages.Count == 1, TimeSpan.FromSeconds(5));
        await vm.UpdateAllCommand.ExecuteAsync(null);
        Assert.Equal(["add alpha@2.0.0 dependencies"], ran);
        Assert.Equal(0, vm.UpdateCount);
        Assert.Equal("All packages are up to date.", vm.Status);
        Assert.Equal(1, changed);
        Assert.Contains("> fake add alpha@2.0.0 dependencies", log);

        // Install another into the group chosen.
        vm.IsBrowseTab = true;
        vm.SearchText = "beta";
        await vm.SearchCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.SelectedVersion == "1.0.0", TimeSpan.FromSeconds(5));
        Assert.Equal("Install", vm.InstallButtonText);
        vm.SelectedGroup = "devDependencies";
        await vm.InstallCommand.ExecuteAsync(null);
        Assert.Equal("add beta@1.0.0 devDependencies", ran[^1]);

        // Installed: both (the search filters them too), and one uninstalled.
        vm.SearchText = "";
        vm.IsInstalledTab = true;
        await WaitUntil(() => vm.Packages.Count == 2, TimeSpan.FromSeconds(5));
        Assert.Equal([("alpha", "dependencies"), ("beta", "devDependencies")], vm.Packages.Select(p => (p.Id, p.Group!)));
        vm.SelectedPackage = vm.Packages.Single(p => p.Id == "beta");
        await vm.UninstallCommand.ExecuteAsync(null);
        Assert.Equal("remove beta", ran[^1]);
        await WaitUntil(() => vm.Packages.Count == 1, TimeSpan.FromSeconds(5));
        Assert.Equal("alpha", Assert.Single(vm.Packages).Id);
    }

    [AvaloniaFact]
    public async Task A_package_json_folder_has_its_package_manager_in_solution_explorer()
    {
        var dir = Path.Combine(Path.GetTempPath(), "karvon-npm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "package.json"), """{ "name": "shop", "dependencies": { "left-pad": "^1.3.0" }, "devDependencies": { "vite": "^8.0.0" } }""");
        File.WriteAllText(Path.Combine(dir, "pnpm-lock.yaml"), "");
        Directory.CreateDirectory(Path.Combine(dir, "node_modules", "vite"));
        File.WriteAllText(Path.Combine(dir, "node_modules", "vite", "package.json"), """{ "name": "vite", "version": "8.1.2" }""");
        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        await vm.OpenSolutionAsync(dir);

        // The folder's menu, and its package.json's: the folder's package manager by its lock file.
        var root = vm.ExplorerRoots.Single();
        var item = window.ExplorerMenuItems(root).OfType<MenuItem>().Single(i => (string?)i.Header == "Manage pnpm Packages...");
        root.IsExpanded = true;
        Assert.Contains(window.ExplorerMenuItems(root.Children.Single(n => n.Name == "package.json")).OfType<MenuItem>(), i => (string?)i.Header == "Manage pnpm Packages...");

        // Its packages: what package.json asks for, what node_modules has, in their groups.
        var source = new NpmPackages(dir);
        Assert.Equal(("pnpm", "shop"), (source.Name, source.Project));
        Assert.Equal([new DependencyPackage("left-pad", "^1.3.0", null, "dependencies"), new DependencyPackage("vite", "^8.0.0", "8.1.2", "devDependencies")],
            source.Dependencies());
        Assert.Equal(["add", "is-odd@3.0.1", "--dev"], source.Add("is-odd", "3.0.1", "devDependencies").Arguments);
        Assert.Equal(["remove", "vite"], source.Remove(source.Dependencies()[1]).Arguments);

        // The menu opens it as a document, once.
        item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        await WaitUntil(() => vm.Documents.OfType<PackagesViewModel>().Any(), TimeSpan.FromSeconds(10));
        var manager = Assert.Single(vm.Documents.OfType<PackagesViewModel>());
        Assert.Equal("pnpm: shop", manager.Title);
        await WaitUntil(() => window.GetVisualDescendants().OfType<PackagesView>().Any(), TimeSpan.FromSeconds(5));

        // npm without a lock file of another's.
        File.Delete(Path.Combine(dir, "pnpm-lock.yaml"));
        Assert.Equal(["install", "is-odd@3.0.1", "--save-dev"], new NpmPackages(dir).Add("is-odd", "3.0.1", "devDependencies").Arguments);
        Assert.Equal(["uninstall", "vite"], new NpmPackages(dir).Remove(source.Dependencies()[1]).Arguments);
        Assert.True(await vm.CloseSolutionCoreAsync());
        window.Close();
        Directory.Delete(dir, recursive: true);
    }

    [AvaloniaFact]
    public async Task Npm_packages_are_searched_installed_and_uninstalled()
    {
        var path = string.Join(Path.PathSeparator, Karvon.Build.NodeLocator.DirectoryOffPath() ?? "", Environment.GetEnvironmentVariable("PATH") ?? "");
        Assert.SkipUnless(Environment.GetEnvironmentVariable("KARVON_TYPESCRIPT_LIVE") == "1" && TaskVariables.Find("npm", path, "/") is not null,
            "Set KARVON_TYPESCRIPT_LIVE=1, with Node.js installed, to search npmjs.com and install from it.");
        var dir = Path.Combine(Path.GetTempPath(), "karvon-npm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "package.json"), """{ "name": "shop", "private": true }""");
        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        await vm.OpenSolutionAsync(dir);
        await ((IWorkbench)vm).ManagePackagesAsync(new NpmPackages(dir));
        var manager = Assert.Single(vm.Documents.OfType<PackagesViewModel>());

        // npmjs.com's: a search, a package's versions and details.
        manager.SearchText = "is-odd";
        await manager.SearchCommand.ExecuteAsync(null);
        Assert.Equal("is-odd", manager.Packages[0].Id);
        await WaitUntil(() => manager.Versions.Contains("3.0.1") && manager.SelectedPackage?.License is not null, TimeSpan.FromSeconds(30));
        Assert.Equal("MIT", manager.SelectedPackage!.License);

        // Install as a devDependency, by npm: package.json and node_modules have it, and Installed shows it.
        manager.SelectedVersion = "3.0.1";
        manager.SelectedGroup = "devDependencies";
        await manager.InstallCommand.ExecuteAsync(null);
        Assert.True(manager.Status == "Done.", manager.Status);
        using (var package = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "package.json"))))
            Assert.Equal("^3.0.1", package.RootElement.GetProperty("devDependencies").GetProperty("is-odd").GetString());
        manager.IsInstalledTab = true;
        await WaitUntil(() => manager.Packages.Any(p => p.Id == "is-odd"), TimeSpan.FromSeconds(10));
        Assert.Equal(("3.0.1", "devDependencies"), (manager.Packages.Single(p => p.Id == "is-odd").InstalledVersion, manager.Packages.Single(p => p.Id == "is-odd").Group));

        // Uninstall.
        manager.SelectedPackage = manager.Packages.Single(p => p.Id == "is-odd");
        await manager.UninstallCommand.ExecuteAsync(null);
        Assert.DoesNotContain("is-odd", File.ReadAllText(Path.Combine(dir, "package.json")));
        Assert.Contains("> npm uninstall is-odd", ((IWorkbench)vm).Output("Package Manager").Document.Text);
        Assert.True(await vm.CloseSolutionCoreAsync());
        window.Close();
        Directory.Delete(dir, recursive: true);
    }
}
