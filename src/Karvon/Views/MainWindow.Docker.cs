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
        McpTab.PropertyChanged += (_, e) =>
        {
            if (e.Property == TabItem.IsSelectedProperty && McpTab.IsSelected)
                EnsureMcp();
        };
        SkillsTab.PropertyChanged += (_, e) =>
        {
            if (e.Property == TabItem.IsSelectedProperty && SkillsTab.IsSelected)
                EnsureSkills();
        };
    }

    /// <summary>The Skills tool window, made the first time it shows.</summary>
    internal Skills.Views.SkillsExplorerView? EnsureSkills()
    {
        if (SkillsHost.Content is Skills.Views.SkillsExplorerView view)
            return view;
        if (Vm is not { } vm || _closing)
            return null;
        view = new Skills.Views.SkillsExplorerView { DataContext = vm.EnsureSkills() };
        SkillsHost.Content = view;
        return view;
    }

    /// <summary>The MCP Servers tool window, made the first time it shows: a Karvon that never opens it reads no agent's settings.</summary>
    internal Mcp.Views.McpExplorerView? EnsureMcp()
    {
        if (McpHost.Content is Mcp.Views.McpExplorerView view)
            return view;
        if (Vm is not { } vm || _closing)
            return null;
        view = new Mcp.Views.McpExplorerView { DataContext = vm.EnsureMcp() };
        McpHost.Content = view;
        return view;
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
