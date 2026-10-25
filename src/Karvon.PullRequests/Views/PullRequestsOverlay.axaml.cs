using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Karvon.PullRequests.ViewModels;

namespace Karvon.PullRequests.Views;

/// <summary>The Pull Requests window's dialog, over the window. Escape cancels one; its first box (a new one's title) gets the caret.</summary>
public partial class PullRequestsOverlay : UserControl
{
    TopLevel? _topLevel;
    PullRequestsViewModel? _vm;

    public PullRequestsOverlay() => InitializeComponent();

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
        _vm = DataContext as PullRequestsViewModel;
        if (_vm is not null)
            _vm.PropertyChanged += OnVmChanged;
    }

    void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PullRequestsViewModel.Dialog) && _vm?.Dialog is not null)
            Dispatcher.UIThread.Post(() => this.GetVisualDescendants().OfType<TextBox>().Where(t => t.IsEffectivelyVisible && t.IsEnabled && !t.IsReadOnly)
                .OrderBy(t => t.Name == "TitleBox" ? 0 : 1).FirstOrDefault()?.Focus(),
                DispatcherPriority.Loaded);
    }

    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is PullRequestsViewModel { Dialog: { } dialog })
        {
            dialog.Cancel();
            e.Handled = true;
        }
    }
}
