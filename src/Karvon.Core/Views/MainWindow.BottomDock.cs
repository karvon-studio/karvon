using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.VisualTree;
using Karvon.Modules;

namespace Karvon.Views;

/// <summary>
/// The bottom tool group's Auto Hide and Minimize buttons, at the right of its tabs. Minimized, the group is its tabs
/// only, and a tab opens it again. On Auto Hide (Visual Studio's pin) it stays minimized: a tab, or a tool window that
/// is asked for, shows it over the documents, and a click outside it hides it again.
/// </summary>
public partial class MainWindow
{
    bool _bottomCollapsed, _bottomAutoHide;
    double _bottomHeight = DefaultBottomHeight;

    /// <summary>The row holds the group's height docked open (not its tabs' height, nor nothing).</summary>
    bool _bottomRowDocked = true;

    static readonly BoxShadows OverDocumentsShadow = BoxShadows.Parse("0 -2 8 0 #80000000");

    public bool IsAutoHide(DockSide side) => side == DockSide.Bottom && _bottomAutoHide;

    /// <summary>The bottom group's height docked open: the row's while it is so, the one it opens at again otherwise.</summary>
    double BottomHeight =>
        _bottomRowDocked && LeftGrid.RowDefinitions[2].Height is { IsAbsolute: true, Value: > 0 } height ? height.Value : _bottomHeight;

    void InitializeBottomDock()
    {
        AddHandler(PointerPressedEvent, (_, e) => HideBottomOutside(e.Source), RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(GotFocusEvent, (_, e) => HideBottomOutside(e.Source), RoutingStrategies.Bubble, handledEventsToo: true);
    }

    void OnBottomMinimize(object? sender, RoutedEventArgs e) => SetCollapsed(DockSide.Bottom, !_bottomCollapsed);

    void OnBottomAutoHide(object? sender, RoutedEventArgs e) => SetAutoHide(DockSide.Bottom, !_bottomAutoHide);

    /// <summary>Auto Hide on: the group hides to its tabs at once; off: it is docked open again.</summary>
    public void SetAutoHide(DockSide side, bool autoHide, bool remember = true)
    {
        if (side != DockSide.Bottom || _bottomAutoHide == autoHide)
            return;
        _bottomAutoHide = autoHide;
        _bottomCollapsed = autoHide;
        UpdateDockLayout();
        if (remember)
            RememberBottomDock();
    }

    void SetBottomCollapsed(bool collapsed, bool remember)
    {
        _bottomCollapsed = collapsed;
        UpdateDockLayout();
        // On Auto Hide it is minimized whenever Karvon starts.
        if (remember && !_bottomAutoHide)
            RememberBottomDock();
    }

    void RememberBottomDock()
    {
        if (Vm is not { } vm)
            return;
        vm.Settings.Layout.BottomCollapsed = _bottomCollapsed;
        vm.Settings.Layout.BottomAutoHide = _bottomAutoHide;
        vm.Settings.Save();
    }

    /// <summary>Shown over the documents, the group hides again when a click or the focus goes outside it.</summary>
    void HideBottomOutside(object? source)
    {
        if (_bottomAutoHide && !_bottomCollapsed && !IsWithin(BottomDock, source))
            SetBottomCollapsed(true, remember: false);
    }

    /// <summary>In the control, or in a popup of it (a combo box's list, a context menu).</summary>
    static bool IsWithin(Control container, object? source)
    {
        for (var node = source as ILogical; node is not null; node = node.LogicalParent)
            if (node == container)
                return true;
        return source is Visual visual && container.IsVisualAncestorOf(visual);
    }

    /// <summary>
    /// Lays the bottom group out: docked open in its row under the splitter; minimized, its tabs' height; shown on Auto
    /// Hide, over the documents from its tabs up, the documents ending where its tabs begin.
    /// </summary>
    void UpdateBottomDock(bool bottom)
    {
        var rows = LeftGrid.RowDefinitions;
        _bottomHeight = BottomHeight;
        var docked = bottom && !_bottomCollapsed && !_bottomAutoHide;
        var over = bottom && !_bottomCollapsed && _bottomAutoHide;
        if (over && !BottomDock.Classes.Contains("over"))
            rows[2].Height = new GridLength(BottomTabsHeight);
        else if (!over)
            rows[2].Height = !bottom ? new GridLength(0) : _bottomCollapsed ? GridLength.Auto : new GridLength(_bottomHeight);
        _bottomRowDocked = docked;
        rows[1].Height = new GridLength(docked ? 4 : 0);
        BottomSplitter.IsVisible = docked;
        BottomDock.IsVisible = bottom;
        BottomTabs.Classes.Set("collapsed", bottom && _bottomCollapsed);

        BottomDock.Classes.Set("over", over);
        Grid.SetRow(BottomDock, over ? 0 : 2);
        Grid.SetRowSpan(BottomDock, over ? 3 : 1);
        BottomDock.VerticalAlignment = over ? VerticalAlignment.Bottom : VerticalAlignment.Stretch;
        BottomDock.Height = !over ? double.NaN : LeftGrid.Bounds.Height > 0 ? Math.Min(_bottomHeight, LeftGrid.Bounds.Height - 40) : _bottomHeight;
        BottomDock.BoxShadow = over ? OverDocumentsShadow : default;

        BottomMinimizeIcon.Kind = _bottomCollapsed ? "ChevronUp" : "Minus";
        ToolTip.SetTip(BottomMinimizeButton, (_bottomCollapsed, _bottomAutoHide) switch
        {
            (true, false) => "Restore",
            (true, true) => "Show",
            (false, false) => "Minimize",
            (false, true) => "Hide",
        });
        // VS's pin: upright while the group stays open, on its side on Auto Hide.
        BottomAutoHideIcon.RenderTransform = _bottomAutoHide ? new RotateTransform(90) : null;
        ToolTip.SetTip(BottomAutoHideButton, _bottomAutoHide ? "Dock: keep it open" : "Auto Hide: show it over the documents only when it is asked for");
    }

    /// <summary>The height of the group minimized (its tabs or its buttons, and its top border): what the row keeps while it shows over the documents.</summary>
    double BottomTabsHeight =>
        Math.Max(BottomTabs.ItemsPanelRoot?.Bounds.Height is > 0 and var tabs ? tabs : 20, BottomButtons.Bounds.Height) + BottomDock.BorderThickness.Top;
}
