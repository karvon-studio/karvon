using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Karvon.ViewModels;
using Karvon.Ui;

namespace Karvon.Docker.ViewModels;

public enum DockerSection
{
    Containers,
    Images,
    Volumes,
    Networks,
}

/// <summary>What the Docker Explorer asks of Karvon: the solution's folder, the browser, Output, the Database Explorer, Claude.</summary>
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

    /// <summary>The explorer's documents open in the document well.</summary>
    IEnumerable<DockerDocument> Documents { get; }

    /// <summary>Opens the document in the document well (when it is not open yet) and brings it to the front.</summary>
    void ShowDocument(DockerDocument document);

    void CloseDocument(DockerDocument document);
}

/// <summary>
/// The Docker Explorer tool window (View > Docker Explorer), as the Database Explorer: a tree of the engine's
/// containers (compose projects as stacks), images, volumes and networks that follows its events. What it opens is a
/// document: a section's table as Docker Desktop's (CPU and memory from the stats stream), or a container's page
/// (logs, inspect, mounts, a shell, stats), an image's, a volume's or a network's. The engine is the docker command
/// line (<see cref="DockerEngine"/>); when it is not running the explorer says why and tries again every few seconds.
/// </summary>
public sealed partial class DockerExplorerViewModel : ObservableObject, IDisposable
{
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
    int _watchers;

