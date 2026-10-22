using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using VSL.ViewModels;
using VSL.Views;
using static VSL.Tests.UiTests;

namespace VSL.Tests;

/// <summary>The side tool groups as VS Code's activity bar: an icon per tool window, a badge, folding to the bar.</summary>
public class ActivityBarTests
{
    static TabItem Tab(Window window, string id) => window.GetVisualDescendants().OfType<TabItem>().Single(t => (string?)t.Tag == id);

    static void Click(Window window, Control control)
    {
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
    }

    [AvaloniaFact]
    public async Task Side_tool_windows_are_icons_that_fold_their_group_like_vs_code()
    {
        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        window.Show();
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(200));
        var columns = window.FindControl<Grid>("MainGrid")!.ColumnDefinitions;
        var rightTabs = window.FindControl<TabControl>("RightTabs")!;

        // One column of icons at the right edge, however many tool windows: the names are tooltips.
        var solution = Tab(window, ToolWindows.SolutionExplorer);
        var git = Tab(window, ToolWindows.GitChanges);
        var docker = Tab(window, ToolWindows.DockerExplorer);
        Assert.Equal(Dock.Right, rightTabs.TabStripPlacement);
        Assert.All(rightTabs.Items.OfType<TabItem>(), t => Assert.Equal(44, t.Bounds.Width));
        Assert.Equal(solution.Bounds.X, docker.Bounds.X);
        Assert.True(docker.Bounds.Y > solution.Bounds.Y);
        Assert.Equal("ToolGit", ToolWindowTabs.IconOf(ToolWindows.GitChanges));
        Assert.True(solution.Classes.Contains("right"));

        // Git Changes counts the changes on its icon.
        vm.GitChangeCount = 3;
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(100));
        var badge = git.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_Badge");
        Assert.True(badge.IsVisible);
        Assert.Equal("3", badge.GetVisualDescendants().OfType<TextBlock>().Single().Text);
        Snapshot(window, "activity-bar");

        // The icon in front, clicked, folds the group to its bar; another icon opens it again, at its width.
        Assert.True(solution.IsSelected);
        var width = columns[4].Width.Value;
        Click(window, solution);
        Assert.True(window.IsCollapsed(DockSide.Right));
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(100));
        Assert.Equal(45, columns[4].Width.Value);
        Assert.False(window.IsToolWindowShowing(ToolWindows.SolutionExplorer));
        Snapshot(window, "activity-bar-folded");
        Click(window, git);
        Assert.False(window.IsCollapsed(DockSide.Right));
        Assert.True(git.IsSelected);
        Assert.Equal(width, columns[4].Width.Value);

        // Folded, a tool window that is asked for opens its group (View > Git Changes, Claude asking for attention).
        Click(window, git);
        Assert.True(window.IsCollapsed(DockSide.Right));
        vm.ShowToolWindow(ToolWindows.Claude);
        Assert.False(window.IsCollapsed(DockSide.Right));
        Assert.True(window.IsToolWindowShowing(ToolWindows.Claude));

        // Docked at the bottom, a tool window is a named tab again; back at a side, an icon (on the left: the bar at the left edge).
        window.MoveToolWindow(ToolWindows.DockerExplorer, DockSide.Bottom);
        Assert.Equal("VsToolTab", ThemeKey(window, docker));
        window.MoveToolWindow(ToolWindows.DockerExplorer, DockSide.Left);
        Assert.Equal("VsActivityTab", ThemeKey(window, docker));
        Assert.False(docker.Classes.Contains("right"));
        Assert.True(columns[0].Width.Value > 45);
        window.MoveToolWindow(ToolWindows.DatabaseExplorer, DockSide.Left);
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(150));
        Snapshot(window, "activity-bar-left");
        window.ResetWindowLayout();
        window.Close();
    }

    static string ThemeKey(Window window, TabItem item) =>
        ReferenceEquals(item.Theme, window.FindResource("VsActivityTab")) ? "VsActivityTab" : ReferenceEquals(item.Theme, window.FindResource("VsToolTab")) ? "VsToolTab" : "?";
}
