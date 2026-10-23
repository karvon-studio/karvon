using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Karvon.Build;
using Karvon.Packages;

namespace Karvon.Updates;

public enum UpdateState
{
    /// <summary>Not checked yet.</summary>
    Idle,
    Checking,
    UpToDate,
    /// <summary>A newer release has this install's file.</summary>
    Available,
    Downloading,
    /// <summary>Downloaded, checked and unpacked: it installs when Karvon Studio restarts (or closes).</summary>
    Ready,
    Installing,
    Failed,
}

/// <summary>
/// Karvon Studio's updates, as Visual Studio's installer has them: the newest release checked for, its file for this
/// install downloaded, its checksum (the release's SHA256SUMS) checked and unpacked beside the install; then, when
/// Karvon Studio restarts or closes, put in place of the install: a folder or an AppImage replaced (Linux and macOS let a
/// running program's files be renamed), Windows' files copied over once Karvon has exited (a script waits for it), its
/// installer run silently, Debian's package installed by apt as root (pkexec asks). A build of the sources does not update.
/// </summary>
public sealed partial class Updater(AppInstall install, UpdateFeed feed, string cacheRoot) : ObservableObject
{
    public AppInstall Install => install;

    [ObservableProperty]
    public partial UpdateState State { get; private set; }

    /// <summary>What it says now: why it failed, how far a download is.</summary>
    [ObservableProperty]
    public partial string Message { get; private set; } = "";

    /// <summary>The newest release (null until checked); <see cref="IsNewer"/> when it is this install's update.</summary>
    [ObservableProperty]
    public partial ReleaseInfo? Latest { get; private set; }

    /// <summary>A download's progress, 0 to 1.</summary>
    [ObservableProperty]
    public partial double Progress { get; private set; }

    /// <summary>When it last checked.</summary>
    public DateTimeOffset? LastChecked { get; private set; }

    /// <summary>The update downloaded and unpacked: the file or folder that takes the install's place.</summary>
    string? _staged;

    public bool IsNewer => Latest is { } latest && PackageVersions.Compare(latest.Version, install.Version) > 0;

