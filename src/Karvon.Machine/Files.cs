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
    /// What a folder holds, in bytes: its files and its folders' (links not followed, other file systems not entered, what
    /// cannot be read left out).
    /// </summary>
    public static long SizeOf(string folder, HashSet<string> mounts, CancellationToken ct)
    {
        long size = 0;
        var pending = new Stack<string>([folder]);
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint, RecurseSubdirectories = false };
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = pending.Pop();
            try
            {
                foreach (var info in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", options))
                {
                    if (info is FileInfo file)
                        size += file.Length;
                    else if (!mounts.Contains(info.FullName))
                        pending.Push(info.FullName);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
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
