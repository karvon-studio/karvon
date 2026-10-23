using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Karvon.ViewModels;

namespace Karvon.Views;

public partial class NuGetView : UserControl
{
    public NuGetView() => InitializeComponent();

    NuGetViewModel? Vm => DataContext as NuGetViewModel;

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
