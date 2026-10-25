using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Karvon.Http.ViewModels;

namespace Karvon.Http.Views;

/// <summary>The Endpoints Explorer (see <see cref="EndpointsViewModel"/>): an endpoint double-clicked opens where it is written.</summary>
public partial class EndpointsView : UserControl
{
    public EndpointsView() => InitializeComponent();

    void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Control source && source.FindAncestorOfType<Button>(includeSelf: true) is null && Tree.SelectedItem is EndpointRow row)
            (DataContext as EndpointsViewModel)?.OpenCommand.Execute(row);
    }
}
