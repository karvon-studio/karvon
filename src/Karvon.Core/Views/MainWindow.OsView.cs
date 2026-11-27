using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Karvon.Modules;
using Karvon.ViewModels;

namespace Karvon.Views;

/// <summary>
/// The OS view (View > OS View), Karvon as a desktop of apps: each tool window an app, opened as it always is. A dock as
/// Ubuntu's stands for the activity bars: the apps pinned to it, and those open (a dot under them), at the left, the
/// right or the bottom; an app clicked comes up, clicked again (open) goes down. Its last button shows the apps grid: all
/// of them over the IDE blurred, searched as they are typed. The IDE (its editor, its explorer) is the desktop itself.
/// </summary>
public partial class MainWindow
{
    /// <summary>The dock's apps before any is pinned or unpinned.</summary>
    static readonly string[] DefaultPinned = [ToolWindows.SolutionExplorer, "Claude", ToolWindows.Git, "DatabaseExplorer", ToolWindows.Terminal];

    bool _osView;
    TextBox? _appsSearch;
    WrapPanel? _appsGrid;
    Border? _appsContent;

    /// <summary>The apps: each docked tool window (a set as one), its id and its name.</summary>
    public IReadOnlyList<(string Id, string Title)> Apps =>
        [.. ToolTabs.Where(t => t.Tag is string).Select(t => ((string)t.Tag!, t.Header?.ToString() ?? (string)t.Tag!))];

    /// <summary>The dock's apps, in its order: those pinned, then those open.</summary>
    public IReadOnlyList<string> DockApps
    {
        get
        {
            var apps = Apps.Select(a => a.Id).ToList();
            var pinned = Pinned.Where(apps.Contains).ToList();
            return [.. pinned, .. apps.Where(id => !pinned.Contains(id) && IsAppOpen(id))];
        }
    }

    List<string> Pinned => Vm?.Settings.DockPinned ?? [.. DefaultPinned];

    /// <summary>An app open: its tool window (or one of its set) in front of a group that shows.</summary>
    public bool IsAppOpen(string id) => DockedTab(id) is { } item && IdsOf(item).Any(IsToolWindowShowing);

    public bool IsPinned(string id) => Pinned.Contains(id);

    public DockSide DockSide => Vm?.Settings.DockPosition switch
    {
        "Right" => DockSide.Right,
        "Bottom" => DockSide.Bottom,
        _ => DockSide.Left,
    };

