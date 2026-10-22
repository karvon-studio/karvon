using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Karvon.Docker.ViewModels;

/// <summary>A row of the Images table.</summary>
public sealed partial class ImageRow : DockerTreeItem
{
    readonly ImagesPage _page;

    internal ImageRow(ImagesPage page, DockerImage image)
    {
        _page = page;
        Image = image;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name), nameof(Tag), nameof(ShortId), nameof(CreatedText), nameof(SizeText), nameof(InUse), nameof(StatusText), nameof(CanRun), nameof(Reference))]
    public partial DockerImage Image { get; set; }

    public string Name => Image.Repository;

    /// <summary>"nginx:1.27" (the tree's name for it), or the ID of an untagged image.</summary>
    public string Reference => Image.IsDangling ? $"<none> ({ShortId})" : Image.Reference;

    public string Tag => Image.Tag;

    public string ShortId => Image.ShortId;

    public string CreatedText => DockerFormat.Ago(Image.Created);

    public string SizeText => DockerFormat.Size(Image.Size);

    public bool InUse => Image.InUse;

    public string StatusText => Image.InUse ? "In use" : "Unused";

    public bool CanRun => true;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    public partial string? BusyText { get; set; }

    public bool IsBusy => BusyText is not null;

    partial void OnIsSelectedChanged(bool value) => _page.SelectionChanged();

    [RelayCommand]
    void Open() => _page.Open(this);

    [RelayCommand]
    Task Run() => _page.RunAsync(this);

    [RelayCommand]
    Task Delete() => _page.DeleteAsync([this]);
}

/// <summary>Docker Desktop's Images page: the local images, their size and use, Pull, Run, Clean up.</summary>
public sealed partial class ImagesPage : ObservableObject
{
    readonly DockerExplorerViewModel _explorer;
    readonly Dictionary<string, ImageRow> _rows = [];

    internal ImagesPage(DockerExplorerViewModel explorer) => _explorer = explorer;

    public ObservableCollection<ImageRow> Rows { get; } = [];

    public IEnumerable<ImageRow> AllRows => _rows.Values;

    public bool Contains(ImageRow row) => _rows.TryGetValue(Key(row.Image), out var kept) && kept == row;

    [ObservableProperty]
    public partial string Search { get; set; } = "";

