using Avalonia.Controls;
using Avalonia.Interactivity;
using Karvon.Ports.ViewModels;
using Karvon.Ui;

namespace Karvon.Ports.Views;

/// <summary>The Ports tool window (see <see cref="PortsViewModel"/>).</summary>
public partial class PortsView : UserControl
{
    public PortsView() => InitializeComponent();

    void OnCopy(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is PortForward { Address: { } address } && DataContext is PortsViewModel vm)
        {
            ExplorerViews.Copy(this, address);
            vm.Host.Status($"Copied {address}.");
        }
    }
}
