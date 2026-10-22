using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace VSL.Docker.ViewModels;

/// <summary>A row of the Containers table: one container, alone or in its compose stack.</summary>
public sealed partial class ContainerRow : ObservableObject
{
    const int HistoryLength = 60;

    readonly ContainersPage _page;

    internal ContainerRow(ContainersPage page, DockerContainer container)
    {
        _page = page;
        Container = container;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name), nameof(Image), nameof(ShortId), nameof(FirstPort), nameof(HasPorts), nameof(MorePortsText), nameof(HasMorePorts),
        nameof(PortsTip), nameof(LastStartedText), nameof(StateKind), nameof(StateText), nameof(IsUp), nameof(IsRunning), nameof(IsPaused), nameof(CpuText),
        nameof(MemoryText), nameof(StartStopTip), nameof(IsFailing), nameof(Ports))]
    public partial DockerContainer Container { get; set; }

    /// <summary>In a compose stack: shown under it, indented.</summary>
    public bool IsChild => Container.Project is not null;

    public string Name => Container.Name;

    public string Image => Container.Image;

    public string ShortId => Container.ShortId;

    public IReadOnlyList<DockerPort> Ports => Container.Ports.Where(p => p.HostPort is not null).ToList();

    /// <summary>The first published port, shown as a link ("8080:80").</summary>
    public DockerPort? FirstPort => Ports.FirstOrDefault();

    public bool HasPorts => FirstPort is not null;

    public string MorePortsText => Ports.Count > 1 ? $"+{Ports.Count - 1}" : "";

    public bool HasMorePorts => Ports.Count > 1;

    public string PortsTip => string.Join("\n", Container.Ports.Select(p => p.Text + "/" + p.Protocol));

    public string LastStartedText => Container.StartedAt is { } started ? DockerFormat.Ago(started) : "";

    public bool IsUp => Container.IsUp;

    public bool IsRunning => Container.IsRunning;

    public bool IsPaused => Container.IsPaused;

    public bool IsFailing => Container.IsFailing;

    /// <summary>The dot's color: running (green), paused (amber), failing (red), stopped (gray).</summary>
    public string StateKind => Container.IsFailing ? "failing" : Container.IsRunning ? "running" : Container.IsPaused ? "paused" : "stopped";

    public string StateText
    {
        get
        {
            var c = Container;
            var state = c.State switch
            {
                "running" => "Running",
                "paused" => "Paused",
                "exited" => c.ExitCode is { } code ? $"Exited ({code})" : "Exited",
                "created" => "Created",
                "restarting" => "Restarting",
                "dead" => "Dead",
                "removing" => "Removing",
                _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(c.State),
            };
            return c.Health is { } health && c.IsRunning ? $"{state} ({health})" : state;
        }
    }

    public string StartStopTip => IsUp ? "Stop" : "Start";

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>"Stopping…" while an action runs on it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy), nameof(IsIdle))]
    public partial string? BusyText { get; set; }

    public bool IsBusy => BusyText is not null;

    public bool IsIdle => BusyText is null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CpuText))]
    public partial double? Cpu { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MemoryText))]
    public partial long? Memory { get; set; }

    public long MemoryLimit { get; private set; }

    public string CpuText => IsRunning && Cpu is { } cpu ? cpu.ToString("0.00", CultureInfo.InvariantCulture) + "%" : IsRunning ? "" : "N/A";

    public string MemoryText => IsRunning && Memory is { } memory
        ? MemoryLimit > 0 ? $"{DockerFormat.Size(memory)} / {DockerFormat.Size(MemoryLimit)}" : DockerFormat.Size(memory)
        : "";

    /// <summary>The last minute of samples, for the Stats tab's charts (CPU %, memory, and I/O rates per second).</summary>
    public List<double> CpuHistory { get; } = [];

    public List<double> MemoryHistory { get; } = [];

    public List<double> NetReceiveHistory { get; } = [];

    public List<double> NetSendHistory { get; } = [];

    public List<double> DiskReadHistory { get; } = [];

    public List<double> DiskWriteHistory { get; } = [];

    /// <summary>Changes with each sample, so the charts draw again.</summary>
    [ObservableProperty]
    public partial int StatsVersion { get; set; }

    public DockerStats? LastStats { get; private set; }

    DateTime _lastSample;

    internal void ApplyStats(DockerStats stats)
    {
        var now = DateTime.UtcNow;
        var seconds = LastStats is null ? 0 : Math.Max((now - _lastSample).TotalSeconds, 0.5);
        double Rate(long current, long? previous) => seconds > 0 && previous is { } p ? Math.Max(current - p, 0) / seconds : 0;
        Add(CpuHistory, stats.Cpu);
        Add(MemoryHistory, stats.Memory);
        Add(NetReceiveHistory, Rate(stats.NetReceived, LastStats?.NetReceived));
        Add(NetSendHistory, Rate(stats.NetSent, LastStats?.NetSent));
        Add(DiskReadHistory, Rate(stats.BlockRead, LastStats?.BlockRead));
        Add(DiskWriteHistory, Rate(stats.BlockWritten, LastStats?.BlockWritten));
        LastStats = stats;
        _lastSample = now;
        MemoryLimit = stats.MemoryLimit;
        Cpu = stats.Cpu;
        Memory = stats.Memory;
        StatsVersion++;
        OnPropertyChanged(nameof(NetRateText));
        OnPropertyChanged(nameof(DiskRateText));
        OnPropertyChanged(nameof(PidsText));
    }

    /// <summary>The Stats tab's figures: bytes a second received and sent, read and written, and the processes.</summary>
    public string NetRateText => $"{Rate(NetReceiveHistory)} received · {Rate(NetSendHistory)} sent";

    public string DiskRateText => $"{Rate(DiskReadHistory)} read · {Rate(DiskWriteHistory)} written";

    public string PidsText => LastStats is { } stats ? $"{stats.Pids} process{(stats.Pids == 1 ? "" : "es")}" : "";

    static string Rate(List<double> history) => (history.Count == 0 ? "0 B" : DockerFormat.Size((long)history[^1])) + "/s";

    static void Add(List<double> history, double value)
    {
        history.Add(value);
        if (history.Count > HistoryLength)
            history.RemoveAt(0);
    }

    /// <summary>Stopped: no samples any more, and the charts start again when it runs.</summary>
    internal void ClearStats()
    {
        if (LastStats is null && Cpu is null)
            return;
        LastStats = null;
        Cpu = null;
        Memory = null;
        foreach (var history in new[] { CpuHistory, MemoryHistory, NetReceiveHistory, NetSendHistory, DiskReadHistory, DiskWriteHistory })
            history.Clear();
        StatsVersion++;
    }

    partial void OnIsSelectedChanged(bool value) => _page.SelectionChanged();

    [RelayCommand]
    void Open() => _page.Open(this);

    [RelayCommand]
    Task StartStop() => _page.StartStopAsync(this);

    [RelayCommand]
    Task Restart() => _page.RestartAsync(this);

    [RelayCommand]
    Task PauseResume() => _page.PauseResumeAsync(this);

    [RelayCommand]
    Task Delete() => _page.DeleteAsync(this);

    [RelayCommand]
    void OpenPort(DockerPort? port) => _page.OpenPort(port ?? FirstPort);

    [RelayCommand]
    void OpenImage() => _page.OpenImage(this);
}

