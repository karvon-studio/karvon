using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Karvon.TypeScript;

/// <summary>
/// Finds or installs TypeScript 7 (Apache-2.0), the native compiler that is its own language server (<c>tsc --lsp</c>):
/// the package of this platform (<c>@typescript/typescript-linux-x64</c>...) from the npm registry, checked against the
/// hash pinned here, into ~/.local/share/karvon/typescript/&lt;version&gt;. Neither Node nor npm is needed.
/// </summary>
public static class TypeScriptServerLocator
{
    public const string Version = "7.0.2";

    /// <summary>Each platform's package: its npm name's suffix and its tarball's SHA-512 (npm's dist.integrity).</summary>
    static readonly Dictionary<string, string> Integrity = new(StringComparer.Ordinal)
    {
        ["linux-x64"] = "EYdf2cNg7rgCWJnxCdJ+F3V39O8ihb37eHAu1LK8oAFizgTQbPOK7zHHXbPt8rX24COqODXeI3sIf0fCXG7H/A==",
        ["linux-arm64"] = "Qh4eU4/y3yDjnfjjyPYihMj5/ODIlmt+Bzu17OI+fiSRDW57QmU5SiN63exPRNJPKUzcc1INa1NXdrJ+MqHjUQ==",
        ["linux-arm"] = "gffT3xPz9sR7j/YJExkyPntrI0P2EP9XbOyWzth2/Gs0RstK+90RBcO0ncXoXy/beYll1SXw846Nf2zdnEz0QQ==",
        ["darwin-x64"] = "SZ9xZInqApNlNGc9s0W1VSsktYSOe9cFqNOIqmN1Gs8SmkjKZYFt017G4VwPxASInODuAdbTW7sXiFUf893RgA==",
        ["darwin-arm64"] = "gowzar9MwS/aRWp6f3a4KUqzRjAZjOsmGNCM6LcTgXum+dBfgsBVMN+AgvOCCbguXyick6LJhpBszxMebJ8syA==",
        ["win32-x64"] = "0BQ3HkAHHlKLSp1qRvf3SUhGpGsDuhB/jgFw75guyqbxJqEaS0Cw/VFO8i2nHglJUzQCRtMMR/IBAKE3ETMC4g==",
        ["win32-arm64"] = "Gyl1Vy6OsWesLzmq+EP0Fb7b4Nid5232AvcA2SFcdYreldpNtYFFofPjnt62y9hQy7VTaZp65ICJjuAQRaVcIQ==",
    };

    public static string InstallRoot =>
        Path.Combine(Karvon.Modules.ModuleComponent.DataRoot, "typescript", Version);

    /// <summary>npm's name for this platform (process.platform-process.arch); null for one TypeScript has no package for.</summary>
    public static string? Platform
    {
        get
        {
            var os = OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "darwin" : OperatingSystem.IsWindows() ? "win32" : null;
            var cpu = RuntimeInformation.OSArchitecture switch
            {
                Architecture.X64 => "x64",
                Architecture.Arm64 => "arm64",
                Architecture.Arm => "arm",
                _ => null,
            };
            return os is null || cpu is null || !Integrity.ContainsKey($"{os}-{cpu}") ? null : $"{os}-{cpu}";
        }
    }

    static string ExecutableName => OperatingSystem.IsWindows() ? "tsc.exe" : "tsc";

    /// <summary>KARVON_TYPESCRIPT_SERVER (a tsc of TypeScript 7 or a tsgo), else the one installed here; null when there is none.</summary>
    public static string? Find()
    {
        var overridePath = Environment.GetEnvironmentVariable("KARVON_TYPESCRIPT_SERVER");
        if (!string.IsNullOrEmpty(overridePath))
            return File.Exists(overridePath) ? overridePath : null;
        var exe = Path.Combine(InstallRoot, "lib", ExecutableName);
        return File.Exists(exe) ? exe : null;
    }

    public static async Task<string> EnsureInstalledAsync(Action<string> log, CancellationToken ct)
    {
        if (Find() is { } existing)
            return existing;
        if (Platform is not { } platform)
            throw new InvalidOperationException($"TypeScript {Version} has no language server for {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}).");

        var package = $"typescript-{platform}";
        var url = $"https://registry.npmjs.org/@typescript/{package}/-/{package}-{Version}.tgz";
        log($"Downloading TypeScript {Version} ({platform}) from {url} ...");
        using var http = new HttpClient();
        var bytes = await http.GetByteArrayAsync(url, ct);
        var hash = Convert.ToBase64String(SHA512.HashData(bytes));
        if (hash != Integrity[platform])
            throw new InvalidOperationException($"TypeScript download checksum mismatch (sha512-{hash}).");

        // Another Karvon may be installing it at the same time: each extracts on its own, the first to finish wins.
        var staging = $"{InstallRoot}.tmp-{Environment.ProcessId}";
        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);
        await using (var gzip = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress))
            await TarFile.ExtractToDirectoryAsync(gzip, staging, overwriteFiles: true, ct);
        // An npm tarball holds its files under package/.
        var extracted = Path.Combine(staging, "package");
        var exe = Path.Combine(extracted, "lib", ExecutableName);
        if (!File.Exists(exe))
            throw new InvalidOperationException($"The TypeScript package has no {ExecutableName}.");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(exe, File.GetUnixFileMode(exe) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(InstallRoot)!);
            Directory.Move(extracted, InstallRoot);
        }
        catch (IOException) when (Find() is not null)
        {
        }
        finally
        {
            Directory.Delete(staging, recursive: true);
        }
        log($"TypeScript {Version} installed into {InstallRoot}");
        return Find() ?? throw new InvalidOperationException($"TypeScript {Version} installed but {ExecutableName} was not found.");
    }
}
