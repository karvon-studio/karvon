using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Karvon.ViewModels;

namespace Karvon.Docker.ViewModels;

/// <summary>A node of the Docker Explorer's tree: a section, a compose stack, a container, an image, a volume or a network.</summary>
public abstract partial class DockerTreeItem : ObservableObject
{
    [ObservableProperty]
    public partial bool IsTreeExpanded { get; set; }
}

/// <summary>Containers, Images, Volumes or Networks in the tree: what is there under it, and its table as a document.</summary>
public sealed partial class DockerSectionNode : DockerTreeItem
{
    internal DockerSectionNode(DockerExplorerViewModel explorer, DockerSection section, string icon)
    {
        Explorer = explorer;
        Section = section;
        Icon = icon;
    }

    public DockerExplorerViewModel Explorer { get; }

    public DockerSection Section { get; }

    public string Title => Section.ToString();

    public string Icon { get; }

    public ObservableCollection<DockerTreeItem> Items { get; } = [];

    [ObservableProperty]
    public partial int Count { get; set; }

    /// <summary>Containers, with the solution's compose file: Compose Up and Down.</summary>
    [ObservableProperty]
    public partial bool HasCompose { get; set; }

    public bool IsContainers => Section == DockerSection.Containers;

    public bool IsImages => Section == DockerSection.Images;

    public bool IsVolumes => Section == DockerSection.Volumes;

    public bool IsNetworks => Section == DockerSection.Networks;

    [RelayCommand]
    void Open() => Explorer.OpenSection(Section);
}

/// <summary>
/// A Docker Explorer document in Karvon's document well: a section's table (Docker Desktop's Containers, Images,
/// Volumes, Networks) or a container's, image's, volume's or network's page. The explorer keeps the tables; a page is
/// the document's own and goes with it.
/// </summary>
public sealed class DockerDocument : DocumentTab
{
    internal DockerDocument(DockerExplorerViewModel explorer, string key, string title, string toolTip, object content)
    {
        Explorer = explorer;
        Key = key;
        Title = title;
        ToolTip = toolTip;
        Content = content;
    }

    public DockerExplorerViewModel Explorer { get; }

    /// <summary>A page (<see cref="ContainersPage"/>, ...) or a detail (<see cref="ContainerDetailViewModel"/>, ...).</summary>
    public object Content { get; }

    public override string Key { get; }

    public override string Title { get; }

    public override string ToolTip { get; }

    public override void Dispose() => (Content as IDisposable)?.Dispose();
}
