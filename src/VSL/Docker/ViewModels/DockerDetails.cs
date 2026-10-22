using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VSL.Terminal;
using VSL.ViewModels;

namespace VSL.Docker.ViewModels;

/// <summary>A mount of a container: a bind mount (a host folder) or a volume.</summary>
public sealed record MountItem(string Type, string Source, string Destination, bool ReadOnly)
{
    public bool IsVolume => Type == "volume";

    public string ModeText => ReadOnly ? "Read only" : "Read and write";
}

/// <summary>A container on a network, with its address there.</summary>
public sealed record NetworkMember(string Id, string Name, string Address);

static class InspectText
{
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Of(JsonElement element) => JsonSerializer.Serialize(element, Indented);
}

/// <summary>
/// A container's page, as Docker Desktop's: its state and ports with the actions over tabs for its logs (followed
/// as they come), inspect, bind mounts, a shell in it (Exec) and live stats.
/// </summary>
public sealed partial class ContainerDetailViewModel : ObservableObject, IDisposable
{
    const int LogTail = 5000;

    readonly DockerExplorerViewModel _explorer;
    DockerStream? _logs;
    JsonElement? _inspect;
    string _lastState;
    bool _disposed;

    internal ContainerDetailViewModel(DockerExplorerViewModel explorer, ContainerRow row)
    {
        _explorer = explorer;
        Row = row;
        _lastState = row.Container.State;
        row.PropertyChanged += OnRowChanged;
    }

    public ContainerRow Row { get; }

    /// <summary>"logs", "inspect", "mounts", "exec" or "stats".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLogs), nameof(IsInspect), nameof(IsMounts), nameof(IsExec), nameof(IsStats))]
    public partial string Tab { get; set; } = "logs";

    public bool IsLogs => Tab == "logs";

    public bool IsInspect => Tab == "inspect";

    public bool IsMounts => Tab == "mounts";

    public bool IsExec => Tab == "exec";

    public bool IsStats => Tab == "stats";

    /// <summary>The logs, standard output and error together, colored as the container wrote them.</summary>
    public OutputChannel Logs { get; } = new("Logs");

    [ObservableProperty]
    public partial bool Timestamps { get; set; }

    [ObservableProperty]
    public partial bool WrapLogs { get; set; } = true;

    [ObservableProperty]
    public partial string InspectJson { get; set; } = "";

    public ObservableCollection<MountItem> Mounts { get; } = [];

    public bool HasNoMounts => Mounts.Count == 0;

    /// <summary>A shell in the container (docker exec -it), started the first time Exec shows.</summary>
    [ObservableProperty]
    public partial TerminalSession? Terminal { get; set; }

    public string StatusText => Row.IsRunning && Row.Container.StartedAt is { } started
        ? $"{Row.StateText} ({DockerFormat.Ago(started)})"
        : Row.Container.FinishedAt is { } finished && !Row.IsUp ? $"{Row.StateText} ({DockerFormat.Ago(finished)})" : Row.StateText;

    /// <summary>The image is a database the Database Explorer knows.</summary>
    public bool IsDatabase => DockerDatabases.ProviderOf(Row.Container.Image) is not null;

    public string AskClaudeText => Row.IsFailing ? "Ask Claude why it failed" : "Ask Claude";

    public void Initialize()
    {
        StartLogs();
        _ = LoadInspectAsync();
    }

