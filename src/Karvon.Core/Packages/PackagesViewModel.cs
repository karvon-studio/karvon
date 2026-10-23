using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Karvon.Modules;
using Karvon.ViewModels;

namespace Karvon.Packages;

public enum PackagesTab
{
    Browse,
    Installed,
    Updates,
}

/// <summary>A package row of the Package Manager's list.</summary>
public sealed partial class PackageRow(PackageSource source) : ObservableObject
{
    public required string Id { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate), nameof(VersionText))]
    public partial string? LatestVersion { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate), nameof(VersionText), nameof(IsInstalled))]
    public partial string? InstalledVersion { get; set; }

    /// <summary>What the manifest asks for ("^1.2.0").</summary>
    [ObservableProperty]
    public partial string? Requested { get; set; }

    [ObservableProperty]
    public partial string? Group { get; set; }

    [ObservableProperty]
    public partial string? Description { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Byline))]
    public partial string? Owners { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Byline), nameof(DownloadsText))]
    public partial long Downloads { get; set; }

    [ObservableProperty]
    public partial string? ProjectUrl { get; set; }

    [ObservableProperty]
    public partial string? License { get; set; }

    public string DownloadsText => Downloads > 0 ? Downloads.ToString("N0") : "";

    /// <summary>In the manifest (installed or not yet).</summary>
    public bool IsInstalled => InstalledVersion is not null || Group is not null;

    public bool HasUpdate => InstalledVersion is not null && LatestVersion is not null && source.Compare(LatestVersion, InstalledVersion) > 0;

    /// <summary>The installed version on the right, as Visual Studio has it; the latest one when not installed.</summary>
    public string VersionText => InstalledVersion ?? Requested ?? LatestVersion ?? "";

    public string Byline => string.Join(", ", new[]
    {
        string.IsNullOrEmpty(Owners) ? null : $"by {Owners}",
        Downloads > 0 ? $"{PackageVersions.FormatCount(Downloads)} downloads" : null,
    }.Where(s => s is not null));

    internal void Fill(RegistryPackage package)
    {
        Description ??= package.Description;
        Owners ??= package.Owners;
        if (Downloads == 0)
            Downloads = package.Downloads;
        ProjectUrl ??= package.ProjectUrl;
        License ??= package.License;
        LatestVersion ??= package.LatestVersion;
    }
}

/// <summary>
/// A project's Package Manager, as Visual Studio's NuGet Package Manager is: Browse the registry, the Installed packages
/// and their Updates, a package's versions and details, Install, Update and Uninstall by the project's package manager,
/// its output in Output (Package Manager).
/// </summary>
public sealed partial class PackagesViewModel : DocumentTab
{
    readonly PackageSource _source;
    readonly Func<WorkspaceTask, Action<string>, CancellationToken, Task<int>> _run;
    readonly Action<string> _log;
    readonly Func<Task> _changed;
    IReadOnlyList<DependencyPackage> _dependencies = [];
    readonly Dictionary<string, RegistryPackage> _latest = new(StringComparer.Ordinal);
    CancellationTokenSource? _cts;

    /// <param name="run">Runs a command of the package manager's as the tasks run: its exit code.</param>
    /// <param name="changed">What follows a change (Solution Explorer's dependencies read again).</param>
    public PackagesViewModel(PackageSource source, Func<WorkspaceTask, Action<string>, CancellationToken, Task<int>> run, Action<string> log, Func<Task> changed)
    {
        _source = source;
        _run = run;
        _log = log;
        _changed = changed;
        foreach (var group in source.Groups)
            Groups.Add(group);
        SelectedGroup = Groups.FirstOrDefault();
    }

    public PackageSource Source => _source;

    public static string KeyOf(PackageSource source) => $"packages:{source.Name}:{source.Directory}";

    public override string Key => KeyOf(_source);

    public override string Title => $"{_source.Name}: {_source.Project}";

    public override string ToolTip => $"{_source.Name} Package Manager: {_source.Directory}";

    public string Header => $"{_source.Name} Package Manager: {_source.Project}";

    public string SearchHint => $"Search {_source.Registry} (Enter)";

    public ObservableCollection<PackageRow> Packages { get; } = [];

    public ObservableCollection<string> Versions { get; } = [];

    /// <summary>The groups a package goes in ("dependencies", "devDependencies"), when the project has more than one.</summary>
    public ObservableCollection<string> Groups { get; } = [];

    public bool HasGroups => Groups.Count > 1;

