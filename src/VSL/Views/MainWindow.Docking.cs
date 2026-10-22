using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using VSL.ViewModels;

namespace VSL.Views;

public enum DockSide
{
    Left,
    Right,
    Bottom,
}

/// <summary>
/// Tool window docking: drag a tool window's tab onto the Dock Left / Right / Bottom guides (or use its context
/// menu) to move it between the three tool groups. Empty groups collapse; the layout, the groups' sizes and the
/// tabs in front are remembered. The side groups show their tool windows as VS Code's activity bar, an icon each;
/// clicking the icon in front folds the group to its bar, and any icon opens it again.
/// </summary>
public partial class MainWindow
{
    const double DragThreshold = 8;

    /// <summary>A side group folded to its activity bar: the icons (44) and the group's border.</summary>
    const double ActivityBarWidth = 45;

    TabItem? _dragItem;
    Point _dragStart;
    bool _dragging;
    DockSide? _dropTarget;
    bool _pressedInFront;
    bool _leftCollapsed, _rightCollapsed;
    double _leftWidth = DefaultLeftWidth, _rightWidth = DefaultRightWidth;

    readonly List<FloatingToolWindow> _floatingTools = [];

    /// <summary>Where each floating tool window was docked before it floated (it goes back there when its window closes).</summary>
    readonly Dictionary<string, DockSide> _floatedFrom = [];

    /// <summary>Saved floating tool windows, floated once the main window is shown (an owned window needs its owner).</summary>
    readonly List<(TabItem Item, double[]? Bounds)> _pendingFloats = [];

    IEnumerable<TabControl> ToolGroups => [LeftTabs, RightTabs, BottomTabs, .. _floatingTools.Select(w => w.Tabs)];

    IEnumerable<TabItem> ToolTabs => ToolGroups.SelectMany(g => g.Items.OfType<TabItem>());

    TabControl GroupOf(DockSide side) => side switch
    {
        DockSide.Left => LeftTabs,
        DockSide.Right => RightTabs,
        _ => BottomTabs,
    };

    /// <summary>The side the tool window is docked on; null while it floats.</summary>
    public DockSide? SideOf(string toolWindow)
    {
        foreach (var side in Enum.GetValues<DockSide>())
            if (GroupOf(side).Items.OfType<TabItem>().Any(t => (string?)t.Tag == toolWindow))
                return side;
        return null;
    }

    public bool IsFloating(string toolWindow) => _floatingTools.Any(w => w.Tabs.Items.OfType<TabItem>().Any(t => (string?)t.Tag == toolWindow));

    /// <summary>The floating window a tool window is in.</summary>
    public FloatingToolWindow? FloatingWindowOf(string toolWindow) =>
        _floatingTools.FirstOrDefault(w => w.Tabs.Items.OfType<TabItem>().Any(t => (string?)t.Tag == toolWindow));

    /// <summary>True when the tool window is the front tab of its group.</summary>
    public bool IsToolWindowShowing(string toolWindow) =>
        ToolGroups.Any(g => g.IsVisible && !g.Classes.Contains("collapsed") && g.SelectedItem is TabItem { Tag: string id } && id == toolWindow);

    /// <summary>The side group a tool window's tab is in (left or right), or null.</summary>
    DockSide? SideGroupOf(TabItem item) => LeftTabs.Items.Contains(item) ? DockSide.Left : RightTabs.Items.Contains(item) ? DockSide.Right : null;

    public bool IsCollapsed(DockSide side) => side switch
    {
        DockSide.Left => _leftCollapsed,
        DockSide.Right => _rightCollapsed,
        _ => false,
    };

    /// <summary>A tool window's tab as its group shows them: an activity bar icon at the sides, a named tab at the bottom or in its own window.</summary>
    void ApplyToolTabTheme(TabItem item)
    {
        var side = SideGroupOf(item);
        item.Theme = this.FindResource(side is null ? "VsToolTab" : "VsActivityTab") as Avalonia.Styling.ControlTheme;
        item.Classes.Set("right", side == DockSide.Right);
    }

