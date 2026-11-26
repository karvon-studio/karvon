using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Karvon.Machine.ViewModels;

/// <summary>
/// A disk, as its card shows it: its name, where it is mounted, how full; a click browses it. A disk not mounted (a
/// Windows partition, a second disk) is mounted first, as the file manager mounts it.
/// </summary>
public sealed partial class DiskCard(DisksPage page, string key) : ObservableObject
{
    /// <summary>Where it is mounted, or its device when it is not.</summary>
    public string Key { get; } = key;

    /// <summary>Its label, the computer's name for its root, or its size ("1.0 TB Volume").</summary>
    [ObservableProperty]
    public partial string Name { get; set; } = "";

    /// <summary>Where it is mounted; none when it is not.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMounted), nameof(Where), nameof(Tip))]
    public partial string? Mount { get; set; }

    /// <summary>Its device (/dev/sda2), for one not mounted.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Where), nameof(Tip))]
    public partial string? Device { get; set; }

    public bool IsMounted => Mount is not null;

    /// <summary>Under its name: where it is mounted, or its device.</summary>
    public string Where => Mount ?? Device ?? "";

    public string Tip => IsMounted ? $"Browse {Mount}" : $"Mount {Device} and browse it";

    [ObservableProperty]
    public partial bool IsMounting { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHigh))]
    public partial double Share { get; set; }

    [ObservableProperty]
    public partial string Text { get; set; } = "";

    [ObservableProperty]
    public partial string Detail { get; set; } = "";

    [ObservableProperty]
    public partial bool IsCurrent { get; set; }

    public bool IsHigh => Share >= 90;

    [RelayCommand]
    Task BrowseAsync() => page.BrowseAsync(this);
}

/// <summary>A folder of the path browsed: a step back to it.</summary>
public sealed record FolderCrumb(string Name, string Path);

/// <summary>A file or folder, as its row shows it; a folder's size once it is measured.</summary>
public sealed partial class FileRow(FileEntry entry) : ObservableObject
{
    public FileEntry Entry { get; } = entry;

    public string Name => Entry.Name;

    public bool IsFolder => Entry.Kind == FileKind.Folder;

    public string Icon => Entry.Kind switch
    {
        FileKind.Folder => "Folder",
        FileKind.Image => "FileImage",
        FileKind.Video => "FileVideo",
        FileKind.Audio => "FileAudio",
        _ => "FileCode",
    };

    public string KindText => Entry.Kind switch
    {
        FileKind.Folder => Entry.IsLink ? "Folder (link)" : "Folder",
        FileKind.Image => "Picture",
        FileKind.Video => "Video",
        FileKind.Audio => "Sound",
        _ => System.IO.Path.GetExtension(Entry.Name) is { Length: > 1 } e ? e[1..].ToUpperInvariant() + " file" : "File",
    };

    public string ModifiedText => Entry.Modified.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Its size: a file's, or a folder's once measured (in bytes).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    public partial long? Size { get; set; } = entry.Size;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    public partial bool IsMeasuring { get; set; }

    public string SizeText => IsMeasuring ? "…" : Size is { } size ? ResourceSampler.Bytes(size) : "";

    /// <summary>Its share of the largest row's size: the bar behind its size.</summary>
    [ObservableProperty]
    public partial double Share { get; set; }
}

/// <summary>
/// The Disks page: each disk (where it is mounted, how full), and a folder of one browsed: its files and folders, a
/// folder opened, a picture, a video or a sound opened in Karvon (the rest in its editor, or with the system's program);
/// its folders measured to see what the space went to.
/// </summary>
public sealed partial class DisksPage : ObservableObject
{
    /// <summary>How deep folders are measured: the rings shown (four), and one more for a folder gone into.</summary>
    internal const int Depth = 5;

    readonly MachineExplorerViewModel _explorer;
    List<FileRow> _all = [];
    CancellationTokenSource? _measuring;

    /// <summary>The folder last measured: its folders' folders too, to <see cref="Depth"/>.</summary>
    FolderSize? _measured;

