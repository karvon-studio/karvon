using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Karvon.Debugging;

/// <summary>
/// Finds or installs Samsung's netcoredbg (MIT), the open-source .NET debugger that speaks DAP: the build of this
/// platform from its GitHub release, checked against the hash pinned here. Microsoft's vsdbg cannot be used: its license
/// limits it to Visual Studio / VS Code.
/// </summary>
public static class NetcoredbgLocator
{
    public const string Version = "3.2.0-1092";

    /// <summary>Each platform's build (Karvon's name for it, then the release's file) and its SHA-256.</summary>
    static readonly Dictionary<string, (string File, string Sha256)> Builds = new(StringComparer.Ordinal)
    {
        ["linux-x64"] = ("netcoredbg-linux-amd64.tar.gz", "080eb3b2d2152465f599d3b33d1ee6e747794e11cc0a3773ec689f5e5f2c5afa"),
        ["linux-arm64"] = ("netcoredbg-linux-arm64.tar.gz", "065ff49badec8a695dbea2de6ab6a330c774a191e426a217ab8cc05250627ccb"),
        ["osx-arm64"] = ("netcoredbg-osx-arm64.zip", "f4fa33b3ff874910cc184b4bb3b9c56d0abdf5c6521cee0b144d7c6e4a6e59ea"),
        ["win-x64"] = ("netcoredbg-win64.zip", "3c410a45fa502415203a94fcb88654af65bf8e3dac158a5527a722e7a6b9274a"),
    };

    public static string InstallRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "karvon", "netcoredbg", Version);

    /// <summary>This platform's build (linux-x64...); null for one netcoredbg has none for (an Intel Mac).</summary>
    public static string? Platform
    {
        get
        {
            var os = OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "osx" : OperatingSystem.IsWindows() ? "win" : null;
            var cpu = RuntimeInformation.OSArchitecture switch
            {
                Architecture.X64 => "x64",
                Architecture.Arm64 => "arm64",
                _ => null,
            };
            return os is null || cpu is null || !Builds.ContainsKey($"{os}-{cpu}") ? null : $"{os}-{cpu}";
        }
    }

    static string ExecutableName => OperatingSystem.IsWindows() ? "netcoredbg.exe" : "netcoredbg";

    public static string? Find()
    {
        var overridePath = Environment.GetEnvironmentVariable("KARVON_NETCOREDBG");
        if (!string.IsNullOrEmpty(overridePath))
            return File.Exists(overridePath) ? overridePath : null;
        var exe = Path.Combine(InstallRoot, "netcoredbg", ExecutableName);
        return File.Exists(exe) ? exe : null;
    }

    /// <summary>A build's archive (a .tar.gz, or a .zip) into a folder: its netcoredbg/ folder, the program executable.</summary>
    internal static async Task ExtractAsync(byte[] archive, string file, string into, string executable, CancellationToken ct)
    {
        Directory.CreateDirectory(into);
        if (file.EndsWith(".zip", StringComparison.Ordinal))
        {
            using var zip = new ZipArchive(new MemoryStream(archive));
            zip.ExtractToDirectory(into, overwriteFiles: true);
        }
        else
        {
            await using var gzip = new GZipStream(new MemoryStream(archive), CompressionMode.Decompress);
            await TarFile.ExtractToDirectoryAsync(gzip, into, overwriteFiles: true, ct);
        }
        var exe = Path.Combine(into, "netcoredbg", executable);
        if (!File.Exists(exe))
            throw new InvalidOperationException($"The netcoredbg archive has no {executable}.");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(exe, File.GetUnixFileMode(exe) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
    }

    /// <summary>The release's file of each platform and its SHA-256 (for the tests that check them all).</summary>
    internal static IReadOnlyDictionary<string, (string File, string Sha256)> AllBuilds => Builds;

    public static async Task<string> EnsureInstalledAsync(Action<string> log, CancellationToken ct)
    {
        if (Find() is { } existing)
            return existing;
        if (Platform is not { } platform)
            throw new InvalidOperationException($"netcoredbg {Version} has no build for {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}).");

        var (file, sha256) = Builds[platform];
        var url = $"https://github.com/Samsung/netcoredbg/releases/download/{Version}/{file}";
        log($"Downloading netcoredbg {Version} ({platform}) ...");
        using var http = new HttpClient();
        var bytes = await http.GetByteArrayAsync(url, ct);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (hash != sha256)
            throw new InvalidOperationException($"netcoredbg download checksum mismatch ({hash}).");

        // Another Karvon may be installing it at the same time: each extracts on its own, the first to finish wins.
        var staging = $"{InstallRoot}.tmp-{Environment.ProcessId}";
        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);
        try
        {
            await ExtractAsync(bytes, file, staging, ExecutableName, ct);
            // What a broken install left (its program missing) makes way.
            if (Directory.Exists(InstallRoot))
                Directory.Delete(InstallRoot, recursive: true);
            Directory.CreateDirectory(Path.GetDirectoryName(InstallRoot)!);
            Directory.Move(staging, InstallRoot);
        }
        catch (IOException) when (Find() is not null)
        {
        }
        finally
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }
        log($"netcoredbg installed into {InstallRoot}");
        return Find() ?? throw new InvalidOperationException($"netcoredbg {Version} installed but {ExecutableName} was not found.");
    }
}
