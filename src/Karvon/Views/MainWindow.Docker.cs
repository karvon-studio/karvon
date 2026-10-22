using Avalonia.Controls;
using Karvon.Docker.Views;

namespace Karvon.Views;

/// <summary>
/// The Docker Explorer tool window. Its explorer is made (and asks the engine for everything) the first time it
/// shows, as the Database Explorer: a Karvon that never opens it runs no docker command.
/// </summary>
public partial class MainWindow
{
    void InitializeDockerExplorer()
    {
        DockerExplorerTab.PropertyChanged += (_, e) =>
        {
            if (e.Property == TabItem.IsSelectedProperty && DockerExplorerTab.IsSelected)
                EnsureDockerExplorer();
        };
        ServersTab.PropertyChanged += (_, e) =>
        {
            if (e.Property == TabItem.IsSelectedProperty && ServersTab.IsSelected)
                EnsureServers();
        };
    }

    /// <summary>The Servers tool window, made the first time it shows: a Karvon that never opens it reads no ~/.ssh/config.</summary>
    internal Servers.Views.ServersExplorerView? EnsureServers()
    {
        if (ServersHost.Content is Servers.Views.ServersExplorerView view)
            return view;
        if (Vm is not { } vm || _closing)
            return null;
        view = new Servers.Views.ServersExplorerView { DataContext = vm.EnsureServers() };
        ServersHost.Content = view;
        return view;
    }

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
