using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Karvon.TypeScript;

/// <summary>
/// Finds or installs js-debug (MIT), the JavaScript debugger of VS Code and Visual Studio, as its standalone DAP server:
/// one archive for every platform (it runs on Node.js) from its GitHub release, checked against the hash pinned here, into
/// ~/.local/share/karvon/js-debug/&lt;version&gt;.
/// </summary>
public static class JsDebugLocator
{
    public const string Version = "1.140.0";
    const string Url = "https://github.com/microsoft/vscode-js-debug/releases/download/v" + Version + "/js-debug-dap-v" + Version + ".tar.gz";
    const string Sha256 = "27dab92937ec1ab35821ae955aac867544fe06a1b6307229049f2d789af10968";

    public static string InstallRoot =>
        Path.Combine(Karvon.Modules.ModuleComponent.DataRoot, "js-debug", Version);

    /// <summary>KARVON_JS_DEBUG (a dapDebugServer.js), else the one installed here; null when there is none.</summary>
    public static string? Find()
    {
        var overridePath = Environment.GetEnvironmentVariable("KARVON_JS_DEBUG");
        if (!string.IsNullOrEmpty(overridePath))
            return File.Exists(overridePath) ? overridePath : null;
        var server = Path.Combine(InstallRoot, "js-debug", "src", "dapDebugServer.js");
        return File.Exists(server) ? server : null;
    }

    public static async Task<string> EnsureInstalledAsync(Action<string> log, CancellationToken ct)
    {
        if (Find() is { } existing)
            return existing;

        log($"Downloading the JavaScript debugger (js-debug {Version}) from {Url} ...");
        using var http = new HttpClient();
        var bytes = await http.GetByteArrayAsync(Url, ct);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (hash != Sha256)
            throw new InvalidOperationException($"js-debug download checksum mismatch ({hash}).");

        // Another Karvon may be installing it at the same time: each extracts on its own, the first to finish wins.
        var staging = $"{InstallRoot}.tmp-{Environment.ProcessId}";
        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);
        try
        {
            await using (var gzip = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress))
                await TarFile.ExtractToDirectoryAsync(gzip, staging, overwriteFiles: true, ct);
            if (!File.Exists(Path.Combine(staging, "js-debug", "src", "dapDebugServer.js")))
                throw new InvalidOperationException("The js-debug archive has no dapDebugServer.js.");
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
        log($"js-debug {Version} installed into {InstallRoot}");
        return Find() ?? throw new InvalidOperationException($"js-debug {Version} installed but its dapDebugServer.js was not found.");
    }
}