    [ObservableProperty]
    public partial string? SelectedGroup { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBrowseTab), nameof(IsInstalledTab), nameof(IsUpdatesTab))]
    public partial PackagesTab Tab { get; set; }

    public bool IsBrowseTab
    {
        get => Tab == PackagesTab.Browse;
        set
        {
            if (value)
                Tab = PackagesTab.Browse;
        }
    }

    public bool IsInstalledTab
    {
        get => Tab == PackagesTab.Installed;
        set
        {
            if (value)
                Tab = PackagesTab.Installed;
        }
    }

    public bool IsUpdatesTab
    {
        get => Tab == PackagesTab.Updates;
        set
        {
            if (value)
                Tab = PackagesTab.Updates;
        }
    }

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    public partial bool IncludePrerelease { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(UninstallCommand), nameof(UpdateAllCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = "";

    [ObservableProperty]
    public partial int UpdateCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(InstallButtonText))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(UninstallCommand))]
    public partial PackageRow? SelectedPackage { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    public partial string? SelectedVersion { get; set; }

    public bool HasSelection => SelectedPackage is not null;

    /// <summary>"Install", or "Update" when the selected package is the project's already.</summary>
    public string InstallButtonText => SelectedPackage is { IsInstalled: true } ? "Update" : "Install";

    partial void OnTabChanged(PackagesTab value) => _ = LoadAsync();

    partial void OnIncludePrereleaseChanged(bool value) => _ = LoadDetailsAsync(SelectedPackage);

    partial void OnSelectedPackageChanged(PackageRow? value) => _ = LoadDetailsAsync(value);

    public async Task InitializeAsync()
    {
        await RefreshInstalledAsync();
        await LoadAsync();
    }

    [RelayCommand]
    Task Search() => LoadAsync();

    [RelayCommand]
    async Task Refresh()
    {
        await RefreshInstalledAsync();
        await LoadAsync();
    }

    /// <summary>The project's dependencies read again, and each one's latest version from the registry.</summary>
    async Task RefreshInstalledAsync()
    {
        await BusyAsync("Reading the installed packages...", async ct =>
        {
            _dependencies = _source.Dependencies();
            using var gate = new SemaphoreSlim(8);
            var found = await Task.WhenAll(_dependencies.Select(d => d.Id).Distinct().Select(async id =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    return (Id: id, Package: await _source.FindAsync(id, ct));
                }
                catch (Exception ex) when (IsLookupFailure(ex))
                {
                    return (Id: id, Package: (RegistryPackage?)null);
                }
                finally
                {
                    gate.Release();
                }
            }));
            _latest.Clear();
            foreach (var (id, package) in found)
                if (package is not null)
                    _latest[id] = package;
            UpdateCount = Outdated().Count();
        });
    }

    IEnumerable<DependencyPackage> Outdated() => _dependencies.Where(d => d.Version is not null
        && _latest.TryGetValue(d.Id, out var latest) && latest.LatestVersion is { } version && _source.Compare(version, d.Version) > 0);

    PackageRow RowOf(DependencyPackage d)
    {
        var row = new PackageRow(_source) { Id = d.Id, InstalledVersion = d.Version, Requested = d.Requested, Group = d.Group };
        if (_latest.TryGetValue(d.Id, out var latest))
            row.Fill(latest);
        return row;
    }

    public async Task LoadAsync()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var selectedId = SelectedPackage?.Id;
        var text = SearchText.Trim();
        await BusyAsync(Tab == PackagesTab.Browse ? "Searching..." : "Loading...", async ct =>
        {
            List<PackageRow> rows;
            switch (Tab)
            {
                case PackagesTab.Browse:
                    rows = text.Length == 0 ? [] : [.. (await _source.SearchAsync(text, ct)).Select(p =>
                    {
                        var row = new PackageRow(_source) { Id = p.Id };
                        if (_dependencies.FirstOrDefault(d => d.Id == p.Id) is { } installed)
                            (row.InstalledVersion, row.Requested, row.Group) = (installed.Version, installed.Requested, installed.Group);
                        row.Fill(p);
                        return row;
                    })];
                    break;
                case PackagesTab.Installed:
                    rows = [.. _dependencies.Where(d => d.Id.Contains(text, StringComparison.OrdinalIgnoreCase)).OrderBy(d => d.Id, StringComparer.OrdinalIgnoreCase).Select(RowOf)];
                    break;
                default:
                    rows = [.. Outdated().OrderBy(d => d.Id, StringComparer.OrdinalIgnoreCase).Select(RowOf)];
                    break;
            }
            if (cts.IsCancellationRequested)
                return;
            Packages.Clear();
            foreach (var row in rows)
                Packages.Add(row);
            SelectedPackage = Packages.FirstOrDefault(p => p.Id == selectedId) ?? Packages.FirstOrDefault();
            Status = Packages.Count > 0 ? "" : Tab switch
            {
                PackagesTab.Browse when text.Length == 0 => $"Search {_source.Registry} for a package.",
                PackagesTab.Updates => "All packages are up to date.",
                _ => "No packages found.",
            };
        }, cts.Token);
    }

    async Task LoadDetailsAsync(PackageRow? row)
    {
        Versions.Clear();
        SelectedVersion = null;
        if (row is null)
            return;
        if (row.Group is { } group && Groups.Contains(group))
            SelectedGroup = group;
        try
        {
            // An installed package's row has no description: the registry has it.
            if (row.Description is null && await _source.FindAsync(row.Id, CancellationToken.None) is { } found)
                row.Fill(found);
            var versions = await _source.VersionsAsync(row.Id, IncludePrerelease, CancellationToken.None);
            if (SelectedPackage != row)
                return;
            foreach (var v in versions)
                Versions.Add(v);
            SelectedVersion = Versions.FirstOrDefault();
        }
        catch (Exception ex) when (IsLookupFailure(ex))
        {
            Status = ex.Message;
        }
    }

    bool CanInstall => !IsBusy && SelectedPackage is not null && SelectedVersion is not null;

    [RelayCommand(CanExecute = nameof(CanInstall))]
    async Task Install()
    {
        if (SelectedPackage is not { } package || SelectedVersion is not { } version)
            return;
        var verb = package.IsInstalled ? "Updating" : "Installing";
        await ChangeAsync($"{verb} {package.Id} {version}...", [_source.Add(package.Id, version, package.Group ?? SelectedGroup)]);
    }

    bool CanUninstall => !IsBusy && SelectedPackage is { IsInstalled: true };

    [RelayCommand(CanExecute = nameof(CanUninstall))]
    async Task Uninstall()
    {
        if (SelectedPackage is not { } package || _dependencies.FirstOrDefault(d => d.Id == package.Id) is not { } dependency)
            return;
        await ChangeAsync($"Uninstalling {package.Id}...", [_source.Remove(dependency)]);
    }

    bool CanUpdateAll => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanUpdateAll))]
    async Task UpdateAll()
    {
        var updates = Outdated().ToList();
        await ChangeAsync($"Updating {updates.Count} package(s)...", [.. updates.Select(u => _source.Add(u.Id, _latest[u.Id].LatestVersion!, u.Group))]);
    }

    /// <summary>The package manager's commands, one after another, their output in Output (Package Manager); then all read again.</summary>
    async Task ChangeAsync(string message, IReadOnlyList<WorkspaceTask> commands)
    {
        string? failure = null;
        await BusyAsync(message, async ct =>
        {
            foreach (var command in commands)
            {
                _log("> " + command.CommandLine);
                var lines = new List<string>();
                var code = await _run(command, line =>
                {
                    lines.Add(line);
                    _log(line);
                }, ct);
                if (code != 0)
                {
                    failure = $"{command.CommandLine} failed: {LastLine(lines)}";
                    return;
                }
            }
        });
        // One that could not start says why in Status already.
        failure ??= Status.Length > 0 && Status != message ? Status : null;
        await RefreshInstalledAsync();
        await LoadAsync();
        await _changed();
        if (failure is not null)
            Status = failure;
        else if (Status.Length == 0)
            Status = "Done.";
    }

    static string LastLine(List<string> lines) =>
        lines.LastOrDefault(l => l.Contains("error", StringComparison.OrdinalIgnoreCase))?.Trim() ?? lines.LastOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "see Output (Package Manager)";

    static bool IsLookupFailure(Exception ex) =>
        ex is HttpRequestException or System.Text.Json.JsonException or InvalidOperationException or IOException
        || ex is TaskCanceledException { InnerException: TimeoutException };

    async Task BusyAsync(string message, Func<CancellationToken, Task> work, CancellationToken ct = default)
    {
        IsBusy = true;
        Status = message;
        try
        {
            await work(ct);
            if (Status == message)
                Status = "";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (IsLookupFailure(ex) || ex is System.ComponentModel.Win32Exception)
        {
            Status = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public override void Dispose() => _cts?.Cancel();
}
