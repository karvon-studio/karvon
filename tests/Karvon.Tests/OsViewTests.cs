using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
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

        // Off, the IDE as it was: its activity bars, no dock. A module's icon on them is its 2D picture; Karvon's own
        // tool windows keep theirs drawn.
        Assert.False(dock.IsVisible);
        Assert.DoesNotContain("os", right.Classes);
        Assert.All(window.Apps, app => Assert.NotNull(ToolWindowTabs.Icon2DOf(app.Id)));
        Assert.Null(ToolWindowTabs.Icon2DOf("NoSuchToolWindow"));
        Snapshot(window, "activity-bar-2d");

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
        // Each app is drawn by its picture (Assets/Apps); one without (a module's new tool window), its icon on a tile.
        Assert.True(IconOf(Button(window, "Dock_DatabaseExplorer")).HasPicture);
        Assert.True(IconOf(Button(window, "Dock_SolutionExplorer")).HasPicture);
        Assert.All(window.Apps, app => Assert.NotNull(ToolWindowTabs.AppIconOf(app.Id)));
        Assert.False(new AppIcon { AppId = "NoSuchToolWindow" }.HasPicture);
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
        Assert.IsAssignableFrom<IBlurEffect>(window.FindControl<DockPanel>("Shell")!.Effect);
        Assert.Equal(window.Apps.Count, window.ShownApps.Count);
        Assert.Contains("Machine", window.ShownApps);
        // In the middle, as wide as its columns: a few on a laptop's screen, larger tiles on a large one, never a row across it.
        Assert.Equal((120, 65, 8), MainWindow.GridMetrics(new Avalonia.Size(1400, 900)));
        Assert.Equal((171, 92, 8), MainWindow.GridMetrics(new Avalonia.Size(2560, 1380)));
        Assert.Equal((120, 65, 5), MainWindow.GridMetrics(new Avalonia.Size(1000, 700)));
        var grid = window.AppsGrid!;
        Assert.Equal(8 * 120, grid.Width);
        Assert.Equal(Avalonia.Layout.HorizontalAlignment.Center, grid.HorizontalAlignment);
        // Its tiles come up one after another; once they have, each is there whole.
        for (var tick = 0; tick < 60; tick++)
        {
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await WaitUntil(() => false, TimeSpan.FromMilliseconds(20));
        }
        Assert.All(grid.Children, tile => Assert.Equal(1, tile.Opacity));
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

    [AvaloniaFact]
    public async Task The_dock_s_and_the_apps_grid_s_apps_go_where_they_are_dragged_and_stay_there()
    {
        var vm = NewViewModel(settings =>
        {
            settings.OsView = true;
            settings.DockPosition = "Left";
            settings.DockPinned = null;
            settings.AppsOrder = null;
        });
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        window.Show();
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(200));
        Assert.Equal(["SolutionExplorer", "Claude", "Git", "DatabaseExplorer", "Terminal", "ErrorList"], window.DockApps);
        Assert.False(window.IsAppOpen("Terminal"));

        // Terminal dragged up the dock to Claude's place: there, Claude and those after it a place down. A drag is not
        // a click: Terminal stays as it was.
        await Drag(window, Button(window, "Dock_Terminal"), Button(window, "Dock_Claude"));
        Assert.Equal(["SolutionExplorer", "Terminal", "Claude", "Git", "DatabaseExplorer", "ErrorList"], window.DockApps);
        Assert.Equal(["SolutionExplorer", "Terminal", "Claude", "Git", "DatabaseExplorer"], vm.Settings.DockPinned);
        Assert.False(window.IsAppOpen("Terminal"));
        Assert.All(window.GetVisualDescendants().OfType<Button>().Where(b => b.Name?.StartsWith("Dock_") == true), b => Assert.True(b.RenderTransform is null || b.RenderTransform.Value.IsIdentity));
        // The Error List, open but not pinned, dragged to the top: pinned there, as Ubuntu's.
        await Drag(window, Button(window, "Dock_ErrorList"), Button(window, "Dock_SolutionExplorer"));
        Assert.Equal(["ErrorList", "SolutionExplorer", "Terminal", "Claude", "Git", "DatabaseExplorer"], vm.Settings.DockPinned);
        // Let go where it was: nothing moves.
        await Drag(window, Button(window, "Dock_Git"), Button(window, "Dock_Git"), by: 10);
        Assert.Equal(["ErrorList", "SolutionExplorer", "Terminal", "Claude", "Git", "DatabaseExplorer"], window.DockApps);

        // The apps grid's last app (by name, as they first are) dragged to the first place: there, the grid open still.
        window.ShowApps();
        for (var tick = 0; tick < 60; tick++)
        {
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await WaitUntil(() => false, TimeSpan.FromMilliseconds(20));
        }
        var byName = window.ShownApps.ToList();
        Assert.Equal(window.Apps.Count, byName.Count);
        var (first, last) = (byName[0], byName[^1]);
        await Drag(window, Button(window, "App_" + last), Button(window, "App_" + first));
        Assert.True(window.IsAppsShown);
        Assert.Equal([last, .. byName[..^1]], window.ShownApps);
        Assert.Equal(window.ShownApps, vm.Settings.AppsOrder);
        // Esc goes back to the IDE after a drag too (the tile dragged, focused, is laid again).
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.False(window.IsAppsShown);
        window.ShowApps();
        for (var tick = 0; tick < 60; tick++)
        {
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await WaitUntil(() => false, TimeSpan.FromMilliseconds(20));
        }
        // A row down (the first app to the second row's first place).
        var columns = (int)(window.AppsGrid!.Width / window.AppsGrid.ItemWidth);
        await Drag(window, Button(window, "App_" + last), Button(window, "App_" + window.ShownApps[columns]));
        Assert.Equal(last, window.ShownApps[columns]);
        Snapshot(window, "os-view-apps-reordered");
        // Typing goes on to the search once a tile is dragged.
        Assert.Equal("AppsSearch", (window.FocusManager?.GetFocusedElement() as Control)?.Name);
        // Not while searching: what is shown is not all of them.
        window.SearchApps("e");
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(100));
        var found = window.ShownApps.ToList();
        await Drag(window, Button(window, "App_" + found[^1]), Button(window, "App_" + found[0]));
        Assert.Equal(found, window.ShownApps);
        // Esc goes back to the IDE with the focus on a tile pressed, not in the search.
        Assert.StartsWith("App_", (window.FocusManager?.GetFocusedElement() as Control)?.Name);
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.False(window.IsAppsShown);
        var order = vm.Settings.AppsOrder!.ToList();
        window.Close();

        // Kept: Karvon opened again (its settings read from where they were written) docks and shows them so.
        var file = Path.Combine(Path.GetTempPath(), "karvon-os-view-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(vm.Settings));
        var saved = Karvon.Services.Settings.Load(file);
        File.Delete(file);
        var again = NewViewModel(settings =>
        {
            settings.OsView = true;
            settings.DockPinned = saved.DockPinned;
            settings.AppsOrder = saved.AppsOrder;
        });
        var reopened = new MainWindow { DataContext = again, Width = 1400, Height = 900 };
        reopened.Show();
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(200));
        Assert.Equal(["ErrorList", "SolutionExplorer", "Terminal", "Claude", "Git", "DatabaseExplorer"], reopened.DockApps.Take(6));
        Assert.Equal(order, reopened.GridApps.Select(a => a.Id));
        Assert.Contains(nameof(Karvon.Services.Settings.AppsOrder), Karvon.Services.SettingsSync.Of(again.Settings).Select(p => p.Key));
        reopened.Close();
    }

    /// <summary>A press on one, the pointer to the other (a little at first, then there), let go.</summary>
    static async Task Drag(Window window, Control from, Control to, double by = 12)
    {
        var start = CenterOf(from, window);
        var end = CenterOf(to, window);
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(start + new Avalonia.Point(0, by), RawInputModifiers.LeftMouseButton);
        window.MouseMove(end, RawInputModifiers.LeftMouseButton);
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(50));
        window.MouseUp(end, MouseButton.Left);
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(100));
    }

    static Avalonia.Point CenterOf(Avalonia.Visual visual, Avalonia.Visual relativeTo) =>
        visual.TranslatePoint(new Avalonia.Point(visual.Bounds.Width / 2, visual.Bounds.Height / 2), relativeTo)!.Value;

    static Button Button(Window window, string name) =>
        window.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == name) ?? throw new InvalidOperationException($"No {name}.");

    static AppIcon IconOf(Button button) => ((Panel)button.Content!).Children.OfType<AppIcon>().Single();

    static void Click(Button button) => button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
}