    [ObservableProperty]
    public partial bool OnlyUnused { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameSort), nameof(CreatedSort), nameof(SizeSort))]
    public partial string SortColumn { get; set; } = "created";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameSort), nameof(CreatedSort), nameof(SizeSort))]
    public partial bool SortAscending { get; set; }

    public string NameSort => SortColumn == "name" ? SortAscending ? "↑" : "↓" : "";

    public string CreatedSort => SortColumn == "created" ? SortAscending ? "↑" : "↓" : "";

    public string SizeSort => SortColumn == "size" ? SortAscending ? "↑" : "↓" : "";

    public int Count => _rows.Count;

    public bool IsEmpty => _rows.Count == 0;

    public bool HasNoMatches => _rows.Count > 0 && Rows.Count == 0;

    /// <summary>"12.4 GB / 34 images", and how much of it nothing uses.</summary>
    [ObservableProperty]
    public partial string TotalText { get; set; } = "";

    [ObservableProperty]
    public partial string UnusedText { get; set; } = "";

    public int UnusedCount => _rows.Values.Count(r => !r.InUse);

    public bool HasUnused => UnusedCount > 0;

    /// <summary>The image to pull ("nginx:latest").</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PullCommand))]
    public partial string PullReference { get; set; } = "";

    /// <summary>The pull's progress ("Pulling from library/nginx", "Downloaded newer image...").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPulling))]
    [NotifyCanExecuteChangedFor(nameof(PullCommand))]
    public partial string? PullStatus { get; set; }

    [ObservableProperty]
    public partial bool IsPullRunning { get; set; }

    public bool IsPulling => PullStatus is not null;

    public IReadOnlyList<ImageRow> Selected => _rows.Values.Where(r => r.IsSelected).ToList();

    public int SelectedCount => Selected.Count;

    public bool HasSelection => SelectedCount > 0;

    public string SelectionText => SelectedCount == 1 ? "1 item selected" : $"{SelectedCount} items selected";

    public bool AllSelected
    {
        get => Rows.Count > 0 && Rows.All(r => r.IsSelected);
        set
        {
            foreach (var row in Rows)
                row.IsSelected = value;
        }
    }

    internal void SelectionChanged()
    {
        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(AllSelected));
    }

    partial void OnSearchChanged(string value) => Rebuild();

    partial void OnOnlyUnusedChanged(bool value) => Rebuild();

    partial void OnSortColumnChanged(string value) => Rebuild();

    partial void OnSortAscendingChanged(bool value) => Rebuild();

    internal void Apply(DockerDiskUsage usage)
    {
        // An image with several tags is a row for each, as docker images lists them.
        var keys = usage.Images.Select(Key).ToHashSet();
        foreach (var gone in _rows.Keys.Where(k => !keys.Contains(k)).ToList())
            _rows.Remove(gone);
        foreach (var image in usage.Images)
        {
            if (_rows.TryGetValue(Key(image), out var row))
                row.Image = image;
            else
                _rows[Key(image)] = new ImageRow(this, image);
        }
        var unused = usage.Images.Where(i => !i.InUse).ToList();
        TotalText = $"{DockerFormat.Size(usage.ImagesSize)} / {usage.Images.Count} image{(usage.Images.Count == 1 ? "" : "s")}";
        UnusedText = unused.Count == 0 ? "All images are in use" : $"{DockerFormat.Size(unused.Sum(i => i.Size))} in {unused.Count} unused image{(unused.Count == 1 ? "" : "s")}";
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(UnusedCount));
        OnPropertyChanged(nameof(HasUnused));
        Rebuild();
        SelectionChanged();
    }

    static string Key(DockerImage image) => image.Id + "|" + image.Repository + ":" + image.Tag;

    /// <summary>The image a container was started with: "postgres:17", "postgres" (latest), or its id.</summary>
    internal ImageRow? Find(string reference)
    {
        // "registry:5000/app" has a port, not a tag: a tag comes after the last slash.
        var full = reference.LastIndexOf(':') > reference.LastIndexOf('/') ? reference : reference + ":latest";
        var id = reference.StartsWith("sha256:", StringComparison.Ordinal) ? reference[7..] : reference;
        return _rows.Values.FirstOrDefault(r => $"{r.Image.Repository}:{r.Image.Tag}" == full)
            ?? (id.Length >= 12 ? _rows.Values.FirstOrDefault(r => r.Image.Id.Replace("sha256:", "", StringComparison.Ordinal).StartsWith(id, StringComparison.Ordinal)) : null);
    }

    void Rebuild()
    {
        var search = Search.Trim();
        var rows = _rows.Values.Where(r => (!OnlyUnused || !r.InUse) && (search.Length == 0
            || r.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || r.Tag.Contains(search, StringComparison.OrdinalIgnoreCase)
            || r.ShortId.StartsWith(search, StringComparison.OrdinalIgnoreCase)));
        rows = (SortColumn, SortAscending) switch
        {
            ("name", true) => rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Tag, StringComparer.OrdinalIgnoreCase),
            ("name", false) => rows.OrderByDescending(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenByDescending(r => r.Tag, StringComparer.OrdinalIgnoreCase),
            ("size", true) => rows.OrderBy(r => r.Image.Size),
            ("size", false) => rows.OrderByDescending(r => r.Image.Size),
            ("created", true) => rows.OrderBy(r => r.Image.Created ?? DateTimeOffset.MinValue),
            _ => rows.OrderByDescending(r => r.Image.Created ?? DateTimeOffset.MinValue),
        };
        ContainersPage.Sync(Rows, rows.ToList());
        OnPropertyChanged(nameof(HasNoMatches));
        OnPropertyChanged(nameof(AllSelected));
    }

    [RelayCommand]
    void SortBy(string column)
    {
        if (SortColumn == column)
            SortAscending = !SortAscending;
        else
        {
            SortColumn = column;
            SortAscending = column == "name";
        }
    }

    internal void Open(ImageRow row) => _explorer.OpenImage(row);

    internal Task RunAsync(ImageRow row) => _explorer.RunImageAsync(row.Image.Reference);

    [RelayCommand]
    Task DeleteSelected() => DeleteAsync(Selected);

    [RelayCommand]
    void ClearSelection() => AllSelected = false;

    internal async Task DeleteAsync(IReadOnlyList<ImageRow> rows)
    {
        if (rows.Count == 0)
            return;
        var inUse = rows.Count(r => r.InUse);
        var what = rows.Count == 1 ? rows[0].Image.Reference : $"{rows.Count} images";
        if (!await _explorer.ConfirmAsync($"Delete {what}?",
                inUse > 0 ? "Containers use it: delete those containers first, or Docker refuses." : "It can be pulled again later.", "Delete", danger: true))
            return;
        foreach (var row in rows)
            row.BusyText = "Deleting…";
        try
        {
            // A tagged image goes by its name (only that tag), an untagged one by its id.
            var result = await _explorer.Engine.RemoveImagesAsync(rows.Select(r => r.Image.Reference));
            _explorer.Report(result, $"delete {what}");
        }
        catch (DockerException ex)
        {
            _explorer.Report(ex);
        }
        finally
        {
            foreach (var row in rows)
                row.BusyText = null;
        }
        await _explorer.RefreshDiskAsync();
    }

    bool CanPull => PullReference.Trim().Length > 0 && !IsPullRunning;

    [RelayCommand(CanExecute = nameof(CanPull))]
    public async Task Pull()
    {
        var reference = PullReference.Trim();
        if (reference.Length == 0)
            return;
        IsPullRunning = true;
        PullStatus = $"Pulling {reference}…";
        PullCommand.NotifyCanExecuteChanged();
        try
        {
            var result = await _explorer.Engine.PullAsync(reference, line =>
            {
                _explorer.Host.WriteOutput(line);
                Avalonia.Threading.Dispatcher.UIThread.Post(() => PullStatus = line.Trim());
            });
            if (result.Succeeded)
                PullReference = "";
            else
                _explorer.Report(_explorer.Engine.Failure($"pull {reference}: {result.Message}"));
        }
        catch (DockerException ex)
        {
            _explorer.Report(ex);
        }
        finally
        {
            IsPullRunning = false;
            PullStatus = null;
            PullCommand.NotifyCanExecuteChanged();
        }
        await _explorer.RefreshDiskAsync();
    }

    /// <summary>Docker Desktop's Clean up: every image no container uses goes.</summary>
    [RelayCommand]
    async Task CleanUp()
    {
        var unused = _rows.Values.Where(r => !r.InUse).ToList();
        if (unused.Count == 0)
            return;
        if (!await _explorer.ConfirmAsync("Clean up unused images?",
                $"{unused.Count} image{(unused.Count == 1 ? "" : "s")} that no container uses ({DockerFormat.Size(unused.Sum(r => r.Image.Size))}) will be deleted.", "Clean up", danger: true))
            return;
        foreach (var row in unused)
            row.BusyText = "Deleting…";
        try
        {
            _explorer.Report(await _explorer.Engine.PruneImagesAsync(), "clean up");
        }
        catch (DockerException ex)
        {
            _explorer.Report(ex);
        }
        await _explorer.RefreshDiskAsync();
    }

    [RelayCommand]
    void ClearSearch() => Search = "";
}

