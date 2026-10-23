using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Karvon.ViewModels;

namespace Karvon.Views;

/// <summary>
/// The document well as in Visual Studio: tab groups side by side or one above the other, floating document windows,
/// and document tabs dragged to reorder them, onto another group, to a group's edge for a new group, or out of the
/// window to float.
/// </summary>
public partial class MainWindow
{
    readonly Dictionary<DocumentGroup, DocumentGroupView> _groupViews = [];
    readonly Dictionary<DocumentGroup, DocumentWindow> _floatingWindows = [];
    MainViewModel? _wellVm;
    PixelPoint? _nextFloatAt;
    Size? _nextFloatSize;

    void AttachDocumentWell(MainViewModel vm)
    {
        if (_wellVm is { } old)
        {
            old.DocumentGroups.CollectionChanged -= OnDocumentGroupsChanged;
            old.FloatingGroups.CollectionChanged -= OnFloatingGroupsChanged;
            old.PropertyChanged -= OnWellPropertyChanged;
        }
        _wellVm = vm;
        vm.DocumentGroups.CollectionChanged += OnDocumentGroupsChanged;
        vm.FloatingGroups.CollectionChanged += OnFloatingGroupsChanged;
        vm.PropertyChanged += OnWellPropertyChanged;
        LayoutDocumentGroups();
        foreach (var group in vm.FloatingGroups)
            OpenFloatingWindow(group);
    }

