using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Karvon.Completions.ViewModels;

namespace Karvon.Completions.Views;

/// <summary>Inline Completions' settings over the window. Escape cancels them.</summary>
public partial class CompletionsOverlayView : UserControl
{
    TopLevel? _topLevel;

    public CompletionsOverlayView() => InitializeComponent();

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        _topLevel?.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _topLevel?.RemoveHandler(KeyDownEvent, OnKeyDown);
        _topLevel = null;
    }

    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is CompletionsOverlay { Dialog: { } dialog })
        {
            dialog.Cancel();
            e.Handled = true;
        }
    }
}
