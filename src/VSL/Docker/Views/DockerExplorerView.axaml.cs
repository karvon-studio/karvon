using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using VSL.Docker.ViewModels;

namespace VSL.Docker.Views;

/// <summary>The Docker Explorer document (see <see cref="DockerExplorerViewModel"/>). Escape closes a dialog, then a page.</summary>
public partial class DockerExplorerView : UserControl
{
    public DockerExplorerView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || DataContext is not DockerExplorerViewModel vm)
            return;
        if (vm.Dialog is { } dialog)
            dialog.Cancel();
        else if (vm.HasDetail && e.Source is not TextBox)
            vm.BackCommand.Execute(null);
        else
            return;
        e.Handled = true;
    }
}
