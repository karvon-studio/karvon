using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Karvon.Machine.ViewModels;

/// <summary>A disk, as its card shows it: where it is, how full; a click browses it.</summary>
public sealed partial class DiskCard(DisksPage page, string mount) : ObservableObject
{
    public string Mount { get; } = mount;

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
    void Browse() => page.Browse(Mount);
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
    readonly MachineExplorerViewModel _explorer;
    List<FileRow> _all = [];
    CancellationTokenSource? _measuring;

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

    internal void Update(IReadOnlyList<DiskUsage> disks)
    {
        var mounts = disks.Select(d => d.Mount).ToList();
        for (var i = Disks.Count - 1; i >= 0; i--)
            if (!mounts.Contains(Disks[i].Mount))
                Disks.RemoveAt(i);
        foreach (var disk in disks)
        {
            var card = Disks.FirstOrDefault(c => c.Mount == disk.Mount);
            if (card is null)
                Disks.Add(card = new DiskCard(this, disk.Mount));
            card.Share = disk.Share;
            card.Text = $"{ResourceSampler.Bytes(disk.Free)} free of {ResourceSampler.Bytes(disk.Total)}";
            card.Detail = $"{disk.Format} · {disk.Share:0}% used";
        }
        MarkCurrent();
        if (_all.Count == 0 && Problem is null)
            Browse(Folder);
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
        // Another folder's folders are not measured yet: by name again.
        SortBy = "name";
        Show();
        MarkCurrent();
        OnPropertyChanged(nameof(CanGoUp));
    }

    /// <summary>The disk the folder is on (its deepest mount), shown as the one browsed.</summary>
    void MarkCurrent()
    {
        var current = Disks.Where(d => Folder.StartsWith(Path.TrimEndingDirectorySeparator(d.Mount) + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Folder == d.Mount
                || d.Mount == Path.GetPathRoot(Folder))
            .MaxBy(d => d.Mount.Length);
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
        try
        {
            foreach (var folder in folders)
            {
                folder.Size = await Task.Run(() => Files.SizeOf(folder.Entry.Path, mounts, ct), ct);
                folder.IsMeasuring = false;
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
