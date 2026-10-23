using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Karvon.Modules;
using Karvon.Services;
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

/// <summary>Tools > Modules: what the modules download, installed at once and removed when Karvon starts again.</summary>
public class ModuleComponentTests
{
    [AvaloniaFact]
    public void Every_download_is_a_component_of_its_module()
    {
        var vm = NewViewModel();
        Assert.Equal([(".NET", "roslyn"), (".NET", "netcoredbg"), ("TypeScript and JavaScript", "typescript"), ("TypeScript and JavaScript", "js-debug"), ("Python", "pyright"), ("Python", "debugpy")],
            vm.Components.Select(c => (c.Owner, c.Component.Id)));
        Assert.All(vm.Components, c => Assert.True(FilePaths.IsUnder(c.Component.InstallRoot, ModuleComponent.DataRoot), c.Component.InstallRoot));
    }

    [AvaloniaFact]
    public async Task A_component_installs_now_and_goes_when_Karvon_starts_again()
    {
        var root = Path.Combine(Path.GetTempPath(), "karvon-components-" + Guid.NewGuid().ToString("N"));
        var module = new FakeComponentModule(Path.Combine(root, "fake", "1.0"));
        var vm = NewViewModel(modules: [.. Karvon.BuiltInModules.Create(), module]);
        vm.Settings.RemovedComponents.Clear();
        var component = module.Components[0];
        var row = MainWindow.ComponentRow(vm, component, null);
        var button = row.GetLogicalDescendants().OfType<Button>().Single();
        string State() => string.Join(" | ", row.GetLogicalDescendants().OfType<TextBlock>().Where(t => t.FindLogicalAncestorOfType<Button>() is null).Select(t => t.Text));
        var window = new Window { Content = row };
        window.Show();
        Assert.Equal("Fake server 1.0 | downloads the first time it is needed", State());
        Assert.Equal("Install", button.Content);

        // Install: at once, its log in Output (Modules).
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await WaitUntil(() => (string?)button.Content == "Remove", TimeSpan.FromSeconds(10));
        Assert.Equal("Fake server 1.0 | installed, 3 MB", State());
        Assert.Equal(1, module.Installs);
        Assert.Contains("Downloading the fake server...", ((IWorkbench)vm).Output("Modules").Document.Text);

        // Remove: when Karvon Studio starts again (it may be running); Keep takes it back.
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("Fake server 1.0 | removed when Karvon Studio starts again", State());
        Assert.Equal([component.InstallRoot], vm.Settings.RemovedComponents);
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
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
        window.Close();
        Directory.Delete(elsewhere);
        Directory.Delete(root, recursive: true);
    }
}
