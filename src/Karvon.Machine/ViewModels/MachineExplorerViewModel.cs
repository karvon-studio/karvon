using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Karvon.ViewModels;

namespace Karvon.Machine.ViewModels;

/// <summary>What This Computer asks of Karvon.</summary>
public interface IMachineHost
{
    Task<bool?> ConfirmAsync(string title, string message);

    void OpenUrl(string url);

    /// <summary>The explorer shows: what it lists is read again as it goes.</summary>
    bool IsShown { get; }

    IEnumerable<DocumentTab> Documents { get; }

    void ShowDocument(DocumentTab document);

    /// <summary>A file in Karvon's editor (what is not a picture, a video or a sound).</summary>
    void OpenFile(string path);
}

/// <summary>A section of the explorer (Ports, Processes, Resources): how much there is, and its page when it is chosen.</summary>
public sealed partial class MachineSection(MachineExplorerViewModel explorer, string id, string title, string icon, string description) : ObservableObject
{
    public string Id { get; } = id;

    public string Title { get; } = title;

    public string Icon { get; } = icon;

    public string Description { get; } = description;

    /// <summary>"44", "312", "12% CPU": after its title.</summary>
    [ObservableProperty]
    public partial string Detail { get; set; } = "";

    [RelayCommand]
    public void Open() => explorer.Open(this);
}

/// <summary>A section's page in the document well (as Docker Desktop's Containers): the explorer keeps it.</summary>
public sealed class MachineDocument(MachineExplorerViewModel explorer, MachineSection section, object page) : DocumentTab
{
    public MachineExplorerViewModel Explorer { get; } = explorer;

    public MachineSection Section { get; } = section;

    /// <summary>Its page: <see cref="PortsPage"/>, <see cref="ProcessesPage"/>, <see cref="ResourcesPage"/> or <see cref="DisksPage"/>.</summary>
    public object Content { get; } = page;

    public override string Key => "machine:" + Section.Id;

    public override string Title => Section.Title;

    public override string ToolTip => "This Computer: " + Section.Title;
}

/// <summary>
/// This Computer's explorer: its sections (the ports it listens on, its processes, what it uses), each opening its page
/// in the document well; all of it read again every few seconds while the explorer or a page shows.
/// </summary>
public sealed partial class MachineExplorerViewModel : ObservableObject, IDisposable
{
    readonly IMachineHost _host;
    readonly DispatcherTimer _timer;
    readonly ProcessSampler _processes = new();
    readonly ResourceSampler _resources = new();
    bool _refreshing;
    IReadOnlyList<Volume> _volumes = [];
    DateTime _volumesAt;

    public MachineExplorerViewModel(IMachineHost host)
    {
        _host = host;
        Ports = new PortsPage(this);
        Processes = new ProcessesPage(this);
        Resources = new ResourcesPage();
        Disks = new DisksPage(this);
        Sections =
        [
            new(this, "ports", "Ports", "ToolPorts", "The ports this computer listens on, each with the process that holds it."),
            new(this, "processes", "Processes", "Processes", "What runs on this computer: the CPU and memory each process uses."),
            new(this, "resources", "Resources", "Activity", "The CPU, memory, disks and network of this computer."),
            new(this, "disks", "Disks", "Disk", "Its disks, and their files: browsed, measured, pictures, videos and sounds opened in Karvon."),
        ];
        ListPorts = Listening.All;
        SampleProcesses = _processes.Sample;
        SampleResources = _resources.Sample;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _timer.Tick += async (_, _) =>
        {
            if (_host.IsShown || _host.Documents.OfType<MachineDocument>().Any())
                await RefreshAsync();
        };
        _timer.Start();
    }

    internal IMachineHost Host => _host;

    public IReadOnlyList<MachineSection> Sections { get; }

    public PortsPage Ports { get; }

    public ProcessesPage Processes { get; }

    public ResourcesPage Resources { get; }

    public DisksPage Disks { get; }

    /// <summary>How the machine is read (the tests' own).</summary>
    internal Func<IReadOnlyList<ListeningPort>> ListPorts { get; set; }

    internal Func<IReadOnlyList<ProcessInfo>> SampleProcesses { get; set; }

    internal Func<IReadOnlyList<ProcessInfo>, ResourceUsage> SampleResources { get; set; }

