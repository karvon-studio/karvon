using Avalonia.Controls;
using Avalonia.Input;
using Karvon.Docker.ViewModels;

namespace Karvon.Docker.Views;

public partial class DockerNetworksView : UserControl
{
    public DockerNetworksView() => InitializeComponent();

    void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is NetworkRow row && e.Source is not Button)
            row.OpenCommand.Execute(null);
    }
}