/// <summary>A compose project: its containers under it, started, stopped and deleted together (Docker Desktop's stack).</summary>
public sealed partial class StackRow : ObservableObject
{
    readonly ContainersPage _page;

    internal StackRow(ContainersPage page, string name)
    {
        _page = page;
        Name = name;
    }

    public string Name { get; }

    /// <summary>All its containers (whether the filters show them or not).</summary>
    public List<ContainerRow> Children { get; } = [];

    [ObservableProperty]
    public partial bool IsExpanded { get; set; } = true;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>The open solution's own stack (compose ran in its folder): listed first.</summary>
    [ObservableProperty]
    public partial bool IsThisSolution { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy), nameof(IsIdle))]
    public partial string? BusyText { get; set; }

    public bool IsBusy => BusyText is not null;

    public bool IsIdle => BusyText is null;

    public int RunningCount => Children.Count(c => c.IsRunning);

    public bool IsUp => Children.Any(c => c.IsUp);

    public string StateKind => Children.Any(c => c.IsFailing) ? "failing" : RunningCount > 0 ? "running" : Children.Any(c => c.IsPaused) ? "paused" : "stopped";

    public string StateText => RunningCount == Children.Count ? "Running" : RunningCount > 0 ? $"Running ({RunningCount}/{Children.Count})"
        : Children.Any(c => c.IsFailing) ? "Exited with errors" : "Exited";

    public string CpuText => RunningCount > 0 ? Children.Where(c => c.IsRunning).Sum(c => c.Cpu ?? 0).ToString("0.00", CultureInfo.InvariantCulture) + "%" : "N/A";

    public string LastStartedText => Children.Max(c => c.Container.StartedAt) is { } started ? DockerFormat.Ago(started) : "";

    public string StartStopTip => IsUp ? "Stop" : "Start";

    /// <summary>The stack's numbers follow its containers.</summary>
    internal void Refresh()
    {
        OnPropertyChanged(nameof(RunningCount));
        OnPropertyChanged(nameof(IsUp));
        OnPropertyChanged(nameof(StateKind));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(CpuText));
        OnPropertyChanged(nameof(LastStartedText));
        OnPropertyChanged(nameof(StartStopTip));
    }

    partial void OnIsSelectedChanged(bool value)
    {
        foreach (var child in Children)
            child.IsSelected = value;
        _page.SelectionChanged();
    }

    partial void OnIsExpandedChanged(bool value) => _page.Rebuild();

    [RelayCommand]
    void Toggle() => IsExpanded = !IsExpanded;

    [RelayCommand]
    Task StartStop() => _page.StartStopAsync(this);

    [RelayCommand]
    Task Restart() => _page.RestartAsync(this);

    [RelayCommand]
    Task Delete() => _page.DeleteAsync(this);
}