    /// <summary>The disks' file systems (lsblk): read again every few refreshes, they change seldom.</summary>
    internal Func<IReadOnlyList<Volume>> ListVolumes { get; set; } = Volumes.Read;

    /// <summary>A disk not mounted, mounted (udisks); why not, when it was not.</summary>
    internal Func<string, CancellationToken, Task<string?>> MountVolume { get; set; } = Volumes.MountAsync;

    /// <summary>The disks' file systems read again at the next refresh (one was mounted).</summary>
    internal void ForgetVolumes() => _volumesAt = default;

    /// <summary>The explorer's foot: what the machine uses now.</summary>
    [ObservableProperty]
    public partial string CpuText { get; set; } = "";

    [ObservableProperty]
    public partial string MemoryText { get; set; } = "";

    [ObservableProperty]
    public partial string UptimeText { get; set; } = "";

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (_refreshing)
            return;
        _refreshing = true;
        try
        {
            var readVolumes = DateTime.UtcNow - _volumesAt > TimeSpan.FromSeconds(10);
            var (ports, processes, resources, volumes) = await Task.Run(() =>
            {
                var ports = ListPorts();
                var processes = SampleProcesses();
                return (ports, processes, SampleResources(processes), readVolumes ? ListVolumes() : null);
            });
            if (volumes is not null)
                (_volumes, _volumesAt) = (volumes, DateTime.UtcNow);
            Ports.Update(ports);
            Processes.Update(processes, ports, resources);
            Resources.Update(resources);
            Disks.Update(resources.Disks, _volumes);
            Sections[0].Detail = ports.Select(p => (p.Protocol, p.Port, p.Pid)).Distinct().Count().ToString(System.Globalization.CultureInfo.InvariantCulture);
            Sections[1].Detail = processes.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Sections[2].Detail = $"{resources.Cpu.Total:0}% CPU";
            Sections[3].Detail = resources.Disks.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            CpuText = $"CPU {resources.Cpu.Total:0}% of {Environment.ProcessorCount} cores";
            MemoryText = resources.Memory.Total > 0
                ? $"Memory {ResourceSampler.Bytes(resources.Memory.Used)} of {ResourceSampler.Bytes(resources.Memory.Total)}" : "";
            UptimeText = "Up " + ResourcesPage.Took(resources.Uptime);
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>A section's page, in front in the document well (the one it had, when it is still open).</summary>
    public void Open(MachineSection section)
    {
        var document = _host.Documents.OfType<MachineDocument>().FirstOrDefault(d => d.Section == section)
            ?? new MachineDocument(this, section, section.Id switch
            {
                "ports" => Ports,
                "processes" => Processes,
                "disks" => Disks,
                _ => (object)Resources,
            });
        _host.ShowDocument(document);
        _ = RefreshAsync();
    }

    /// <summary>A file opened: a picture, a video or a sound in a page of its own (the one it has, when open), the rest in the editor.</summary>
    public void OpenFile(string path)
    {
        var kind = Files.KindOf(path);
        if (kind is FileKind.Other or FileKind.Folder)
        {
            _host.OpenFile(path);
            return;
        }
        var key = "machine-file:" + path;
        if (_host.Documents.FirstOrDefault(d => d.Key == key) is { } open)
        {
            _host.ShowDocument(open);
            return;
        }
        if (kind == FileKind.Image)
        {
            _host.ShowDocument(new MachineFileDocument(path, new ImagePage(path)));
            return;
        }
        var media = new MediaPage(path);
        var document = new MachineFileDocument(path, media);
        // From the mini player: its page in front, opened again (on the same player) when it was closed.
        media.Reveal = () =>
        {
            if (media.IsPageClosed)
            {
                media.PageOpened();
                document = new MachineFileDocument(path, media);
            }
            _host.ShowDocument(document);
        };
        _host.ShowDocument(document);
    }

    /// <summary>A process ended; why not, when it could not be.</summary>
    internal static string? StopProcess(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            process.Kill();
            process.WaitForExit(3000);
            return null;
        }
        catch (ArgumentException)
        {
            // Gone already.
            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return $"Process {pid} could not be stopped: {ex.Message}";
        }
    }

    public void Dispose() => _timer.Stop();
}
