using Karvon.Services;

namespace Karvon.Modules;

/// <summary>
/// A part a module downloads the first time it is needed (a language server, a debugger): Tools > Get Tools and Features shows it,
/// installs it at once, and removes it (when Karvon Studio starts again, since it may be running now).
/// </summary>
/// <param name="InstallRoot">Its folder, in Karvon's own data folder (~/.local/share/karvon/...).</param>
/// <param name="IsInstalled">Whether it is there to use (its folder, or the one an environment variable names).</param>
/// <param name="InstallAsync">Downloads and installs it (what it does in the log).</param>
public sealed record ModuleComponent(string Id, string Name, string Version, string InstallRoot, Func<bool> IsInstalled,
    Func<Action<string>, CancellationToken, Task> InstallAsync)
{
    /// <summary>What it is, and where it comes from.</summary>
    public string Description { get; init; } = "";

    /// <summary>About the disk it takes once installed (as measured on Linux x64), for the installer's space required; 0 when not known.</summary>
    public long ApproximateSize { get; init; }

    /// <summary>
    /// The folder every component is in (~/.local/share/karvon, %LOCALAPPDATA%\karvon); nothing out of it is ever removed.
    /// Where the user's data folder is not made yet, it is still its path (a fresh home has no ~/.local/share).
    /// </summary>
    public static string DataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), "karvon");

    /// <summary>The disk it takes, installed (0 when it is not).</summary>
    public long Size()
    {
        if (!Directory.Exists(InstallRoot))
            return 0;
        try
        {
            return new DirectoryInfo(InstallRoot).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>
    /// The components removed in Tools > Get Tools and Features go as Karvon Studio starts, before anything can run them; only folders
    /// in <paramref name="dataRoot"/> (a settings file written elsewhere cannot point it at another).
    /// </summary>
    public static void RemovePending(Settings settings, string dataRoot)
    {
        if (settings.RemovedComponents.Count == 0)
            return;
        foreach (var folder in settings.RemovedComponents)
        {
            if (!FilePaths.IsUnder(Path.GetFullPath(folder), dataRoot) || !Directory.Exists(folder))
                continue;
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // In use by another Karvon: it goes another time.
                continue;
            }
        }
        settings.RemovedComponents = [.. settings.RemovedComponents.Where(Directory.Exists)];
        settings.Save();
    }
}
