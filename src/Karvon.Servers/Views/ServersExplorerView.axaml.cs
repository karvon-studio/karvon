using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Karvon.Servers.ViewModels;
using Karvon.Ui;

namespace Karvon.Servers.Views;

/// <summary>The Servers tool window (see <see cref="ServersExplorerViewModel"/>): double-click or Enter opens a machine's page.</summary>
public partial class ServersExplorerView : UserControl
{
    public ServersExplorerView()
    {
        InitializeComponent();
        Tree.AddHandler(DoubleTappedEvent, OnTreeDoubleTapped, RoutingStrategies.Bubble, handledEventsToo: true);
        Tree.AddHandler(KeyDownEvent, OnTreeKeyDown, RoutingStrategies.Tunnel);
    }

    void OnTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is not Visual source || source.FindAncestorOfType<ToggleButton>(includeSelf: true) is not null
            || source.FindAncestorOfType<Button>(includeSelf: true) is not null)
            return;
        if (source.FindAncestorOfType<TreeViewItem>(includeSelf: true)?.DataContext is ServerNode node)
        {
            node.OpenCommand.Execute(null);
            e.Handled = true;
        }
    }

    void OnTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Tree.SelectedItem is ServerNode node)
        {
            node.OpenCommand.Execute(null);
            e.Handled = true;
        }
    }

    void OnCopy(object? sender, RoutedEventArgs e) => ExplorerViews.Copy(this, (sender as Control)?.Tag as string);
}