    void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ContainerRow.Container))
            return;
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(AskClaudeText));
        // Started again: its new output follows; a shell opened in it before is gone.
        var state = Row.Container.State;
        if (state != _lastState)
        {
            if (state == "running" && _logs?.HasExited != false)
                StartLogs();
            _ = LoadInspectAsync();
        }
        _lastState = state;
    }

    partial void OnTabChanged(string value)
    {
        if (value == "exec")
            OpenShell();
    }

    partial void OnTimestampsChanged(bool value) => StartLogs();

    [RelayCommand]
    void ShowTab(string tab) => Tab = tab;

    void StartLogs()
    {
        _logs?.Dispose();
        Logs.Clear();
        if (_disposed)
            return;
        List<string> args = ["logs", "--follow", "--tail", LogTail.ToString(System.Globalization.CultureInfo.InvariantCulture)];
        if (Timestamps)
            args.Add("--timestamps");
        args.Add(Row.Container.Id);
        try
        {
            _logs = _explorer.Engine.Stream(args, Logs.Append);
        }
        catch (DockerException ex)
        {
            Logs.Append(ex.Message);
        }
    }

    async Task LoadInspectAsync()
    {
        try
        {
            var inspect = await _explorer.Engine.InspectAsync("container", Row.Container.Id);
            if (_disposed)
                return;
            _inspect = inspect;
            InspectJson = InspectText.Of(inspect);
            Mounts.Clear();
            if (inspect.TryGetProperty("Mounts", out var mounts) && mounts.ValueKind == JsonValueKind.Array)
                foreach (var m in mounts.EnumerateArray())
                    Mounts.Add(new MountItem(DockerFormat.Str(m, "Type"), DockerFormat.Str(m, "Type") == "volume" ? DockerFormat.Str(m, "Name") : DockerFormat.Str(m, "Source"),
                        DockerFormat.Str(m, "Destination"), m.TryGetProperty("RW", out var rw) && rw.ValueKind == JsonValueKind.False));
            OnPropertyChanged(nameof(HasNoMounts));
        }
        catch (DockerException ex)
        {
            if (!_disposed)
                InspectJson = ex.Message;
        }
    }

    /// <summary>bash when the image has it, else sh, as Docker Desktop's Exec tab.</summary>
    void OpenShell()
    {
        if (Terminal is not null || !Row.IsRunning)
            return;
        Terminal = new TerminalSession(_explorer.Engine.Program,
            ["exec", "-it", Row.Container.Id, "sh", "-c", "if command -v bash >/dev/null 2>&1; then exec bash; else exec sh; fi"])
        {
            RestartHint = "Press Enter to connect again.",
        };
    }

    [RelayCommand]
    Task StartStop() => _explorer.ActAsync(Row.IsPaused ? "unpause" : Row.IsUp ? "stop" : "start", [Row]);

    [RelayCommand]
    Task Restart() => _explorer.ActAsync("restart", [Row]);

    [RelayCommand]
    Task PauseResume() => _explorer.ActAsync(Row.IsPaused ? "unpause" : "pause", [Row]);

    [RelayCommand]
    Task Delete() => _explorer.ActAsync("rm", [Row]);

    [RelayCommand]
    void OpenImage() => _explorer.OpenImage(Row.Container.Image);

    [RelayCommand]
    void OpenPort(DockerPort? port)
    {
        if (port?.Url is { } url)
            _explorer.Host.OpenUrl(url);
    }

    [RelayCommand]
    void OpenVolume(MountItem? mount)
    {
        if (mount is { IsVolume: true })
            _explorer.OpenVolume(mount.Source);
    }

    [RelayCommand]
    void ClearLogs() => Logs.Clear();

    /// <summary>Claude Code with the container's state and last log lines in the prompt, ready to send.</summary>
    [RelayCommand]
    void AskClaude()
    {
        var c = Row.Container;
        var lines = Logs.Document.Text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).TakeLast(60).ToList();
        var prompt = new StringBuilder();
        prompt.Append($"The Docker container {c.Name} (image {c.Image}{(c.Project is { } p ? $", compose project {p}, service {c.Service}" : "")}) is {Row.StateText.ToLowerInvariant()}.");
        if (lines.Count > 0)
            prompt.Append("\nIts last log lines:\n```\n").Append(string.Join("\n", lines)).Append("\n```");
        prompt.Append(Row.IsFailing ? "\nWhy did it fail, and how do I fix it?" : "\n");
        _explorer.Host.AskClaude(prompt.ToString());
    }

    [RelayCommand]
    async Task OpenInDatabaseExplorer()
    {
        try
        {
            var inspect = _inspect ?? await _explorer.Engine.InspectAsync("container", Row.Container.Id);
            if (_explorer.Host.OpenInDatabaseExplorer(Row.Container, inspect) is { } problem)
                _explorer.Error = problem;
        }
        catch (DockerException ex)
        {
            _explorer.Report(ex);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        Row.PropertyChanged -= OnRowChanged;
        _logs?.Dispose();
        _logs = null;
        if (Terminal is { } terminal)
            _ = terminal.DisposeAsync().AsTask();
        Terminal = null;
    }
}

