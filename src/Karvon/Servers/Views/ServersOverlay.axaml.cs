using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Karvon.Servers.ViewModels;

namespace Karvon.Servers.Views;

/// <summary>The Servers explorer's dialogs and errors, over the window. Escape cancels a dialog; a password box gets the caret.</summary>
public partial class ServersOverlay : UserControl
{
    TopLevel? _topLevel;
    ServersExplorerViewModel? _vm;

    public ServersOverlay() => InitializeComponent();

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
        _vm = DataContext as ServersExplorerViewModel;
        if (_vm is not null)
            _vm.PropertyChanged += OnVmChanged;
    }

    void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ServersExplorerViewModel.Dialog) && _vm?.Dialog is not null)
            Dispatcher.UIThread.Post(() => this.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.IsEffectivelyVisible && t.IsEnabled)?.Focus(),
                DispatcherPriority.Loaded);
    }

    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is ServersExplorerViewModel { Dialog: { } dialog })
        {
            dialog.Cancel();
            e.Handled = true;
        }
    }
}
