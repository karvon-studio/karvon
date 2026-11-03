using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Karvon.Documents;

/// <summary>
/// The document engines' library (karvon-documents, built from native/documents of Karvon's sources): this platform's
/// build from Karvon's documents-&lt;version&gt; release on GitHub, checked against the hash pinned here, into
/// ~/.local/share/karvon/documents/&lt;version&gt;, with the engines' licenses and notices beside it.
/// KARVON_DOCUMENTS_LIBRARY names another build of it to load (a local one); "off" none.
/// </summary>
public static class DocumentEnginesLocator
{
    public const string Version = "0.1.0";

    /// <summary>Each platform's zip and its SHA-256.</summary>
    internal static readonly IReadOnlyDictionary<string, string> AllBuilds = new Dictionary<string, string>
    {
        ["linux-x64"] = "542f82cfc8203abaafc99bb2278d019cbcaf8c61c4d2710be8dad3f288f1ce96",
        ["osx-arm64"] = "63a4d96e505a2fcb04714774bb45658f18cd17df29d4ddbc950b490e8cc7fdd7",
        ["osx-x64"] = "7e3a307289a546d77db9f4f49cd957cddad7f4de64143f4b59a0c61295722f9f",
        ["win-x64"] = "4b99d2270e088d96dcb7cbdaacbc57b899861b8a40f1829574ccec27773097f1",
    };

    static string Root => Path.Combine(Karvon.Modules.ModuleComponent.DataRoot, "documents");

    public static string InstallRoot => Path.Combine(Root, Version);

    internal static string Platform =>
        (OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux")
        + (RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "-arm64" : "-x64");

    static string? Given => Environment.GetEnvironmentVariable("KARVON_DOCUMENTS_LIBRARY") is { Length: > 0 } given ? given : null;

    /// <summary>The library to load, or null when it is not installed (or turned off).</summary>
    public static string? Find()
    {
        if (Given is { } given)
            return given != "off" && File.Exists(given) ? Path.GetFullPath(given) : null;
        var library = Path.Combine(InstallRoot, DocumentLibrary.FileName);
        return File.Exists(library) ? library : null;
    }

    public static async Task<string> EnsureInstalledAsync(Action<string> log, CancellationToken ct)
    {
        if (Find() is { } found)
            return found;
        if (Given is { } given)
            throw new InvalidOperationException(given == "off" ? "The document engines are turned off (KARVON_DOCUMENTS_LIBRARY=off)." : $"{given} (KARVON_DOCUMENTS_LIBRARY) does not exist.");
        if (!AllBuilds.TryGetValue(Platform, out var sha256))
            throw new InvalidOperationException($"The document engines {Version} have no build for {Platform} (only for {string.Join(", ", AllBuilds.Keys.Order())}).");
        var url = $"https://github.com/{Updates.UpdateFeed.Repository}/releases/download/documents-{Version}/karvon-documents-{Version}-{Platform}.zip";
        log($"Downloading {url} (about 36 MB) ...");
        var staging = $"{InstallRoot}.tmp-{Environment.ProcessId}";
        var archive = staging + ".download";
        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);
        try
        {
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) })
            await using (var source = await http.GetStreamAsync(url, ct))
            await using (var file = File.Create(archive))
                await source.CopyToAsync(file, ct);
            string hash;
            await using (var file = File.OpenRead(archive))
                hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));
            if (hash != sha256)
                throw new InvalidOperationException($"{url} checksum mismatch ({hash}).");
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
        log($"The document engines {Version} installed into {InstallRoot}");
        return Find() ?? throw new InvalidOperationException("The document engines were installed but their library is not there.");
    }
}