    /// <summary>Folds a side group to its activity bar (VS Code's side bar hidden), or opens it again at the width it had.</summary>
    public void SetCollapsed(DockSide side, bool collapsed, bool remember = true)
    {
        if (side == DockSide.Bottom || IsCollapsed(side) == collapsed)
            return;
        var column = MainGrid.ColumnDefinitions[side == DockSide.Left ? 0 : 4];
        if (collapsed && column.Width.IsAbsolute && column.Width.Value > ActivityBarWidth)
        {
            if (side == DockSide.Left)
                _leftWidth = column.Width.Value;
            else
                _rightWidth = column.Width.Value;
        }
        else if (!collapsed)
            column.Width = new GridLength(Math.Max(side == DockSide.Left ? _leftWidth : _rightWidth, 160));
        if (side == DockSide.Left)
            _leftCollapsed = collapsed;
        else
            _rightCollapsed = collapsed;
        UpdateDockLayout();
        if (remember && Vm is { } vm)
        {
            vm.Settings.Layout.LeftCollapsed = _leftCollapsed;
            vm.Settings.Layout.RightCollapsed = _rightCollapsed;
            vm.Settings.Save();
        }
    }

    void InitializeDocking()
    {
        foreach (var item in ToolTabs.ToList())
        {
            item.AddHandler(PointerPressedEvent, OnToolTabPressed, RoutingStrategies.Tunnel);
            item.AddHandler(PointerMovedEvent, OnToolTabMoved, RoutingStrategies.Tunnel);
            item.AddHandler(PointerReleasedEvent, OnToolTabReleased, RoutingStrategies.Tunnel);
            item.AddHandler(PointerCaptureLostEvent, (_, _) => EndDrag(), RoutingStrategies.Bubble);
            item.ContextRequested += OnToolTabContextRequested;
            ApplyToolTabTheme(item);
        }
        UpdateDockLayout();
    }

    const double DefaultLeftWidth = 300, DefaultRightWidth = 400, DefaultBottomHeight = 250;

    /// <summary>
    /// Restores the saved layout when the view model arrives: which group each tool window is in, the groups' sizes,
    /// the tab in front of each group and the window's size.
    /// </summary>
    void ApplySavedDockLayout(MainViewModel vm)
    {
        foreach (var (id, sideName) in vm.Settings.ToolLayout.ToList())
        {
            if (ToolTabs.FirstOrDefault(t => (string?)t.Tag == id) is not { } item)
                continue;
            if (Enum.TryParse<DockSide>(sideName, out var side))
                MoveToolWindow(item, side, remember: false);
            else if (sideName == "Float")
                _pendingFloats.Add((item, vm.Settings.Layout.FloatingTools.GetValueOrDefault(id)));
        }

        var layout = vm.Settings.Layout;
        foreach (var side in Enum.GetValues<DockSide>())
            if (layout.SelectedTools.TryGetValue(side.ToString(), out var id)
                && GroupOf(side).Items.OfType<TabItem>().FirstOrDefault(t => (string?)t.Tag == id) is { } tab)
                GroupOf(side).SelectedItem = tab;
        var columns = MainGrid.ColumnDefinitions;
        if (LeftTabs.ItemCount > 0 && layout.LeftWidth is { } left)
            columns[0].Width = new GridLength(Math.Max(left, 80));
        if (RightTabs.ItemCount > 0 && layout.RightWidth is { } right)
            columns[4].Width = new GridLength(Math.Max(right, 80));
        if (BottomTabs.ItemCount > 0 && layout.BottomHeight is { } bottom)
            LeftGrid.RowDefinitions[2].Height = new GridLength(Math.Max(bottom, 60));
        if (layout.LeftCollapsed && LeftTabs.ItemCount > 0)
            SetCollapsed(DockSide.Left, true, remember: false);
        if (layout.RightCollapsed && RightTabs.ItemCount > 0)
            SetCollapsed(DockSide.Right, true, remember: false);
        if (layout.Width is > 0 and var width && layout.Height is > 0 and var height)
        {
            Width = width;
            Height = height;
        }
        if (layout.Maximized)
            WindowState = WindowState.Maximized;
    }

