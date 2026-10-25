using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Karvon.PullRequests.ViewModels;

namespace Karvon.PullRequests.Views;

/// <summary>The Pull Requests tool window (see <see cref="PullRequestsViewModel"/>): a double click or Enter opens one's page.</summary>
public partial class PullRequestsView : UserControl
{
    public PullRequestsView() => InitializeComponent();

    PullRequestsViewModel? Vm => DataContext as PullRequestsViewModel;

    void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Control source && source.FindAncestorOfType<Button>(includeSelf: true) is null && source.DataContext is PullRequestRow row)
            Vm?.Open(row);
    }

    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && PullRequestList.SelectedItem is PullRequestRow row)
        {
            Vm?.Open(row);
            e.Handled = true;
        }
    }
}
