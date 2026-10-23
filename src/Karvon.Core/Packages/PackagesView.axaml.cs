using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Karvon.Packages;

public partial class PackagesView : UserControl
{
    public PackagesView() => InitializeComponent();

    PackagesViewModel? Vm => DataContext as PackagesViewModel;

    void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Vm?.SearchCommand.Execute(null);
            e.Handled = true;
        }
    }

    void OnProjectUrlClick(object? sender, RoutedEventArgs e)
    {
        if (Vm?.SelectedPackage?.ProjectUrl is { } url)
            Services.DesktopShell.Open(url);
    }
}
