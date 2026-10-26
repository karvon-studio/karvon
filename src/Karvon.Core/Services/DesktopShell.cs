using System.ComponentModel;
using System.Diagnostics;

namespace Karvon.Services;

/// <summary>
/// What the desktop does for Karvon: a page in the browser, a folder in the file manager, a file into the trash. On
/// Linux by xdg-open and gio, on macOS by open and the user's Trash, on Windows by the shell and the Recycle Bin.
/// </summary>
public static class DesktopShell
{
    /// <summary>A URL, file or folder opened as a double click opens it; false when nothing on the desktop can.</summary>
    public static bool Open(string target)
    {
        try
        {
            var start = OperatingSystem.IsWindows()
                ? new ProcessStartInfo(target) { UseShellExecute = true }
                : new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open", [target]) { UseShellExecute = false, CreateNoWindow = true };
            using var process = Process.Start(start);
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    /// <summary>A file or folder into the trash; false when the desktop has none, and it is to be deleted for good.</summary>
    public static async Task<bool> MoveToTrashAsync(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return RecycleOnWindows(path);
            if (OperatingSystem.IsMacOS())
                return MoveToUserTrash(path);
            using var gio = Process.Start(new ProcessStartInfo("gio", ["trash", path])
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (gio is null)
                return false;
            await gio.WaitForExitAsync();
            return gio.ExitCode == 0;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // No trash: deleted for good.
            return false;
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static bool RecycleOnWindows(string path)
    {
        const Microsoft.VisualBasic.FileIO.UIOption errorsOnly = Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs;
        const Microsoft.VisualBasic.FileIO.RecycleOption recycle = Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin;
        if (Directory.Exists(path))
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path, errorsOnly, recycle);
        else
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, errorsOnly, recycle);
        return true;
    }

    /// <summary>macOS's Trash is ~/.Trash: the file goes there, by another name when one of its name is there already.</summary>
    static bool MoveToUserTrash(string path)
    {
        var trash = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".Trash");
        if (!Directory.Exists(trash))
            return false;
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
        var target = Path.Combine(trash, name);
        for (var i = 2; File.Exists(target) || Directory.Exists(target); i++)
            target = Path.Combine(trash, $"{Path.GetFileNameWithoutExtension(name)} {i}{Path.GetExtension(name)}");
        if (Directory.Exists(path))
            Directory.Move(path, target);
        else
            File.Move(path, target);
        return true;
    }
}
