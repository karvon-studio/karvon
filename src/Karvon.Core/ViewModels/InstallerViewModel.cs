using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Karvon.Modules;
using Karvon.Services;
using Karvon.Updates;
using Karvon.Views;

namespace Karvon.ViewModels;

public enum InstallerPage
{
    /// <summary>The installed product: its version, its update, Modify, Launch.</summary>
    Product,
    /// <summary>Its workloads and individual components, as Visual Studio's Modify has them.</summary>
    Modify,
}

public enum InstallerTab
{
    Workloads,
    Components,
    Locations,
}

/// <summary>A workload: a module, as its card shows it (checked: on).</summary>
public sealed partial class WorkloadItem(KarvonModule module, bool on, Action<WorkloadItem> changed) : ObservableObject
{
    public KarvonModule Module => module;

    public string Name => module.Name;

    public string Description => module.Description;

    public IImage? Icon => module.Icon is { } icon ? MaterialIcons.Image(icon) : null;

    /// <summary>Whether it was on before this Modify.</summary>
    public bool WasOn => on;

    [ObservableProperty]
    public partial bool IsSelected { get; set; } = on;

    partial void OnIsSelectedChanged(bool value) => changed(this);

    [RelayCommand]
    void Toggle() => IsSelected = !IsSelected;
}

/// <summary>Workloads under a heading ("Languages", "Tools").</summary>
public sealed record WorkloadGroup(string Title, IReadOnlyList<WorkloadItem> Items);

/// <summary>An individual component: checked, it is there after this Modify (installed now, or kept).</summary>
public sealed partial class ComponentItem(string owner, ModuleComponent component, bool installed, bool removing, Action changed) : ObservableObject
{
    public string Owner => owner;

    public ModuleComponent Component => component;

    public string Name => $"{component.Name} {component.Version}";

    /// <summary>Installed now (and not to be removed as Karvon Studio starts again).</summary>
    public bool WasInstalled => installed && !removing;

    public bool IsInstalled => installed;

    public string State => removing ? "removed when Karvon Studio starts again"
        : installed ? $"installed, {InstallerViewModel.SizeText(component.Size())}"
        : component.ApproximateSize > 0 ? $"about {InstallerViewModel.SizeText(component.ApproximateSize)}" : "downloads the first time it is needed";

    [ObservableProperty]
    public partial bool IsSelected { get; set; } = installed && !removing;

    partial void OnIsSelectedChanged(bool value) => changed();

    [ObservableProperty]
    public partial bool IsVisible { get; set; } = true;
}

/// <summary>A module's components (Individual components' heading).</summary>
public sealed partial class ComponentGroup(string title, IReadOnlyList<ComponentItem> items) : ObservableObject
{
    public string Title => title;

    public IReadOnlyList<ComponentItem> Items => items;

    [ObservableProperty]
    public partial bool IsVisible { get; set; } = true;
}

/// <summary>A line of Installation details: what this Modify does.</summary>
public sealed record InstallerChange(string Text, IReadOnlyList<string> Items);

/// <summary>
/// Karvon Studio Installer, as Visual Studio's: the installed product (its version, its update, Modify), and Modify:
/// workloads (the modules, turned on or off when Karvon Studio starts again; one turned on downloads its components),
/// individual components (installed now, or removed when Karvon Studio starts again), where it all is; Installation
/// details and the space required beside them.
/// </summary>
public sealed partial class InstallerViewModel : ObservableObject
{
    readonly MainViewModel _vm;

    public InstallerViewModel(MainViewModel vm, Updater updater, bool standalone = false)
    {
        _vm = vm;
        Updater = updater;
        IsStandalone = standalone;
        Updater.PropertyChanged += OnUpdaterChanged;
        Reload();
    }

    public Updater Updater { get; }

    /// <summary>Its window closed: the application's updater (which outlives it) lets it go.</summary>
    public void Detach() => Updater.PropertyChanged -= OnUpdaterChanged;

    public AppInstall Install => Updater.Install;

    /// <summary>Started on its own (karvon --installer): Launch starts Karvon Studio.</summary>
    public bool IsStandalone { get; }

    public string VersionText => $"{Install.Version} · {Install.Description}";

