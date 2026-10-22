using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VSL.ViewModels;

namespace VSL.Docker.ViewModels;

public enum DockerSection
{
    Containers,
    Images,
    Volumes,
    Networks,
}

/// <summary>What the Docker Explorer asks of VSL: the solution's folder, the browser, Output, the Database Explorer, Claude.</summary>
public interface IDockerHost
{
    /// <summary>The open solution's folder (its compose file, its stack), or null.</summary>
    string? SolutionDirectory { get; }

    void OpenUrl(string url);

    /// <summary>A line in Output's "Docker" pane (compose, pull).</summary>
    void WriteOutput(string line);

    void ShowOutput();

    /// <summary>A database container as a data source of the solution's Database Explorer; why it cannot, or null.</summary>
    string? OpenInDatabaseExplorer(DockerContainer container, JsonElement inspect);

    /// <summary>Claude Code, with this prompt ready to send.</summary>
    void AskClaude(string prompt);
}

/// <summary>
/// The Docker Explorer (View > Docker Explorer): Docker Desktop's dashboard in a document. A sidebar for containers,
/// images, volumes and networks; each a table that follows the engine's events, with CPU and memory from its stats
/// stream; a page for a container (logs, inspect, mounts, a shell, stats), an image, a volume or a network. The
/// engine is the docker command line (<see cref="DockerEngine"/>); when it is not running the page says why and
/// tries again every few seconds.
/// </summary>
public sealed partial class DockerExplorerViewModel : DocumentTab
{
    public const string DocumentKey = "docker:explorer";