/// <summary>A row of the Volumes table.</summary>
public sealed partial class VolumeRow : DockerTreeItem
{
    readonly VolumesPage _page;

    internal VolumeRow(VolumesPage page, DockerVolume volume)
    {
        _page = page;
        Volume = volume;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name), nameof(StatusText), nameof(InUse), nameof(CreatedText), nameof(SizeText), nameof(Driver), nameof(TreeName))]
    public partial DockerVolume Volume { get; set; }

    public string Name => Volume.Name;

    /// <summary>The tree's name: an anonymous volume's long ID shortened.</summary>
    public string TreeName => Volume.IsAnonymous ? Name[..12] + "…" : Name;

    public string Driver => Volume.Driver;

    public bool InUse => Volume.InUse;

    public string StatusText => Volume.InUse ? "In use" : "";

    public string CreatedText => DockerFormat.Ago(Volume.Created);

    public string SizeText => Volume.Size is { } size ? DockerFormat.Size(size) : "N/A";

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    public partial string? BusyText { get; set; }

    public bool IsBusy => BusyText is not null;

    partial void OnIsSelectedChanged(bool value) => _page.SelectionChanged();

    [RelayCommand]
    void Open() => _page.Open(this);

    [RelayCommand]
    Task Delete() => _page.DeleteAsync([this]);
}