    void OnWellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.DocumentGroupsVertical))
            LayoutDocumentGroups();
    }

    void OnDocumentGroupsChanged(object? sender, NotifyCollectionChangedEventArgs e) => LayoutDocumentGroups();

    /// <summary>The docked groups in a row or a column, with a splitter between each two.</summary>
    void LayoutDocumentGroups()
    {
        if (_wellVm is not { } vm)
            return;
        foreach (var gone in _groupViews.Keys.Where(g => !vm.DocumentGroups.Contains(g)).ToList())
            _groupViews.Remove(gone);
        DocumentWell.Children.Clear();
        DocumentWell.ColumnDefinitions.Clear();
        DocumentWell.RowDefinitions.Clear();
        var vertical = vm.DocumentGroupsVertical;
        for (var i = 0; i < vm.DocumentGroups.Count; i++)
        {
            var group = vm.DocumentGroups[i];
            if (i > 0)
            {
                if (vertical)
                    DocumentWell.ColumnDefinitions.Add(new ColumnDefinition(4, GridUnitType.Pixel));
                else
                    DocumentWell.RowDefinitions.Add(new RowDefinition(4, GridUnitType.Pixel));
                var splitter = new GridSplitter { ResizeDirection = vertical ? GridResizeDirection.Columns : GridResizeDirection.Rows, Background = Avalonia.Media.Brushes.Transparent };
                Place(splitter, 2 * i - 1);
                DocumentWell.Children.Add(splitter);
            }
            if (vertical)
                DocumentWell.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
            else
                DocumentWell.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star));
            if (!_groupViews.TryGetValue(group, out var view))
                _groupViews[group] = view = new DocumentGroupView { DataContext = group };
            Place(view, 2 * i);
            DocumentWell.Children.Add(view);
        }

        void Place(Control control, int index)
        {
            Grid.SetColumn(control, vertical ? index : 0);
            Grid.SetRow(control, vertical ? 0 : index);
        }
    }

    // ---- Floating windows -----------------------------------------------------------------------------------

    void OnFloatingGroupsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_wellVm is not { } vm)
            return;
        foreach (var (group, window) in _floatingWindows.Where(f => !vm.FloatingGroups.Contains(f.Key)).ToList())
        {
            _floatingWindows.Remove(group);
            window.CloseForGroupRemoved();
        }
        foreach (var group in vm.FloatingGroups.Where(g => !_floatingWindows.ContainsKey(g)).ToList())
            OpenFloatingWindow(group);
    }

    void OpenFloatingWindow(DocumentGroup group)
    {
        var window = new DocumentWindow(this, group);
        if (_nextFloatSize is { } size)
            (window.Width, window.Height) = (Math.Max(size.Width, 400), Math.Max(size.Height, 300));
        // Where the tab was dropped, else a little in from the main window's corner.
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Position = _nextFloatAt ?? new PixelPoint(Position.X + 120, Position.Y + 120);
        _nextFloatAt = null;
        _nextFloatSize = null;
        _floatingWindows[group] = window;
        window.Show(this);
    }

    /// <summary>The main window's shortcuts (Ctrl+S, F5, Ctrl+Shift+B...) in a floating document window.</summary>
    internal void HandleKeyFromFloatingWindow(KeyEventArgs e)
    {
        foreach (var binding in KeyBindings)
        {
            if (binding.Gesture?.Matches(e) == true && binding.Command?.CanExecute(binding.CommandParameter) == true)
            {
                binding.Command.Execute(binding.CommandParameter);
                e.Handled = true;
                return;
            }
        }
        OnWindowKeyDown(this, e);
    }

    /// <summary>Every group view: those of floating windows first (they are above the main window).</summary>
    IEnumerable<(DocumentGroupView View, Canvas Layer, Border Preview)> GroupViews() =>
        _floatingWindows.Values.Where(w => w.IsVisible).Select(w => (w.View, w.Drop, w.Preview))
            .Concat(_groupViews.Values.Where(v => v.IsEffectivelyVisible).Select(v => (v, DocumentDropLayer, DocumentDropPreview)));

    // ---- Window menu ----------------------------------------------------------------------------------------

    /// <summary>Window menu items are enabled as Visual Studio enables them, for the active document.</summary>
    void UpdateWindowMenu()
    {
        if (Vm is not { } vm)
            return;
        var doc = vm.ActiveDocument;
        var group = doc is null ? null : vm.GroupOf(doc);
        SplitMenuItem.IsEnabled = vm.ActiveEditor is not null;
        SplitMenuItem.Header = vm.ActiveEditor?.Host.IsSplit == true ? "Remove Spl_it" : "Spl_it";
        FloatMenuItem.IsEnabled = doc is not null && group is not { IsFloating: true, Tabs.Count: 1 };
        DockMenuItem.IsEnabled = group is { IsFloating: true };
        PinMenuItem.IsEnabled = doc is not null;
        PinMenuItem.Header = doc?.IsPinned == true ? "Unpin _Tab" : "Pin _Tab";
        NewHorizontalGroupMenuItem.IsEnabled = vm.CanCreateTabGroup(doc, vertical: false);
        NewVerticalGroupMenuItem.IsEnabled = vm.CanCreateTabGroup(doc, vertical: true);
        NextGroupMenuItem.IsEnabled = vm.CanMoveToTabGroup(doc, 1);
        PreviousGroupMenuItem.IsEnabled = vm.CanMoveToTabGroup(doc, -1);
    }

    /// <summary>Window > Split: the active document in two panes, one above the other (again to remove it).</summary>
    void OnSplitWindow(object? sender, RoutedEventArgs e) => ActiveHost?.ToggleSplit();

    // ---- Tab context menu -----------------------------------------------------------------------------------

    internal void OpenDocumentTabMenu(Control anchor, DocumentTab doc)
    {
        if (Vm is not { } vm)
            return;
        MenuItem Item(string header, Action action, string? gesture = null, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            if (gesture is not null)
                item.InputGesture = PlatformKeys.Parse(gesture);
            item.Click += (_, _) => action();
            return item;
        }
        var group = vm.GroupOf(doc);
        var tabs = group?.Tabs.ToList() ?? vm.Documents.ToList();
        var index = tabs.IndexOf(doc);
        var menu = new ContextMenu
        {
            ItemsSource = new object[]
            {
                Item($"Save {doc.Title.TrimEnd('*')}", () => vm.SaveDocument((DocumentViewModel)doc), "Ctrl+S", doc is DocumentViewModel { IsDirty: true }),
                new Separator(),
                Item("Close", () => vm.CloseDocumentCommand.Execute(doc), "Ctrl+F4"),
                Item("Close All But This", () => vm.CloseAllButThisCommand.Execute(doc), enabled: vm.Documents.Count > 1),
                Item("Close All But Pinned", () => vm.CloseAllButPinnedCommand.Execute(null), enabled: vm.Documents.Any(d => !d.IsPinned)),
                Item("Close All Documents", () => vm.CloseAllDocumentsCommand.Execute(null)),
                Item("Close All Tabs to the Left", () => vm.CloseTabsToLeftCommand.Execute(doc), enabled: index > 0),
                Item("Close All Tabs to the Right", () => vm.CloseTabsToRightCommand.Execute(doc), enabled: index < tabs.Count - 1),
                new Separator(),
                Item(doc.IsPinned ? "Unpin Tab" : "Pin Tab", () => vm.TogglePinCommand.Execute(doc)),
                new Separator(),
                Item("Copy Full Path", () => _ = Clipboard?.SetTextAsync((doc as DocumentViewModel)?.FilePath ?? doc.Key), enabled: doc is DocumentViewModel),
                Item("Open Containing Folder", () => OpenFolder(Path.GetDirectoryName(((DocumentViewModel)doc).FilePath)!), enabled: doc is DocumentViewModel),
                new Separator(),
                Item("Float", () => vm.FloatDocument(doc), enabled: group is not { IsFloating: true, Tabs.Count: 1 }),
                Item("Dock as Tabbed Document", () => vm.DockDocument(doc), enabled: group is { IsFloating: true }),
                Item("New Horizontal Tab Group", () => vm.NewTabGroup(doc, vertical: false), enabled: vm.CanCreateTabGroup(doc, vertical: false)),
                Item("New Vertical Tab Group", () => vm.NewTabGroup(doc, vertical: true), enabled: vm.CanCreateTabGroup(doc, vertical: true)),
                Item("Move to Next Tab Group", () => vm.MoveToNextTabGroupCommand.Execute(doc), enabled: vm.CanMoveToTabGroup(doc, 1)),
                Item("Move to Previous Tab Group", () => vm.MoveToPreviousTabGroupCommand.Execute(doc), enabled: vm.CanMoveToTabGroup(doc, -1)),
                new Separator(),
                Item("Select in Solution Explorer", () =>
                {
                    vm.ActiveDocument = doc;
                    vm.ShowToolWindow(ToolWindows.SolutionExplorer);
                    OnSyncWithActiveDocument(this, new RoutedEventArgs());
                }, enabled: vm.Solution is not null && doc is DocumentViewModel),
            },
        };
        menu.Open(anchor);
    }

    // ---- Dragging document tabs -----------------------------------------------------------------------------

    enum DropKind
    {
        /// <summary>Into a group's tabs at a position (the end when dropped on the document).</summary>
        Insert,

        /// <summary>A new group before or after the group it was dropped on (its left/top or right/bottom edge).</summary>
        NewGroup,

        /// <summary>Outside every group: a floating window there.</summary>
        Float,
    }

    sealed record DocumentDrop(DropKind Kind, DocumentGroup? Group = null, int Index = 0, bool Vertical = false, bool After = false);

    internal void DragDocument(DocumentTab doc, PixelPoint screen) => ShowDrop(FindDrop(doc, screen, out var preview), preview);

    internal void DropDocument(DocumentTab doc, PixelPoint screen)
    {
        var drop = FindDrop(doc, screen, out _);
        EndDocumentDrag();
        if (Vm is not { } vm)
            return;
        switch (drop.Kind)
        {
            case DropKind.Insert when drop.Group is { } group:
                vm.MoveToGroup(doc, group, drop.Index);
                break;
            case DropKind.NewGroup when drop.Group is { } group:
                vm.NewTabGroupBeside(doc, group, drop.Vertical, drop.After);
                break;
            case DropKind.Float:
                if (vm.GroupOf(doc) is { IsFloating: true, Tabs.Count: 1 } alone && _floatingWindows.TryGetValue(alone, out var window))
                {
                    // The only tab of a floating window moves the window.
                    window.Position = new PixelPoint(screen.X - 60, screen.Y - 12);
                    break;
                }
                _nextFloatAt = new PixelPoint(screen.X - 60, screen.Y - 12);
                _nextFloatSize = vm.GroupOf(doc) is { } source && _groupViews.TryGetValue(source, out var sourceView) ? sourceView.Bounds.Size : null;
                vm.FloatDocument(doc);
                _nextFloatAt = null;
                _nextFloatSize = null;
                break;
        }
    }

    internal void EndDocumentDrag()
    {
        foreach (var (_, _, preview) in GroupViews())
            preview.IsVisible = false;
        DocumentDropPreview.IsVisible = false;
    }

    /// <summary>Where a tab dropped at this point of the screen goes, and the rectangle that shows it (in its layer).</summary>
    DocumentDrop FindDrop(DocumentTab doc, PixelPoint screen, out (Border Preview, Canvas Layer, Rect Rect)? preview)
    {
        preview = null;
        if (Vm is not { } vm)
            return new DocumentDrop(DropKind.Float);
        foreach (var (view, layer, border) in GroupViews())
        {
            if (view.Group is not { } group)
                continue;
            var point = view.PointToClient(screen);
            var size = view.Bounds.Size;
            if (point.X < 0 || point.Y < 0 || point.X > size.Width || point.Y > size.Height)
                continue;
            Rect InLayer(Rect r) => view.TranslatePoint(r.TopLeft, layer) is { } at ? new Rect(at, r.Size) : r;
            var strip = view.TabStripHeight;
            if (point.Y <= strip)
            {
                // Between two tabs: a bar where the tab would go.
                var others = view.TabItems().Where(t => t.Doc != doc).ToList();
                var index = others.Count(t => t.Item.Bounds.Center.X < point.X);
                var x = index < others.Count ? others[index].Item.Bounds.X : others.Count > 0 ? others[^1].Item.Bounds.Right : 0;
                preview = (border, layer, InLayer(new Rect(x - 1, 0, 3, strip)));
                return new DocumentDrop(DropKind.Insert, group, index);
            }
            var content = new Rect(0, strip, size.Width, size.Height - strip);
            var rx = point.X / size.Width;
            var ry = (point.Y - strip) / Math.Max(content.Height, 1);
            var single = vm.DocumentGroups.Count == 1;
            var canVertical = !group.IsFloating && (single || vm.DocumentGroupsVertical);
            var canHorizontal = !group.IsFloating && (single || !vm.DocumentGroupsVertical);
            // Its own only tab beside its own group would leave the layout as it is.
            var alone = group.Tabs.Count == 1 && group.Tabs[0] == doc;
            if (!alone && canVertical && (rx < 0.25 || rx > 0.75))
            {
                var after = rx > 0.75;
                preview = (border, layer, InLayer(new Rect(after ? content.Width / 2 : 0, content.Y, content.Width / 2, content.Height)));
                return new DocumentDrop(DropKind.NewGroup, group, Vertical: true, After: after);
            }
            if (!alone && canHorizontal && (ry < 0.25 || ry > 0.75))
            {
                var after = ry > 0.75;
                preview = (border, layer, InLayer(new Rect(0, after ? content.Y + content.Height / 2 : content.Y, content.Width, content.Height / 2)));
                return new DocumentDrop(DropKind.NewGroup, group, Vertical: false, After: after);
            }
            preview = (border, layer, InLayer(content));
            return new DocumentDrop(DropKind.Insert, group, int.MaxValue);
        }
        return new DocumentDrop(DropKind.Float);
    }

    void ShowDrop(DocumentDrop drop, (Border Preview, Canvas Layer, Rect Rect)? preview)
    {
        foreach (var (_, _, border) in GroupViews())
            border.IsVisible = false;
        if (preview is not { } p)
            return;
        Canvas.SetLeft(p.Preview, p.Rect.X);
        Canvas.SetTop(p.Preview, p.Rect.Y);
        p.Preview.Width = p.Rect.Width;
        p.Preview.Height = p.Rect.Height;
        p.Preview.IsVisible = true;
    }
}