    /// <summary>Remembers the groups' sizes, their front tabs and the window's size (on closing the window).</summary>
    void SaveDockLayout(MainViewModel vm)
    {
        var layout = vm.Settings.Layout;
        double? Size(GridLength length, double actual) => length.IsAbsolute && length.Value > 0 ? length.Value : actual > 0 ? actual : null;
        var columns = MainGrid.ColumnDefinitions;
        // A folded group keeps the width it opens at.
        if (LeftTabs.ItemCount > 0)
            layout.LeftWidth = _leftCollapsed ? _leftWidth : Size(columns[0].Width, columns[0].ActualWidth);
        if (RightTabs.ItemCount > 0)
            layout.RightWidth = _rightCollapsed ? _rightWidth : Size(columns[4].Width, columns[4].ActualWidth);
        layout.LeftCollapsed = _leftCollapsed && LeftTabs.ItemCount > 0;
        layout.RightCollapsed = _rightCollapsed && RightTabs.ItemCount > 0;
        if (BottomTabs.ItemCount > 0)
            layout.BottomHeight = Size(LeftGrid.RowDefinitions[2].Height, LeftGrid.RowDefinitions[2].ActualHeight);
        foreach (var side in Enum.GetValues<DockSide>())
            if (GroupOf(side).SelectedItem is TabItem { Tag: string id })
                layout.SelectedTools[side.ToString()] = id;
            else
                layout.SelectedTools.Remove(side.ToString());
        foreach (var window in _floatingTools)
            foreach (var item in window.Tabs.Items.OfType<TabItem>())
                if (item.Tag is string id)
                    layout.FloatingTools[id] = [window.Position.X, window.Position.Y, window.Width, window.Height];
        layout.Maximized = WindowState == WindowState.Maximized;
        // A maximized window keeps the size it returns to.
        if (WindowState == WindowState.Normal)
        {
            layout.Width = Width;
            layout.Height = Height;
        }
        vm.Settings.Save();
    }

    void OnToolWindowRequested(string id, string[] unlessShowing)
    {
        if (ToolGroups.Any(g => g.SelectedItem is TabItem { Tag: string selected } && unlessShowing.Contains(selected)))
            return;
        if (ToolTabs.FirstOrDefault(t => (string?)t.Tag == id) is { } item && ToolGroups.FirstOrDefault(g => g.Items.Contains(item)) is { } group)
        {
            group.SelectedItem = item;
            if (SideGroupOf(item) is { } side)
                SetCollapsed(side, false);
            FloatingWindowOf(id)?.Activate();
        }
    }

    /// <summary>Floats the saved floating tool windows once this window is on screen.</summary>
    void FloatPendingToolWindows()
    {
        foreach (var (item, bounds) in _pendingFloats)
            FloatToolWindow(item, bounds is [var x, var y, var w, var h] ? new PixelPoint((int)x, (int)y) : null,
                bounds is [_, _, var width, var height] ? new Size(width, height) : null, remember: false);
        _pendingFloats.Clear();
    }

    /// <summary>Window > Float for a tool window: its tab in a window of its own (at a point of the screen, or near this window).</summary>
    public void FloatToolWindow(string id)
    {
        if (ToolTabs.FirstOrDefault(t => (string?)t.Tag == id) is { } item)
            FloatToolWindow(item, null, null, remember: true);
    }

    void FloatToolWindow(TabItem item, PixelPoint? at, Size? size, bool remember)
    {
        var from = ToolGroups.FirstOrDefault(g => g.Items.Contains(item));
        if (from is null || _floatingTools.Any(w => w.Tabs == from && w.Tabs.ItemCount == 1) || item.Tag is not string id)
            return;
        if (SideOf(id) is { } side)
            _floatedFrom[id] = side;
        var window = new FloatingToolWindow(this);
        if (size is { } s)
            (window.Width, window.Height) = (Math.Max(s.Width, 200), Math.Max(s.Height, 150));
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Position = at ?? new PixelPoint(Position.X + 160, Position.Y + 160);
        from.Items.Remove(item);
        if (from.SelectedItem is null && from.ItemCount > 0)
            from.SelectedIndex = 0;
        window.Tabs.Items.Add(item);
        ApplyToolTabTheme(item);
        window.Tabs.SelectedItem = item;
        window.UpdateTitle();
        _floatingTools.Add(window);
        window.Show(this);
        CloseEmptyFloatingTools();
        UpdateDockLayout();
        if (remember && Vm is { } vm)
        {
            vm.Settings.ToolLayout[id] = "Float";
            vm.Settings.Save();
        }
    }

    /// <summary>A floating tool window was closed: its tool windows go back to where they were docked.</summary>
    internal void DockFloatingTools(FloatingToolWindow window)
    {
        foreach (var item in window.Tabs.Items.OfType<TabItem>().ToList())
            if (item.Tag is string id)
                MoveToolWindow(item, _floatedFrom.GetValueOrDefault(id, DockSide.Right), remember: true);
    }

