using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Karvon.Pipelines.ViewModels;

namespace Karvon.Pipelines.Views;

/// <summary>The Pipelines window's dialog, over the window. Escape cancels one; its first box gets the caret.</summary>
public partial class PipelinesOverlay : UserControl
{
    TopLevel? _topLevel;
    PipelinesViewModel? _vm;

    public PipelinesOverlay() => InitializeComponent();

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

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
            _vm.PropertyChanged -= OnVmChanged;
        _vm = DataContext as PipelinesViewModel;
        if (_vm is not null)
            _vm.PropertyChanged += OnVmChanged;
    }

    void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PipelinesViewModel.Dialog) && _vm?.Dialog is not null)
            Dispatcher.UIThread.Post(() => this.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.IsEffectivelyVisible && t.IsEnabled)?.Focus(),
                DispatcherPriority.Loaded);
    }

    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is PipelinesViewModel { Dialog: { } dialog })
        {
            dialog.Cancel();
            e.Handled = true;
        }
    }
}
