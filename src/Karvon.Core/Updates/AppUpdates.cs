using System.Diagnostics;
using Karvon.Modules;
using Karvon.Services;

namespace Karvon.Updates;

/// <summary>What Karvon Studio does as it exits: nothing more, start again, or start again updated.</summary>
public enum ExitAction
{
    None,
    Restart,
    RestartUpdated,
}

/// <summary>
/// The running Karvon Studio's updates: one updater; checked for each time it starts (as the settings say), the update
/// downloaded when they say so; installed as it exits (and it starts again), when the user asked for it or the
/// settings say an update installs as Karvon Studio closes.
/// </summary>
public static class AppUpdates
{
    public static Updater Current { get; set; } = new(AppInstall.Current, UpdateFeed.Default, Path.Combine(ModuleComponent.DataRoot, "updates"));

    /// <summary>What happens as Karvon Studio exits (Restart and update, a module's Restart).</summary>
    public static ExitAction OnExit { get; set; }

    /// <summary>What Karvon Studio starts again with (the solution that was open).</summary>
    public static IReadOnlyList<string> RestartArguments { get; set; } = [];

    /// <summary>
    /// The check as Karvon Studio starts, when the settings ask for it; the update downloaded when they say so. The
    /// downloads of what is installed go first.
    /// </summary>
    public static async Task StartAsync(Updater updater, Settings settings, CancellationToken ct = default)
    {
        updater.CleanCache();
        if (!settings.UpdatesCheck || !updater.Install.CanUpdate)
            return;
        await updater.CheckAsync(ct);
        if (updater.State != UpdateState.Failed)
        {
            settings.UpdatesLastCheck = DateTimeOffset.Now;
            settings.Save();
        }
        if (updater.State == UpdateState.Available && settings.UpdatesDownload)
            await updater.DownloadAsync(ct);
    }

    /// <summary>
    /// As Karvon Studio exits (its windows closed): the update installed when asked for, or when it is ready and the
    /// settings say it installs as Karvon Studio closes (not Debian's package, whose apt asks for a password, nor while
    /// another Karvon Studio of the install runs on Windows, whose files it holds); then it starts again when asked to.
    /// </summary>
    public static async Task ExitAsync(Updater updater, Settings settings, IReadOnlyList<string> arguments)
    {
        var restart = OnExit != ExitAction.None;
        var install = updater.State == UpdateState.Ready
            && (OnExit == ExitAction.RestartUpdated
                || settings.UpdatesInstallOnClose && updater.InstallsQuietly && !(OperatingSystem.IsWindows() && OthersRunning(updater.Install)));
        if (install && await updater.ApplyAsync(restart, Environment.ProcessId, arguments))
            return;
        if (restart)
            updater.Install.StartAgain(arguments);
    }

    /// <summary>Whether another Karvon Studio of the same install is running (its files are in use on Windows).</summary>
    static bool OthersRunning(AppInstall install)
    {
        foreach (var process in Process.GetProcessesByName("Karvon"))
            using (process)
            {
                try
                {
                    if (process.Id != Environment.ProcessId && process.MainModule?.FileName is { } file
                        && string.Equals(Path.GetDirectoryName(file), install.Directory, FilePaths.Comparison))
                        return true;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
            }
        return false;
    }
}
