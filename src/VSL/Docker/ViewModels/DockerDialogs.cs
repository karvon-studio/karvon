using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace VSL.Docker.ViewModels;

/// <summary>A dialog over the Docker Explorer's page; its <see cref="Result"/> is true when confirmed.</summary>
public abstract partial class DockerDialog : ObservableObject
{
    readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected DockerDialog(string title, string confirmText)
    {
        Title = title;
        ConfirmText = confirmText;
    }

    public string Title { get; }

    public string ConfirmText { get; }

    public Task<bool> Result => _result.Task;

    [RelayCommand]
    public void Confirm()
    {
        if (CanConfirm)
            _result.TrySetResult(true);
    }

    [RelayCommand]
    public void Cancel() => _result.TrySetResult(false);

    public virtual bool CanConfirm => true;
}

/// <summary>"Delete web-1?": what happens, and the button that does it (red when it cannot be undone).</summary>
public sealed class ConfirmDialog(string title, string message, string confirmText, bool isDanger) : DockerDialog(title, confirmText)
{
    public string Message { get; } = message;

    public bool IsDanger { get; } = isDanger;
}

/// <summary>A name (a new volume, a new network), and a choice when there is one (the network's driver).</summary>
public sealed partial class InputDialog(string title, string label, string placeholder, string confirmText, IReadOnlyList<string>? choices = null)
    : DockerDialog(title, confirmText)
{
    public string Label { get; } = label;

    public string Placeholder { get; } = placeholder;

    public IReadOnlyList<string> Choices { get; } = choices ?? [];

    public bool HasChoices => Choices.Count > 0;

    [ObservableProperty]
    public partial string Text { get; set; } = "";

    [ObservableProperty]
    public partial string? Choice { get; set; } = choices?.FirstOrDefault();

    public override bool CanConfirm => Text.Trim().Length > 0;
}

/// <summary>A port to publish: the host's port for one of the container's.</summary>
public sealed partial class PortMapping : ObservableObject
{
    [ObservableProperty]
    public partial string Host { get; set; } = "";

    [ObservableProperty]
    public partial string Container { get; set; } = "";

    /// <summary>Exposed by the image: its container port is given.</summary>
    public bool IsExposed { get; init; }
}

/// <summary>A host folder (or a volume's name) mounted at a path in the container.</summary>
public sealed partial class VolumeMapping : ObservableObject
{
    [ObservableProperty]
    public partial string Host { get; set; } = "";

    [ObservableProperty]
    public partial string Container { get; set; } = "";
}

public sealed partial class EnvironmentVariable : ObservableObject
{
    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial string Value { get; set; } = "";
}

/// <summary>
/// Docker Desktop's "Run a new container": optional settings for a name, the host ports of the image's exposed
/// ports (empty is not published), volumes and environment variables.
/// </summary>
public sealed partial class RunDialog : DockerDialog
{
    public RunDialog(string image, IEnumerable<string> exposedPorts) : base($"Run a new container", "Run")
    {
        Image = image;
        foreach (var port in exposedPorts)
            Ports.Add(new PortMapping { Container = port, IsExposed = true });
    }

    public string Image { get; }

    [ObservableProperty]
    public partial string ContainerName { get; set; } = "";

    [ObservableProperty]
    public partial bool ShowOptions { get; set; }

    public ObservableCollection<PortMapping> Ports { get; } = [];

    public ObservableCollection<VolumeMapping> Volumes { get; } = [];

    public ObservableCollection<EnvironmentVariable> Environment { get; } = [];

    [RelayCommand]
    void ToggleOptions() => ShowOptions = !ShowOptions;

    [RelayCommand]
    void AddPort() => Ports.Add(new PortMapping());

    [RelayCommand]
    void RemovePort(PortMapping? port)
    {
        if (port is not null)
            Ports.Remove(port);
    }

    [RelayCommand]
    void AddVolume() => Volumes.Add(new VolumeMapping());

    [RelayCommand]
    void RemoveVolume(VolumeMapping? volume)
    {
        if (volume is not null)
            Volumes.Remove(volume);
    }

    [RelayCommand]
    void AddVariable() => Environment.Add(new EnvironmentVariable());

    [RelayCommand]
    void RemoveVariable(EnvironmentVariable? variable)
    {
        if (variable is not null)
            Environment.Remove(variable);
    }

    /// <summary>"8080" for "80/tcp" publishes as -p 8080:80/tcp; an empty host port is left unpublished.</summary>
    internal IEnumerable<(string Host, string Container)> PortsToPublish() =>
        Ports.Where(p => p.Host.Trim().Length > 0 && p.Container.Trim().Length > 0).Select(p => (p.Host.Trim(), p.Container.Trim()));

    internal IEnumerable<(string Host, string Container)> VolumesToMount() =>
        Volumes.Where(v => v.Host.Trim().Length > 0 && v.Container.Trim().Length > 0).Select(v => (v.Host.Trim(), v.Container.Trim()));

    internal IEnumerable<(string Name, string Value)> Variables() =>
        Environment.Where(e => e.Name.Trim().Length > 0).Select(e => (e.Name.Trim(), e.Value));
}
