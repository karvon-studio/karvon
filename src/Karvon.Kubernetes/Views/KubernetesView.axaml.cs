using Avalonia.Controls;
using Avalonia.Input;
using Karvon.Kubernetes.ViewModels;

namespace Karvon.Kubernetes.Views;

/// <summary>The Kubernetes tool window (see <see cref="KubernetesViewModel"/>): an object double-clicked (or Enter) opens its page.</summary>
public partial class KubernetesView : UserControl
{
    public KubernetesView() => InitializeComponent();

    KubernetesViewModel? Vm => DataContext as KubernetesViewModel;

    void OnTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Tree.SelectedItem is KubeRow row)
            Vm?.Open(row);
    }

    void OnTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Tree.SelectedItem is KubeRow row)
        {
            Vm?.Open(row);
            e.Handled = true;
        }
    }
}
