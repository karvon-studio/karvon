using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Karvon.Python;

/// <summary>
/// Finds or installs Pyright (MIT), Microsoft's Python type checker and language server: its npm package (one for every
/// platform; it runs on Node.js) from the npm registry, checked against the hash pinned here, into
/// ~/.local/share/karvon/pyright/&lt;version&gt;.
/// </summary>
public static class PyrightLocator
{
    public const string Version = "1.1.414";
    const string Url = "https://registry.npmjs.org/pyright/-/pyright-" + Version + ".tgz";
    const string Integrity = "FPZZb51jepDX4eP7TEYDeNFtmE3WgwkkEcJpvH3/QmUSsj0EAy3LXu+xB4T/FWDejtsXlCfFr/rbWFjwuwuXww==";

    public static string InstallRoot =>
        Path.Combine(Karvon.Modules.ModuleComponent.DataRoot, "pyright", Version);

    /// <summary>KARVON_PYRIGHT (a langserver.index.js), else the one installed here; null when there is none.</summary>
    public static string? Find()
    {
        var overridePath = Environment.GetEnvironmentVariable("KARVON_PYRIGHT");
        if (!string.IsNullOrEmpty(overridePath))
            return File.Exists(overridePath) ? overridePath : null;
        var server = Path.Combine(InstallRoot, "langserver.index.js");
        return File.Exists(server) ? server : null;
    }

    public static async Task<string> EnsureInstalledAsync(Action<string> log, CancellationToken ct)
    {
        if (Find() is { } existing)
            return existing;
        log($"Downloading Pyright {Version} from {Url} ...");
        using var http = new HttpClient();
        var bytes = await http.GetByteArrayAsync(Url, ct);
        var hash = Convert.ToBase64String(SHA512.HashData(bytes));
        if (hash != Integrity)
            throw new InvalidOperationException($"Pyright download checksum mismatch (sha512-{hash}).");

        // Another Karvon may be installing it at the same time: each extracts on its own, the first to finish wins.
        var staging = $"{InstallRoot}.tmp-{Environment.ProcessId}";
        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);
        try
        {
            await using (var gzip = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress))
                await TarFile.ExtractToDirectoryAsync(gzip, staging, overwriteFiles: true, ct);
            // An npm tarball holds its files under package/.
            var extracted = Path.Combine(staging, "package");
            if (!File.Exists(Path.Combine(extracted, "langserver.index.js")))
                throw new InvalidOperationException("The Pyright package has no langserver.index.js.");
            if (Directory.Exists(InstallRoot))
                Directory.Delete(InstallRoot, recursive: true);
            Directory.CreateDirectory(Path.GetDirectoryName(InstallRoot)!);
            Directory.Move(extracted, InstallRoot);
        }
        catch (IOException) when (Find() is not null)
        {
        }
        finally
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }
        log($"Pyright {Version} installed into {InstallRoot}");
        return Find() ?? throw new InvalidOperationException($"Pyright {Version} installed but its langserver.index.js was not found.");
    }
}
