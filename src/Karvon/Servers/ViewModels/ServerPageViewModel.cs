using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;
using Karvon.Terminal;
using Karvon.ViewModels;
using Karvon.Ui;

namespace Karvon.Servers.ViewModels;

/// <summary>A file or folder on a machine, in the Files tab.</summary>
public sealed partial class RemoteEntry(string name, string path, bool isFolder, long size, DateTime modified, string permissions) : ObservableObject
{
    public string Name { get; } = name;

    public string Path { get; } = path;

    public bool IsFolder { get; } = isFolder;

    public long Size { get; } = size;

    public string SizeText => IsFolder ? "" : RemoteCommands.Size(Size);

    public DateTime Modified { get; } = modified;

    public string ModifiedText => Modified == default ? "" : Modified.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    public string Permissions { get; } = permissions;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    public partial string? BusyText { get; set; }

    public bool IsBusy => BusyText is not null;
}

/// <summary>A folder of the path above the Files table, to go back up to.</summary>
public sealed record RemoteCrumb(string Name, string Path);

/// <summary>A service in the Services tab.</summary>
public sealed partial class ServiceRow(ServerService service) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DotKind))]
    public partial ServerService Service { get; set; } = service;

    public string DotKind => Service.IsFailed ? "failing" : Service.IsRunning ? "running" : Service.Active == "active" ? "paused" : "stopped";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    public partial string? BusyText { get; set; }

    public bool IsBusy => BusyText is not null;
}

/// <summary>
/// A machine's page: an Overview of its state (system, uptime, load, CPU, memory, disks), a terminal (the system's ssh,
/// so the user's config, agent and jump hosts apply), its Files over SFTP (opened in the editor and saved back), its systemd
/// Services, and Logs from the journal or a file, followed as they come.
/// </summary>
public sealed partial class ServerPageViewModel : ObservableObject, IDisposable
{
    readonly ServersExplorerViewModel _explorer;
    CancellationTokenSource? _follow;
    bool _disposed;

    internal ServerPageViewModel(ServersExplorerViewModel explorer, ServerNode node)
    {
        _explorer = explorer;
        Node = node;
    }

    public ServerNode Node { get; }

    ServerHost Host => Node.Host;

    /// <summary>"overview", "terminal", "files", "services" or "logs".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOverview), nameof(IsTerminal), nameof(IsFiles), nameof(IsServices), nameof(IsLogs))]
    public partial string Tab { get; set; } = "overview";

    public bool IsOverview => Tab == "overview";

    public bool IsTerminal => Tab == "terminal";

    public bool IsFiles => Tab == "files";

    public bool IsServices => Tab == "services";

    public bool IsLogs => Tab == "logs";

    partial void OnTabChanged(string value) => _ = ActivateAsync();

    [RelayCommand]
    void ShowTab(string tab) => Tab = tab;

    /// <summary>What went wrong on the page (the red line under its header).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string? Problem { get; set; }

    public bool HasProblem => Problem is not null;

    /// <summary>Connects, then fills the tab in front the first time it shows.</summary>
    public async Task ActivateAsync()
    {
        if (_disposed)
            return;
        if (IsTerminal)
        {
            OpenTerminal();
            return;
        }
        if (!await _explorer.ConnectAsync(Node))
        {
            Problem = Node.Error;
            return;
        }
        Problem = null;
        if (IsOverview && Metrics is null)
            await RefreshOverviewAsync();
        else if (IsFiles && Entries.Count == 0)
            await GoAsync(CurrentPath.Length > 0 ? CurrentPath : ".");
        else if (IsServices && Services.Count == 0)
            await RefreshServicesAsync();
        else if (IsLogs && Logs.Document.TextLength == 0)
            await RefreshLogsAsync();
    }

    [RelayCommand]
    async Task ConnectAgain()
    {
        await _explorer.ConnectAgainAsync(Node);
        (Metrics, Problem) = (null, Node.Error);
        Entries.Clear();
        Services.Clear();
        await ActivateAsync();
    }

