using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Karvon.Claude.Tools;

namespace Karvon.Machine.ViewModels;

/// <summary>A process, as its row shows it: kept from one look to the next, its figures changed in place.</summary>
public sealed partial class ProcessRow(ProcessInfo info) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CpuText), nameof(MemoryText), nameof(UserText), nameof(CommandText), nameof(Name), nameof(IsBusy))]
    public partial ProcessInfo Info { get; set; } = info;

    public int Pid => Info.Pid;

    public string Name => Info.Name;

    public string PidText => Info.Pid.ToString(CultureInfo.InvariantCulture);

    public string CpuText => Info.Cpu >= 0.05 ? $"{Info.Cpu:0.0}%" : "0%";

    public string MemoryText => ResourceSampler.Bytes(Info.Memory);

    public string UserText => Info.User ?? "";

    /// <summary>Its command line, its secrets hidden.</summary>
    public string CommandText => Redaction.Text(Info.CommandLine ?? "");

    /// <summary>The ports it listens on: "5000, 5001".</summary>
    [ObservableProperty]
    public partial string PortsText { get; set; } = "";

    /// <summary>It takes a good share of the CPU now (shown as such).</summary>
    public bool IsBusy => Info.Cpu >= 25;

    public bool IsMine => Info.User is null || Info.User == Environment.UserName;

    public bool CanStop => Info.Pid > 1 && Info.Pid != Environment.ProcessId;

    public bool IsKarvon => Info.Pid == Environment.ProcessId;
}

/// <summary>
/// The Processes page: what runs on this computer, by the CPU or memory it uses (or its name, its id), each with the ports
/// it listens on; the whole machine's CPU and memory above; searched, or the user's alone; a process stopped (asked first).
/// </summary>
public sealed partial class ProcessesPage(MachineExplorerViewModel explorer) : ObservableObject
{
    readonly Dictionary<int, ProcessRow> _rows = [];
    IReadOnlyList<ProcessInfo> _all = [];
    ResourceUsage _resources = ResourceUsage.None;
    ILookup<int?, ListeningPort> _ports = Array.Empty<ListeningPort>().ToLookup(p => p.Pid);

    public MachineExplorerViewModel Explorer { get; } = explorer;

    public ObservableCollection<ProcessRow> Rows { get; } = [];

    [ObservableProperty]
    public partial string Search { get; set; } = "";

    [ObservableProperty]
    public partial bool MineOnly { get; set; }

    /// <summary>"cpu" and "memory" most first, "name" and "pid" in their order.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CpuSort), nameof(MemorySort), nameof(NameSort), nameof(PidSort))]
    public partial string SortBy { get; set; } = "cpu";

    public string CpuSort => SortBy == "cpu" ? "↓" : "";

    public string MemorySort => SortBy == "memory" ? "↓" : "";

    public string NameSort => SortBy == "name" ? "↑" : "";

    public string PidSort => SortBy == "pid" ? "↑" : "";

    [ObservableProperty]
    public partial string? Problem { get; set; }

    public bool HasProblem => Problem is not null;

    public string CpuUsageText => $"{_resources.Cpu.Total:0.0}%";

    public string CpuLimitText => $"/ 100% ({Environment.ProcessorCount} CPUs)";

    public string MemoryUsageText => ResourceSampler.Bytes(_resources.Memory.Used);

    public string MemoryLimitText => "/ " + ResourceSampler.Bytes(_resources.Memory.Total);

    public string ProcessCountText => _all.Count.ToString(CultureInfo.InvariantCulture);

    public string CountText => Rows.Count == _all.Count ? $"{_all.Count} processes" : $"Showing {Rows.Count} of {_all.Count}";

    partial void OnSearchChanged(string value) => Show();

    partial void OnMineOnlyChanged(bool value) => Show();

    partial void OnSortByChanged(string value) => Show();

    partial void OnProblemChanged(string? value) => OnPropertyChanged(nameof(HasProblem));

    internal void Update(IReadOnlyList<ProcessInfo> processes, IReadOnlyList<ListeningPort> ports, ResourceUsage resources)
    {
        _all = processes;
        _resources = resources;
        _ports = ports.Where(p => p.Pid is not null).ToLookup(p => p.Pid);
        var seen = processes.Select(p => p.Pid).ToHashSet();
        foreach (var gone in _rows.Keys.Where(k => !seen.Contains(k)).ToList())
            _rows.Remove(gone);
        foreach (var process in processes)
        {
            if (_rows.TryGetValue(process.Pid, out var row))
                row.Info = process;
            else
                _rows[process.Pid] = row = new ProcessRow(process);
            row.PortsText = string.Join(", ", _ports[process.Pid].Select(p => p.Port).Distinct().Order());
        }
        Show();
        OnPropertyChanged(nameof(CpuUsageText));
        OnPropertyChanged(nameof(MemoryUsageText));
        OnPropertyChanged(nameof(MemoryLimitText));
        OnPropertyChanged(nameof(ProcessCountText));
    }

    void Show()
    {
        var words = Search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var rows = _rows.Values.Where(r => (!MineOnly || r.IsMine) && words.All(w =>
            $"{r.Name} {r.Pid} {r.Info.User} {r.Info.CommandLine} {r.PortsText}".Contains(w, StringComparison.OrdinalIgnoreCase)));
        List<ProcessRow> wanted = SortBy switch
        {
            "memory" => [.. rows.OrderByDescending(r => r.Info.Memory).ThenBy(r => r.Pid)],
            "name" => [.. rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Pid)],
            "pid" => [.. rows.OrderBy(r => r.Pid)],
            _ => [.. rows.OrderByDescending(r => r.Info.Cpu).ThenByDescending(r => r.Info.Memory).ThenBy(r => r.Pid)],
        };
        Sync(Rows, wanted);
        OnPropertyChanged(nameof(CountText));
    }

    /// <summary>The rows in the order wanted, moved rather than made again (the list keeps its place as it reads again).</summary>
    static void Sync(ObservableCollection<ProcessRow> rows, List<ProcessRow> wanted)
    {
        var keep = wanted.ToHashSet();
        for (var i = rows.Count - 1; i >= 0; i--)
            if (!keep.Contains(rows[i]))
                rows.RemoveAt(i);
        for (var i = 0; i < wanted.Count; i++)
        {
            if (i < rows.Count && rows[i] == wanted[i])
                continue;
            var at = rows.IndexOf(wanted[i]);
            if (at >= 0)
                rows.Move(at, i);
            else
                rows.Insert(i, wanted[i]);
        }
    }

    [RelayCommand]
    void Sort(string by) => SortBy = by;

    /// <summary>A process stopped, when the user says so.</summary>
    [RelayCommand]
    async Task Stop(ProcessRow? row)
    {
        if (row is not { CanStop: true })
            return;
        var ports = row.PortsText.Length > 0 ? $" It listens on {row.PortsText}." : "";
        if (await Explorer.Host.ConfirmAsync("Stop the Process", $"Stop {row.Name} (process {row.Pid})?{ports} What it has not saved is lost.") != true)
            return;
        Problem = MachineExplorerViewModel.StopProcess(row.Pid);
        await Explorer.RefreshAsync();
    }
}