    static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);

    readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly DispatcherTimer _retryTimer = new() { Interval = RetryInterval };
    DockerStream? _events;
    DockerStream? _stats;
    bool _containersDirty;
    bool _diskDirty;
    bool _networksDirty;
    bool _connecting;
    bool _disposed;

    public DockerExplorerViewModel(DockerEngine engine, IDockerHost host)
    {
        Engine = engine;
        Host = host;
        Containers = new ContainersPage(this);
        Images = new ImagesPage(this);
        Volumes = new VolumesPage(this);
        Networks = new NetworksPage(this);
        _refreshTimer.Tick += (_, _) =>
        {
            _refreshTimer.Stop();
            _ = RefreshDirtyAsync();
        };
        _retryTimer.Tick += (_, _) =>
        {
            if (EngineState != "running")
                _ = ConnectAsync();
        };
    }

    public override string Key => DocumentKey;

    public override string Title => "Docker";

    public override string ToolTip => $"Docker Explorer ({Engine.Name})";

    public DockerEngine Engine { get; }

    internal IDockerHost Host { get; }

    public ContainersPage Containers { get; }

    public ImagesPage Images { get; }

    public VolumesPage Volumes { get; }

    public NetworksPage Networks { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsContainers), nameof(IsImages), nameof(IsVolumes), nameof(IsNetworks))]
    public partial DockerSection Section { get; set; }

    public bool IsContainers => Section == DockerSection.Containers;

    /// <summary>Which table shows: the section's, while the engine runs and no page is open over it.</summary>
    public bool ShowContainers => IsEngineRunning && Detail is null && IsContainers;

    public bool ShowImages => IsEngineRunning && Detail is null && IsImages;

    public bool ShowVolumes => IsEngineRunning && Detail is null && IsVolumes;

    public bool ShowNetworks => IsEngineRunning && Detail is null && IsNetworks;

    public bool ShowDetail => IsEngineRunning && Detail is not null;

    partial void OnSectionChanged(DockerSection value) => PagesChanged();

    partial void OnDetailChanged(object? value) => PagesChanged();

    partial void OnEngineStateChanged(string value) => PagesChanged();

    void PagesChanged()
    {
        OnPropertyChanged(nameof(ShowContainers));
        OnPropertyChanged(nameof(ShowImages));
        OnPropertyChanged(nameof(ShowVolumes));
        OnPropertyChanged(nameof(ShowNetworks));
        OnPropertyChanged(nameof(ShowDetail));
    }

    public bool IsImages => Section == DockerSection.Images;

    public bool IsVolumes => Section == DockerSection.Volumes;

    public bool IsNetworks => Section == DockerSection.Networks;

    /// <summary>"starting" (first look), "running", or "stopped" (not installed, not running, no access).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEngineRunning), nameof(IsEngineStopped), nameof(IsEngineStarting), nameof(EngineStatusText))]
    public partial string EngineState { get; set; } = "starting";

    public bool IsEngineRunning => EngineState == "running";

    public bool IsEngineStopped => EngineState == "stopped";

    public bool IsEngineStarting => EngineState == "starting";

    public string EngineStatusText => EngineState switch
    {
        "running" => "Engine running",
        "starting" => "Engine starting",
        _ => "Engine stopped",
    };

    /// <summary>Why the engine cannot be used, as the engine said it.</summary>
    [ObservableProperty]
    public partial string? EngineMessage { get; set; }

    /// <summary>What to do about it.</summary>
    [ObservableProperty]
    public partial string? EngineHint { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VersionText), nameof(MachineText))]
    public partial DockerInfo? Info { get; set; }

    public string VersionText => Info is { Version.Length: > 0 } info ? $"{Engine.Name} {info.Version}" : Engine.Name;

    public string MachineText => Info is { } info
        ? string.Join(" · ", new[] { info.OperatingSystem, info.Cpus > 0 ? $"{info.Cpus} CPUs" : null, info.Memory > 0 ? DockerFormat.Size(info.Memory) : null }.Where(s => s is { Length: > 0 }))
        : "";

    /// <summary>A container's, image's, volume's or network's page, over the section's table; null shows the table.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetail))]
    public partial object? Detail { get; set; }

    public bool HasDetail => Detail is not null;

    /// <summary>The dialog over the page (a confirmation, Run, Create), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDialog))]
    public partial DockerDialog? Dialog { get; set; }

    public bool HasDialog => Dialog is not null;

    /// <summary>What the engine said when something failed (the red banner).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    public bool HasError => Error is not null;

    /// <summary>The footer's totals, as Docker Desktop's: memory and CPU of the running containers, and the disk used.</summary>
    [ObservableProperty]
    public partial string RamText { get; set; } = "";

    [ObservableProperty]
    public partial string CpuText { get; set; } = "";

    [ObservableProperty]
    public partial string DiskText { get; set; } = "";

    // ---- Connecting -------------------------------------------------------------------------------------------

    public Task InitializeAsync() => ConnectAsync();

    /// <summary>Asks the engine who it is; running, reads everything and follows its events and stats.</summary>
    async Task ConnectAsync()
    {
        if (_connecting || _disposed)
            return;
        _connecting = true;
        try
        {
            Info = await Engine.InfoAsync();
        }
        catch (DockerException ex)
        {
            SetStopped(ex);
            return;
        }
        finally
        {
            _connecting = false;
        }
        if (_disposed)
            return;
        EngineState = "running";
        EngineMessage = EngineHint = null;
        _retryTimer.Stop();
        await RefreshAllAsync();
        StartStreams();
    }

    void SetStopped(DockerException ex)
    {
        StopStreams();
        EngineState = "stopped";
        EngineMessage = ex.Message;
        EngineHint = ex.Problem switch
        {
            DockerProblem.NotInstalled => "Install Docker Engine (or Podman), or set VSL_DOCKER_PATH to its command line, then retry.",
            DockerProblem.PermissionDenied => "Your user may not use the Docker socket. Add it to the docker group (sudo usermod -aG docker $USER), sign in again, then retry.",
            DockerProblem.NotRunning => "Start the Docker daemon (sudo systemctl start docker), or Docker Desktop. VSL connects as soon as it is up.",
            _ => null,
        };
        Containers.Apply([]);
        if (!_disposed)
            _retryTimer.Start();
    }

    [RelayCommand]
    Task Retry() => ConnectAsync();

    /// <summary>Everything again: containers, images and volumes (with their disk use), networks.</summary>
    [RelayCommand]
    public Task RefreshAllAsync() => Task.WhenAll(RefreshContainersAsync(), RefreshDiskAsync(), RefreshNetworksAsync());

    internal async Task RefreshContainersAsync()
    {
        try
        {
            var containers = await Engine.ContainersAsync();
            Containers.Apply(containers);
            Networks.ApplyContainers(containers);
            UpdateTotals();
        }
        catch (DockerException ex)
        {
            Report(ex);
        }
    }

    internal async Task RefreshDiskAsync()
    {
        try
        {
            var usage = await Engine.DiskUsageAsync();
            Images.Apply(usage);
            Volumes.Apply(usage.Volumes);
            DiskText = $"Disk {DockerFormat.Size(usage.Total)} used";
        }
        catch (DockerException ex)
        {
            Report(ex);
        }
    }

    internal async Task RefreshNetworksAsync()
    {
        try
        {
            Networks.Apply(await Engine.NetworksAsync());
        }
        catch (DockerException ex)
        {
            Report(ex);
        }
    }

    /// <summary>The engine went away: the page says so; anything else is the red banner.</summary>
    internal void Report(DockerException ex)
    {
        if (ex.Problem != DockerProblem.Failed)
            SetStopped(ex);
        else
            Error = ex.Message;
    }

    internal void Report(DockerResult result, string what)
    {
        if (!result.Succeeded)
            Report(Engine.Failure($"{what}: {result.Message}"));
    }

    [RelayCommand]
    void DismissError() => Error = null;

    // ---- Events and stats ---------------------------------------------------------------------------------------

    void StartStreams()
    {
        StopStreams();
        try
        {
            _events = Engine.Stream(["events", "--format", "{{json .}}"], line => Dispatcher.UIThread.Post(() => OnEvent(line)),
                _ => Dispatcher.UIThread.Post(OnEventsEnded));
            _stats = Engine.Stream(["stats", "--format", "{{json .}}"], line =>
            {
                if (DockerEngine.ParseStats(line) is { } stats)
                    Dispatcher.UIThread.Post(() => OnStats(stats));
            });
        }
        catch (DockerException ex)
        {
            Report(ex);
        }
    }

    void StopStreams()
    {
        _events?.Dispose();
        _events = null;
        _stats?.Dispose();
        _stats = null;
    }

    /// <summary>One engine event: what it concerns is read again a moment later (several events come together).</summary>
    void OnEvent(string line)
    {
        if (_disposed || !line.TrimStart().StartsWith('{'))
            return;
        string type, action;
        try
        {
            using var json = JsonDocument.Parse(line);
            type = DockerFormat.Str(json.RootElement, "Type");
            action = DockerFormat.Str(json.RootElement, "Action");
        }
        catch (JsonException)
        {
            return;
        }
        // Health checks run a command in the container every few seconds; only their verdict changes anything.
        if (action.StartsWith("exec_", StringComparison.Ordinal) || action == "top")
            return;
        switch (type)
        {
            case "container":
                _containersDirty = true;
                // Created or removed containers change which images and volumes are in use.
                if (action is "create" or "destroy")
                    _diskDirty = true;
                break;
            case "image" or "volume":
                _diskDirty = true;
                break;
            case "network":
                _networksDirty = true;
                _containersDirty |= action is "connect" or "disconnect";
                break;
            default:
                return;
        }
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    async Task RefreshDirtyAsync()
    {
        var tasks = new List<Task>();
        if (_containersDirty)
            tasks.Add(RefreshContainersAsync());
        if (_diskDirty)
            tasks.Add(RefreshDiskAsync());
        if (_networksDirty)
            tasks.Add(RefreshNetworksAsync());
        _containersDirty = _diskDirty = _networksDirty = false;
        await Task.WhenAll(tasks);
    }

    /// <summary>The events stream ends when the daemon stops: look again in a moment.</summary>
    void OnEventsEnded()
    {
        if (_disposed || EngineState != "running")
            return;
        _events = null;
        DispatcherTimer.RunOnce(() => _ = ReconnectAsync(), TimeSpan.FromSeconds(1));
    }

    async Task ReconnectAsync()
    {
        if (_disposed)
            return;
        try
        {
            Info = await Engine.InfoAsync();
            StartStreams();
            await RefreshAllAsync();
        }
        catch (DockerException ex)
        {
            SetStopped(ex);
        }
    }

    void OnStats(DockerStats stats)
    {
        if (_disposed)
            return;
        Containers.ApplyStats(stats);
        UpdateTotals();
    }

    void UpdateTotals()
    {
        var running = Containers.AllRows.Where(r => r.Container.IsRunning).ToList();
        var memory = running.Sum(r => r.Memory ?? 0);
        var cpu = running.Sum(r => r.Cpu ?? 0);
        var cpus = Math.Max(Info?.Cpus ?? 1, 1);
        RamText = Info?.Memory > 0 ? $"RAM {DockerFormat.Size(memory)} / {DockerFormat.Size(Info.Memory)}" : $"RAM {DockerFormat.Size(memory)}";
        CpuText = $"CPU {cpu / cpus:0.00}%";
        Containers.UpdateSummary(cpu, memory, cpus, Info?.Memory ?? 0);
    }

    // ---- Navigation ---------------------------------------------------------------------------------------------

    [RelayCommand]
    void ShowSection(DockerSection section)
    {
        CloseDetail();
        Section = section;
    }

    [RelayCommand]
    void Back() => CloseDetail();

    void CloseDetail()
    {
        (Detail as IDisposable)?.Dispose();
        Detail = null;
    }

    internal void OpenContainer(ContainerRow row)
    {
        CloseDetail();
        Section = DockerSection.Containers;
        var detail = new ContainerDetailViewModel(this, row);
        Detail = detail;
        detail.Initialize();
    }

    internal void OpenImage(ImageRow row)
    {
        CloseDetail();
        Section = DockerSection.Images;
        var detail = new ImageDetailViewModel(this, row);
        Detail = detail;
        _ = detail.LoadAsync();
    }

    /// <summary>The image a container runs, by the reference it was started with.</summary>
    internal void OpenImage(string reference)
    {
        if (Images.Find(reference) is { } row)
            OpenImage(row);
        else
            Error = $"{reference} is not a local image any more.";
    }

    internal void OpenVolume(VolumeRow row)
    {
        CloseDetail();
        Section = DockerSection.Volumes;
        var detail = new VolumeDetailViewModel(this, row);
        Detail = detail;
        _ = detail.LoadAsync();
    }

    internal void OpenVolume(string name)
    {
        if (Volumes.Rows.FirstOrDefault(v => v.Volume.Name == name) is { } row)
            OpenVolume(row);
    }

    internal void OpenNetwork(NetworkRow row)
    {
        CloseDetail();
        Section = DockerSection.Networks;
        var detail = new NetworkDetailViewModel(this, row);
        Detail = detail;
        _ = detail.LoadAsync();
    }

    internal void OpenContainer(string idOrName)
    {
        if (Containers.AllRows.FirstOrDefault(r => r.Container.Id.StartsWith(idOrName, StringComparison.Ordinal) || r.Container.Name == idOrName) is { } row)
            OpenContainer(row);
    }

    // ---- Actions ------------------------------------------------------------------------------------------------

    /// <summary>start, stop, restart, pause or unpause containers, each row busy meanwhile; rm asks first.</summary>
    internal async Task ActAsync(string verb, IReadOnlyList<ContainerRow> rows)
    {
        if (rows.Count == 0)
            return;
        if (verb == "rm")
        {
            var running = rows.Count(r => r.Container.IsUp);
            var what = rows.Count == 1 ? rows[0].Container.Name : $"{rows.Count} containers";
            if (!await ConfirmAsync($"Delete {what}?",
                    (running > 0 ? (rows.Count == 1 ? "It is running and will be stopped. " : $"{running} of them are running and will be stopped. ") : "")
                    + (rows.Count == 1 ? "Its writable layer goes with it" : "Their writable layers go with them") + "; named volumes stay.", "Delete", danger: true))
                return;
        }
        var busy = verb switch
        {
            "start" => "Starting…",
            "stop" => "Stopping…",
            "restart" => "Restarting…",
            "pause" => "Pausing…",
            "unpause" => "Resuming…",
            _ => "Deleting…",
        };
        foreach (var row in rows)
            row.BusyText = busy;
        try
        {
            var result = await Engine.ContainersAsync(verb, rows.Select(r => r.Container.Id));
            Report(result, $"{verb} {(rows.Count == 1 ? rows[0].Container.Name : $"{rows.Count} containers")}");
        }
        catch (DockerException ex)
        {
            Report(ex);
        }
        finally
        {
            foreach (var row in rows)
                row.BusyText = null;
        }
        if (verb == "rm" && Detail is ContainerDetailViewModel open && rows.Contains(open.Row))
            CloseDetail();
        await RefreshContainersAsync();
        if (verb == "rm")
            await RefreshDiskAsync();
    }

    internal async Task<bool> ConfirmAsync(string title, string message, string confirm, bool danger = false)
    {
        var dialog = new ConfirmDialog(title, message, confirm, danger);
        Dialog = dialog;
        try
        {
            return await dialog.Result;
        }
        finally
        {
            if (Dialog == dialog)
                Dialog = null;
        }
    }

    internal async Task<T?> ShowAsync<T>(T dialog) where T : DockerDialog
    {
        Dialog = dialog;
        try
        {
            return await dialog.Result ? dialog : null;
        }
        finally
        {
            if (Dialog == dialog)
                Dialog = null;
        }
    }

    /// <summary>
    /// Docker Desktop's Run: the optional settings (name, the image's exposed ports, volumes, variables), then
    /// <c>docker run -d</c>; the new container's page opens on its logs.
    /// </summary>
    internal async Task RunImageAsync(string reference)
    {
        var exposed = new List<string>();
        try
        {
            var inspect = await Engine.InspectAsync("image", reference);
            if (inspect.TryGetProperty("Config", out var config) && config.ValueKind == JsonValueKind.Object
                && config.TryGetProperty("ExposedPorts", out var ports) && ports.ValueKind == JsonValueKind.Object)
                exposed.AddRange(ports.EnumerateObject().Select(p => p.Name));
        }
        catch (DockerException)
        {
            // Run without suggested ports.
        }
        if (await ShowAsync(new RunDialog(reference, exposed)) is not { } run)
            return;
        DockerResult result;
        try
        {
            result = await Engine.RunContainerAsync(reference, run.ContainerName, run.PortsToPublish(), run.VolumesToMount(), run.Variables());
        }
        catch (DockerException ex)
        {
            Report(ex);
            return;
        }
        if (!result.Succeeded)
        {
            Report(Engine.Failure($"run {reference}: {result.Message}"));
            await RefreshContainersAsync();
            return;
        }
        var id = result.Output.Split('\n').LastOrDefault()?.Trim() ?? "";
        await RefreshContainersAsync();
        await RefreshDiskAsync();
        if (id.Length > 0)
            OpenContainer(id);
    }

    public override void Dispose()
    {
        _disposed = true;
        _refreshTimer.Stop();
        _retryTimer.Stop();
        StopStreams();
        CloseDetail();
        Dialog?.Cancel();
    }
}
