using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Karvon.Postman.ViewModels;

namespace Karvon.Postman.Views;

/// <summary>The Postman tool window (see <see cref="PostmanExplorerViewModel"/>): a double click or Enter opens a request or an environment; F2 renames, Delete removes.</summary>
public partial class PostmanExplorerView : UserControl
{
    public PostmanExplorerView()
    {
        InitializeComponent();
        Tree.AddHandler(DoubleTappedEvent, OnTreeDoubleTapped, RoutingStrategies.Bubble, handledEventsToo: true);
        Tree.AddHandler(KeyDownEvent, OnTreeKeyDown, RoutingStrategies.Tunnel);
    }

    PostmanExplorerViewModel? Vm => DataContext as PostmanExplorerViewModel;

    void OnTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is not Visual source || source.FindAncestorOfType<ToggleButton>(includeSelf: true) is not null)
            return;
        if (source.FindAncestorOfType<TreeViewItem>(includeSelf: true)?.DataContext is PostmanNode { IsContainer: false, IsHeading: false } node)
        {
            Vm?.Open(node);
            e.Handled = true;
        }
    }

    void OnTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (Tree.SelectedItem is not PostmanNode node || Vm is not { } vm)
            return;
        switch (e.Key)
        {
            case Key.Enter:
                vm.Open(node);
                break;
            case Key.F2 when !node.IsHeading && !node.IsProblem:
                vm.RenameCommand.Execute(node);
                break;
            case Key.Delete when !node.IsHeading:
                vm.DeleteCommand.Execute(node);
                break;
            default:
                return;
        }
        e.Handled = true;
    }
}
