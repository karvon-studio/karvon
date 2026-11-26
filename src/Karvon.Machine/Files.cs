using System.Diagnostics;

namespace Karvon.Machine;

/// <summary>What a file is, as This Computer opens it: a picture and a video or sound in Karvon, the rest in its editor.</summary>
public enum FileKind
{
    Folder,
    Image,
    Video,
    Audio,
    Other,
}

/// <summary>A file or folder of a folder, as it was read.</summary>
public sealed record FileEntry(string Name, string Path, FileKind Kind, long? Size, DateTime Modified, bool IsHidden, bool IsLink);

/// <summary>
/// A folder measured: its size, and its folders' (the largest first) to the depth it was measured to; what a disk's space
/// went to, as its rings show it.
/// </summary>
public sealed class FolderSize(string name, string path)
{
    /// <summary>The most folders a folder keeps: the rest are in its size alone (too small to be seen in its ring).</summary>
    internal const int Most = 200;

    public string Name { get; } = name;

    public string Path { get; } = path;

    public long Size { get; internal set; }

    public FolderSize? Parent { get; private set; }

    /// <summary>Its folders, the largest first (none past the depth measured: see <see cref="IsListed"/>).</summary>
    public IReadOnlyList<FolderSize> Folders => _folders;

    /// <summary>Its folders were measured one by one (it was within the depth measured).</summary>
    public bool IsListed { get; internal set; }

    readonly List<FolderSize> _folders = [];

    /// <summary>A folder of it measured: in its place by size (its size is the caller's to add).</summary>
    internal void Add(FolderSize folder)
    {
        folder.Parent = this;
        var at = _folders.FindIndex(f => f.Size < folder.Size);
        _folders.Insert(at < 0 ? _folders.Count : at, folder);
        if (_folders.Count > Most)
            _folders.RemoveAt(Most);
    }

    /// <summary>The folder measured at a path within it (itself, or one of its folders' folders); none past its depth.</summary>
    public FolderSize? Find(string path)
    {
        var node = this;
        while (node is not null && !string.Equals(node.Path, path, StringComparison.Ordinal))
        {
            var within = System.IO.Path.TrimEndingDirectorySeparator(node.Path) + System.IO.Path.DirectorySeparatorChar;
            node = path.StartsWith(within, StringComparison.Ordinal) ? node._folders.FirstOrDefault(f =>
                string.Equals(f.Path, path, StringComparison.Ordinal) || path.StartsWith(f.Path + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal)) : null;
        }
        return node;
    }
}

/// <summary>Folders read, files told apart by what they are, and a folder's size (what a disk's space went to).</summary>
public static class Files
{
    static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico" };

    static readonly HashSet<string> Videos = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".m4v", ".mkv", ".webm", ".mov", ".avi", ".mpg", ".mpeg", ".wmv", ".flv", ".3gp", ".ts", ".ogv" };

    static readonly HashSet<string> Sounds = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".flac", ".wav", ".ogg", ".oga", ".opus", ".m4a", ".aac", ".wma", ".aiff", ".aif" };

    public static FileKind KindOf(string path) => System.IO.Path.GetExtension(path) switch
    {
        var e when Images.Contains(e) => FileKind.Image,
        var e when Videos.Contains(e) => FileKind.Video,
        var e when Sounds.Contains(e) => FileKind.Audio,
        _ => FileKind.Other,
    };

    /// <summary>A folder's files and folders, its folders first; what cannot be read is left out.</summary>
    public static IReadOnlyList<FileEntry> List(string folder)
    {
        List<FileEntry> entries = [];
        foreach (var info in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }))
        {
            try
            {
                var isLink = info.LinkTarget is not null;
                var isFolder = info is DirectoryInfo || isLink && Directory.Exists(info.FullName);
                var hidden = info.Name.StartsWith('.') || info.Attributes.HasFlag(FileAttributes.Hidden);
                entries.Add(new FileEntry(info.Name, info.FullName, isFolder ? FileKind.Folder : KindOf(info.Name),
                    isFolder ? null : info is FileInfo file && file.Exists ? file.Length : null, info.LastWriteTime, hidden, isLink));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return [.. entries.OrderBy(e => e.Kind == FileKind.Folder ? 0 : 1).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Where other file systems are mounted: a folder's size stops at them (and at /proc's, /sys's...).</summary>
    public static HashSet<string> MountPoints()
    {
        HashSet<string> mounts = new(StringComparer.Ordinal);
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
                mounts.Add(System.IO.Path.TrimEndingDirectorySeparator(drive.Name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return mounts;
    }

    /// <summary>
    /// A folder measured, its folders listed with their sizes to <paramref name="depth"/> levels below it (deeper ones are in
    /// their sizes alone): links not followed, other file systems (and /proc's, /sys's) not entered, what cannot be read left out.
    /// </summary>
    public static FolderSize Measure(string folder, HashSet<string> mounts, int depth, CancellationToken ct)
    {
        var node = new FolderSize(System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(folder)) is { Length: > 0 } name ? name : folder, folder);
        node.Size = Walk(folder, node, depth, mounts, new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }, ct);
        return node;
    }

    static long Walk(string dir, FolderSize? node, int depth, HashSet<string> mounts, EnumerationOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var listed = node is not null && depth > 0;
        if (listed)
            node!.IsListed = true;
        long size = 0;
        try
        {
            foreach (var info in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", options))
            {
                if (info is FileInfo file)
                    size += file.Length;
                else if (!mounts.Contains(info.FullName))
                {
                    var folder = listed ? new FolderSize(info.Name, info.FullName) : null;
                    var folderSize = Walk(info.FullName, folder, depth - 1, mounts, options, ct);
                    size += folderSize;
                    if (folder is not null)
                    {
                        folder.Size = folderSize;
                        node!.Add(folder);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return size;
    }

    /// <summary>A file opened with the program the system opens it with (its own player, viewer...).</summary>
    public static void OpenWithSystem(string path)
    {
        try
        {
            var start = OperatingSystem.IsWindows() ? new ProcessStartInfo(path) { UseShellExecute = true }
                : new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open", [path]) { UseShellExecute = false, CreateNoWindow = true };
            using var _ = Process.Start(start);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }
}
