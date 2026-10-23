using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Karvon.Debugging;

namespace Karvon.Lldb;

/// <summary>
/// CodeLLDB (MIT): LLDB as a debug adapter, the native debugger VS Code's Rust, C and C++ users debug with. Its
/// extension for this platform from its GitHub release, checked against the hash pinned here, into
/// ~/.local/share/karvon/codelldb/&lt;version&gt;; its adapter speaks DAP on stdio, for launch.json's lldb configurations.
/// </summary>
public static class CodeLldb
{
    public const string Version = "1.12.3";

    /// <summary>The launch.json type it debugs.</summary>
    public static readonly IReadOnlyList<string> Types = ["lldb"];

    /// <summary>CodeLLDB's extension for each platform (VS Code's names) and its SHA-256.</summary>
    static readonly Dictionary<string, (string File, string Sha256)> Builds = new(StringComparer.Ordinal)
    {
        ["linux-x64"] = ("codelldb-linux-x64.vsix", "1cd7f386598022b51a5b93b9ffa23e812b23f519cfe1833384ec4bef4bfd1be1"),
        ["linux-arm64"] = ("codelldb-linux-arm64.vsix", "0887f67d440554617894266f80706b700907c36b95e6e49d23b95a0e05318101"),
        ["darwin-x64"] = ("codelldb-darwin-x64.vsix", "e25cc716b94c62c07fec268ff2785d2b797245b160502baef8b9c970a0c4d8e8"),
        ["darwin-arm64"] = ("codelldb-darwin-arm64.vsix", "2f114a990e1b368dd1dbd33c80c0e719767af2d228391ec0df0571c957f9ac91"),
        ["win32-x64"] = ("codelldb-win32-x64.vsix", "a916e509308dac817732f63ca604a8b93ed29cd16f38a2fa9f0b64ed58e8f51a"),
    };

    static string Exe(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    public static string InstallRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "karvon", "codelldb", Version);

    static string? Platform => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        _ => null,
    } is { } cpu && (OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "darwin" : OperatingSystem.IsWindows() ? "win32" : null) is { } os ? $"{os}-{cpu}" : null;

    /// <summary>CodeLLDB's adapter (its folder holds lldb beside it).</summary>
    public static string? Find()
    {
        if (Environment.GetEnvironmentVariable("KARVON_CODELLDB") is { Length: > 0 } own)
            return File.Exists(own) ? own : null;
        var exe = Path.Combine(InstallRoot, "adapter", Exe("codelldb"));
        return File.Exists(exe) ? exe : null;
    }

    /// <summary>The lldb library CodeLLDB loads (the VS Code extension names it, as Karvon does).</summary>
    public static string LibLldb(string adapter)
    {
        var root = Path.GetDirectoryName(Path.GetDirectoryName(adapter))!;
        return OperatingSystem.IsWindows() ? Path.Combine(root, "lldb", "bin", "liblldb.dll")
            : Path.Combine(root, "lldb", "lib", OperatingSystem.IsMacOS() ? "liblldb.dylib" : "liblldb.so");
    }

    public static async Task<string> EnsureInstalledAsync(Action<string> log, CancellationToken ct)
    {
        if (Find() is { } existing)
            return existing;
        if (Platform is not { } platform || !Builds.TryGetValue(platform, out var build))
            throw new InvalidOperationException($"CodeLLDB has no build for {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}).");
        var bytes = await DownloadAsync($"https://github.com/vadimcn/codelldb/releases/download/v{Version}/{build.File}", build.Sha256, "CodeLLDB", log, ct);
        await InstallAsync(InstallRoot, Find, staging =>
        {
            // A .vsix is a zip with the extension under extension/: its adapter, and the lldb it runs.
            using var vsix = new ZipArchive(new MemoryStream(bytes));
            foreach (var entry in vsix.Entries.Where(e => e.FullName.StartsWith("extension/", StringComparison.Ordinal) && e.Name.Length > 0))
            {
                var target = Path.GetFullPath(Path.Combine(staging, entry.FullName["extension/".Length..]));
                if (!target.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
                // Its programs and libraries keep their modes (the adapter, lldb-server, liblldb).
                if (!OperatingSystem.IsWindows() && ((entry.ExternalAttributes >> 16) & 0b001_001_001) != 0)
                    MakeExecutable(target);
            }
            if (!File.Exists(Path.Combine(staging, "adapter", Exe("codelldb"))))
                throw new InvalidOperationException("The CodeLLDB download has no adapter.");
            return Task.CompletedTask;
        });
        log($"CodeLLDB {Version} installed into {InstallRoot}");
        return Find() ?? throw new InvalidOperationException("CodeLLDB was installed but not found.");
    }

    /// <summary>The adapter on stdio, with the lldb library it loads; the PATH what it launches runs with.</summary>
    public static async Task<DebugAdapter> StartAsync(string workspace, string path, Action<string> log, CancellationToken ct)
    {
        var adapter = await EnsureInstalledAsync(log, ct);
        var program = new System.Diagnostics.ProcessStartInfo(adapter, ["--liblldb", LibLldb(adapter)]) { WorkingDirectory = workspace };
        program.Environment["PATH"] = path;
        return DebugAdapter.Start("lldb", program);
    }

    static async Task<byte[]> DownloadAsync(string url, string sha256, string name, Action<string> log, CancellationToken ct)
    {
        log($"Downloading {name} from {url} ...");
        using var http = new HttpClient();
        var bytes = await http.GetByteArrayAsync(url, ct);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (hash != sha256)
            throw new InvalidOperationException($"{name} download checksum mismatch ({hash}).");
        return bytes;
    }

    /// <summary>Extracts into a folder of its own, then moves it where it goes: another Karvon installing it at once, the first wins.</summary>
    static async Task InstallAsync(string root, Func<string?> find, Func<string, Task> extract)
    {
        var staging = $"{root}.tmp-{Environment.ProcessId}";
        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);
        try
        {
            await extract(staging);
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
            Directory.CreateDirectory(Path.GetDirectoryName(root)!);
            Directory.Move(staging, root);
        }
        catch (IOException) when (find() is not null)
        {
        }
        finally
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }
    }

    static void MakeExecutable(string file)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(file, File.GetUnixFileMode(file) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
    }

    /// <summary>The component Tools > Modules shows of it (under each module that debugs with it).</summary>
    public static Modules.ModuleComponent Component => new("codelldb", "Native debugger (CodeLLDB)", Version, InstallRoot, () => Find() is not null, EnsureInstalledAsync)
    {
        Description = "CodeLLDB (MIT), LLDB as a debug adapter, from its GitHub release: Rust, C and C++.",
    };
}
