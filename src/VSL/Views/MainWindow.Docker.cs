using Avalonia.Controls;
using VSL.Docker.Views;

namespace VSL.Views;

/// <summary>
/// The Docker Explorer tool window. Its explorer is made (and asks the engine for everything) the first time it
/// shows, as the Database Explorer: a VSL that never opens it runs no docker command.
/// </summary>
public partial class MainWindow
{
    void InitializeDockerExplorer() =>
        DockerExplorerTab.PropertyChanged += (_, e) =>
        {
            if (e.Property == TabItem.IsSelectedProperty && DockerExplorerTab.IsSelected)
                EnsureDockerExplorer();
        };

    internal DockerExplorerView? EnsureDockerExplorer()
    {
        if (DockerExplorerHost.Content is DockerExplorerView view)
            return view;
        if (Vm is not { } vm || _closing)
            return null;
        view = new DockerExplorerView { DataContext = vm.EnsureDocker() };
        DockerExplorerHost.Content = view;
        return view;
    }
}
