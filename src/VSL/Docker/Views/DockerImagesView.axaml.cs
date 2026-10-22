using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using VSL.Docker.ViewModels;

namespace VSL.Docker.Views;

public partial class DockerImagesView : UserControl
{
    public DockerImagesView() => InitializeComponent();

    void OnCopy(object? sender, RoutedEventArgs e) => DockerViews.Copy(this, (sender as Control)?.Tag as string);

    void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ImageRow row && e.Source is not Button)
            row.OpenCommand.Execute(null);
    }

    void OnPullKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is ImagesPage page && page.PullCommand.CanExecute(null))
        {
            page.PullCommand.Execute(null);
            e.Handled = true;
        }
    }
}