    void AttachOsView(MainViewModel vm)
    {
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.OsView))
                ApplyOsView();
        };
        foreach (var group in new[] { LeftTabs, RightTabs, BottomTabs })
            group.SelectionChanged += (_, e) =>
            {
                // Only its own: a list in a tool window bubbles its selection up here too.
                if (e.Source == group)
                    RefreshDock();
            };
        // Esc puts the apps grid down wherever the focus is: in its search, on a tile pressed or dragged, or nowhere once
        // its tiles were laid again.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape && IsAppsShown)
            {
                HideApps();
                e.Handled = true;
            }
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        ApplyOsView();
    }

    void ApplyOsView()
    {
        _osView = Vm?.OsView == true;
        LeftTabs.Classes.Set("os", _osView);
        RightTabs.Classes.Set("os", _osView);
        OsDock.IsVisible = _osView;
        if (!_osView)
            HideApps();
        UpdateDockLayout();
    }

    /// <summary>Pins an app to the dock (at its end), or unpins it.</summary>
    public void SetPinned(string id, bool pinned)
    {
        if (Vm is not { } vm || IsPinned(id) == pinned)
            return;
        var list = Pinned;
        if (pinned)
            list.Add(id);
        else
            list.Remove(id);
        vm.Settings.DockPinned = list;
        vm.Settings.Save();
        RefreshDock();
    }

    /// <summary>
    /// A dock's app dragged to another place in it (as Ubuntu's, one open but not pinned is pinned there); kept.
    /// </summary>
    public void MoveDockApp(string id, int index)
    {
        if (Vm is not { } vm)
            return;
        var order = DockApps.ToList();
        order.Remove(id);
        order.Insert(Math.Clamp(index, 0, order.Count), id);
        var pinned = order.Where(a => a == id || IsPinned(a)).ToList();
        // Those pinned whose module is off now keep their place, at the end.
        pinned.AddRange(Pinned.Where(p => !pinned.Contains(p)));
        vm.Settings.DockPinned = pinned;
        vm.Settings.Save();
        RefreshDock();
    }

    /// <summary>The apps grid's apps, in the order they were dragged to (new ones, and all before any was, by name).</summary>
    public IReadOnlyList<(string Id, string Title)> GridApps
    {
        get
        {
            var byName = Apps.OrderBy(a => a.Title, StringComparer.CurrentCultureIgnoreCase);
            if (Vm?.Settings.AppsOrder is not { } order)
                return [.. byName];
            return [.. byName.OrderBy(a => order.IndexOf(a.Id) is var at and >= 0 ? at : int.MaxValue)];
        }
    }

    /// <summary>An app dragged to another place in the apps grid; kept.</summary>
    public void MoveApp(string id, int index)
    {
        if (Vm is not { } vm)
            return;
        var order = GridApps.Select(a => a.Id).ToList();
        if (!order.Remove(id))
            return;
        order.Insert(Math.Clamp(index, 0, order.Count), id);
        // Those whose module is off now keep their place, at the end.
        order.AddRange((vm.Settings.AppsOrder ?? []).Where(a => !order.Contains(a)));
        vm.Settings.AppsOrder = order;
        vm.Settings.Save();
        FillApps(animate: false);
        // The tile dragged is laid again: typing goes on to the search.
        _appsSearch?.Focus();
    }

    /// <summary>The dock at the left, the right or the bottom.</summary>
    public void SetDockSide(DockSide side)
    {
        if (Vm is not { } vm)
            return;
        vm.Settings.DockPosition = side.ToString();
        vm.Settings.Save();
        RefreshDock();
    }

    /// <summary>An app from the dock or the grid: up when it is not open, down (its group folded) when it is.</summary>
    public void ToggleApp(string id)
    {
        if (IsAppOpen(id) && DockedTab(id) is { } item && DockedGroupOf(item) is { } side)
            SetCollapsed(side, true);
        else
            OpenApp(id);
        RefreshDock();
    }

    /// <summary>An app up, as its activity bar icon brings it (a set: as it was last).</summary>
    void OpenApp(string id) => OnToolWindowRequested(id, []);

    /// <summary>The dock as it is now: its side, its apps, which are open.</summary>
    void RefreshDock()
    {
        if (!_osView || Vm is null)
            return;
        var side = DockSide;
        DockPanel.SetDock(OsDock, side switch { DockSide.Right => Dock.Right, DockSide.Bottom => Dock.Bottom, _ => Dock.Left });
        var vertical = side != DockSide.Bottom;
        var orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;
        var items = new StackPanel { Orientation = orientation, Spacing = 4 };
        // Its apps dragged along it to another place; the apps button and the line before it stay last.
        var apps = DockApps;
        var reorder = new DragReorder(items, orientation, () => true, (from, to) => MoveDockApp(apps[from], to));
        foreach (var id in apps)
        {
            var item = DockItem(id, vertical, reorder);
            items.Children.Add(item);
            reorder.Add(item);
        }
        items.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.Parse("#33FFFFFF")),
            Width = vertical ? 32 : 1,
            Height = vertical ? 1 : 32,
            Margin = vertical ? new Thickness(0, 2) : new Thickness(2, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        items.Children.Add(AppsButton());
        OsDock.Child = items;
        OsDock.Classes.Set("osdock", true);
        OsDock.Margin = side switch { DockSide.Right => new Thickness(4, 8, 6, 8), DockSide.Bottom => new Thickness(8, 4, 8, 6), _ => new Thickness(6, 8, 4, 8) };
        OsDock.HorizontalAlignment = vertical ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;
        OsDock.VerticalAlignment = vertical ? VerticalAlignment.Center : VerticalAlignment.Stretch;
        OsDock.ContextMenu = DockMenu();
    }

    Control DockItem(string id, bool vertical, DragReorder reorder)
    {
        var open = IsAppOpen(id);
        var title = Apps.FirstOrDefault(a => a.Id == id).Title ?? id;
        // The dot of an open app: under it at the bottom, at its outer edge at a side.
        var dot = new Ellipse
        {
            Width = 5,
            Height = 5,
            Fill = Brushes.White,
            IsVisible = open,
            HorizontalAlignment = vertical ? DockSide == DockSide.Right ? HorizontalAlignment.Right : HorizontalAlignment.Left : HorizontalAlignment.Center,
            VerticalAlignment = vertical ? VerticalAlignment.Center : VerticalAlignment.Bottom,
            Margin = new Thickness(1),
        };
        var button = new Button
        {
            Name = "Dock_" + id,
            Classes = { "dockapp" },
            Tag = id,
            Content = new Panel { Children = { new AppIcon { AppId = id, Width = 40, Height = 40 }, dot } },
        };
        button.Classes.Set("open", open);
        ToolTip.SetTip(button, title);
        ToolTip.SetPlacement(button, DockSide switch { DockSide.Right => PlacementMode.Left, DockSide.Bottom => PlacementMode.Top, _ => PlacementMode.Right });
        button.Click += (_, _) =>
        {
            if (!reorder.Dragged)
                ToggleApp(id);
        };
        button.ContextMenu = AppMenu(id, open);
        return button;
    }

    Button AppsButton()
    {
        // Nine dots, as Ubuntu's Show Applications.
        var dots = new Canvas { Width = 22, Height = 22 };
        for (var row = 0; row < 3; row++)
            for (var column = 0; column < 3; column++)
            {
                var dot = new Ellipse { Width = 5, Height = 5, Fill = Brushes.White };
                Canvas.SetLeft(dot, column * 8.5);
                Canvas.SetTop(dot, row * 8.5);
                dots.Children.Add(dot);
            }
        var button = new Button { Name = "ShowApps", Classes = { "dockapp" }, Content = dots };
        ToolTip.SetTip(button, "Show Applications");
        button.Click += (_, _) => ShowApps();
        return button;
    }

    ContextMenu AppMenu(string id, bool open)
    {
        var pin = new MenuItem { Header = IsPinned(id) ? "Unpin from Dock" : "Pin to Dock" };
        pin.Click += (_, _) => SetPinned(id, !IsPinned(id));
        var toggle = new MenuItem { Header = open ? "Close" : "Open" };
        toggle.Click += (_, _) => ToggleApp(id);
        object[] items = [toggle, pin, new Separator(), .. DockMenuItems()];
        return new ContextMenu { ItemsSource = items };
    }

    ContextMenu DockMenu() => new() { ItemsSource = DockMenuItems() };

    object[] DockMenuItems()
    {
        MenuItem Side(string header, DockSide side)
        {
            var item = new MenuItem { Header = header, ToggleType = MenuItemToggleType.Radio, IsChecked = DockSide == side };
            item.Click += (_, _) => SetDockSide(side);
            return item;
        }
        var leave = new MenuItem { Header = "Leave the OS View" };
        leave.Click += (_, _) =>
        {
            if (Vm is { } vm)
                vm.OsView = false;
        };
        return [Side("Dock on the Left", DockSide.Left), Side("Dock on the Right", DockSide.Right), Side("Dock at the Bottom", DockSide.Bottom), new Separator(), leave];
    }

    // ---- The apps grid ----------------------------------------------------------------------------------------------

    /// <summary>The apps grid is up (not on its way out).</summary>
    public bool IsAppsShown => AppsLayer.IsVisible && AppsLayer.IsHitTestVisible;

    /// <summary>The grid's tiles: their size and their icons' by the window's (a large screen's larger), and how many a row.</summary>
    public static (double Tile, double Icon, int Columns) GridMetrics(Size window)
    {
        var tile = Math.Round(Math.Clamp(window.Width / 15, 120, 176));
        // As Ubuntu's: a few columns in the middle of the screen, not a row across it.
        var columns = (int)Math.Clamp(Math.Floor(window.Width * 0.7 / tile), 3, 8);
        return (tile, Math.Round(tile * 0.54), columns);
    }

    /// <summary>The apps grid over the IDE, which blurs behind it; its tiles come up one after another.</summary>
    public void ShowApps()
    {
        if (IsAppsShown)
        {
            HideApps();
            return;
        }
        _appsSearch = new TextBox { Name = "AppsSearch", Watermark = "Type to search", Width = 360, HorizontalAlignment = HorizontalAlignment.Center, Classes = { "appssearch" } };
        _appsSearch.TextChanged += (_, _) => FillApps(animate: false);
        _appsSearch.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                HideApps();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && _appsGrid?.Children.OfType<Button>().FirstOrDefault() is { Tag: string first })
            {
                HideApps();
                OpenApp(first);
                RefreshDock();
                e.Handled = true;
            }
        };
        _appsGrid = new WrapPanel { Name = "AppsGrid", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _appsContent = new Border
        {
            Padding = new Thickness(40, 36),
            Child = new DockPanel
            {
                Children =
                {
                    Docked(_appsSearch, Dock.Top),
                    new ScrollViewer { Margin = new Thickness(0, 28, 0, 0), Content = _appsGrid, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled },
                },
            },
        };
        var dim = new Border { Background = new SolidColorBrush(Color.Parse("#99101014")) };
        // A click on nothing (not on an app) goes back.
        dim.PointerPressed += (_, _) => HideApps();
        _appsContent.PointerPressed += (_, e) =>
        {
            if (e.Source == _appsContent || e.Source is WrapPanel or ScrollViewer or DockPanel)
                HideApps();
        };
        AppsLayer.Children.Clear();
        AppsLayer.Children.Add(dim);
        AppsLayer.Children.Add(_appsContent);
        AppsLayer.IsHitTestVisible = true;
        AppsLayer.Opacity = 0;
        AppsLayer.Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(220), Easing = new CubicEaseOut() }];
        AppsLayer.IsVisible = true;
        AppsLayer.SizeChanged -= OnAppsLayerSized;
        AppsLayer.SizeChanged += OnAppsLayerSized;
        // The IDE blurs as the grid comes (from none to its full blur).
        Shell.Transitions = [new EffectTransition { Property = EffectProperty, Duration = TimeSpan.FromMilliseconds(260), Easing = new CubicEaseOut() }];
        Shell.Effect = new BlurEffect { Radius = 0 };
        FillApps(animate: true);
        Dispatcher.UIThread.Post(() =>
        {
            AppsLayer.Opacity = 1;
            Shell.Effect = new BlurEffect { Radius = 30 };
            _appsSearch?.Focus();
        }, DispatcherPriority.Render);
    }

    void OnAppsLayerSized(object? sender, SizeChangedEventArgs e) => SizeApps();

    /// <summary>The grid laid out for the window as it is now: its tiles' size, and as wide as its columns (in the middle).</summary>
    void SizeApps()
    {
        if (_appsGrid is not { } grid)
            return;
        var (tile, icon, columns) = GridMetrics(OsRoot.Bounds.Size.Width > 0 ? OsRoot.Bounds.Size : new Size(Width, Height));
        grid.ItemWidth = tile;
        // Its icon, and two lines of its name under it.
        grid.ItemHeight = icon + (tile >= 150 ? 84 : 76);
        grid.Width = columns * tile;
        foreach (var button in grid.Children.OfType<Button>())
        {
            button.Width = tile - 8;
            button.Height = grid.ItemHeight - 8;
            if (button.Content is StackPanel { Children: [AppIcon appIcon, TextBlock label] })
            {
                appIcon.Width = appIcon.Height = icon;
                label.FontSize = tile >= 150 ? 14 : 12.5;
            }
        }
    }

    /// <summary>The grid goes as it came: faded, a little smaller, the IDE's blur with it.</summary>
    public void HideApps()
    {
        if (!AppsLayer.IsVisible || !AppsLayer.IsHitTestVisible)
            return;
        AppsLayer.IsHitTestVisible = false;
        AppsLayer.SizeChanged -= OnAppsLayerSized;
        var content = _appsContent;
        _appsSearch = null;
        _appsGrid = null;
        _appsContent = null;
        // The blur and the grid go together; the IDE is usable at once.
        Shell.Transitions = null;
        Shell.Effect = null;
        if (content is null)
        {
            Gone();
            return;
        }
        content.Transitions = [new TransformOperationsTransition { Property = RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(140) }];
        content.RenderTransform = TransformOperations.Parse("scale(0.96)");
        AppsLayer.Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(140) }];
        AppsLayer.Opacity = 0;
        DispatcherTimer.RunOnce(Gone, TimeSpan.FromMilliseconds(150));

        void Gone()
        {
            // Shown again meanwhile: that one stays.
            if (AppsLayer.IsHitTestVisible)
                return;
            AppsLayer.IsVisible = false;
            AppsLayer.Children.Clear();
        }
    }

    /// <summary>Searches the apps by name (as typed), in the order they are docked.</summary>
    public void SearchApps(string text)
    {
        if (_appsSearch is { } search)
        {
            search.Text = text;
            FillApps(animate: false);
        }
    }

    /// <summary>The apps grid's panel while it is up.</summary>
    public WrapPanel? AppsGrid => _appsGrid;

    /// <summary>The apps the grid shows now, by their ids.</summary>
    public IReadOnlyList<string> ShownApps => _appsGrid?.Children.OfType<Button>().Select(b => (string)b.Tag!).ToList() ?? [];

    void FillApps(bool animate)
    {
        if (_appsGrid is not { } grid)
            return;
        var text = _appsSearch?.Text?.Trim() ?? "";
        grid.Children.Clear();
        // Its apps dragged to another place, all of them shown (not while searching).
        var shown = new List<string>();
        var reorder = new DragReorder(grid, null, () => text.Length == 0, (from, to) => MoveApp(shown[from], to));
        var index = 0;
        foreach (var (id, title) in GridApps)
        {
            if (text.Length > 0 && !title.Contains(text, StringComparison.CurrentCultureIgnoreCase))
                continue;
            var tile = new Button
            {
                Name = "App_" + id,
                Classes = { "apptile" },
                Tag = id,
                Content = new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new AppIcon { AppId = id, Width = 64, Height = 64, HorizontalAlignment = HorizontalAlignment.Center },
                        new TextBlock { Text = title, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Brushes.White },
                    },
                },
            };
            tile.Click += (_, _) =>
            {
                if (reorder.Dragged)
                    return;
                HideApps();
                OpenApp(id);
                RefreshDock();
            };
            tile.ContextMenu = AppMenu(id, IsAppOpen(id));
            grid.Children.Add(tile);
            shown.Add(id);
            reorder.Add(tile);
            if (animate)
                Arrive(tile, index++);
        }
        SizeApps();
    }

    /// <summary>A tile coming up into its place, from a little below and smaller, a moment after the one before.</summary>
    static void Arrive(Control tile, int index)
    {
        tile.Opacity = 0;
        tile.RenderTransform = TransformOperations.Parse("translate(0px, 28px) scale(0.7)");
        tile.Transitions =
        [
            new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(300), Easing = new CubicEaseOut() },
            new TransformOperationsTransition { Property = RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(340), Easing = new BackEaseOut() },
        ];
        void Up()
        {
            tile.Opacity = 1;
            tile.RenderTransform = TransformOperations.Parse("translate(0px, 0px) scale(1)");
        }
        // Once it is drawn where it starts; the ones after it a little later each (the last ones together).
        var delay = TimeSpan.FromMilliseconds(30 + Math.Min(index, 24) * 14);
        DispatcherTimer.RunOnce(Up, delay);
    }

    static Control Docked(Control control, Dock dock)
    {
        DockPanel.SetDock(control, dock);
        return control;
    }
}