/// <summary>An image's page: its layers (docker history), the containers that use it, inspect; Run, Pull, Delete.</summary>
public sealed partial class ImageDetailViewModel : ObservableObject, IDisposable
{
    readonly DockerExplorerViewModel _explorer;

    internal ImageDetailViewModel(DockerExplorerViewModel explorer, ImageRow row)
    {
        _explorer = explorer;
        Row = row;
    }

    public ImageRow Row { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLayers), nameof(IsInspect))]
    public partial string Tab { get; set; } = "layers";

    public bool IsLayers => Tab == "layers";

    public bool IsInspect => Tab == "inspect";

    public ObservableCollection<DockerLayer> Layers { get; } = [];

    [ObservableProperty]
    public partial string InspectJson { get; set; } = "";

    /// <summary>The containers started from it.</summary>
    public IReadOnlyList<ContainerRow> UsedBy => _explorer.Containers.AllRows
        .Where(c => _explorer.Images.Find(c.Container.Image) == Row).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public bool IsUsed => UsedBy.Count > 0;

    public string UsedByText => UsedBy.Count switch { 0 => "No container uses it", 1 => "Used by 1 container", var n => $"Used by {n} containers" };

    public bool CanPull => !Row.Image.IsDangling;

    public async Task LoadAsync()
    {
        try
        {
            var layers = await _explorer.Engine.HistoryAsync(Row.Image.Id);
            Layers.Clear();
            foreach (var layer in layers)
                Layers.Add(layer);
            InspectJson = InspectText.Of(await _explorer.Engine.InspectAsync("image", Row.Image.Id));
        }
        catch (DockerException ex)
        {
            InspectJson = ex.Message;
        }
    }

    [RelayCommand]
    void ShowTab(string tab) => Tab = tab;

    [RelayCommand]
    Task Run() => _explorer.RunImageAsync(Row.Image.Reference);

    [RelayCommand]
    Task Delete() => _explorer.Images.DeleteAsync([Row]);

    [RelayCommand]
    async Task Pull()
    {
        _explorer.Images.PullReference = Row.Image.Reference;
        await _explorer.Images.Pull();
        await LoadAsync();
    }

    [RelayCommand]
    void OpenContainer(ContainerRow? row)
    {
        if (row is not null)
            _explorer.OpenContainer(row);
    }

    public void Dispose()
    {
    }
}

/// <summary>A volume's page: the containers that mount it, and inspect.</summary>
public sealed partial class VolumeDetailViewModel : ObservableObject, IDisposable
{
    readonly DockerExplorerViewModel _explorer;

    internal VolumeDetailViewModel(DockerExplorerViewModel explorer, VolumeRow row)
    {
        _explorer = explorer;
        Row = row;
    }

    public VolumeRow Row { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsContainers), nameof(IsInspect))]
    public partial string Tab { get; set; } = "containers";

    public bool IsContainers => Tab == "containers";

    public bool IsInspect => Tab == "inspect";

    [ObservableProperty]
    public partial string InspectJson { get; set; } = "";

    public IReadOnlyList<ContainerRow> UsedBy => _explorer.Containers.AllRows
        .Where(c => c.Container.Mounts.Contains(Row.Name)).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public bool IsUnused => UsedBy.Count == 0;

    public async Task LoadAsync()
    {
        try
        {
            InspectJson = InspectText.Of(await _explorer.Engine.InspectAsync("volume", Row.Name));
        }
        catch (DockerException ex)
        {
            InspectJson = ex.Message;
        }
    }

    [RelayCommand]
    void ShowTab(string tab) => Tab = tab;

    [RelayCommand]
    Task Delete() => _explorer.Volumes.DeleteAsync([Row]);

    [RelayCommand]
    void OpenContainer(ContainerRow? row)
    {
        if (row is not null)
            _explorer.OpenContainer(row);
    }

    public void Dispose()
    {
    }
}