    public string ModifyTitle => $"Modifying — Karvon Studio — {Install.Version}";

    public string InstallerVersion => $"Installer version {Install.Version}";

    [ObservableProperty]
    public partial InstallerPage Page { get; set; }

    [ObservableProperty]
    public partial InstallerTab Tab { get; set; }

    public bool IsProduct => Page == InstallerPage.Product;

    public bool IsModify => Page == InstallerPage.Modify;

    public bool IsWorkloads => Tab == InstallerTab.Workloads;

    public bool IsComponents => Tab == InstallerTab.Components;

    public bool IsLocations => Tab == InstallerTab.Locations;

    partial void OnPageChanged(InstallerPage value)
    {
        OnPropertyChanged(nameof(IsProduct));
        OnPropertyChanged(nameof(IsModify));
    }

    partial void OnTabChanged(InstallerTab value)
    {
        OnPropertyChanged(nameof(IsWorkloads));
        OnPropertyChanged(nameof(IsComponents));
        OnPropertyChanged(nameof(IsLocations));
    }

    public IReadOnlyList<WorkloadGroup> Workloads { get; private set; } = [];

    public IReadOnlyList<ComponentGroup> ComponentGroups { get; private set; } = [];

    IEnumerable<ComponentItem> AllComponents => ComponentGroups.SelectMany(g => g.Items);

    public ObservableCollection<InstallerChange> Changes { get; } = [];

    [ObservableProperty]
    public partial string SpaceText { get; private set; } = "";

    [ObservableProperty]
    public partial bool HasChanges { get; private set; }

    [ObservableProperty]
    public partial string ComponentFilter { get; set; } = "";

    partial void OnComponentFilterChanged(string value)
    {
        foreach (var group in ComponentGroups)
        {
            foreach (var item in group.Items)
                item.IsVisible = value.Length == 0 || item.Name.Contains(value, StringComparison.OrdinalIgnoreCase)
                    || item.Owner.Contains(value, StringComparison.OrdinalIgnoreCase) || item.Component.Description.Contains(value, StringComparison.OrdinalIgnoreCase);
            group.IsVisible = group.Items.Any(i => i.IsVisible);
        }
    }

    [ObservableProperty]
    public partial bool IsApplying { get; private set; }

    [ObservableProperty]
    public partial string ApplyText { get; private set; } = "";

    [ObservableProperty]
    public partial double ApplyProgress { get; private set; }

    /// <summary>Modules turned on or off, or components removed: they change when Karvon Studio starts again.</summary>
    [ObservableProperty]
    public partial bool RestartNeeded { get; private set; }

    public string ComponentsLocation => ModuleComponent.DataRoot;

    public string AppLocation => Install.Distribution == Distribution.Dmg ? Install.Bundle : Install.AppImage ?? Install.Directory;

    public string SettingsLocation => Settings.ConfigDirectory;

