using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Karvon.CodeMap.ViewModels;

namespace Karvon.CodeMap.Views;

/// <summary>
/// The Code Map tool window (see <see cref="CodeMapViewModel"/>): double-click or Enter opens a row's file at its line; Down
/// in the search box goes to the hits, Enter there opens the first.
/// </summary>
public partial class CodeMapView : UserControl
{
    public CodeMapView()
    {
        InitializeComponent();
        Tree.AddHandler(DoubleTappedEvent, OnTreeDoubleTapped, RoutingStrategies.Bubble, handledEventsToo: true);
        Tree.AddHandler(KeyDownEvent, OnTreeKeyDown, RoutingStrategies.Tunnel);
        ResultList.AddHandler(DoubleTappedEvent, (_, e) => e.Handled = OpenHit(ResultList.SelectedItem), RoutingStrategies.Bubble, handledEventsToo: true);
        ResultList.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter)
                e.Handled = OpenHit(ResultList.SelectedItem);
        }, RoutingStrategies.Tunnel);
        SearchBox.AddHandler(KeyDownEvent, OnSearchKeyDown, RoutingStrategies.Tunnel);
    }

    CodeMapViewModel? Vm => DataContext as CodeMapViewModel;

    void OnTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is not Visual source || source.FindAncestorOfType<ToggleButton>(includeSelf: true) is not null)
            return;
        if (source.FindAncestorOfType<TreeViewItem>(includeSelf: true)?.DataContext is CodeMapNode { Line: > 0 } node)
        {
            Vm?.Open(node.Path, node.Line);
            e.Handled = true;
        }
    }

    void OnTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Tree.SelectedItem is CodeMapNode { Line: > 0 } node)
        {
            Vm?.Open(node.Path, node.Line);
            e.Handled = true;
        }
    }

    void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down when Vm?.Results.Count > 0:
                ResultList.SelectedIndex = 0;
                (ResultList.ContainerFromIndex(0) as Control)?.Focus();
                e.Handled = true;
                break;
            case Key.Enter when Vm?.Results.Count > 0:
                e.Handled = OpenHit(Vm.Results[0]);
                break;
            case Key.Escape when Vm is { IsSearching: true } vm:
                vm.Query = "";
                e.Handled = true;
                break;
        }
    }

    bool OpenHit(object? item)
    {
        if (item is not CodeMapHitItem hit || Vm is not { } vm)
            return false;
        if (hit.Line > 0)
            vm.Open(hit.Path, hit.Line);
        return hit.Line > 0;
    }
}