    /// <summary>
    /// The newest release (not a prerelease) that has this install's file; Available when it is newer than this one.
    /// </summary>
    public async Task<ReleaseInfo?> CheckAsync(CancellationToken ct = default)
    {
        if (State is UpdateState.Downloading or UpdateState.Installing)
            return Latest;
        State = UpdateState.Checking;
        Message = "Checking for updates...";
        try
        {
            var releases = await feed.ReleasesAsync(ct);
            Latest = releases.Where(r => !r.Prerelease && (!install.CanUpdate || r.Asset(install.AssetName(r.Version)!) is not null))
                .OrderByDescending(r => r.Version, Comparer<string>.Create(PackageVersions.Compare)).FirstOrDefault();
            LastChecked = DateTimeOffset.Now;
            if (State == UpdateState.Ready && _staged is not null)
                return Latest;
            (State, Message) = IsNewer && install.CanUpdate
                ? (UpdateState.Available, $"Karvon Studio {Latest!.Version} is available.")
                : (UpdateState.UpToDate, Latest is null ? "No release yet." : IsNewer ? $"Karvon Studio {Latest.Version} is out (this build is of the sources: it updates with git pull)." : "Karvon Studio is up to date.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            (State, Message) = (UpdateState.Failed, $"Could not check for updates: {ex.Message}");
        }
        return Latest;
    }

    /// <summary>The update's folder in the cache: its downloads.</summary>
    string CacheOf(string version) => Path.Combine(cacheRoot, version);

    /// <summary>
    /// The newest release's file downloaded, its checksum checked against the release's SHA256SUMS, and unpacked beside
    /// the install, ready to take its place: Ready, else Failed (why in <see cref="Message"/>).
    /// </summary>
    public async Task<bool> DownloadAsync(CancellationToken ct = default)
    {
        if (State == UpdateState.Ready)
            return true;
        if (!install.CanUpdate || !IsNewer || Latest is not { } release || release.Asset(install.AssetName(release.Version)!) is not { } asset)
            return false;
        State = UpdateState.Downloading;
        Progress = 0;
        try
        {
            if (release.Asset("SHA256SUMS") is not { } sums)
                throw new InvalidOperationException($"the release has no SHA256SUMS to check {asset.Name} with.");
            var folder = CacheOf(release.Version);
            var sumsFile = Path.Combine(folder, "SHA256SUMS");
            await feed.DownloadAsync(release, sums, sumsFile, null, ct);
            var expected = File.ReadAllLines(sumsFile).Select(l => l.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries))
                .FirstOrDefault(p => p.Length == 2 && p[1].TrimStart('*').Trim() == asset.Name)?[0];
            if (expected is null)
                throw new InvalidOperationException($"SHA256SUMS does not list {asset.Name}.");
            var file = Path.Combine(folder, asset.Name);
            Message = $"Downloading Karvon Studio {release.Version}...";
            await feed.DownloadAsync(release, asset, file, new Progress<(long Done, long Total)>(p =>
            {
                Progress = p.Total > 0 ? (double)p.Done / p.Total : 0;
                Message = $"Downloading Karvon Studio {release.Version}: {p.Done >> 20} MB of {p.Total >> 20} MB";
            }), ct);
            string actual;
            await using (var stream = File.OpenRead(file))
                actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
            // (Closed first: Windows deletes no open file.)
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(file);
                throw new InvalidOperationException($"{asset.Name}'s checksum is not the release's: it was not kept.");
            }
            Message = $"Unpacking Karvon Studio {release.Version}...";
            _staged = await Task.Run(() => StageAsync(file, release.Version, ct), ct);
            (State, Message, Progress) = (UpdateState.Ready, $"Karvon Studio {release.Version} is ready: it installs when Karvon Studio restarts.", 1);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException
            or TaskCanceledException && !ct.IsCancellationRequested)
        {
            (State, Message) = (UpdateState.Failed, $"Karvon Studio {release.Version} could not be downloaded: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// The downloaded file made ready to take the install's place, beside it (a rename, then, is all it takes): a
    /// folder unpacked, an AppImage made runnable, a .dmg's app copied out; an installer and a package as they are.
    /// </summary>
    async Task<string> StageAsync(string file, string version, CancellationToken ct)
    {
        switch (install.Distribution)
        {
            case Distribution.Tar:
            {
                var staging = Path.Combine(Path.GetDirectoryName(install.Directory)!, $".karvon-update-{version}");
                var unpacked = staging + ".unpacked";
                DeleteIfThere(staging);
                DeleteIfThere(unpacked);
                Directory.CreateDirectory(unpacked);
                await using (var gz = new GZipStream(File.OpenRead(file), CompressionMode.Decompress))
                    await TarFile.ExtractToDirectoryAsync(gz, unpacked, overwriteFiles: true, ct);
                // The archive's one folder (karvon-X.Y.Z) is the app.
                if (Directory.GetDirectories(unpacked) is [var only] && Directory.GetFiles(unpacked).Length == 0)
                {
                    Directory.Move(only, staging);
                    Directory.Delete(unpacked);
                }
                else
                    Directory.Move(unpacked, staging);
                return staging;
            }
            case Distribution.AppImage:
            {
                var staging = Path.Combine(Path.GetDirectoryName(install.AppImage!)!, $".{Path.GetFileName(install.AppImage)}.update");
                File.Copy(file, staging, overwrite: true);
                File.SetUnixFileMode(staging, (UnixFileMode)0b111_101_101);
                return staging;
            }
            case Distribution.Dmg:
            {
                var staging = Path.Combine(Path.GetDirectoryName(install.Bundle)!, $".Karvon Studio {version}.app");
                DeleteIfThere(staging);
                var mount = Directory.CreateTempSubdirectory("karvon-dmg-").FullName;
                if (await RunAsync("hdiutil", ["attach", "-nobrowse", "-readonly", "-noautoopen", "-mountpoint", mount, file], ct) != 0)
                    throw new InvalidOperationException("hdiutil could not open the .dmg.");
                try
                {
                    if (await RunAsync("ditto", [Path.Combine(mount, "Karvon Studio.app"), staging], ct) != 0)
                        throw new InvalidOperationException("the .dmg's Karvon Studio.app could not be copied.");
                }
                finally
                {
                    await RunAsync("hdiutil", ["detach", mount, "-force"], CancellationToken.None);
                }
                return staging;
            }
            case Distribution.Zip:
            {
                var staging = Path.Combine(CacheOf(version), "app");
                DeleteIfThere(staging);
                await ZipFile.ExtractToDirectoryAsync(file, staging, overwriteFiles: true, ct);
                return staging;
            }
            default:
                return file;
        }
    }

    /// <summary>The downloads of updates that are installed now (this version's, and older ones') go.</summary>
    public void CleanCache()
    {
        if (!Directory.Exists(cacheRoot))
            return;
        foreach (var folder in Directory.GetDirectories(cacheRoot).Where(f => PackageVersions.Compare(Path.GetFileName(f), install.Version) <= 0))
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    static void DeleteIfThere(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
        else if (File.Exists(path))
            File.Delete(path);
    }

    /// <summary>Whether a downloaded update installs as Karvon Studio closes without asking (apt asks for a password).</summary>
    public bool InstallsQuietly => install.Distribution != Distribution.Deb;

    /// <summary>
    /// The ready update put in the install's place, as this Karvon Studio exits; started again after it when
    /// <paramref name="restart"/>. Windows' copy and installer wait for <paramref name="exitingProcess"/> to exit first.
    /// False when it did not (why in <see cref="Message"/>): the install is as it was.
    /// </summary>
    public async Task<bool> ApplyAsync(bool restart, int exitingProcess, IReadOnlyList<string>? arguments = null)
    {
        if (State != UpdateState.Ready || _staged is not { } staged || Latest is not { } release)
            return false;
        State = UpdateState.Installing;
        Message = $"Installing Karvon Studio {release.Version}...";
        try
        {
            switch (install.Distribution)
            {
                case Distribution.Tar:
                    Swap(install.Directory, staged);
                    break;
                case Distribution.Dmg:
                    Swap(install.Bundle, staged);
                    break;
                case Distribution.AppImage:
                    File.Move(staged, install.AppImage!, overwrite: true);
                    break;
                case Distribution.Deb:
                    // apt, as root: pkexec asks for the password (it may be said no to).
                    if (await RunAsync("pkexec", ["apt-get", "install", "-y", staged], CancellationToken.None) != 0)
                        throw new InvalidOperationException("apt did not install it (pkexec's password was not given, or apt failed).");
                    break;
                case Distribution.Setup:
                case Distribution.Zip:
                    StartWindowsScript(staged, restart, exitingProcess, arguments ?? []);
                    State = UpdateState.Idle;
                    return true;
            }
            if (restart)
                install.StartAgain(arguments ?? []);
            (State, Message) = (UpdateState.Idle, $"Karvon Studio {release.Version} is installed.");
            _staged = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            (State, Message) = (UpdateState.Failed, $"Karvon Studio {release.Version} could not be installed: {ex.Message}");
            return false;
        }
    }

    /// <summary>A folder in place of another: the old one moved aside (a running program's may be), back when the new one cannot go.</summary>
    static void Swap(string target, string staged)
    {
        var old = $"{target}.old-{Environment.ProcessId}";
        DeleteIfThere(old);
        Directory.Move(target, old);
        try
        {
            Directory.Move(staged, target);
        }
        catch
        {
            Directory.Move(old, target);
            throw;
        }
        try
        {
            Directory.Delete(old, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // In use: it goes another time (it is beside the install, not in it).
        }
    }

    /// <summary>
    /// Windows' update, once Karvon Studio has exited (its files are in use until then): a script that waits for it, then
    /// copies the .zip's files over the install, or runs the installer silently (for this user, or all users where it
    /// is in Program Files), then starts it again.
    /// </summary>
    void StartWindowsScript(string staged, bool restart, int exitingProcess, IReadOnlyList<string> arguments)
    {
        var script = ScriptFor(staged, restart, exitingProcess, arguments);
        var path = Path.Combine(Path.GetDirectoryName(staged)!, "update.cmd");
        File.WriteAllText(path, script);
        Process.Start(new ProcessStartInfo("cmd.exe", ["/d", "/c", path]) { UseShellExecute = false, CreateNoWindow = true })?.Dispose();
    }

    /// <summary>The script of a Windows update (see <see cref="StartWindowsScript"/>).</summary>
    internal string ScriptFor(string staged, bool restart, int exitingProcess, IReadOnlyList<string> arguments)
    {
        static string Quote(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var forAll = programFiles.Length > 0 && Services.FilePaths.IsUnder(install.Directory, programFiles);
        // Windows' own find and ping (Git's find may come first on the PATH; timeout needs a console): a second's wait.
        List<string> lines =
        [
            "@echo off",
            ":wait",
            $"tasklist /fi \"PID eq {exitingProcess}\" 2>nul | \"%SystemRoot%\\System32\\find.exe\" \"{exitingProcess}\" >nul && (\"%SystemRoot%\\System32\\ping.exe\" -n 2 127.0.0.1 >nul & goto wait)",
            install.Distribution == Distribution.Setup
                ? $"{Quote(staged)} /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /NOCANCEL {(forAll ? "/ALLUSERS" : "/CURRENTUSER")}"
                : $"robocopy {Quote(staged)} {Quote(install.Directory)} /E /IS /IT /R:10 /W:1 /NFL /NDL /NJH /NJS /NP >nul",
        ];
        if (restart)
            lines.Add($"start \"\" {Quote(install.Program)} {string.Join(' ', arguments.Select(Quote))}".TrimEnd());
        return string.Join("\r\n", lines) + "\r\n";
    }

    static async Task<int> RunAsync(string program, IReadOnlyList<string> args, CancellationToken ct)
    {
        using var runner = ProcessRunner.Start(program, args, Path.GetTempPath(), _ => { });
        return await runner.WaitAsync(ct);
    }
}