    /// <summary>The workloads and components as they are now (after a Modify, again).</summary>
    void Reload()
    {
        var disabled = _vm.Settings.DisabledModules;
        var items = _vm.AllModules.Select(m => new WorkloadItem(m, !disabled.Contains(m.Id), OnWorkloadChanged)).ToList();
        Workloads =
        [
            new("Languages", [.. items.Where(i => i.Module.LanguageServers.Count > 0 || i.Module.Debuggers.Count > 0)]),
            new("Tools", [.. items.Where(i => i.Module.LanguageServers.Count == 0 && i.Module.Debuggers.Count == 0)]),
        ];
        // A component two modules share (CodeLLDB: Rust's, C and C++'s) is listed once, under the first.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        ComponentGroups = [.. _vm.AllModules.Where(m => m.Components.Count > 0)
            .Select(m => new ComponentGroup(m.Name, [.. m.Components.Where(c => seen.Add(c.InstallRoot))
                .Select(c => new ComponentItem(m.Name, c, c.IsInstalled(), _vm.IsRemovalPending(c), UpdateChanges))]))
            .Where(g => g.Items.Count > 0)];
        OnPropertyChanged(nameof(Workloads));
        OnPropertyChanged(nameof(ComponentGroups));
        OnComponentFilterChanged(ComponentFilter);
        UpdateChanges();
    }

    /// <summary>A workload checked gets its components to download, as Visual Studio's; unchecked, they are as they were.</summary>
    void OnWorkloadChanged(WorkloadItem workload)
    {
        foreach (var item in AllComponents.Where(i => workload.Module.Components.Any(c => c.InstallRoot == i.Component.InstallRoot) && !i.WasInstalled))
            item.IsSelected = workload.IsSelected && !workload.WasOn;
        UpdateChanges();
    }

    IEnumerable<WorkloadItem> AllWorkloads => Workloads.SelectMany(g => g.Items);

    /// <summary>Installation details: what turns on and off, what downloads, what goes; the space it takes.</summary>
    void UpdateChanges()
    {
        Changes.Clear();
        var installs = AllComponents.Where(i => i.IsSelected && !i.IsInstalled).ToList();
        var removals = AllComponents.Where(i => i.WasInstalled && !i.IsSelected).ToList();
        foreach (var workload in AllWorkloads.Where(w => w.IsSelected && !w.WasOn))
            Changes.Add(new($"{workload.Name}: turned on", [.. workload.Module.Components.Where(c => !c.IsInstalled()).Select(c => $"{c.Name} downloads now")]));
        foreach (var workload in AllWorkloads.Where(w => !w.IsSelected && w.WasOn))
            Changes.Add(new($"{workload.Name}: turned off when Karvon Studio starts again", []));
        var others = installs.Where(i => !AllWorkloads.Any(w => w.IsSelected && !w.WasOn && w.Module.Components.Any(c => c.InstallRoot == i.Component.InstallRoot))).ToList();
        if (others.Count > 0)
            Changes.Add(new("Individual components to install", [.. others.Select(i => i.Name)]));
        if (AllComponents.Where(i => i.IsSelected && i.IsInstalled && !i.WasInstalled).ToList() is { Count: > 0 } kept)
            Changes.Add(new("Individual components to keep", [.. kept.Select(i => i.Name)]));
        if (removals.Count > 0)
            Changes.Add(new("Individual components to remove when Karvon Studio starts again", [.. removals.Select(i => i.Name)]));
        HasChanges = Changes.Count > 0;
        var space = installs.Sum(i => i.Component.ApproximateSize) - removals.Sum(i => i.Component.Size());
        SpaceText = space switch
        {
            > 0 => $"Total space required: about {SizeText(space)}",
            < 0 => $"Space freed: {SizeText(-space)}",
            _ => "Total space required: 0 KB",
        };
    }

    public static string SizeText(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes >> 20} MB",
        _ => $"{Math.Max(1, bytes >> 10)} KB",
    };

    [RelayCommand]
    void ShowModify()
    {
        Reload();
        Page = InstallerPage.Modify;
    }

    [RelayCommand]
    void ShowProduct() => Page = InstallerPage.Product;

    [RelayCommand]
    void ShowTab(InstallerTab tab) => Tab = tab;

    /// <summary>
    /// Modify: the components to install, one after another (their log in Output (Modules)); those to remove or keep;
    /// the modules turned on and off (as Karvon Studio starts again). Then the lists as they are now.
    /// </summary>
    [RelayCommand]
    async Task ApplyAsync()
    {
        if (IsApplying || !HasChanges)
            return;
        IsApplying = true;
        try
        {
            var installs = AllComponents.Where(i => i.IsSelected && !i.IsInstalled).ToList();
            var failed = new List<string>();
            for (var n = 0; n < installs.Count; n++)
            {
                ApplyText = $"Installing {n + 1} of {installs.Count}: {installs[n].Name}...";
                ApplyProgress = (double)n / installs.Count;
                if (!await _vm.InstallComponentAsync(installs[n].Component))
                    failed.Add(installs[n].Component.Name);
            }
            var restart = false;
            foreach (var item in AllComponents)
            {
                if (item.WasInstalled && !item.IsSelected)
                {
                    _vm.RemoveComponent(item.Component);
                    restart = true;
                }
                else if (item.IsSelected && item.IsInstalled && !item.WasInstalled)
                    _vm.KeepComponent(item.Component);
            }
            var disabled = AllWorkloads.Where(w => !w.IsSelected).Select(w => w.Module.Id).ToList();
            if (!disabled.Order().SequenceEqual(_vm.Settings.DisabledModules.Order()))
            {
                await _vm.ChangeModulesAsync(disabled);
                restart = true;
            }
            RestartNeeded |= restart;
            ApplyProgress = 1;
            ApplyText = failed.Count > 0
                ? $"{string.Join(", ", failed)} could not be installed: see Output (Modules)."
                : RestartNeeded ? "Done. The changes take effect when Karvon Studio starts again." : "Done.";
            Reload();
        }
        finally
        {
            IsApplying = false;
        }
    }

    // ---- Updates ----

    /// <summary>The window's answer to Restart: it closes Karvon Studio, which then does this as it exits.</summary>
    public event Action<ExitAction>? RestartRequested;

    /// <summary>Launch (the installer on its own): Karvon Studio starts.</summary>
    public event Action? LaunchRequested;

    public bool IsChecking => Updater.State == UpdateState.Checking;

    public bool IsDownloading => Updater.State == UpdateState.Downloading;

    public bool CanUpdate => Updater.State is UpdateState.Available or UpdateState.Ready;

    public string UpdateButtonText => Updater.State == UpdateState.Ready ? "Restart and update" : "Update";

    public bool HasFailed => Updater.State == UpdateState.Failed;

    public bool IsUpToDate => Updater.State == UpdateState.UpToDate;

    public string ReleaseNotes => Updater.Latest is { } latest
        ? $"# {latest.Name}\n\n{(latest.Notes.Length > 0 ? latest.Notes : "No notes.")}"
        : "No release has been checked for yet.";

    void OnUpdaterChanged(object? sender, PropertyChangedEventArgs e) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        OnPropertyChanged(nameof(IsChecking));
        OnPropertyChanged(nameof(IsDownloading));
        OnPropertyChanged(nameof(CanUpdate));
        OnPropertyChanged(nameof(UpdateButtonText));
        OnPropertyChanged(nameof(HasFailed));
        OnPropertyChanged(nameof(IsUpToDate));
        OnPropertyChanged(nameof(ReleaseNotes));
        UpdateCommand.NotifyCanExecuteChanged();
    });

    [RelayCommand]
    async Task CheckForUpdatesAsync()
    {
        await Updater.CheckAsync();
        if (Updater.State != UpdateState.Failed)
        {
            _vm.Settings.UpdatesLastCheck = DateTimeOffset.Now;
            _vm.Settings.Save();
        }
    }

    /// <summary>Update: the update downloaded; once it is, Karvon Studio restarts into it.</summary>
    [RelayCommand(CanExecute = nameof(CanUpdate))]
    async Task UpdateAsync()
    {
        if (Updater.State == UpdateState.Available)
            await Updater.DownloadAsync();
        else if (Updater.State == UpdateState.Ready)
            RestartRequested?.Invoke(ExitAction.RestartUpdated);
    }

    [RelayCommand]
    void Restart() => RestartRequested?.Invoke(ExitAction.Restart);

    [RelayCommand]
    void Launch() => LaunchRequested?.Invoke();

    [RelayCommand]
    void OpenReleasePage()
    {
        if (Updater.Latest?.PageUrl is { } url)
            DesktopShell.Open(url);
        else
            DesktopShell.Open($"https://github.com/{UpdateFeed.Repository}/releases");
    }

    [RelayCommand]
    static void OpenLocation(string path) => DesktopShell.Open(path);

    public bool UpdatesCheck
    {
        get => _vm.Settings.UpdatesCheck;
        set
        {
            _vm.Settings.UpdatesCheck = value;
            _vm.Settings.Save();
            OnPropertyChanged();
        }
    }

    public bool UpdatesDownload
    {
        get => _vm.Settings.UpdatesDownload;
        set
        {
            _vm.Settings.UpdatesDownload = value;
            _vm.Settings.Save();
            OnPropertyChanged();
        }
    }

    public bool UpdatesInstallOnClose
    {
        get => _vm.Settings.UpdatesInstallOnClose;
        set
        {
            _vm.Settings.UpdatesInstallOnClose = value;
            _vm.Settings.Save();
            OnPropertyChanged();
        }
    }
}
