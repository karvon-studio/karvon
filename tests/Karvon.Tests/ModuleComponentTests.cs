using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Karvon.Modules;
using Karvon.Services;
using Karvon.ViewModels;
using Karvon.Views;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>A module that downloads one component (into a folder of the test's).</summary>
sealed class FakeComponentModule(string folder) : KarvonModule
{
    public override string Id => "fake-component";

    public override string Name => "Fake component";

    public override string Description => "A module with one component for the tests.";

    public int Installs { get; private set; }

    public override IReadOnlyList<ModuleComponent> Components =>
    [
        new("fake", "Fake server", "1.0", folder, () => File.Exists(Path.Combine(folder, "server")), async (log, ct) =>
        {
            Installs++;
            log("Downloading the fake server...");
            Directory.CreateDirectory(folder);
            await File.WriteAllBytesAsync(Path.Combine(folder, "server"), new byte[3 << 20], ct);
        }),
    ];
}

/// <summary>Tools > Get Tools and Features: what the modules download, installed at once and removed when Karvon starts again.</summary>
public class ModuleComponentTests
{
    [AvaloniaFact]
    public void Every_download_is_a_component_of_its_module()
    {
        var vm = NewViewModel();
        Assert.Equal([(".NET", "roslyn"), (".NET", "netcoredbg"), ("TypeScript and JavaScript", "typescript"), ("TypeScript and JavaScript", "js-debug"), ("TypeScript and JavaScript", "web-languages"), ("TypeScript and JavaScript", "eslint"), ("Python", "pyright"), ("Python", "debugpy"), ("Go", "gopls"), ("Go", "dlv"), ("Rust", "rust-analyzer"), ("Rust", "codelldb"), ("C and C++", "clangd"), ("C and C++", "codelldb"), ("Java", "jdtls"), ("Java", "java-debug"), ("Java", "junit"), ("Kotlin", "kotlin-lsp")],
            vm.Components.Select(c => (c.Owner, c.Component.Id)));
        Assert.All(vm.Components, c => Assert.True(FilePaths.IsUnder(c.Component.InstallRoot, ModuleComponent.DataRoot), c.Component.InstallRoot));
    }

    [Fact]
    public async Task A_component_installs_once_at_a_time()
    {
        // Two starts of the same server at once: the second waits for the first, then finds it there.
        var folder = Path.Combine(Path.GetTempPath(), "karvon-lock-" + Guid.NewGuid().ToString("N"), "server");
        var first = await InstallLock.AcquireAsync(folder, TestContext.Current.CancellationToken);
        var second = InstallLock.AcquireAsync(folder, TestContext.Current.CancellationToken);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.False(second.IsCompleted);
        Assert.True(File.Exists(folder + ".lock"));
        await first.DisposeAsync();
        await (await second.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).DisposeAsync();
        Assert.False(File.Exists(folder + ".lock"));
        Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true);
    }

    [AvaloniaFact]
    public async Task A_module_turned_on_downloads_its_components_then()
    {
        // As Visual Studio installs a workload's components: Karvon's module turned on gets them now (once), one turned
        // off gets none.
        var root = Path.Combine(Path.GetTempPath(), "karvon-components-" + Guid.NewGuid().ToString("N"));
        var module = new FakeComponentModule(Path.Combine(root, "fake", "1.0"));
        var vm = NewViewModel(modules: [.. Karvon.BuiltInModules.Create(), module]);
        vm.Settings.DisabledModules = ["fake-component"];
        Assert.True(await vm.ChangeModulesAsync(["fake-component", "kotlin"]));
        Assert.Equal(0, module.Installs);
        Assert.True(await vm.ChangeModulesAsync(["kotlin"]));
        Assert.Equal(1, module.Installs);
        Assert.Equal(["kotlin"], vm.Settings.DisabledModules);
        Assert.Contains("Downloading the fake server...", ((IWorkbench)vm).Output("Modules").Document.Text);
        Assert.True(await vm.ChangeModulesAsync(["kotlin"]));
        Assert.Equal(1, module.Installs);
        vm.Settings.DisabledModules = [];
        Directory.Delete(root, recursive: true);
    }

    [AvaloniaFact]
    public async Task A_component_installs_now_and_goes_when_Karvon_starts_again()
    {
        var root = Path.Combine(Path.GetTempPath(), "karvon-components-" + Guid.NewGuid().ToString("N"));
        var module = new FakeComponentModule(Path.Combine(root, "fake", "1.0"));
        var vm = NewViewModel(modules: [.. Karvon.BuiltInModules.Create(), module]);
        vm.Settings.RemovedComponents.Clear();
        var component = module.Components[0];
        // Karvon Studio Installer's Individual components: the module's, as they are now.
        var installer = new InstallerViewModel(vm, vm.Updates);
        installer.ShowModifyCommand.Execute(null);
        ComponentItem Item() => installer.ComponentGroups.Single(g => g.Title == "Fake component").Items.Single();
        Assert.Equal(("Fake server 1.0", "downloads the first time it is needed", false), (Item().Name, Item().State, Item().IsSelected));

        // Checked, then Modify: installed at once, its log in Output (Modules).
        Item().IsSelected = true;
        Assert.Equal(["Individual components to install: Fake server 1.0"], installer.Changes.Select(c => $"{c.Text}: {string.Join(", ", c.Items)}"));
        await installer.ApplyCommand.ExecuteAsync(null);
        Assert.Equal(("installed, 3 MB", true, "Done."), (Item().State, Item().IsSelected, installer.ApplyText));
        Assert.Equal(1, module.Installs);
        Assert.Contains("Downloading the fake server...", ((IWorkbench)vm).Output("Modules").Document.Text);
        Assert.False(installer.HasChanges);

        // Unchecked: removed when Karvon Studio starts again (it may be running); checked again, it is kept.
        Item().IsSelected = false;
        Assert.Equal("Space freed: 3 MB", installer.SpaceText);
        await installer.ApplyCommand.ExecuteAsync(null);
        Assert.Equal(("removed when Karvon Studio starts again", false), (Item().State, Item().IsSelected));
        Assert.Equal([component.InstallRoot], vm.Settings.RemovedComponents);
        Assert.True(installer.RestartNeeded);
        Item().IsSelected = true;
        Assert.Equal(["Individual components to keep: Fake server 1.0"], installer.Changes.Select(c => $"{c.Text}: {string.Join(", ", c.Items)}"));
        await installer.ApplyCommand.ExecuteAsync(null);
        Assert.Empty(vm.Settings.RemovedComponents);
        vm.RemoveComponent(component);

        // Karvon starts again: it goes, and a folder out of Karvon's own never does.
        var elsewhere = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "karvon-not-a-component-" + Guid.NewGuid().ToString("N"))).FullName;
        vm.Settings.RemovedComponents.Add(elsewhere);
        ModuleComponent.RemovePending(vm.Settings, root);
        Assert.False(Directory.Exists(component.InstallRoot));
        Assert.True(Directory.Exists(elsewhere));
        Assert.Equal([elsewhere], vm.Settings.RemovedComponents);
        vm.Settings.RemovedComponents.Clear();
        vm.Settings.Save();
        Directory.Delete(elsewhere);
        Directory.Delete(root, recursive: true);
    }
}
