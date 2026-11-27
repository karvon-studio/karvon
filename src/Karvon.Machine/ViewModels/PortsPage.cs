using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Karvon.Claude.Tools;

namespace Karvon.Machine.ViewModels;

/// <summary>
/// A port a process listens on, as its row shows it: the addresses it is bound to put together (a service bound to each
/// interface's address is one row, not one a address).
/// </summary>
public sealed class PortRow
{
    public PortRow(IReadOnlyList<ListeningPort> ports)
    {
        Ports = ports;
        Port = ports[0];
    }

    /// <summary>Its sockets: the same protocol, port and process, on each address.</summary>
    public IReadOnlyList<ListeningPort> Ports { get; }

    public ListeningPort Port { get; }

    public IEnumerable<string> Addresses => Ports.Select(p => p.Address);

    public string PortText => Port.Port.ToString(CultureInfo.InvariantCulture);

    public string Protocol => Port.Protocol;

    public bool IsEverywhere => Ports.Any(p => p.IsEverywhere);

    public bool IsLocalOnly => Ports.All(p => p.IsLocalOnly);

    public string AddressText => IsEverywhere ? "All addresses" : IsLocalOnly ? "This computer only"
        : Ports.Count == 1 ? Port.Address : $"{Port.Address} +{Ports.Count - 1}";

    public string AddressTip => (IsEverywhere ? "Reached from the network too (unless a firewall says no):\n"
        : IsLocalOnly ? "Only programs on this computer reach it:\n" : "") + string.Join("\n", Addresses);

    public string ProcessText => Port.Process ?? (Port.Pid is null ? "Another user's" : "?");

    public string PidText => Port.Pid?.ToString(CultureInfo.InvariantCulture) ?? "";

    public string UserText => Port.User ?? "";

    /// <summary>Its command line, its secrets hidden.</summary>
    public string CommandText => Redaction.Text(Port.CommandLine ?? "");

    public bool CanOpen => Port.Protocol == "TCP";

    /// <summary>Its process is the user's own (not another user's, not the system's).</summary>
    public bool IsMine => Port.Pid is not null && (Port.User is null || Port.User == Environment.UserName);

    /// <summary>Its process may be stopped from here (not Karvon itself, not one it cannot tell).</summary>
    public bool CanStop => Port.Pid is > 1 and var pid && pid != Environment.ProcessId;

    public bool IsKarvon => Port.Pid == Environment.ProcessId;

    internal string Identity => $"{Protocol} {Port.Port} {Port.Pid} {string.Join(',', Addresses)}";

    /// <summary>The ports with their sockets on several addresses put together, in the port's order.</summary>
    internal static List<PortRow> Group(IEnumerable<ListeningPort> ports) =>
    [
        .. ports.GroupBy(p => (p.Protocol, p.Port, p.Pid, p.Process))
            .Select(g => new PortRow([.. g.OrderBy(p => p.IsEverywhere ? 0 : p.IsLocalOnly ? 2 : 1).ThenBy(p => p.Address, StringComparer.Ordinal)]))
            .OrderBy(r => r.Port.Port).ThenBy(r => r.Protocol, StringComparer.Ordinal),
    ];
}

/// <summary>
/// The Ports page: the ports this computer listens on, each with the process that holds it; how many, how many are the
/// user's, how many the network reaches; searched, or the user's alone; a port opened in the browser, its process stopped
/// (asked first).
/// </summary>
public sealed partial class PortsPage(MachineExplorerViewModel explorer) : ObservableObject
{
    List<PortRow> _all = [];

    public MachineExplorerViewModel Explorer { get; } = explorer;

    public ObservableCollection<PortRow> Rows { get; } = [];

    [ObservableProperty]
    public partial string Search { get; set; } = "";

    /// <summary>Only the user's own programs' ports (not the system's, not another user's).</summary>
    [ObservableProperty]
    public partial bool MineOnly { get; set; }

    [ObservableProperty]
    public partial string? Problem { get; set; }

    public bool HasProblem => Problem is not null;

    public string ListeningText => _all.Count.ToString(CultureInfo.InvariantCulture);

    public string MineText => _all.Count(r => r.IsMine).ToString(CultureInfo.InvariantCulture);

    public string NetworkText => _all.Count(r => !r.IsLocalOnly).ToString(CultureInfo.InvariantCulture);

    public string CountText => Rows.Count == _all.Count ? $"{_all.Count} ports" : $"Showing {Rows.Count} of {_all.Count}";

    partial void OnSearchChanged(string value) => Show();

    partial void OnMineOnlyChanged(bool value) => Show();

    partial void OnProblemChanged(string? value) => OnPropertyChanged(nameof(HasProblem));

    internal void Update(IReadOnlyList<ListeningPort> ports)
    {
        _all = PortRow.Group(ports);
        Show();
        OnPropertyChanged(nameof(ListeningText));
        OnPropertyChanged(nameof(MineText));
        OnPropertyChanged(nameof(NetworkText));
    }

    /// <summary>The rows the search lets through; left as they are when nothing changed (no flicker as it reads again).</summary>
    void Show()
    {
        var words = Search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        List<PortRow> shown = [.. _all.Where(r => (!MineOnly || r.IsMine) && words.All(w =>
            $"{r.PortText} {r.Protocol} {string.Join(' ', r.Addresses)} {r.Port.Process} {r.Port.Pid} {r.Port.User} {r.Port.CommandLine}".Contains(w, StringComparison.OrdinalIgnoreCase)))];
        if (!shown.Select(r => r.Identity).SequenceEqual(Rows.Select(r => r.Identity)))
        {
            Rows.Clear();
            foreach (var row in shown)
                Rows.Add(row);
        }
        OnPropertyChanged(nameof(CountText));
    }

    [RelayCommand]
    void Open(PortRow? row)
    {
        if (row is { CanOpen: true })
            Explorer.Host.OpenUrl(row.Port.Url);
    }

    /// <summary>The port's process stopped, when the user says so.</summary>
    [RelayCommand]
    async Task Stop(PortRow? row)
    {
        if (row is not { CanStop: true, Port.Pid: { } pid })
            return;
        if (await Explorer.Host.ConfirmAsync("Stop the Process",
                $"Stop {row.ProcessText} (process {pid})? It listens on {row.Protocol} port {row.PortText}; what it has not saved is lost.") != true)
            return;
        Problem = await Task.Run(() => MachineExplorerViewModel.StopProcess(pid));
        await Explorer.RefreshAsync();
    }
}
