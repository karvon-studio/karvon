using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Karvon.Docker.ViewModels;

namespace Karvon.Docker.Views;

/// <summary>A Docker Explorer document (see <see cref="DockerDocument"/>): a section's table or a page, while the engine runs.</summary>
public partial class DockerDocumentView : UserControl
{
    DockerExplorerViewModel? _watching;

    public DockerDocumentView() => InitializeComponent();

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

    /// <summary>The explorer reads stats while one of its views shows (the tables' CPU, a container's charts).</summary>
    void UpdateWatch()
    {
        var explorer = this.IsAttachedToVisualTree() ? (DataContext as DockerDocument)?.Explorer : null;
        if (explorer == _watching)
            return;
        _watching?.Watch(-1);
        _watching = explorer;
        _watching?.Watch(+1);
    }
}