    public DockerExplorerViewModel(DockerEngine engine, IDockerHost host)
    {
        Engine = engine;
        Host = host;
        Containers = new ContainersPage(this);
        Images = new ImagesPage(this);
        Volumes = new VolumesPage(this);
        Networks = new NetworksPage(this);
        Sections =
        [
            new DockerSectionNode(this, DockerSection.Containers, "DockerContainer") { IsTreeExpanded = true },
            new DockerSectionNode(this, DockerSection.Images, "DockerImage"),
            new DockerSectionNode(this, DockerSection.Volumes, "DockerVolume"),
            new DockerSectionNode(this, DockerSection.Networks, "DockerNetwork"),
        ];
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

    public DockerEngine Engine { get; }

    internal IDockerHost Host { get; }

    public ContainersPage Containers { get; }

    public ImagesPage Images { get; }

    public VolumesPage Volumes { get; }

    public NetworksPage Networks { get; }

    /// <summary>The tree's top level: Containers, Images, Volumes, Networks.</summary>
    public IReadOnlyList<DockerSectionNode> Sections { get; }

    /// <summary>The tree's search: names that contain it (a stack's containers when the stack's does).</summary>
    [ObservableProperty]
    public partial string Filter { get; set; } = "";

    partial void OnFilterChanged(string value) => SyncTree();

    /// <summary>"starting" (first look), "running", or "stopped" (not installed, not running, no access).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEngineRunning), nameof(IsEngineStopped), nameof(IsEngineStarting), nameof(EngineStatusText))]
    public partial string EngineState { get; set; } = "starting";

    partial void OnEngineStateChanged(string value) => UpdateStatsStream();

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

    /// <summary>The dialog over the page (a confirmation, Run, Create), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDialog))]
    public partial OverlayDialog? Dialog { get; set; }

    public bool HasDialog => Dialog is not null;

    /// <summary>What the engine said when something failed (the red banner).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    public bool HasError => Error is not null;

    /// <summary>The status bar's totals, as Docker Desktop's: memory and CPU of the running containers, and the disk used.</summary>
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
            DockerProblem.NotInstalled => "Install Docker Engine (or Podman), or set KARVON_DOCKER_PATH to its command line, then retry.",
            DockerProblem.PermissionDenied => "Your user may not use the Docker socket. Add it to the docker group (sudo usermod -aG docker $USER), sign in again, then retry.",
            DockerProblem.NotRunning => "Start the Docker daemon (sudo systemctl start docker), or Docker Desktop. Karvon connects as soon as it is up.",
            _ => null,
        };
        // The rows stay, for the documents open on them; the tree and the documents say the engine is away meanwhile.
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
            SyncTree();
            CloseGone();
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
            SyncTree();
            CloseGone();
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
            SyncTree();
            CloseGone();
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
        }
        catch (DockerException ex)
        {
            Report(ex);
        }
        UpdateStatsStream();
    }

    void StopStreams()
    {
        _events?.Dispose();
        _events = null;
        _stats?.Dispose();
        _stats = null;
    }

    /// <summary>
    /// A view of the explorer (the tool window, a document) shows (+1) or goes (-1). The stats stream, which has the
    /// engine sample every running container each second, runs only while one shows.
    /// </summary>
    internal void Watch(int change)
    {
        _watchers = Math.Max(_watchers + change, 0);
        UpdateStatsStream();
    }

    void UpdateStatsStream()
    {
        var wanted = _watchers > 0 && IsEngineRunning && _events is not null && !_disposed;
        if (wanted == _stats is not null)
            return;
        if (!wanted)
        {
            _stats!.Dispose();
            _stats = null;
            return;
        }
        try
        {
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
        _stats?.Dispose();
        _stats = null;
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

    // ---- The tree ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// The tree shows what the engine has, the search's matches: the solution's stack first, then stacks and lone
    /// containers by name; images by name, untagged last; named volumes before anonymous; the engine's networks first.
    /// </summary>
    void SyncTree()
    {
        var filter = Filter.Trim();
        bool Match(string name) => filter.Length == 0 || name.Contains(filter, StringComparison.OrdinalIgnoreCase);
        var containers = new List<DockerTreeItem>();
        foreach (var stack in Containers.Stacks.OrderByDescending(s => s.IsThisSolution).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            var children = stack.Children.Where(c => Match(stack.Name) || Match(c.Name)).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
            ObservableSync.Sync(stack.TreeChildren, children);
            if (children.Count > 0)
                containers.Add(stack);
        }
        containers.AddRange(Containers.AllRows.Where(r => r.Container.Project is null && Match(r.Name)).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase));
        Show(Sections[0], containers, Containers.Count);
        Sections[0].HasCompose = Containers.HasComposeFile;
        Show(Sections[1], Images.AllRows.Where(r => Match(r.Reference)).OrderBy(r => r.Image.IsDangling).ThenBy(r => r.Reference, StringComparer.OrdinalIgnoreCase), Images.Count);
        Show(Sections[2], Volumes.AllRows.Where(r => Match(r.Name)).OrderBy(r => r.Volume.IsAnonymous).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase), Volumes.Count);
        Show(Sections[3], Networks.AllRows.Where(r => Match(r.Name)).OrderBy(r => !r.Network.IsBuiltIn).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase), Networks.Count);

        void Show(DockerSectionNode section, IEnumerable<DockerTreeItem> items, int count)
        {
            ObservableSync.Sync(section.Items, items.ToList());
            section.Count = count;
            // Searching opens the sections it finds something in.
            if (filter.Length > 0 && section.Items.Count > 0)
                section.IsTreeExpanded = true;
        }
    }

    [RelayCommand]
    void CollapseAll()
    {
        foreach (var section in Sections)
            section.IsTreeExpanded = false;
    }

    /// <summary>Double-click or Enter in the tree: a section's table, or a container's, image's, volume's or network's page.</summary>
    public void Open(DockerTreeItem item)
    {
        switch (item)
        {
            case DockerSectionNode section:
                OpenSection(section.Section);
                break;
            case ContainerRow container:
                OpenContainer(container);
                break;
            case ImageRow image:
                OpenImage(image);
                break;
            case VolumeRow volume:
                OpenVolume(volume);
                break;
            case NetworkRow network:
                OpenNetwork(network);
                break;
            case StackRow stack:
                stack.IsTreeExpanded = !stack.IsTreeExpanded;
                break;
        }
    }

    // ---- Documents ----------------------------------------------------------------------------------------------

    /// <summary>The document with this key, brought to the front; made by <paramref name="create"/> when it is not open.</summary>
    DockerDocument Show(string key, Func<DockerDocument> create)
    {
        var document = Host.Documents.FirstOrDefault(d => d.Key == key) ?? create();
        Host.ShowDocument(document);
        return document;
    }

    /// <summary>A section's table as a document: Docker Desktop's Containers, Images, Volumes or Networks.</summary>
    public DockerDocument OpenSection(DockerSection section)
    {
        var key = "docker:" + section.ToString().ToLowerInvariant();
        return Show(key, () => new DockerDocument(this, key, section.ToString(), $"Docker {section.ToString().ToLowerInvariant()} ({Engine.Name})", section switch
        {
            DockerSection.Containers => Containers,
            DockerSection.Images => Images,
            DockerSection.Volumes => Volumes,
            _ => Networks,
        }));
    }

    /// <summary>A container's page, on its logs (or on <paramref name="tab"/>: "inspect", "mounts", "exec", "stats").</summary>
    internal DockerDocument OpenContainer(ContainerRow row, string? tab = null)
    {
        var key = "docker:container:" + row.Container.Id;
        var document = Show(key, () =>
        {
            var detail = new ContainerDetailViewModel(this, row);
            detail.Initialize();
            return new DockerDocument(this, key, row.Name, $"Container {row.Name} ({row.Image})", detail);
        });
        if (tab is not null && document.Content is ContainerDetailViewModel page)
            page.Tab = tab;
        return document;
    }

    internal void OpenContainer(string idOrName)
    {
        if (Containers.AllRows.FirstOrDefault(r => r.Container.Id.StartsWith(idOrName, StringComparison.Ordinal) || r.Container.Name == idOrName) is { } row)
            OpenContainer(row);
    }

    internal DockerDocument OpenImage(ImageRow row)
    {
        var key = "docker:image:" + row.Image.Id + "|" + row.Image.Reference;
        return Show(key, () =>
        {
            var detail = new ImageDetailViewModel(this, row);
            _ = detail.LoadAsync();
            return new DockerDocument(this, key, row.Image.IsDangling ? row.ShortId : row.Image.Reference, $"Image {row.Reference}", detail);
        });
    }

    /// <summary>
    /// Claude Code about a container: its state, what the compose file gets wrong for it and its last log lines (secrets
    /// hidden), in the prompt, ready to send.
    /// </summary>
    internal async Task AskClaudeAsync(ContainerRow row)
    {
        var lines = new List<string>();
        try
        {
            await Engine.RunAsync(["logs", "--tail", "60", row.Container.Id], onLine: line =>
            {
                lock (lines)
                    lines.Add(line);
            });
        }
        catch (DockerException ex)
        {
            Report(ex);
        }
        Host.AskClaude(ClaudePrompt(row, lines));
    }

    /// <summary>The prompt about a container, with its last log lines.</summary>
    internal static string ClaudePrompt(ContainerRow row, IEnumerable<string> logLines)
    {
        var c = row.Container;
        var lines = logLines.Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).TakeLast(60).Select(Claude.Tools.Redaction.Text).ToList();
        var prompt = new System.Text.StringBuilder();
        prompt.Append($"The Docker container {c.Name} (image {c.Image}{(c.Project is { } p ? $", compose project {p}, service {c.Service}" : "")}) is {row.StateText.ToLowerInvariant()}.");
        foreach (var problem in row.Problems)
            prompt.Append("\nThe compose file: ").Append(problem.Text);
        if (lines.Count > 0)
            prompt.Append("\nIts last log lines:\n```\n").Append(string.Join("\n", lines)).Append("\n```");
        prompt.Append(row.IsFailing || row.HasProblems ? "\nWhy does it fail, and how do I fix it?" : "\n");
        return prompt.ToString();
    }

    /// <summary>The image a container runs, by the reference it was started with.</summary>
    internal void OpenImage(string reference)
    {
        if (Images.Find(reference) is { } row)
            OpenImage(row);
        else
            Error = $"{reference} is not a local image any more.";
    }

    internal DockerDocument OpenVolume(VolumeRow row)
    {
        var key = "docker:volume:" + row.Name;
        return Show(key, () =>
        {
            var detail = new VolumeDetailViewModel(this, row);
            _ = detail.LoadAsync();
            return new DockerDocument(this, key, row.TreeName, $"Volume {row.Name}", detail);
        });
    }

    internal void OpenVolume(string name)
    {
        if (Volumes.AllRows.FirstOrDefault(v => v.Volume.Name == name) is { } row)
            OpenVolume(row);
    }

    internal DockerDocument OpenNetwork(NetworkRow row)
    {
        var key = "docker:network:" + row.Network.Id;
        return Show(key, () =>
        {
            var detail = new NetworkDetailViewModel(this, row);
            _ = detail.LoadAsync();
            return new DockerDocument(this, key, row.Name, $"Network {row.Name} ({row.Driver})", detail);
        });
    }

    /// <summary>A container, image, volume or network that is gone (deleted here or elsewhere) takes its page with it.</summary>
    void CloseGone()
    {
        foreach (var document in Host.Documents.Where(d => d.Explorer == this).ToList())
            if (document.Content switch
                {
                    ContainerDetailViewModel c => !Containers.Contains(c.Row),
                    ImageDetailViewModel i => !Images.Contains(i.Row),
                    VolumeDetailViewModel v => !Volumes.Contains(v.Row),
                    NetworkDetailViewModel n => !Networks.Contains(n.Row),
                    _ => false,
                })
                Host.CloseDocument(document);
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

    internal async Task<T?> ShowAsync<T>(T dialog) where T : OverlayDialog
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

    public void Dispose()
    {
        _disposed = true;
        _refreshTimer.Stop();
        _retryTimer.Stop();
        StopStreams();
        Dialog?.Cancel();
    }
}