/// <summary>Docker Desktop's Volumes page: each volume, whether a container uses it, its size; Create and Delete.</summary>
public sealed partial class VolumesPage : ObservableObject
{
    readonly DockerExplorerViewModel _explorer;
    readonly Dictionary<string, VolumeRow> _rows = [];

    internal VolumesPage(DockerExplorerViewModel explorer) => _explorer = explorer;

    public ObservableCollection<VolumeRow> Rows { get; } = [];

    public IEnumerable<VolumeRow> AllRows => _rows.Values;

    public bool Contains(VolumeRow row) => _rows.TryGetValue(row.Volume.Name, out var kept) && kept == row;

    [ObservableProperty]
    public partial string Search { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameSort), nameof(CreatedSort), nameof(SizeSort))]
    public partial string SortColumn { get; set; } = "name";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameSort), nameof(CreatedSort), nameof(SizeSort))]
    public partial bool SortAscending { get; set; } = true;

    public string NameSort => SortColumn == "name" ? SortAscending ? "↑" : "↓" : "";

    public string CreatedSort => SortColumn == "created" ? SortAscending ? "↑" : "↓" : "";

    public string SizeSort => SortColumn == "size" ? SortAscending ? "↑" : "↓" : "";

    public int Count => _rows.Count;

    public bool IsEmpty => _rows.Count == 0;

    public bool HasNoMatches => _rows.Count > 0 && Rows.Count == 0;

    [ObservableProperty]
    public partial string TotalText { get; set; } = "";

    public IReadOnlyList<VolumeRow> Selected => _rows.Values.Where(r => r.IsSelected).ToList();

    public int SelectedCount => Selected.Count;

    public bool HasSelection => SelectedCount > 0;

    public string SelectionText => SelectedCount == 1 ? "1 item selected" : $"{SelectedCount} items selected";

    public bool AllSelected
    {
        get => Rows.Count > 0 && Rows.All(r => r.IsSelected);
        set
        {
            foreach (var row in Rows)
                row.IsSelected = value;
        }
    }

    internal void SelectionChanged()
    {
        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(AllSelected));
    }

    partial void OnSearchChanged(string value) => Rebuild();

    partial void OnSortColumnChanged(string value) => Rebuild();

    partial void OnSortAscendingChanged(bool value) => Rebuild();

    internal void Apply(IReadOnlyList<DockerVolume> volumes)
    {
        var names = volumes.Select(v => v.Name).ToHashSet();
        foreach (var gone in _rows.Keys.Where(k => !names.Contains(k)).ToList())
            _rows.Remove(gone);
        foreach (var volume in volumes)
        {
            if (_rows.TryGetValue(volume.Name, out var row))
                row.Volume = volume;
            else
                _rows[volume.Name] = new VolumeRow(this, volume);
        }
        TotalText = $"{DockerFormat.Size(volumes.Sum(v => v.Size ?? 0))} / {volumes.Count} volume{(volumes.Count == 1 ? "" : "s")}";
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(IsEmpty));
        Rebuild();
        SelectionChanged();
    }

    void Rebuild()
    {
        var search = Search.Trim();
        var rows = _rows.Values.Where(r => search.Length == 0 || r.Name.Contains(search, StringComparison.OrdinalIgnoreCase));
        rows = (SortColumn, SortAscending) switch
        {
            ("created", true) => rows.OrderBy(r => r.Volume.Created ?? DateTimeOffset.MinValue),
            ("created", false) => rows.OrderByDescending(r => r.Volume.Created ?? DateTimeOffset.MinValue),
            ("size", true) => rows.OrderBy(r => r.Volume.Size ?? 0),
            ("size", false) => rows.OrderByDescending(r => r.Volume.Size ?? 0),
            (_, false) => rows.OrderByDescending(r => r.Volume.IsAnonymous).ThenByDescending(r => r.Name, StringComparer.OrdinalIgnoreCase),
            // Named volumes first: anonymous ones are 64 hex characters nobody reads.
            _ => rows.OrderBy(r => r.Volume.IsAnonymous).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        };
        ContainersPage.Sync(Rows, rows.ToList());
        OnPropertyChanged(nameof(HasNoMatches));
        OnPropertyChanged(nameof(AllSelected));
    }

    [RelayCommand]
    void SortBy(string column)
    {
        if (SortColumn == column)
            SortAscending = !SortAscending;
        else
        {
            SortColumn = column;
            SortAscending = column == "name";
        }
    }

    internal void Open(VolumeRow row) => _explorer.OpenVolume(row);

    [RelayCommand]
    Task DeleteSelected() => DeleteAsync(Selected);

    [RelayCommand]
    void ClearSelection() => AllSelected = false;

    [RelayCommand]
    void ClearSearch() => Search = "";

    internal async Task DeleteAsync(IReadOnlyList<VolumeRow> rows)
    {
        if (rows.Count == 0)
            return;
        var what = rows.Count == 1 ? rows[0].Name : $"{rows.Count} volumes";
        if (!await _explorer.ConfirmAsync($"Delete {what}?", "The data in it is lost for good." + (rows.Any(r => r.InUse) ? " A volume a container uses cannot be deleted." : ""),
                "Delete", danger: true))
            return;
        foreach (var row in rows)
            row.BusyText = "Deleting…";
        try
        {
            _explorer.Report(await _explorer.Engine.RemoveVolumesAsync(rows.Select(r => r.Name)), $"delete {what}");
        }
        catch (DockerException ex)
        {
            _explorer.Report(ex);
        }
        finally
        {
            foreach (var row in rows)
                row.BusyText = null;
        }
        await _explorer.RefreshDiskAsync();
    }

    [RelayCommand]
    async Task Create()
    {
        if (await _explorer.ShowAsync(new InputDialog("New volume", "Name", "my-volume", "Create")) is not { } dialog || dialog.Text.Trim() is not { Length: > 0 } name)
            return;
        try
        {
            _explorer.Report(await _explorer.Engine.CreateVolumeAsync(name), $"create {name}");
        }
        catch (DockerException ex)
        {
            _explorer.Report(ex);
        }
        await _explorer.RefreshDiskAsync();
    }
}