/// <summary>
/// Docker Desktop's Containers page: CPU and memory of the running containers, search and "only running", and the
/// table, compose projects as stacks with their containers under them. Rows are kept across refreshes (by id), so
/// what is selected, expanded or busy stays.
/// </summary>
public sealed partial class ContainersPage : ObservableObject
{
    readonly DockerExplorerViewModel _explorer;
    readonly Dictionary<string, ContainerRow> _rows = [];
    readonly Dictionary<string, StackRow> _stacks = [];
    bool _selecting;

    internal ContainersPage(DockerExplorerViewModel explorer) => _explorer = explorer;

    /// <summary>What the table shows, in order: stacks, their containers when expanded, and lone containers.</summary>
    public ObservableCollection<object> Rows { get; } = [];

    public IEnumerable<ContainerRow> AllRows => _rows.Values;

    public IEnumerable<StackRow> Stacks => _stacks.Values;

    [ObservableProperty]
    public partial string Search { get; set; } = "";

    [ObservableProperty]
    public partial bool OnlyRunning { get; set; }

    /// <summary>"name", "image", "started" or "cpu"; clicking the header again turns the order round.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameSort), nameof(ImageSort), nameof(StartedSort), nameof(CpuSort))]
    public partial string SortColumn { get; set; } = "name";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameSort), nameof(ImageSort), nameof(StartedSort), nameof(CpuSort))]
    public partial bool SortAscending { get; set; } = true;

    public string NameSort => Arrow("name");

    public string ImageSort => Arrow("image");

    public string StartedSort => Arrow("started");

    public string CpuSort => Arrow("cpu");

    string Arrow(string column) => SortColumn == column ? SortAscending ? "↑" : "↓" : "";

    public int Count => _rows.Count;

    public bool IsEmpty => _rows.Count == 0;

    /// <summary>Containers exist, but the search or "only running" hides them all.</summary>
    public bool HasNoMatches => _rows.Count > 0 && Rows.Count == 0;

    public string CountText => $"Showing {Rows.OfType<ContainerRow>().Count()} of {_rows.Count} containers";

    [ObservableProperty]
    public partial string CpuUsageText { get; set; } = "0.00%";

    [ObservableProperty]
    public partial string CpuLimitText { get; set; } = "";

    [ObservableProperty]
    public partial string MemoryUsageText { get; set; } = "0 B";

    [ObservableProperty]
    public partial string MemoryLimitText { get; set; } = "";

    /// <summary>The containers ticked (themselves or with their stack), for the selection bar.</summary>
    public IReadOnlyList<ContainerRow> Selected => _rows.Values.Where(r => r.IsSelected).ToList();

    public int SelectedCount => Selected.Count;

    public bool HasSelection => SelectedCount > 0;

    public string SelectionText => SelectedCount == 1 ? "1 item selected" : $"{SelectedCount} items selected";

    /// <summary>The header's box: every container shown is ticked.</summary>
    public bool AllSelected
    {
        get => Rows.Count > 0 && Rows.OfType<ContainerRow>().All(r => r.IsSelected);
        set
        {
            _selecting = true;
            foreach (var row in Rows)
            {
                if (row is ContainerRow c)
                    c.IsSelected = value;
                else if (row is StackRow s)
                    s.IsSelected = value;
            }
            _selecting = false;
            SelectionChanged();
        }
    }

    internal void SelectionChanged()
    {
        if (_selecting)
            return;
        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(AllSelected));
    }

    /// <summary>The open solution's compose file, for the banner's Compose up / down.</summary>
    public string? ComposeFile => _explorer.Host.SolutionDirectory is { } dir ? Compose.FileIn(dir) : null;

    public bool HasComposeFile => ComposeFile is not null;

    public string ComposeFileName => ComposeFile is { } file ? Path.GetFileName(file) : "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsComposeBusy))]
    public partial string? ComposeBusyText { get; set; }

    public bool IsComposeBusy => ComposeBusyText is not null;

    partial void OnSearchChanged(string value) => Rebuild();

    partial void OnOnlyRunningChanged(bool value) => Rebuild();

    partial void OnSortColumnChanged(string value) => Rebuild();

    partial void OnSortAscendingChanged(bool value) => Rebuild();

    /// <summary>A new list from the engine: rows are updated in place, added and removed.</summary>
    public void Apply(IReadOnlyList<DockerContainer> containers)
    {
        var ids = containers.Select(c => c.Id).ToHashSet();
        foreach (var gone in _rows.Keys.Where(id => !ids.Contains(id)).ToList())
            _rows.Remove(gone);
        foreach (var container in containers)
        {
            if (_rows.TryGetValue(container.Id, out var row))
                row.Container = container;
            else
                _rows[container.Id] = row = new ContainerRow(this, container);
            if (!container.IsRunning)
                row.ClearStats();
        }
        var solution = _explorer.Host.SolutionDirectory;
        foreach (var stack in _stacks.Values)
            stack.Children.Clear();
        foreach (var row in _rows.Values.Where(r => r.Container.Project is not null))
        {
            var project = row.Container.Project!;
            if (!_stacks.TryGetValue(project, out var stack))
                _stacks[project] = stack = new StackRow(this, project);
            stack.Children.Add(row);
        }
        foreach (var empty in _stacks.Where(s => s.Value.Children.Count == 0).Select(s => s.Key).ToList())
            _stacks.Remove(empty);
        foreach (var stack in _stacks.Values)
        {
            stack.IsThisSolution = solution is not null && stack.Children.Any(c => Compose.IsInFolder(c.Container.WorkingDir, solution));
            stack.Refresh();
        }
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(ComposeFile));
        OnPropertyChanged(nameof(HasComposeFile));
        OnPropertyChanged(nameof(ComposeFileName));
        Rebuild();
        SelectionChanged();
    }

    /// <summary>A stats sample: the container's CPU and memory, and its stack's.</summary>
    public void ApplyStats(DockerStats stats)
    {
        var row = _rows.Values.FirstOrDefault(r => r.Container.Id.StartsWith(stats.Id, StringComparison.Ordinal) || r.Container.Name == stats.Id);
        if (row is null || !row.Container.IsRunning)
            return;
        row.ApplyStats(stats);
        if (row.Container.Project is { } project && _stacks.TryGetValue(project, out var stack))
            stack.Refresh();
    }

    internal void UpdateSummary(double cpu, long memory, int cpus, long memoryTotal)
    {
        CpuUsageText = cpu.ToString("0.00", CultureInfo.InvariantCulture) + "%";
        CpuLimitText = $"/ {cpus * 100}% ({cpus} CPUs available)";
        MemoryUsageText = DockerFormat.Size(memory);
        MemoryLimitText = memoryTotal > 0 ? "/ " + DockerFormat.Size(memoryTotal) : "";
    }

    bool Matches(ContainerRow row, string search) =>
        (!OnlyRunning || row.IsUp)
        && (search.Length == 0 || row.Container.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
            || row.Container.Image.Contains(search, StringComparison.OrdinalIgnoreCase)
            || row.Container.Id.StartsWith(search, StringComparison.OrdinalIgnoreCase)
            || row.Container.Project?.Contains(search, StringComparison.OrdinalIgnoreCase) == true);

    /// <summary>The rows in the table's order, filtered; the open solution's stack first.</summary>
    internal void Rebuild()
    {
        var search = Search.Trim();
        var items = new List<(object Item, string Name, string Image, DateTimeOffset? Started, double Cpu, bool First, IReadOnlyList<ContainerRow> Children)>();
        foreach (var stack in _stacks.Values)
        {
            var children = stack.Children.Where(c => Matches(c, search)).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
            if (children.Count == 0)
                continue;
            items.Add((stack, stack.Name, "", children.Max(c => c.Container.StartedAt), children.Sum(c => c.Cpu ?? 0), stack.IsThisSolution, children));
        }
        foreach (var row in _rows.Values.Where(r => r.Container.Project is null && Matches(r, search)))
            items.Add((row, row.Name, row.Image, row.Container.StartedAt, row.Cpu ?? 0, false, []));
        IOrderedEnumerable<(object Item, string Name, string Image, DateTimeOffset? Started, double Cpu, bool First, IReadOnlyList<ContainerRow> Children)> ordered =
            items.OrderByDescending(i => i.First);
        ordered = (SortColumn, SortAscending) switch
        {
            ("image", true) => ordered.ThenBy(i => i.Image, StringComparer.OrdinalIgnoreCase),
            ("image", false) => ordered.ThenByDescending(i => i.Image, StringComparer.OrdinalIgnoreCase),
            ("started", true) => ordered.ThenBy(i => i.Started ?? DateTimeOffset.MinValue),
            ("started", false) => ordered.ThenByDescending(i => i.Started ?? DateTimeOffset.MinValue),
            ("cpu", true) => ordered.ThenBy(i => i.Cpu),
            ("cpu", false) => ordered.ThenByDescending(i => i.Cpu),
            (_, false) => ordered.ThenByDescending(i => i.Name, StringComparer.OrdinalIgnoreCase),
            _ => ordered.ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
        };
        var flat = new List<object>();
        foreach (var item in ordered)
        {
            flat.Add(item.Item);
            if (item.Item is StackRow { IsExpanded: true })
                flat.AddRange(item.Children);
        }
        Sync(Rows, flat);
        OnPropertyChanged(nameof(HasNoMatches));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(AllSelected));
    }

    /// <summary>Makes <paramref name="target"/> show <paramref name="items"/> by moving, adding and removing, not by starting over.</summary>
    internal static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> items) where T : class
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], items[i]))
                continue;
            var old = -1;
            for (var j = i + 1; j < target.Count; j++)
                if (ReferenceEquals(target[j], items[i]))
                {
                    old = j;
                    break;
                }
            if (old >= 0)
                target.Move(old, i);
            else
                target.Insert(i, items[i]);
        }
        while (target.Count > items.Count)
            target.RemoveAt(target.Count - 1);
    }

    // ---- Commands ---------------------------------------------------------------------------------------------

    [RelayCommand]
    void SortBy(string column)
    {
        if (SortColumn == column)
            SortAscending = !SortAscending;
        else
        {
            SortColumn = column;
            SortAscending = column is "name" or "image";
        }
    }

    /// <summary>The row's start / stop button: a stack starts or stops all of its containers.</summary>
    internal Task StartStopAsync(object? row) => row switch
    {
        ContainerRow c => _explorer.ActAsync(c.IsPaused ? "unpause" : c.IsUp ? "stop" : "start", [c]),
        StackRow s => Stack(s, s.IsUp ? "stop" : "start"),
        _ => Task.CompletedTask,
    };

    internal Task RestartAsync(object? row) => row switch
    {
        ContainerRow c => _explorer.ActAsync("restart", [c]),
        StackRow s => Stack(s, "restart"),
        _ => Task.CompletedTask,
    };

    internal Task PauseResumeAsync(ContainerRow? row) => row is null ? Task.CompletedTask : _explorer.ActAsync(row.IsPaused ? "unpause" : "pause", [row]);

    internal Task DeleteAsync(object? row) => row switch
    {
        ContainerRow c => _explorer.ActAsync("rm", [c]),
        StackRow s => Stack(s, "rm"),
        _ => Task.CompletedTask,
    };

    async Task Stack(StackRow stack, string verb)
    {
        var children = stack.Children.Where(c => verb switch { "start" => !c.IsUp, "stop" => c.IsUp, _ => true }).ToList();
        if (verb == "rm" && !await _explorer.ConfirmAsync($"Delete {stack.Name}?",
                $"Its {stack.Children.Count} container{(stack.Children.Count == 1 ? "" : "s")} will be stopped and deleted; named volumes stay.", "Delete", danger: true))
            return;
        stack.BusyText = verb switch { "start" => "Starting…", "stop" => "Stopping…", "restart" => "Restarting…", _ => "Deleting…" };
        try
        {
            // The stack's confirmation was asked above; its containers go without asking again.
            if (verb == "rm")
            {
                var result = await _explorer.Engine.ContainersAsync("rm", children.Select(c => c.Container.Id));
                _explorer.Report(result, $"delete {stack.Name}");
                await _explorer.RefreshContainersAsync();
                await _explorer.RefreshDiskAsync();
            }
            else
            {
                await _explorer.ActAsync(verb, children);
            }
        }
        finally
        {
            stack.BusyText = null;
        }
    }

    internal void Open(ContainerRow row) => _explorer.OpenContainer(row);

    internal void OpenPort(DockerPort? port)
    {
        if (port?.Url is { } url)
            _explorer.Host.OpenUrl(url);
    }

    internal void OpenImage(ContainerRow row) => _explorer.OpenImage(row.Container.Image);

    [RelayCommand]
    Task StartSelected() => _explorer.ActAsync("start", Selected.Where(r => !r.IsUp).ToList());

    [RelayCommand]
    Task StopSelected() => _explorer.ActAsync("stop", Selected.Where(r => r.IsUp).ToList());

    [RelayCommand]
    Task DeleteSelected() => _explorer.ActAsync("rm", Selected);

    [RelayCommand]
    void ClearSelection() => AllSelected = false;

    [RelayCommand]
    void ClearSearch() => Search = "";

    /// <summary>docker compose up -d in the solution's folder (its output in Output > Docker).</summary>
    [RelayCommand]
    Task ComposeUp() => ComposeAsync("Starting…", ["up", "-d"]);

    [RelayCommand]
    async Task ComposeDown()
    {
        if (await _explorer.ConfirmAsync($"Compose down?", $"The containers and networks of {ComposeFileName} are stopped and removed; named volumes stay.", "Compose down", danger: true))
            await ComposeAsync("Stopping…", ["down"]);
    }

    async Task ComposeAsync(string busy, string[] args)
    {
        if (_explorer.Host.SolutionDirectory is not { } folder || ComposeFile is null)
            return;
        ComposeBusyText = busy;
        _explorer.Host.WriteOutput($"> {_explorer.Engine.Name} compose {string.Join(' ', args)}");
        try
        {
            var result = await _explorer.Engine.ComposeAsync(folder, args, _explorer.Host.WriteOutput);
            if (!result.Succeeded)
            {
                _explorer.Report(_explorer.Engine.Failure($"compose {args[0]}: {result.Message.Split('\n').LastOrDefault()}"));
                _explorer.Host.ShowOutput();
            }
        }
        catch (DockerException ex)
        {
            _explorer.Report(ex);
        }
        finally
        {
            ComposeBusyText = null;
        }
        await _explorer.RefreshAllAsync();
    }
}
