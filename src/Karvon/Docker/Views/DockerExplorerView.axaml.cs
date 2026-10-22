using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Karvon.Docker.ViewModels;
using Karvon.Ui;

namespace Karvon.Docker.Views;

/// <summary>
/// The Docker Explorer tool window (see <see cref="DockerExplorerViewModel"/>): its tree, as the Database Explorer's.
/// Double-click or Enter opens a section's table or a container's, image's, volume's or network's page as a document.
/// </summary>
public partial class DockerExplorerView : UserControl
{
    DockerExplorerViewModel? _watching;

    public DockerExplorerView()
    {
        InitializeComponent();
        // The tree item toggles on a double-click and marks it handled; a section opens its table instead.
        Tree.AddHandler(DoubleTappedEvent, OnTreeDoubleTapped, RoutingStrategies.Bubble, handledEventsToo: true);
        Tree.AddHandler(KeyDownEvent, OnTreeKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateWatch();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        UpdateWatch();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        UpdateWatch();
    }

    /// <summary>The explorer reads stats while one of its views shows.</summary>
    void UpdateWatch()
    {
        var explorer = this.IsAttachedToVisualTree() ? DataContext as DockerExplorerViewModel : null;
        if (explorer == _watching)
            return;
        _watching?.Watch(-1);
        _watching = explorer;
        _watching?.Watch(+1);
    }

    void OnTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not DockerExplorerViewModel vm || e.Source is not Visual source || source.FindAncestorOfType<ToggleButton>(includeSelf: true) is not null
            || source.FindAncestorOfType<Button>(includeSelf: true) is not null)
            return;
        var item = source.FindAncestorOfType<TreeViewItem>(includeSelf: true);
        switch (item?.DataContext)
        {
            // A stack expands and collapses, as a folder in Solution Explorer.
            case StackRow:
                return;
            case DockerSectionNode section:
                if (e.Handled)
                    section.IsTreeExpanded = !section.IsTreeExpanded;
                vm.Open(section);
                break;
            case DockerTreeItem node:
                vm.Open(node);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    void OnTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is DockerExplorerViewModel vm && Tree.SelectedItem is DockerTreeItem item)
        {
            vm.Open(item);
            e.Handled = true;
        }
    }

    void OnCopy(object? sender, RoutedEventArgs e) => ExplorerViews.Copy(this, (sender as Control)?.Tag as string);
}
