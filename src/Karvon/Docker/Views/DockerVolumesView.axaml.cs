using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Karvon.Docker.ViewModels;

namespace Karvon.Docker.Views;

public partial class DockerVolumesView : UserControl
{
    public DockerVolumesView() => InitializeComponent();

    void OnCopy(object? sender, RoutedEventArgs e) => DockerViews.Copy(this, (sender as Control)?.Tag as string);

    void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is VolumeRow row && e.Source is not Button)
            row.OpenCommand.Execute(null);
    }
}