    // ---- Overview -------------------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMetrics), nameof(UptimeText), nameof(LoadText), nameof(CpuText), nameof(MemoryText), nameof(MemoryRatio), nameof(SwapText))]
    public partial ServerMetrics? Metrics { get; set; }

    public bool HasMetrics => Metrics is not null;

    public string UptimeText => Metrics?.Uptime is { } up ? RemoteCommands.Duration(up) : "—";

    public string LoadText => Metrics is { Load.Length: 3 } m ? string.Join("  ", m.Load.Select(l => l.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture))) : "—";

    public string CpuText => Metrics?.CpuPercent is { } cpu ? $"{cpu:0}%" + (Metrics.Cpus > 0 ? $" of {Metrics.Cpus} CPU{(Metrics.Cpus == 1 ? "" : "s")}" : "") : "—";

    public string MemoryText => Metrics is { MemoryTotal: > 0 } m ? $"{RemoteCommands.Size(m.MemoryUsed)} / {RemoteCommands.Size(m.MemoryTotal)}" : "—";

    public double MemoryRatio => Metrics is { MemoryTotal: > 0 } m ? (double)m.MemoryUsed / m.MemoryTotal : 0;

    public string SwapText => Metrics is { SwapTotal: > 0 } m ? $"{RemoteCommands.Size(m.SwapTotal - m.SwapFree)} / {RemoteCommands.Size(m.SwapTotal)} swap" : "no swap";

    [ObservableProperty]
    public partial bool IsLoadingOverview { get; set; }

    [RelayCommand]
    public async Task RefreshOverviewAsync()
    {
        IsLoadingOverview = true;
        try
        {
            var result = await _explorer.Connections.RunAsync(Host, RemoteCommands.Metrics, TimeSpan.FromSeconds(30));
            Metrics = RemoteCommands.ParseMetrics(result.Output);
            await _explorer.Connections.PingAsync(Host);
        }
        catch (ServerException ex)
        {
            Problem = ex.Message;
        }
        finally
        {
            IsLoadingOverview = false;
        }
    }

    // ---- Terminal -------------------------------------------------------------------------------------------------

    /// <summary>The system's ssh in a terminal, started the first time the tab shows.</summary>
    [ObservableProperty]
    public partial TerminalSession? Terminal { get; set; }

    /// <summary>ssh's arguments: the alias for a ~/.ssh/config host (its config applies), else user@host with its port and key.</summary>
    public IReadOnlyList<string> SshArguments
    {
        get
        {
            var args = new List<string>();
            if (Host.FromSshConfig)
            {
                if (Environment.GetEnvironmentVariable("KARVON_SSH_HOME") is { Length: > 0 })
                    args.AddRange(["-F", SshConfig.ConfigFile]);
                args.Add(Host.Name);
                return args;
            }
            if (Host.Port != 22)
                args.AddRange(["-p", Host.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
            if (Host.IdentityFiles.FirstOrDefault() is { } key)
                args.AddRange(["-i", key]);
            args.Add($"{Host.User}@{Host.Address}");
            return args;
        }
    }

    void OpenTerminal() => Terminal ??= new TerminalSession("ssh", SshArguments) { RestartHint = "Press Enter to connect again." };

    // ---- Files ----------------------------------------------------------------------------------------------------

    public ObservableCollection<RemoteEntry> Entries { get; } = [];

    public ObservableCollection<RemoteCrumb> Crumbs { get; } = [];

    [ObservableProperty]
    public partial string CurrentPath { get; set; } = "";

    /// <summary>The path box: typed and gone to with Enter.</summary>
    [ObservableProperty]
    public partial string PathText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsLoadingFiles { get; set; }

    [ObservableProperty]
    public partial RemoteEntry? SelectedEntry { get; set; }

    [RelayCommand]
    Task Go() => GoAsync(PathText.Trim().Length > 0 ? PathText.Trim() : ".");

    [RelayCommand]
    Task Up() => GoAsync(CurrentPath == "/" ? "/" : Parent(CurrentPath));

    [RelayCommand]
    Task RefreshFiles() => GoAsync(CurrentPath.Length > 0 ? CurrentPath : ".");

    [RelayCommand]
    Task GoTo(RemoteCrumb? crumb) => crumb is null ? Task.CompletedTask : GoAsync(crumb.Path);

    static string Parent(string path) => path.TrimEnd('/') is var p && p.LastIndexOf('/') is var i and > 0 ? p[..i] : "/";

    static string Join(string folder, string name) => folder.EndsWith('/') ? folder + name : folder + "/" + name;

    /// <summary>Lists a folder (folders first, then by name); "." is the user's home there.</summary>
    public async Task GoAsync(string path)
    {
        IsLoadingFiles = true;
        try
        {
            var sftp = await _explorer.Connections.SftpAsync(Host);
            var full = path == "." ? sftp.WorkingDirectory : path.StartsWith('/') ? path : Join(CurrentPath.Length > 0 ? CurrentPath : sftp.WorkingDirectory, path);
            var entries = new List<RemoteEntry>();
            await foreach (var file in sftp.ListDirectoryAsync(full, CancellationToken.None))
                if (file.Name is not ("." or ".."))
                    entries.Add(new RemoteEntry(file.Name, file.FullName, file.IsDirectory, file.Length, file.LastWriteTimeUtc, Mode(file)));
            Entries.Clear();
            foreach (var entry in entries.OrderBy(e => !e.IsFolder).ThenBy(e => e.Name, StringComparer.Ordinal))
                Entries.Add(entry);
            CurrentPath = PathText = full;
            Crumbs.Clear();
            Crumbs.Add(new RemoteCrumb("/", "/"));
            var at = "";
            foreach (var part in full.Split('/', StringSplitOptions.RemoveEmptyEntries))
                Crumbs.Add(new RemoteCrumb(part, at += "/" + part));
            Problem = null;
        }
        catch (Exception ex) when (ex is ServerException or SshException or IOException)
        {
            Problem = $"{path}: {ex.Message}";
        }
        finally
        {
            IsLoadingFiles = false;
        }
    }

    static string Mode(ISftpFile file) =>
        (file.IsDirectory ? "d" : file.IsSymbolicLink ? "l" : "-")
        + (file.OwnerCanRead ? "r" : "-") + (file.OwnerCanWrite ? "w" : "-") + (file.OwnerCanExecute ? "x" : "-")
        + (file.GroupCanRead ? "r" : "-") + (file.GroupCanWrite ? "w" : "-") + (file.GroupCanExecute ? "x" : "-")
        + (file.OthersCanRead ? "r" : "-") + (file.OthersCanWrite ? "w" : "-") + (file.OthersCanExecute ? "x" : "-");

    /// <summary>A folder opens; a file is copied here and opened in the editor, and saving it puts it back on the machine.</summary>
    [RelayCommand]
    public async Task OpenEntry(RemoteEntry? entry)
    {
        if (entry is null)
            return;
        if (entry.IsFolder)
        {
            await GoAsync(entry.Path);
            return;
        }
        if (entry.Size > 10_000_000)
        {
            Problem = $"{entry.Name} is {RemoteCommands.Size(entry.Size)}: too large for the editor; download it instead.";
            return;
        }
        entry.BusyText = "Opening…";
        try
        {
            var sftp = await _explorer.Connections.SftpAsync(Host);
            var local = _explorer.LocalCopyOf(Host, entry.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(local)!);
            await using (var stream = File.Create(local))
                await sftp.DownloadFileAsync(entry.Path, stream);
            _explorer.RememberCopy(local, Host, entry.Path, sftp.GetLastWriteTimeUtc(entry.Path));
            _explorer.Host.OpenFile(local);
        }
        catch (Exception ex) when (ex is ServerException or SshException or IOException)
        {
            Problem = $"{entry.Name} could not be opened: {ex.Message}";
        }
        finally
        {
            entry.BusyText = null;
        }
    }

    /// <summary>A file to ~/Downloads (another name when one is there already).</summary>
    [RelayCommand]
    async Task Download(RemoteEntry? entry)
    {
        if (entry is not { IsFolder: false })
            return;
        entry.BusyText = "Downloading…";
        try
        {
            var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            Directory.CreateDirectory(downloads);
            var target = Path.Combine(downloads, entry.Name);
            for (var n = 2; File.Exists(target); n++)
                target = Path.Combine(downloads, $"{Path.GetFileNameWithoutExtension(entry.Name)} ({n}){Path.GetExtension(entry.Name)}");
            var sftp = await _explorer.Connections.SftpAsync(Host);
            await using (var stream = File.Create(target))
                await sftp.DownloadFileAsync(entry.Path, stream);
            _explorer.Host.Status($"Downloaded {entry.Name} to {target}.");
        }
        catch (Exception ex) when (ex is ServerException or SshException or IOException)
        {
            Problem = $"{entry.Name} could not be downloaded: {ex.Message}";
        }
        finally
        {
            entry.BusyText = null;
        }
    }

    [RelayCommand]
    async Task Upload()
    {
        var files = await _explorer.Host.PickFilesAsync($"Upload to {Host.Name}:{CurrentPath}");
        await UploadAsync(files);
    }

    /// <summary>Local files into the current folder (dropped on the table, or picked).</summary>
    public async Task UploadAsync(IReadOnlyList<string> files, string? folder = null)
    {
        if (files.Count == 0)
            return;
        folder ??= CurrentPath;
        if (Host.Production && !await _explorer.ConfirmAsync($"Upload to {Host.Name}?",
                $"{Host.Name} is production. {files.Count} file(s) go to {folder}, replacing those with the same names.", "Upload", danger: true))
            return;
        try
        {
            var sftp = await _explorer.Connections.SftpAsync(Host);
            foreach (var file in files.Where(File.Exists))
            {
                _explorer.Host.Status($"Uploading {Path.GetFileName(file)} to {Host.Name}…");
                await using var stream = File.OpenRead(file);
                await sftp.UploadFileAsync(stream, Join(folder, Path.GetFileName(file)));
            }
            _explorer.Host.Status($"Uploaded {files.Count} file(s) to {Host.Name}:{folder}.");
        }
        catch (Exception ex) when (ex is ServerException or SshException or IOException)
        {
            Problem = "The upload failed: " + ex.Message;
        }
        await RefreshFiles();
    }

    [RelayCommand]
    async Task NewFolder()
    {
        if (await _explorer.ShowAsync(new InputDialog("New folder", $"In {CurrentPath}", "folder", "Create")) is not { } dialog)
            return;
        await Change(sftp => sftp.CreateDirectoryAsync(Join(CurrentPath, dialog.Text.Trim())), "The folder could not be created");
    }

    [RelayCommand]
    async Task Rename(RemoteEntry? entry)
    {
        if (entry is null || await _explorer.ShowAsync(new InputDialog($"Rename {entry.Name}", "New name", entry.Name, "Rename") { Text = entry.Name }) is not { } dialog
            || dialog.Text.Trim() == entry.Name)
            return;
        await Change(sftp => sftp.RenameFileAsync(entry.Path, Join(Parent(entry.Path), dialog.Text.Trim()), CancellationToken.None), $"{entry.Name} could not be renamed");
    }

    /// <summary>A file, or a folder with what is in it; always asked first.</summary>
    [RelayCommand]
    async Task Delete(RemoteEntry? entry)
    {
        if (entry is null || !await _explorer.ConfirmAsync($"Delete {entry.Name}?",
                (entry.IsFolder ? $"{entry.Path} and everything in it are" : $"{entry.Path} is") + $" deleted on {Host.Name}{(Host.Production ? " (production)" : "")}. It cannot be undone.",
                "Delete", danger: true))
            return;
        entry.BusyText = "Deleting…";
        await Change(async sftp =>
        {
            if (entry.IsFolder)
                await DeleteFolderAsync(sftp, entry.Path);
            else
                await sftp.DeleteFileAsync(entry.Path, CancellationToken.None);
        }, $"{entry.Name} could not be deleted");
    }

    static async Task DeleteFolderAsync(Renci.SshNet.SftpClient sftp, string folder)
    {
        await foreach (var file in sftp.ListDirectoryAsync(folder, CancellationToken.None))
        {
            if (file.Name is "." or "..")
                continue;
            if (file.IsDirectory && !file.IsSymbolicLink)
                await DeleteFolderAsync(sftp, file.FullName);
            else
                await sftp.DeleteFileAsync(file.FullName, CancellationToken.None);
        }
        await sftp.DeleteDirectoryAsync(folder, CancellationToken.None);
    }

    async Task Change(Func<Renci.SshNet.SftpClient, Task> change, string failure)
    {
        try
        {
            await change(await _explorer.Connections.SftpAsync(Host));
        }
        catch (Exception ex) when (ex is ServerException or SshException or IOException)
        {
            Problem = $"{failure}: {ex.Message}";
        }
        await RefreshFiles();
    }

    // ---- Services -------------------------------------------------------------------------------------------------

    public ObservableCollection<ServiceRow> Services { get; } = [];

    public ObservableCollection<ServiceRow> ShownServices { get; } = [];

    [ObservableProperty]
    public partial string ServiceFilter { get; set; } = "";

    [ObservableProperty]
    public partial bool OnlyRunning { get; set; }

    [ObservableProperty]
    public partial bool IsLoadingServices { get; set; }

    [ObservableProperty]
    public partial string? ServicesProblem { get; set; }

    partial void OnServiceFilterChanged(string value) => ShowServices();

    partial void OnOnlyRunningChanged(bool value) => ShowServices();

    void ShowServices()
    {
        var filter = ServiceFilter.Trim();
        ObservableSync.Sync(ShownServices, Services.Where(s => (!OnlyRunning || s.Service.IsRunning)
            && (filter.Length == 0 || s.Service.Unit.Contains(filter, StringComparison.OrdinalIgnoreCase) || s.Service.Description.Contains(filter, StringComparison.OrdinalIgnoreCase))).ToList());
    }

    [RelayCommand]
    public async Task RefreshServicesAsync()
    {
        IsLoadingServices = true;
        try
        {
            var result = await _explorer.Connections.RunAsync(Host, RemoteCommands.Services, TimeSpan.FromSeconds(30));
            if (!result.Succeeded)
            {
                ServicesProblem = "No systemd here: " + result.Combined.Split('\n')[0];
                return;
            }
            ServicesProblem = null;
            var services = RemoteCommands.ParseServices(result.Output);
            var rows = services.Select(s => Services.FirstOrDefault(r => r.Service.Unit == s.Unit) is { } row ? Update(row, s) : new ServiceRow(s)).ToList();
            ObservableSync.Sync(Services, rows);
            ShowServices();
        }
        catch (ServerException ex)
        {
            ServicesProblem = ex.Message;
        }
        finally
        {
            IsLoadingServices = false;
        }

        static ServiceRow Update(ServiceRow row, ServerService service)
        {
            row.Service = service;
            return row;
        }
    }

    [RelayCommand]
    Task StartService(ServiceRow? row) => ServiceAsync(row, "start");

    [RelayCommand]
    Task StopService(ServiceRow? row) => ServiceAsync(row, "stop");

    [RelayCommand]
    Task RestartService(ServiceRow? row) => ServiceAsync(row, "restart");

    /// <summary>systemctl through sudo -n; on a production machine, or to stop one, asked first.</summary>
    async Task ServiceAsync(ServiceRow? row, string action)
    {
        if (row is null)
            return;
        if ((Host.Production || action == "stop") && !await _explorer.ConfirmAsync(
                $"{char.ToUpperInvariant(action[0])}{action[1..]} {row.Service.Name} on {Host.Name}?",
                $"sudo systemctl {action} {row.Service.Unit}{(Host.Production ? $": {Host.Name} is production." : ".")}",
                char.ToUpperInvariant(action[0]) + action[1..], danger: true))
            return;
        row.BusyText = action switch { "start" => "Starting…", "stop" => "Stopping…", _ => "Restarting…" };
        try
        {
            var result = await _explorer.Connections.RunAsync(Host, RemoteCommands.ServiceAction(action, row.Service.Unit), TimeSpan.FromSeconds(90));
            if (!result.Succeeded)
                ServicesProblem = result.Error.Contains("a password is required", StringComparison.OrdinalIgnoreCase) || result.Error.Contains("sudo:", StringComparison.Ordinal)
                    ? $"sudo needs a password on {Host.Name}: {action} it in the Terminal (sudo systemctl {action} {row.Service.Unit})."
                    : $"{action} {row.Service.Unit}: {result.Combined}";
        }
        catch (ServerException ex)
        {
            ServicesProblem = ex.Message;
        }
        finally
        {
            row.BusyText = null;
        }
        await RefreshServicesAsync();
    }

    [RelayCommand]
    async Task ServiceLogs(ServiceRow? row)
    {
        if (row is null)
            return;
        (LogUnit, LogFile) = (row.Service.Unit, "");
        Tab = "logs";
        await RefreshLogsAsync();
    }

    // ---- Logs -----------------------------------------------------------------------------------------------------

    public OutputChannel Logs { get; } = new("Logs");

    /// <summary>A systemd unit; empty for the whole journal.</summary>
    [ObservableProperty]
    public partial string LogUnit { get; set; } = "";

    /// <summary>A log file instead of the journal (/var/log/nginx/error.log).</summary>
    [ObservableProperty]
    public partial string LogFile { get; set; } = "";

    [ObservableProperty]
    public partial int LogLines { get; set; } = 300;

    /// <summary>New lines come as they are written (journalctl -f, tail -F).</summary>
    [ObservableProperty]
    public partial bool FollowLogs { get; set; }

    partial void OnFollowLogsChanged(bool value) => _ = RefreshLogsAsync();

    [RelayCommand]
    public async Task RefreshLogsAsync()
    {
        _follow?.Cancel();
        _follow = null;
        Logs.Clear();
        var command = RemoteCommands.Logs(LogUnit.Trim(), LogFile.Trim(), LogLines, FollowLogs);
        try
        {
            if (FollowLogs)
            {
                var follow = _follow = new CancellationTokenSource();
                _ = _explorer.Connections.StreamAsync(Host, command, line => Dispatcher.UIThread.Post(() =>
                {
                    if (!follow.IsCancellationRequested)
                        Logs.Append(Claude.Tools.Redaction.Text(line));
                }), follow.Token);
                return;
            }
            var result = await _explorer.Connections.RunAsync(Host, command, TimeSpan.FromSeconds(60));
            Logs.Append(Claude.Tools.Redaction.Text(result.Succeeded ? result.Output : result.Combined));
        }
        catch (ServerException ex)
        {
            Logs.Append(ex.Message);
        }
    }

    [RelayCommand]
    void AskClaudeAboutLogs()
    {
        var lines = Logs.Document.Text.Split('\n').Where(l => l.Length > 0).TakeLast(80).ToList();
        var what = LogFile.Trim() is { Length: > 0 } file ? file : LogUnit.Trim() is { Length: > 0 } unit ? $"the {unit} service" : "the journal";
        _explorer.Host.AskClaude($"These are the last lines of {what} on the server {Host.Name} ({Host.Target}){(Host.Production ? ", a production machine" : "")}:\n```\n"
            + string.Join("\n", lines) + "\n```\nWhat is wrong, and how do I fix it?");
    }

    public void Dispose()
    {
        _disposed = true;
        _follow?.Cancel();
        if (Terminal is { } terminal)
            _ = terminal.DisposeAsync().AsTask();
        Terminal = null;
    }
}