/// <summary>A row of the Networks table.</summary>
public sealed partial class NetworkRow : DockerTreeItem
{
    readonly NetworksPage _page;

    internal NetworkRow(NetworksPage page, DockerNetwork network)
    {
        _page = page;
        Network = network;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name), nameof(Driver), nameof(Scope), nameof(ShortId), nameof(CreatedText), nameof(CanDelete), nameof(Project))]
    public partial DockerNetwork Network { get; set; }

    public string Name => Network.Name;

    public string Driver => Network.Driver;

    public string Scope => Network.Scope;

    public string ShortId => Network.ShortId;

    public string CreatedText => DockerFormat.Ago(Network.Created);

    public bool CanDelete => !Network.IsBuiltIn;

    /// <summary>The compose project that made it, if any.</summary>
    public string? Project => Network.Labels.GetValueOrDefault("com.docker.compose.project");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContainersText), nameof(HasContainers))]
    public partial int Containers { get; set; }

    public bool HasContainers => Containers > 0;

    public string ContainersText => Containers == 0 ? "" : Containers.ToString(System.Globalization.CultureInfo.InvariantCulture);

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    public partial string? BusyText { get; set; }

    public bool IsBusy => BusyText is not null;

    partial void OnIsSelectedChanged(bool value) => _page.SelectionChanged();

    [RelayCommand]
    void Open() => _page.Open(this);

    [RelayCommand]
    Task Delete() => _page.DeleteAsync([this]);
}

/// <summary>The networks, which Docker Desktop leaves to the command line: drivers, the containers on each; Create and Delete.</summary>
public sealed partial class NetworksPage : ObservableObject
{
    readonly DockerExplorerViewModel _explorer;
    readonly Dictionary<string, NetworkRow> _rows = [];
    Dictionary<string, int> _attached = [];

    internal NetworksPage(DockerExplorerViewModel explorer) => _explorer = explorer;

    public ObservableCollection<NetworkRow> Rows { get; } = [];

    public IEnumerable<NetworkRow> AllRows => _rows.Values;

    public bool Contains(NetworkRow row) => _rows.TryGetValue(row.Network.Id, out var kept) && kept == row;

