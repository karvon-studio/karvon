using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Karvon.Docker.ViewModels;
using Karvon.Ui;

namespace Karvon.Docker.Views;

public partial class DockerContainersView : UserControl
{
    public DockerContainersView() => InitializeComponent();

    void OnCopy(object? sender, RoutedEventArgs e) => ExplorerViews.Copy(this, (sender as Control)?.Tag as string);

    /// <summary>Double-clicking a container opens its page, as in Docker Desktop.</summary>
    void OnContainerDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ContainerRow row && e.Source is not Button)
            row.OpenCommand.Execute(null);
    }

    void OnStackDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is StackRow stack && e.Source is not Button)
            stack.ToggleCommand.Execute(null);
    }
}