    void CloseEmptyFloatingTools()
    {
        foreach (var empty in _floatingTools.Where(w => w.Tabs.ItemCount == 0).ToList())
        {
            _floatingTools.Remove(empty);
            empty.CloseEmpty();
        }
    }

    /// <summary>Moves a tool window to another group and selects it there.</summary>
    public void MoveToolWindow(string id, DockSide side)
    {
        if (ToolTabs.FirstOrDefault(t => (string?)t.Tag == id) is { } item)
            MoveToolWindow(item, side, remember: true);
    }

    void MoveToolWindow(TabItem item, DockSide side, bool remember)
    {
        var from = ToolGroups.FirstOrDefault(g => g.Items.Contains(item));
        var to = GroupOf(side);
        if (from is null || from == to)
            return;
        from.Items.Remove(item);
        to.Items.Add(item);
        ApplyToolTabTheme(item);
        to.SelectedItem = item;
        if (side != DockSide.Bottom)
            SetCollapsed(side, false, remember);
        if (from.SelectedItem is null && from.ItemCount > 0)
            from.SelectedIndex = 0;
        CloseEmptyFloatingTools();
        UpdateDockLayout();
        if (remember && Vm is { } vm && item.Tag is string id)
        {
            vm.Settings.ToolLayout[id] = side.ToString();
            vm.Settings.Layout.FloatingTools.Remove(id);
            vm.Settings.Save();
        }
    }

    /// <summary>Shows groups that hold tool windows and collapses empty ones (and their splitters).</summary>
    void UpdateDockLayout()
    {
        var columns = MainGrid.ColumnDefinitions;
        var rows = LeftGrid.RowDefinitions;
        var left = LeftTabs.ItemCount > 0;
        var right = RightTabs.ItemCount > 0;
        var bottom = BottomTabs.ItemCount > 0;

        // A folded group is its activity bar only, with no splitter.
        columns[0].Width = !left ? new GridLength(0) : _leftCollapsed ? new GridLength(ActivityBarWidth)
            : columns[0].Width.Value > ActivityBarWidth ? columns[0].Width : new GridLength(Math.Max(_leftWidth, 160));
        columns[1].Width = new GridLength(left && !_leftCollapsed ? 4 : 0);
        LeftDock.IsVisible = left;
        LeftSplitter.IsVisible = left && !_leftCollapsed;
        LeftTabs.Classes.Set("collapsed", _leftCollapsed);

        columns[4].Width = !right ? new GridLength(0) : _rightCollapsed ? new GridLength(ActivityBarWidth)
            : columns[4].Width.Value > ActivityBarWidth ? columns[4].Width : new GridLength(Math.Max(_rightWidth, 160));
        columns[3].Width = new GridLength(right && !_rightCollapsed ? 4 : 0);
        RightDock.IsVisible = right;
        RightSplitter.IsVisible = right && !_rightCollapsed;
        RightTabs.Classes.Set("collapsed", _rightCollapsed);

        rows[2].Height = bottom ? (rows[2].Height.Value > 0 ? rows[2].Height : new GridLength(DefaultBottomHeight)) : new GridLength(0);
        rows[1].Height = new GridLength(bottom ? 4 : 0);
        BottomDock.IsVisible = BottomSplitter.IsVisible = bottom;
    }

