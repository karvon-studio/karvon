using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Karvon.Kotlin;

/// <summary>
/// JetBrains' Kotlin language server (kotlin-lsp): its standalone build of this platform, with its own JetBrains
/// Runtime, from download.jetbrains.com, checked against the hash pinned here, into ~/.local/share/karvon/kotlin-lsp/&lt;version&gt;.
/// It is partly closed source (IntelliJ's open parts and proprietary ones of JetBrains Air and Fleet); its caches and
/// indexes go into Karvon's folder of it, and it sends nothing (--data-sharing=none).
/// </summary>
public static class KotlinLspLocator
{
    public const string Version = "263.6379.0";

    /// <summary>Each platform's archive (tar.gz on Linux, zip as .sit on macOS and .win.zip on Windows) and its SHA-256.</summary>
    internal static readonly IReadOnlyDictionary<string, (string File, string Sha256)> AllBuilds = new Dictionary<string, (string, string)>
    {
        ["linux-x64"] = ($"kotlin-server-{Version}.tar.gz", "ab8ca4455dc2fc5fe1a24db2bccc46c104254d2c465155c4251ee65df8f3f7cc"),
        ["linux-arm64"] = ($"kotlin-server-{Version}-aarch64.tar.gz", "50999901ef8bcfa1e58561b6a8d782a72dea5620fcf92a64130807f8924a56fc"),
        ["osx-x64"] = ($"kotlin-server-{Version}.sit", "e69e0c9d27b915b2db9ee692ec08d097df1a394d9f21190457ef199f2905f77e"),
        ["osx-arm64"] = ($"kotlin-server-{Version}-aarch64.sit", "ebef2e13cd4adc4ec9e04084b848000a3ec7a9d2917c64f269574ce2efe9ecad"),
        ["win-x64"] = ($"kotlin-server-{Version}.win.zip", "72148fa0832c2a45a4b26c55f56197d8d8886bcdb732b910607efc5e2693b9df"),
        ["win-arm64"] = ($"kotlin-server-{Version}-aarch64.win.zip", "290f48cb564295239bb678bb66b1ebbd06755b14fd4890f3cdf822f41af011ef"),
    };

    static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "karvon", "kotlin-lsp");

    public static string InstallRoot => Path.Combine(Root, Version);

    /// <summary>Its caches and indexes (and its configuration), kept between runs, never in the user's JetBrains folders.</summary>
    public static string SystemPath => Path.Combine(Root, "system");

    internal static string Platform =>
        (OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux")
        + (RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "-arm64" : "-x64");

    /// <summary>The server's launcher, or null when it is not installed.</summary>
    public static string? Find()
    {
        if (!Directory.Exists(InstallRoot))
            return null;
        var launcher = OperatingSystem.IsWindows() ? "intellij-server.exe" : "intellij-server";
        // The archive's folder (kotlin-server-<version>), or a macOS bundle's Contents.
        return Directory.EnumerateFiles(InstallRoot, launcher, SearchOption.AllDirectories)
            .FirstOrDefault(f => Path.GetFileName(Path.GetDirectoryName(f)) is "bin" or "MacOS");
    }

    /// <summary>Its own Java runtime's java (JetBrains Runtime), or null when it is not installed.</summary>
    public static string? BundledJava()
    {
        if (Find() is not { } launcher || Path.GetDirectoryName(Path.GetDirectoryName(launcher)) is not { } home)
            return null;
        var java = OperatingSystem.IsWindows() ? "java.exe" : "java";
        return new[] { Path.Combine(home, "jbr", "bin", java), Path.Combine(home, "jbr", "Contents", "Home", "bin", java) }.FirstOrDefault(File.Exists);
    }

    public static async Task<string> EnsureInstalledAsync(Action<string> log, CancellationToken ct)
    {
        if (Find() is { } found)
            return found;
        if (!AllBuilds.TryGetValue(Platform, out var build))
            throw new InvalidOperationException($"kotlin-lsp {Version} has no build for {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}).");
        var url = $"https://download.jetbrains.com/language-server/kotlin-server/{Version}/{build.File}";
        log($"Downloading {url} (about 350 MB) ...");
        var staging = $"{InstallRoot}.tmp-{Environment.ProcessId}";
        var archive = staging + ".download";
        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);
        try
        {
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
            await using (var source = await http.GetStreamAsync(url, ct))
            await using (var file = File.Create(archive))
                await source.CopyToAsync(file, ct);
            string hash;
            await using (var file = File.OpenRead(archive))
                hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));
            if (hash != build.Sha256)
                throw new InvalidOperationException($"{url} checksum mismatch ({hash}).");
            if (build.File.EndsWith(".tar.gz", StringComparison.Ordinal))
            {
                await using var gzip = new GZipStream(File.OpenRead(archive), CompressionMode.Decompress);
                await TarFile.ExtractToDirectoryAsync(gzip, staging, overwriteFiles: true, ct);
            }
            else
                ZipFile.ExtractToDirectory(archive, staging, overwriteFiles: true);
            if (Directory.Exists(InstallRoot))
                Directory.Delete(InstallRoot, recursive: true);
            Directory.CreateDirectory(Root);
            Directory.Move(staging, InstallRoot);
        }
        catch (IOException) when (Find() is not null)
        {
            // Another Karvon installed it at the same time.
        }
        finally
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
            if (File.Exists(archive))
                File.Delete(archive);
        }
        log($"kotlin-lsp {Version} installed into {InstallRoot}");
        return Find() ?? throw new InvalidOperationException("kotlin-lsp was installed but its launcher is not there.");
    }
}
