using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Karvon.Cpp;

/// <summary>
/// Finds or installs clangd (Apache-2.0 with LLVM's exception), LLVM's C and C++ language server: its release for this
/// platform from GitHub, checked against the hash pinned here, into ~/.local/share/karvon/clangd/&lt;version&gt; (its
/// headers beside it, as it needs them).
/// </summary>
public static class ClangdLocator
{
    public const string Version = "23.1.0";

    static readonly Dictionary<string, (string File, string Sha256)> Builds = new(StringComparer.Ordinal)
    {
        ["linux"] = ("clangd-linux-" + Version + ".zip", "e53b1a96196095faedb7642cf64964f7fb9ad4a0c1f00dd2c172a3d9dcbafdfd"),
        ["mac"] = ("clangd-mac-" + Version + ".zip", "1082e6638223b785ca2daf0939f13afcd0bb95c84ee9a4bbaff4745365159253"),
        ["windows"] = ("clangd-windows-" + Version + ".zip", "23412a240756a162e7b98a282f36aa2a23a88db5ce16a0cbc4fef7253768c810"),
    };

    public static string InstallRoot =>
        Path.Combine(Karvon.Modules.ModuleComponent.DataRoot, "clangd", Version);

    static string Exe => OperatingSystem.IsWindows() ? "clangd.exe" : "clangd";

    /// <summary>LLVM builds clangd for x64 Linux and Windows, and for macOS.</summary>
    static string? Platform => OperatingSystem.IsMacOS() ? "mac"
        : RuntimeInformation.OSArchitecture != Architecture.X64 ? null
        : OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsWindows() ? "windows" : null;

    /// <summary>KARVON_CLANGD, else the one installed here; null when there is none.</summary>
    public static string? Find()
    {
        if (Environment.GetEnvironmentVariable("KARVON_CLANGD") is { Length: > 0 } own)
            return File.Exists(own) ? own : null;
        var exe = Path.Combine(InstallRoot, $"clangd_{Version}", "bin", Exe);
        return File.Exists(exe) ? exe : null;
    }

    public static async Task<string> EnsureInstalledAsync(Action<string> log, CancellationToken ct)
    {
        if (Find() is { } existing)
            return existing;
        if (Platform is not { } platform)
            throw new InvalidOperationException($"clangd has no release for {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}): install clangd from your system's packages.");
        var (file, sha256) = Builds[platform];
        var url = $"https://github.com/clangd/clangd/releases/download/{Version}/{file}";
        log($"Downloading clangd {Version} from {url} ...");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var bytes = await http.GetByteArrayAsync(url, ct);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (hash != sha256)
            throw new InvalidOperationException($"clangd download checksum mismatch ({hash}).");
        var staging = $"{InstallRoot}.tmp-{Environment.ProcessId}";
        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);
        try
        {
            using (var zip = new ZipArchive(new MemoryStream(bytes)))
                zip.ExtractToDirectory(staging, overwriteFiles: true);
            var exe = Path.Combine(staging, $"clangd_{Version}", "bin", Exe);
            if (!File.Exists(exe))
                throw new InvalidOperationException("The clangd release has no clangd.");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(exe, File.GetUnixFileMode(exe) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
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
        log($"clangd {Version} installed into {InstallRoot}");
        return Find() ?? throw new InvalidOperationException("clangd was installed but not found.");
    }
}
