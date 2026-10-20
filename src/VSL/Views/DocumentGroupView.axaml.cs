using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using VSL.ViewModels;

namespace VSL.Views;

/// <summary>
/// One tab group of the document well, or the tabs of a floating document window. A click anywhere in it makes it the
/// active group; its tabs are dragged to reorder them, to another group, or out into a window of their own.
/// </summary>
public partial class DocumentGroupView : UserControl
{
    const double DragThreshold = 8;

    TabItem? _pressed;
    DocumentTab? _pressedDoc;
    Point _pressStart;
    bool _dragging;

    public DocumentGroupView()
    {
        InitializeComponent();
        AddHandler(PointerPressedEvent, (_, _) => Activate(), RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(GotFocusEvent, (_, _) => Activate(), RoutingStrategies.Bubble, handledEventsToo: true);
        Tabs.AddHandler(PointerPressedEvent, OnTabPressed, RoutingStrategies.Tunnel);
        Tabs.AddHandler(PointerMovedEvent, OnTabMoved, RoutingStrategies.Tunnel);
        Tabs.AddHandler(PointerReleasedEvent, OnTabReleased, RoutingStrategies.Tunnel);
        Tabs.AddHandler(PointerCaptureLostEvent, (_, _) => CancelDrag(), RoutingStrategies.Bubble);
    }

    public DocumentGroup? Group => DataContext as DocumentGroup;

    /// <summary>The tab strip of the group (its tabs' containers in order).</summary>
    internal TabControl TabControl => Tabs;

    /// <summary>The main window, also for a group in a floating window.</summary>
    internal MainWindow? Shell => TopLevel.GetTopLevel(this) switch
    {
        MainWindow main => main,
        DocumentWindow floating => floating.Shell,
        _ => null,
    };

    MainViewModel? Vm => Shell?.DataContext as MainViewModel;

    void Activate()
    {
        if (Group is { } group && Vm is { } vm && vm.ActiveGroup != group)
            vm.ActivateGroup(group);
    }

    /// <summary>The tab containers with the documents they show, left to right.</summary>
    internal IEnumerable<(TabItem Item, DocumentTab Doc)> TabItems() =>
        Tabs.GetRealizedContainers().OfType<TabItem>()
            .Select(item => (item, doc: Tabs.ItemFromContainer(item) as DocumentTab))
            .Where(t => t.doc is not null)
            .OrderBy(t => t.item.Bounds.X)
            .Select(t => (t.item, t.doc!));

    /// <summary>How far down the tab strip reaches (the rest of the group is the document).</summary>
    internal double TabStripHeight => TabItems().Select(t => t.Item.Bounds.Bottom).DefaultIfEmpty(26).Max();

    // ---- Tab context menu, middle click, zoom ----------------------------------------------------------------

    void OnTabContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is Control { DataContext: DocumentTab doc } anchor && Shell is { } shell)
        {
            e.Handled = true;
            shell.OpenDocumentTabMenu(anchor, doc);
        }
    }

    /// <summary>Middle-click closes a tab, like VS.</summary>
    void OnTabPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Middle && sender is Control { DataContext: DocumentTab doc })
        {
            Vm?.CloseDocumentCommand.Execute(doc);
            e.Handled = true;
        }
    }

    void OnZoomButtonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: DocumentViewModel doc } button)
            return;
        var menu = new MenuFlyout();
        foreach (var level in DocumentViewModel.ZoomLevels)
        {
            var item = new MenuItem { Header = $"{level} %" };
            item.Click += (_, _) => doc.Host.SetZoom(level / 100.0);
            menu.Items.Add(item);
        }
        menu.ShowAt(button);
    }

    // ---- Dragging tabs ---------------------------------------------------------------------------------------

    void OnTabPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || e.Source is not Visual source
            || source.FindAncestorOfType<Button>(includeSelf: true) is not null
            || source.FindAncestorOfType<TabItem>(includeSelf: true) is not { } item || Tabs.ItemFromContainer(item) is not DocumentTab doc)
            return;
        _pressed = item;
        _pressedDoc = doc;
        _pressStart = e.GetPosition(this);
        _dragging = false;
    }

    void OnTabMoved(object? sender, PointerEventArgs e)
    {
        if (_pressed is null || _pressedDoc is null || Shell is not { } shell || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        var position = e.GetPosition(this);
        if (!_dragging)
        {
            if (Math.Abs(position.X - _pressStart.X) < DragThreshold && Math.Abs(position.Y - _pressStart.Y) < DragThreshold)
                return;
            _dragging = true;
            e.Pointer.Capture(_pressed);
        }
        shell.DragDocument(_pressedDoc, this.PointToScreen(position));
        e.Handled = true;
    }

    void OnTabReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging || _pressedDoc is not { } doc || Shell is not { } shell)
        {
            _pressed = null;
            _pressedDoc = null;
            return;
        }
        var screen = this.PointToScreen(e.GetPosition(this));
        _dragging = false;
        _pressed = null;
        _pressedDoc = null;
        e.Pointer.Capture(null);
        shell.DropDocument(doc, screen);
        e.Handled = true;
    }

    void CancelDrag()
    {
        if (!_dragging)
            return;
        _dragging = false;
        _pressed = null;
        _pressedDoc = null;
        Shell?.EndDocumentDrag();
    }
}
