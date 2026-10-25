using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Karvon.Modules;
using Karvon.ViewModels;
using Karvon.Views;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

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
        var docker = Tab(window, Karvon.Docker.DockerModule.ToolWindowId);
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
        vm.ShowToolWindow(Karvon.Claude.ClaudeModule.ToolWindowId);
        Assert.False(window.IsCollapsed(DockSide.Right));
        Assert.True(window.IsToolWindowShowing(Karvon.Claude.ClaudeModule.ToolWindowId));

        // Docked at the bottom, a tool window is a named tab again; back at a side, an icon (on the left: the bar at the left edge).
        window.MoveToolWindow(Karvon.Docker.DockerModule.ToolWindowId, DockSide.Bottom);
        Assert.Equal("VsToolTab", ThemeKey(window, docker));
        window.MoveToolWindow(Karvon.Docker.DockerModule.ToolWindowId, DockSide.Left);
        Assert.Equal("VsActivityTab", ThemeKey(window, docker));
        Assert.False(docker.Classes.Contains("right"));
        Assert.True(columns[0].Width.Value > 45);
        window.MoveToolWindow(Karvon.Database.DatabaseModule.ToolWindowId, DockSide.Left);
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(150));
        Snapshot(window, "activity-bar-left");
        window.ResetWindowLayout();
        window.Close();
    }

    /// <summary>Drags a control (in steps, as a hand does) to a point of the window, and lets it go there.</summary>
    static void Drag(Window window, Control control, Point to)
    {
        var from = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(from, MouseButton.Left);
        for (var step = 1; step <= 6; step++)
            window.MouseMove(from + (to - from) * (step / 6.0), RawInputModifiers.LeftMouseButton);
        window.MouseUp(to, MouseButton.Left);
    }

    static Point At(Window window, Control control, double x, double y) => control.TranslatePoint(new Point(x, y), window)!.Value;

    static string[] Order(TabControl group) => [.. group.Items.OfType<TabItem>().Select(t => (string)t.Tag!)];

    [AvaloniaFact]
    public async Task Tool_windows_are_dragged_into_another_place_on_their_bar_or_another_strip()
    {
        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        window.Show();
        window.ResetWindowLayout();
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(200));
        var rightTabs = window.FindControl<TabControl>("RightTabs")!;
        var bottomTabs = window.FindControl<TabControl>("BottomTabs")!;
        var solution = Tab(window, ToolWindows.SolutionExplorer);
        var docker = Tab(window, Karvon.Docker.DockerModule.ToolWindowId);
        var claude = Tab(window, Karvon.Claude.ClaudeModule.ToolWindowId);
        Assert.Equal(ToolWindows.SolutionExplorer, Order(rightTabs)[0]);
        Assert.True(solution.IsSelected);

        // Docker Explorer's icon, dragged above Solution Explorer's: first on the bar; the window in front stays so.
        Drag(window, docker, At(window, solution, 22, 6));
        Assert.Equal([Karvon.Docker.DockerModule.ToolWindowId, ToolWindows.SolutionExplorer], Order(rightTabs)[..2]);
        Assert.True(solution.IsSelected);
        Assert.False(window.IsCollapsed(DockSide.Right));
        Assert.Equal(Order(rightTabs), vm.Settings.Layout.ToolOrder["Right"]);
        Assert.Equal(DockSide.Right, window.SideOf(Karvon.Docker.DockerModule.ToolWindowId));

        // While it is dragged, a line shows where it would go.
        var marker = window.FindControl<Border>("ToolDropMarker")!;
        var start = At(window, claude, 22, 22);
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(start + new Point(0, -20), RawInputModifiers.LeftMouseButton);
        window.MouseMove(At(window, docker, 22, 4), RawInputModifiers.LeftMouseButton);
        Assert.True(marker.IsVisible);
        Assert.Equal(36, marker.Width, 1);
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(100));
        Snapshot(window, "activity-bar-dragging");
        window.MouseMove(At(window, claude, 22, 22), RawInputModifiers.LeftMouseButton);
        window.MouseUp(At(window, claude, 22, 22), MouseButton.Left);
        Assert.False(marker.IsVisible);
        Assert.Equal(Karvon.Claude.ClaudeModule.ToolWindowId, Order(rightTabs)[^1]);

        // Below the last icon is the end of the bar.
        Drag(window, solution, At(window, claude, 22, claude.Bounds.Height + 60));
        Assert.Equal(ToolWindows.SolutionExplorer, Order(rightTabs)[^1]);

        // Dropped between two of the bottom tabs, a tool window goes there, a named tab in front.
        var output = Tab(window, ToolWindows.Output);
        Drag(window, docker, At(window, output, 3, output.Bounds.Height / 2));
        Assert.Equal(DockSide.Bottom, window.SideOf(Karvon.Docker.DockerModule.ToolWindowId));
        Assert.Equal(Order(bottomTabs).ToList().IndexOf(ToolWindows.Output) - 1, Order(bottomTabs).ToList().IndexOf(Karvon.Docker.DockerModule.ToolWindowId));
        Assert.True(docker.IsSelected);
        Assert.Equal("VsToolTab", ThemeKey(window, docker));
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(100));
        Snapshot(window, "activity-bar-reordered");

        // Reset Window Layout: every tab back in its place, in the first order.
        window.ResetWindowLayout();
        Assert.Equal([ToolWindows.SolutionExplorer, ToolWindows.TestExplorer, ToolWindows.GitChanges, Karvon.Database.DatabaseModule.ToolWindowId, Karvon.Docker.DockerModule.ToolWindowId, Karvon.Servers.ServersModule.ToolWindowId, Karvon.Jira.JiraModule.ToolWindowId, Karvon.Pipelines.PipelinesModule.ToolWindowId, Karvon.PullRequests.PullRequestsModule.ToolWindowId, Karvon.Mcp.McpModule.ToolWindowId, Karvon.Skills.SkillsModule.ToolWindowId, Karvon.Claude.ClaudeModule.ToolWindowId],
            Order(rightTabs));
        Assert.Empty(vm.Settings.Layout.ToolOrder);
        window.Close();
    }

    static string ThemeKey(Window window, TabItem item) =>
        ReferenceEquals(item.Theme, window.FindResource("VsActivityTab")) ? "VsActivityTab" : ReferenceEquals(item.Theme, window.FindResource("VsToolTab")) ? "VsToolTab" : "?";
}