    [ObservableProperty]
    public partial string Search { get; set; } = "";

    public int Count => _rows.Count;

    public bool IsEmpty => _rows.Count == 0;

    public bool HasNoMatches => _rows.Count > 0 && Rows.Count == 0;

    public IReadOnlyList<NetworkRow> Selected => _rows.Values.Where(r => r.IsSelected).ToList();

    public int SelectedCount => Selected.Count;

    public bool HasSelection => SelectedCount > 0;

    public string SelectionText => SelectedCount == 1 ? "1 item selected" : $"{SelectedCount} items selected";

    public bool AllSelected
    {
        get => Rows.Count > 0 && Rows.Where(r => r.CanDelete).All(r => r.IsSelected);
        set
        {
            foreach (var row in Rows.Where(r => r.CanDelete))
                row.IsSelected = value;
        }
    }

    internal void SelectionChanged()
    {
        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(AllSelected));
    }

    partial void OnSearchChanged(string value) => Rebuild();

    internal void Apply(IReadOnlyList<DockerNetwork> networks)
    {
        var ids = networks.Select(n => n.Id).ToHashSet();
        foreach (var gone in _rows.Keys.Where(k => !ids.Contains(k)).ToList())
            _rows.Remove(gone);
        foreach (var network in networks)
        {
            if (_rows.TryGetValue(network.Id, out var row))
                row.Network = network;
            else
                _rows[network.Id] = new NetworkRow(this, network);
        }
        CountContainers();
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(IsEmpty));
        Rebuild();
        SelectionChanged();
    }

    /// <summary>How many containers each network has, from the containers' own lists.</summary>
    internal void ApplyContainers(IReadOnlyList<DockerContainer> containers)
    {
        _attached = containers.SelectMany(c => c.Networks).GroupBy(n => n).ToDictionary(g => g.Key, g => g.Count());
        CountContainers();
    }

    void CountContainers()
    {
        foreach (var row in _rows.Values)
            row.Containers = _attached.GetValueOrDefault(row.Name);
    }

    void Rebuild()
    {
        var search = Search.Trim();
        var rows = _rows.Values.Where(r => search.Length == 0 || r.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                || r.Driver.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => !r.Network.IsBuiltIn).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        ContainersPage.Sync(Rows, rows);
        OnPropertyChanged(nameof(HasNoMatches));
        OnPropertyChanged(nameof(AllSelected));
    }

    internal void Open(NetworkRow row) => _explorer.OpenNetwork(row);

    [RelayCommand]
    Task DeleteSelected() => DeleteAsync(Selected);

    [RelayCommand]
    void ClearSelection() => AllSelected = false;

    [RelayCommand]
    void ClearSearch() => Search = "";

    internal async Task DeleteAsync(IReadOnlyList<NetworkRow> rows)
    {
        rows = rows.Where(r => r.CanDelete).ToList();
        if (rows.Count == 0)
            return;
        var what = rows.Count == 1 ? rows[0].Name : $"{rows.Count} networks";
        if (!await _explorer.ConfirmAsync($"Delete {what}?", "Containers still attached to it keep it from being deleted.", "Delete", danger: true))
            return;
        foreach (var row in rows)
            row.BusyText = "Deleting…";
        try
        {
            _explorer.Report(await _explorer.Engine.RemoveNetworksAsync(rows.Select(r => r.Network.Id)), $"delete {what}");
        }
        catch (DockerException ex)
        {
            _explorer.Report(ex);
        }
        finally
        {
            foreach (var row in rows)
                row.BusyText = null;
        }
        await _explorer.RefreshNetworksAsync();
    }

    [RelayCommand]
    async Task Create()
    {
        if (await _explorer.ShowAsync(new InputDialog("New network", "Name", "my-network", "Create", ["bridge", "ipvlan", "macvlan"])) is not { } dialog
            || dialog.Text.Trim() is not { Length: > 0 } name)
            return;
        try
        {
            _explorer.Report(await _explorer.Engine.CreateNetworkAsync(name, dialog.Choice ?? "bridge"), $"create {name}");
        }
        catch (DockerException ex)
        {
            _explorer.Report(ex);
        }
        await _explorer.RefreshNetworksAsync();
    }
}
