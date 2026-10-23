using System.IO.Compression;
using System.Security.Cryptography;

namespace Karvon.Python;

/// <summary>
/// Finds or installs debugpy (MIT), Microsoft's Python debugger that speaks DAP: its universal wheel (pure Python, for
/// every platform) from PyPI, checked against the hash pinned here, into ~/.local/share/karvon/debugpy/&lt;version&gt;.
/// The workspace's own interpreter runs it.
/// </summary>
public static class DebugpyLocator
{
    public const string Version = "1.8.22";
    const string Url = "https://files.pythonhosted.org/packages/56/3d/c7dc9f35bc9e22cdb53679f844bee40fd5cff871862f60595869a433400d/debugpy-" + Version + "-py2.py3-none-any.whl";
    const string Sha256 = "a9e9d3550e15ca479c59333e90845029190531f0cacfedab3b815a57bd913947";

    public static string InstallRoot =>
        Path.Combine(Karvon.Modules.ModuleComponent.DataRoot, "debugpy", Version);

    /// <summary>The folder to put on PYTHONPATH (it holds the debugpy package): KARVON_DEBUGPY, else the one installed here.</summary>
    public static string? Find()
    {
        var root = Environment.GetEnvironmentVariable("KARVON_DEBUGPY") is { Length: > 0 } own ? own : InstallRoot;
        return File.Exists(Path.Combine(root, "debugpy", "adapter", "__main__.py")) ? root : null;
    }

    public static async Task<string> EnsureInstalledAsync(Action<string> log, CancellationToken ct)
    {
        if (Find() is { } existing)
            return existing;
        log($"Downloading debugpy {Version} from PyPI ...");
        using var http = new HttpClient();
        var bytes = await http.GetByteArrayAsync(Url, ct);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (hash != Sha256)
            throw new InvalidOperationException($"debugpy download checksum mismatch ({hash}).");

        // Another Karvon may be installing it at the same time: each extracts on its own, the first to finish wins.
        var staging = $"{InstallRoot}.tmp-{Environment.ProcessId}";
        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);
        try
        {
            // A wheel is a zip of the package as it is installed.
            using (var wheel = new ZipArchive(new MemoryStream(bytes)))
                wheel.ExtractToDirectory(staging, overwriteFiles: true);
            if (!File.Exists(Path.Combine(staging, "debugpy", "adapter", "__main__.py")))
                throw new InvalidOperationException("The debugpy wheel has no debugpy.adapter.");
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
        log($"debugpy {Version} installed into {InstallRoot}");
        return Find() ?? throw new InvalidOperationException($"debugpy {Version} installed but its adapter was not found.");
    }
}