    public DisksPage(MachineExplorerViewModel explorer)
    {
        _explorer = explorer;
        Folder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    public ObservableCollection<DiskCard> Disks { get; } = [];

    public ObservableCollection<FileRow> Rows { get; } = [];

    /// <summary>The folder browsed, as its path's folders: each a step back to it.</summary>
    public ObservableCollection<FolderCrumb> Crumbs { get; } = [];

    [ObservableProperty]
    public partial string Folder { get; set; }

    [ObservableProperty]
    public partial string Search { get; set; } = "";

    [ObservableProperty]
    public partial bool ShowHidden { get; set; }

    /// <summary>"name" in its order, "size" and "modified" the most first.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameSort), nameof(SizeSort), nameof(ModifiedSort))]
    public partial string SortBy { get; set; } = "name";

    public string NameSort => SortBy == "name" ? "↑" : "";

    public string SizeSort => SortBy == "size" ? "↓" : "";

    public string ModifiedSort => SortBy == "modified" ? "↓" : "";

    [ObservableProperty]
    public partial string? Problem { get; set; }

    /// <summary>The folder browsed, measured (it or a folder it is in was): its rings, what its space went to.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChart))]
    public partial FolderSize? Chart { get; set; }

    public bool HasChart => Chart is not null;

    /// <summary>Counted up as a folder of the chart is measured: it is drawn again.</summary>
    [ObservableProperty]
    public partial int ChartRevision { get; set; }

    public bool HasProblem => Problem is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MeasureText))]
    public partial bool IsMeasuring { get; set; }

    public string MeasureText => IsMeasuring ? "Measuring…" : "Measure Folders";

    public string CountText => Rows.Count == _all.Count ? $"{_all.Count} items" : $"Showing {Rows.Count} of {_all.Count}";

    public bool CanGoUp => Directory.GetParent(Folder) is not null;

    partial void OnSearchChanged(string value) => Show();

    partial void OnShowHiddenChanged(bool value) => Show();

    partial void OnSortByChanged(string value) => Show();

    partial void OnProblemChanged(string? value) => OnPropertyChanged(nameof(HasProblem));

    /// <summary>The disks mounted, each named by its file system (<paramref name="volumes"/>), then those that may be mounted.</summary>
    internal void Update(IReadOnlyList<DiskUsage> disks, IReadOnlyList<Volume> volumes)
    {
        var unmounted = Volumes.Unmounted(volumes);
        List<string> keys = [.. disks.Select(d => d.Mount), .. unmounted.Select(v => v.Device)];
        for (var i = Disks.Count - 1; i >= 0; i--)
            if (!keys.Contains(Disks[i].Key))
                Disks.RemoveAt(i);
        foreach (var disk in disks)
        {
            var card = Card(disk.Mount, keys);
            card.Name = Volumes.NameOf(disk, volumes);
            card.Mount = disk.Mount;
            card.Share = disk.Share;
            card.Text = $"{ResourceSampler.Bytes(disk.Free)} free of {ResourceSampler.Bytes(disk.Total)}";
            card.Detail = $"{disk.Format} · {disk.Share:0}% used";
        }
        foreach (var volume in unmounted)
        {
            var card = Card(volume.Device, keys);
            card.Name = volume.Name;
            card.Device = volume.Device;
            card.Share = 0;
            if (!card.IsMounting)
                card.Text = "Not mounted";
            card.Detail = $"{volume.Format} · {ResourceSampler.Bytes(volume.Size)}";
        }
        MarkCurrent();
        if (_all.Count == 0 && Problem is null)
            Browse(Folder);
    }

    /// <summary>The card of a disk, in its place (the mounted ones first): the one it had, else a new one.</summary>
    DiskCard Card(string key, List<string> keys)
    {
        var at = keys.IndexOf(key);
        var card = Disks.FirstOrDefault(c => c.Key == key);
        if (card is null)
            Disks.Insert(Math.Min(at, Disks.Count), card = new DiskCard(this, key));
        else if (Disks.IndexOf(card) is var now && now != at && at < Disks.Count)
            Disks.Move(now, at);
        return card;
    }

    /// <summary>A disk's card chosen: browsed, mounted first when it is not (the system asks for the password it wants).</summary>
    internal async Task BrowseAsync(DiskCard card)
    {
        if (card.Mount is { } mount)
        {
            Browse(mount);
            return;
        }
        if (card.IsMounting || card.Device is not { } device)
            return;
        card.IsMounting = true;
        card.Text = "Mounting…";
        Problem = null;
        try
        {
            if (await _explorer.MountVolume(device, CancellationToken.None) is { } why)
            {
                card.Text = "Not mounted";
                Problem = $"{card.Name} could not be mounted: {why}";
                return;
            }
            var volumes = await Task.Run(() => _explorer.ListVolumes());
            _explorer.ForgetVolumes();
            if (volumes.FirstOrDefault(v => v.Device == device)?.Mounts.FirstOrDefault() is { } mounted)
                Browse(mounted);
            _ = _explorer.RefreshAsync();
        }
        finally
        {
            card.IsMounting = false;
        }
    }

    /// <summary>A folder browsed: its files and folders read (what was being measured stops).</summary>
    public void Browse(string folder)
    {
        _measuring?.Cancel();
        IsMeasuring = false;
        try
        {
            var entries = Files.List(folder);
            Folder = Path.GetFullPath(folder);
            _all = [.. entries.Select(e => new FileRow(e))];
            Problem = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Problem = $"{folder} could not be read: {ex.Message}";
            return;
        }
        Crumbs.Clear();
        for (var dir = new DirectoryInfo(Folder); dir is not null; dir = dir.Parent)
            Crumbs.Insert(0, new FolderCrumb(dir.Parent is null ? dir.FullName : dir.Name, dir.FullName));
        Search = "";
        // Within the folder measured, its folders' sizes are known (the largest first); elsewhere not yet (by name).
        Chart = _measured?.Find(Folder) is { IsListed: true } measured ? measured : null;
        if (Chart is { } known)
            foreach (var row in _all.Where(r => r.IsFolder && !r.Entry.IsLink))
                row.Size = known.Folders.FirstOrDefault(f => f.Path == row.Entry.Path)?.Size;
        SortBy = Chart is null ? "name" : "size";
        Show();
        MarkCurrent();
        OnPropertyChanged(nameof(CanGoUp));
    }

    /// <summary>The disk the folder is on (its deepest mount), shown as the one browsed.</summary>
    void MarkCurrent()
    {
        var current = Disks.Where(d => d.Mount is { } mount && (Folder.StartsWith(Path.TrimEndingDirectorySeparator(mount) + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || Folder == mount || mount == Path.GetPathRoot(Folder)))
            .MaxBy(d => d.Mount!.Length);
        foreach (var disk in Disks)
            disk.IsCurrent = disk == current;
    }

    void Show()
    {
        var words = Search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var rows = _all.Where(r => (ShowHidden || !r.Entry.IsHidden) && words.All(w => r.Name.Contains(w, StringComparison.OrdinalIgnoreCase)));
        rows = SortBy switch
        {
            "size" => rows.OrderByDescending(r => r.Size ?? -1).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
            "modified" => rows.OrderByDescending(r => r.Entry.Modified),
            _ => rows.OrderBy(r => r.IsFolder ? 0 : 1).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        };
        Rows.Clear();
        foreach (var row in rows)
            Rows.Add(row);
        Shares();
        OnPropertyChanged(nameof(CountText));
    }

    void Shares()
    {
        var largest = Math.Max(1, _all.Max(r => r.Size) ?? 1);
        foreach (var row in _all)
            row.Share = row.Size is { } size ? 100.0 * size / largest : 0;
    }

    [RelayCommand]
    void Up()
    {
        if (Directory.GetParent(Folder) is { } parent)
            Browse(parent.FullName);
    }

    [RelayCommand]
    void Home() => Browse(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    [RelayCommand]
    void Crumb(string path) => Browse(path);

    [RelayCommand]
    void Sort(string by) => SortBy = by;

    /// <summary>A row opened: a folder browsed, a picture, video or sound in Karvon, the rest in Karvon's editor.</summary>
    [RelayCommand]
    public void Open(FileRow? row)
    {
        if (row is null)
            return;
        if (row.IsFolder)
            Browse(row.Entry.Path);
        else
            _explorer.OpenFile(row.Entry.Path);
    }

    /// <summary>A ring's folder chosen: browsed (its rings shown); the middle chosen, the folder it is in.</summary>
    public void Choose(FolderSize folder)
    {
        if (folder == Chart)
            Up();
        else
            Browse(folder.Path);
    }

    [RelayCommand]
    void OpenWithSystem(FileRow? row) => Files.OpenWithSystem(row?.Entry.Path ?? Folder);

    /// <summary>Each folder's size, the largest first as they come: what the disk's space went to.</summary>
    [RelayCommand]
    async Task MeasureAsync()
    {
        if (IsMeasuring)
        {
            _measuring?.Cancel();
            return;
        }
        _measuring = new CancellationTokenSource();
        var ct = _measuring.Token;
        IsMeasuring = true;
        SortBy = "size";
        var mounts = Files.MountPoints();
        var folders = _all.Where(r => r.IsFolder && !r.Entry.IsLink).ToList();
        foreach (var folder in folders)
            folder.IsMeasuring = true;
        // Its files first, then each folder as it is measured (its folders' folders kept for its rings).
        var root = new FolderSize(Path.GetFileName(Path.TrimEndingDirectorySeparator(Folder)) is { Length: > 0 } name ? name : Folder, Folder)
        {
            IsListed = true,
            Size = _all.Where(r => !r.IsFolder).Sum(r => r.Size ?? 0),
        };
        _measured = root;
        Chart = root;
        try
        {
            foreach (var folder in folders)
            {
                var measured = await Task.Run(() => Files.Measure(folder.Entry.Path, mounts, Depth - 1, ct), ct);
                folder.Size = measured.Size;
                folder.IsMeasuring = false;
                root.Add(measured);
                root.Size += measured.Size;
                ChartRevision++;
                Show();
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            foreach (var folder in folders)
                folder.IsMeasuring = false;
            IsMeasuring = false;
        }
    }
}