    void OnToolTabPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is TabItem item && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            // The icon in front, clicked again, folds its group (decided on release, when it was not a drag).
            _pressedInFront = item.IsSelected && SideGroupOf(item) is not null;
            _dragItem = item;
            _dragStart = e.GetPosition(this);
            _dragging = false;
        }
    }

    void OnToolTabMoved(object? sender, PointerEventArgs e)
    {
        if (_dragItem is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        var position = e.GetPosition(this);
        if (!_dragging)
        {
            if (Math.Abs(position.X - _dragStart.X) < DragThreshold && Math.Abs(position.Y - _dragStart.Y) < DragThreshold)
                return;
            _dragging = true;
            e.Pointer.Capture(_dragItem);
            DockOverlay.IsVisible = true;
        }
        _dropTarget = ZoneAt(DockOverlay.PointToClient(_dragItem.PointToScreen(e.GetPosition(_dragItem))));
        DockZoneLeft.Classes.Set("hot", _dropTarget == DockSide.Left);
        DockZoneRight.Classes.Set("hot", _dropTarget == DockSide.Right);
        DockZoneBottom.Classes.Set("hot", _dropTarget == DockSide.Bottom);
        e.Handled = true;
    }

    void OnToolTabReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging || _dragItem is null)
        {
            if (_dragItem is { } clicked && SideGroupOf(clicked) is { } group)
                SetCollapsed(group, _pressedInFront && !IsCollapsed(group));
            _dragItem = null;
            return;
        }
        var item = _dragItem;
        var target = _dropTarget;
        var screen = item.PointToScreen(e.GetPosition(item));
        e.Pointer.Capture(null);
        EndDrag();
        if (target is { } side)
            MoveToolWindow(item, side, remember: true);
        else if (!new Rect(Bounds.Size).Contains(this.PointToClient(screen)))
            // Dropped outside the main window: the tool window floats there, as in Visual Studio.
            FloatToolWindow(item, new PixelPoint(screen.X - 40, screen.Y - 12), null, remember: true);
        e.Handled = true;
    }

    void EndDrag()
    {
        _dragItem = null;
        _dragging = false;
        _dropTarget = null;
        DockOverlay.IsVisible = false;
        foreach (var zone in new[] { DockZoneLeft, DockZoneRight, DockZoneBottom })
            zone.Classes.Set("hot", false);
    }

    DockSide? ZoneAt(Point p)
    {
        if (DockZoneLeft.Bounds.Contains(p))
            return DockSide.Left;
        if (DockZoneRight.Bounds.Contains(p))
            return DockSide.Right;
        if (DockZoneBottom.Bounds.Contains(p))
            return DockSide.Bottom;
        return null;
    }

    void OnToolTabContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not TabItem { Tag: string id } item)
            return;
        e.Handled = true;
        var current = SideOf(id);
        MenuItem Item(string header, DockSide side)
        {
            var menuItem = new MenuItem { Header = header, IsEnabled = current != side };
            menuItem.Click += (_, _) => MoveToolWindow(item, side, remember: true);
            return menuItem;
        }
        var floatItem = new MenuItem { Header = "Float", IsEnabled = !IsFloating(id) };
        floatItem.Click += (_, _) => FloatToolWindow(item, null, null, remember: true);
        new ContextMenu
        {
            ItemsSource = new object[]
            {
                floatItem,
                Item("Dock Left", DockSide.Left),
                Item("Dock Right", DockSide.Right),
                Item("Dock Bottom", DockSide.Bottom),
                new Separator(),
                new MenuItem
                {
                    Header = "Reset Window Layout",
                    Command = new CommunityToolkit.Mvvm.Input.RelayCommand(ResetWindowLayout),
                },
            },
        }.Open(item);
    }

    /// <summary>Window > Reset Window Layout: every tool window back to its default group and size.</summary>
    public void ResetWindowLayout()
    {
        _leftCollapsed = _rightCollapsed = false;
        (_leftWidth, _rightWidth) = (DefaultLeftWidth, DefaultRightWidth);
        MainGrid.ColumnDefinitions[0].Width = new GridLength(DefaultLeftWidth);
        MainGrid.ColumnDefinitions[4].Width = new GridLength(DefaultRightWidth);
        LeftGrid.RowDefinitions[2].Height = new GridLength(DefaultBottomHeight);
        foreach (var id in new[] { ToolWindows.SolutionExplorer, ToolWindows.TestExplorer, ToolWindows.GitChanges, ToolWindows.DatabaseExplorer, ToolWindows.DockerExplorer, ToolWindows.Servers, ToolWindows.Mcp, ToolWindows.Claude })
            MoveToolWindow(id, DockSide.Right);
        foreach (var id in new[] { ToolWindows.ErrorList, ToolWindows.Output, ToolWindows.FindResults, ToolWindows.Locals, ToolWindows.Watch, ToolWindows.CallStack, ToolWindows.Terminal })
            MoveToolWindow(id, DockSide.Bottom);
        UpdateDockLayout();
        if (Vm is { } vm)
        {
            vm.Settings.ToolLayout.Clear();
            vm.Settings.Layout = new();
            vm.Settings.Save();
        }
    }
}
