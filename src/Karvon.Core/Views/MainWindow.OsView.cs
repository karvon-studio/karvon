using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
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
        var items = new StackPanel { Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal, Spacing = 4 };
        foreach (var id in DockApps)
            items.Children.Add(DockItem(id, vertical));
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

    Control DockItem(string id, bool vertical)
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
        button.Click += (_, _) => ToggleApp(id);
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

    public bool IsAppsShown => AppsLayer.IsVisible;

    /// <summary>The apps grid over the IDE, which is blurred behind it; it comes up as it fades in.</summary>
    public void ShowApps()
    {
        if (IsAppsShown)
        {
            HideApps();
            return;
        }
        _appsSearch = new TextBox { Name = "AppsSearch", Watermark = "Type to search", Width = 340, HorizontalAlignment = HorizontalAlignment.Center, Classes = { "appssearch" } };
        _appsSearch.TextChanged += (_, _) => FillApps();
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
        _appsGrid = new WrapPanel { Name = "AppsGrid", HorizontalAlignment = HorizontalAlignment.Center, ItemWidth = 132, ItemHeight = 124 };
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
            RenderTransform = TransformOperations.Parse("scale(0.94)"),
            Transitions = [new TransformOperationsTransition { Property = RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(200), Easing = new Avalonia.Animation.Easings.CubicEaseOut() }],
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
        AppsLayer.Opacity = 0;
        AppsLayer.Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(180) }];
        AppsLayer.IsVisible = true;
        Shell.Effect = new BlurEffect { Radius = 28 };
        FillApps();
        Dispatcher.UIThread.Post(() =>
        {
            AppsLayer.Opacity = 1;
            if (_appsContent is { } content)
                content.RenderTransform = TransformOperations.Parse("scale(1)");
            _appsSearch?.Focus();
        }, DispatcherPriority.Render);
    }

    public void HideApps()
    {
        if (!AppsLayer.IsVisible)
            return;
        AppsLayer.IsVisible = false;
        AppsLayer.Children.Clear();
        Shell.Effect = null;
        _appsSearch = null;
        _appsGrid = null;
        _appsContent = null;
    }

    /// <summary>Searches the apps by name (as typed), in the order they are docked.</summary>
    public void SearchApps(string text)
    {
        if (_appsSearch is { } search)
        {
            search.Text = text;
            FillApps();
        }
    }

    /// <summary>The apps the grid shows now, by their ids.</summary>
    public IReadOnlyList<string> ShownApps => _appsGrid?.Children.OfType<Button>().Select(b => (string)b.Tag!).ToList() ?? [];

    void FillApps()
    {
        if (_appsGrid is not { } grid)
            return;
        var text = _appsSearch?.Text?.Trim() ?? "";
        grid.Children.Clear();
        foreach (var (id, title) in Apps.OrderBy(a => a.Title, StringComparer.CurrentCultureIgnoreCase))
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
                HideApps();
                OpenApp(id);
                RefreshDock();
            };
            tile.ContextMenu = AppMenu(id, IsAppOpen(id));
            grid.Children.Add(tile);
        }
    }

    static Control Docked(Control control, Dock dock)
    {
        DockPanel.SetDock(control, dock);
        return control;
    }
}