/// <summary>A network's page: the containers on it with their addresses, and inspect.</summary>
public sealed partial class NetworkDetailViewModel : ObservableObject, IDisposable
{
    readonly DockerExplorerViewModel _explorer;

    internal NetworkDetailViewModel(DockerExplorerViewModel explorer, NetworkRow row)
    {
        _explorer = explorer;
        Row = row;
    }

    public NetworkRow Row { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsContainers), nameof(IsInspect))]
    public partial string Tab { get; set; } = "containers";

    public bool IsContainers => Tab == "containers";

    public bool IsInspect => Tab == "inspect";

    [ObservableProperty]
    public partial string InspectJson { get; set; } = "";

    [ObservableProperty]
    public partial string SubnetText { get; set; } = "";

    public ObservableCollection<NetworkMember> Members { get; } = [];

    public bool HasNoMembers => Members.Count == 0;

    public async Task LoadAsync()
    {
        try
        {
            var inspect = await _explorer.Engine.InspectAsync("network", Row.Network.Id);
            InspectJson = InspectText.Of(inspect);
            Members.Clear();
            if (inspect.TryGetProperty("Containers", out var containers) && containers.ValueKind == JsonValueKind.Object)
                foreach (var member in containers.EnumerateObject().OrderBy(m => DockerFormat.Str(m.Value, "Name"), StringComparer.OrdinalIgnoreCase))
                    Members.Add(new NetworkMember(member.Name, DockerFormat.Str(member.Value, "Name"), DockerFormat.Str(member.Value, "IPv4Address")));
            OnPropertyChanged(nameof(HasNoMembers));
            if (inspect.TryGetProperty("IPAM", out var ipam) && ipam.TryGetProperty("Config", out var config) && config.ValueKind == JsonValueKind.Array)
                SubnetText = string.Join(", ", config.EnumerateArray().Select(c => DockerFormat.Str(c, "Subnet")).Where(s => s.Length > 0));
        }
        catch (DockerException ex)
        {
            InspectJson = ex.Message;
        }
    }

    [RelayCommand]
    void ShowTab(string tab) => Tab = tab;

    [RelayCommand]
    Task Delete() => _explorer.Networks.DeleteAsync([Row]);

    [RelayCommand]
    void OpenContainer(NetworkMember? member)
    {
        if (member is not null)
            _explorer.OpenContainer(member.Id);
    }

    public void Dispose()
    {
    }
}

/// <summary>The databases a container can be opened as in the Database Explorer, from its image and variables.</summary>
public static class DockerDatabases
{
    /// <summary>"PostgreSql", "MySql", "SqlServer" or "MongoDb" for a database image; null otherwise.</summary>
    public static string? ProviderOf(string image)
    {
        var name = image.ToLowerInvariant();
        return name.Contains("postgres") || name.Contains("postgis") || name.Contains("timescale") ? "PostgreSql"
            : name.Contains("mysql") || name.Contains("mariadb") ? "MySql"
            : name.Contains("mssql") || name.Contains("sqlserver") || name.Contains("azure-sql-edge") ? "SqlServer"
            : name.Contains("mongo") ? "MongoDb"
            : null;
    }

    /// <summary>The port the database listens on in the container.</summary>
    public static int PortOf(string provider) => provider switch
    {
        "PostgreSql" => 5432,
        "MySql" => 3306,
        "SqlServer" => 1433,
        _ => 27017,
    };
}
