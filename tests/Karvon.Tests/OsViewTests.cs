using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Karvon.Modules;
using Karvon.Views;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>The OS view: a dock of the apps pinned and open, and the apps grid over the IDE blurred.</summary>
public class OsViewTests
{
    [AvaloniaFact]
    public async Task The_os_view_docks_the_apps_pinned_and_open_and_shows_them_all_over_the_ide_blurred()
    {
        var vm = NewViewModel(settings =>
        {
            settings.OsView = false;
            settings.DockPosition = "Left";
            settings.DockPinned = null;
        });
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        window.Show();
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(200));
        var dock = window.FindControl<Border>("OsDock")!;
        var right = window.FindControl<TabControl>("RightTabs")!;

        // Off, the IDE as it was: its activity bars, no dock.
        Assert.False(dock.IsVisible);
        Assert.DoesNotContain("os", right.Classes);

        // On: the dock stands for the activity bars; its apps those pinned (as they first are), then those open.
        vm.OsView = true;
        Assert.True(vm.Settings.OsView);
        Assert.True(dock.IsVisible);
        Assert.Contains("os", right.Classes);
        Assert.Equal(Dock.Left, DockPanel.GetDock(dock));
        Assert.Equal(["SolutionExplorer", "Claude", "Git", "DatabaseExplorer", "Terminal", "ErrorList"], window.DockApps);
        Assert.True(window.IsAppOpen("SolutionExplorer"));
        Assert.Contains("open", Button(window, "Dock_SolutionExplorer").Classes);
        Assert.DoesNotContain("open", Button(window, "Dock_DatabaseExplorer").Classes);
        Assert.Equal("Database Explorer", ToolTip.GetTip(Button(window, "Dock_DatabaseExplorer")));
        // An app with its picture (Assets/Apps) is drawn by it; one without, its icon on a tile.
        Assert.True(IconOf(Button(window, "Dock_DatabaseExplorer")).HasPicture);
        Assert.False(IconOf(Button(window, "Dock_SolutionExplorer")).HasPicture);
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(200));
        Snapshot(window, "os-view-dock");

        // An app clicked comes up (Database Explorer, in Solution Explorer's place); clicked again, goes down.
        Click(Button(window, "Dock_DatabaseExplorer"));
        Assert.True(window.IsAppOpen("DatabaseExplorer"));
        Assert.False(window.IsAppOpen("SolutionExplorer"));
        Assert.Contains("open", Button(window, "Dock_DatabaseExplorer").Classes);
        Click(Button(window, "Dock_DatabaseExplorer"));
        Assert.False(window.IsAppOpen("DatabaseExplorer"));
        Assert.True(window.IsCollapsed(DockSide.Right));
        // Folded, a side group leaves no activity bar behind in the OS view.
        Assert.Equal(0, window.FindControl<Grid>("MainGrid")!.ColumnDefinitions[4].Width.Value);

        // An app not pinned is on the dock while it is open: Docker Explorer, from the apps grid.
        window.ShowApps();
        Assert.True(window.IsAppsShown);
        Assert.IsType<BlurEffect>(window.FindControl<DockPanel>("Shell")!.Effect);
        Assert.Equal(window.Apps.Count, window.ShownApps.Count);
        Assert.Contains("Machine", window.ShownApps);
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(400));
        Snapshot(window, "os-view-apps");
        window.SearchApps("dock");
        Assert.Equal(["DockerExplorer"], window.ShownApps);
        Click(Button(window, "App_DockerExplorer"));
        Assert.False(window.IsAppsShown);
        Assert.Null(window.FindControl<DockPanel>("Shell")!.Effect);
        Assert.True(window.IsAppOpen("DockerExplorer"));
        Assert.Contains("DockerExplorer", window.DockApps);

        // Esc in the grid goes back to the IDE, as it was.
        window.ShowApps();
        window.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "AppsSearch").RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
        Assert.False(window.IsAppsShown);

        // Pinned, an app stays when it is closed; unpinned, it goes; both kept.
        window.SetPinned("DockerExplorer", true);
        Assert.Equal(["SolutionExplorer", "Claude", "Git", "DatabaseExplorer", "Terminal", "DockerExplorer"], vm.Settings.DockPinned);
        window.ToggleApp("DockerExplorer");
        Assert.Contains("DockerExplorer", window.DockApps);
        window.SetPinned("Terminal", false);
        Assert.DoesNotContain("Terminal", window.DockApps);

        // At the bottom, its apps in a row; at the right.
        window.SetDockSide(DockSide.Bottom);
        Assert.Equal(("Bottom", Dock.Bottom), (vm.Settings.DockPosition, DockPanel.GetDock(dock)));
        Assert.Equal(Avalonia.Layout.Orientation.Horizontal, ((StackPanel)dock.Child!).Orientation);
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(200));
        Snapshot(window, "os-view-dock-bottom");
        window.SetDockSide(DockSide.Right);
        Assert.Equal(Dock.Right, DockPanel.GetDock(dock));

        // Off again: the activity bars are back.
        vm.OsView = false;
        Assert.False(dock.IsVisible);
        Assert.DoesNotContain("os", right.Classes);
        window.Close();
    }

    static Button Button(Window window, string name) =>
        window.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == name) ?? throw new InvalidOperationException($"No {name}.");

    static AppIcon IconOf(Button button) => ((Panel)button.Content!).Children.OfType<AppIcon>().Single();

    static void Click(Button button) => button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
}
