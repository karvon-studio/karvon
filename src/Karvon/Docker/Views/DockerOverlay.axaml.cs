using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Karvon.Docker.ViewModels;

namespace Karvon.Docker.Views;

/// <summary>The Docker Explorer's dialogs (confirmations, Run, Create) and error toast, over the window. Escape cancels a dialog.</summary>
public partial class DockerOverlay : UserControl
{
    TopLevel? _topLevel;

    public DockerOverlay() => InitializeComponent();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        _topLevel?.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _topLevel?.RemoveHandler(KeyDownEvent, OnKeyDown);
        _topLevel = null;
    }

    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is DockerExplorerViewModel { Dialog: { } dialog })
        {
            dialog.Cancel();
            e.Handled = true;
        }
    }
}
