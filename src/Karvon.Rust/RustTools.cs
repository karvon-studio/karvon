using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Karvon.Workspace;

namespace Karvon.Rust;

/// <summary>
/// Rust's tools: cargo, the user's (rustup's), and what Karvon installs beside it, each from its GitHub release and
/// checked against the hash pinned here: rust-analyzer (MIT/Apache-2.0) and CodeLLDB (MIT), the debugger VS Code's Rust
/// users debug with, into ~/.local/share/karvon/&lt;tool&gt;/&lt;version&gt;.
/// </summary>
public static class RustTools
{
    public const string RustAnalyzerVersion = "2026-10-19";
    public const string CodeLldbVersion = "1.12.3";

    /// <summary>rust-analyzer's file and SHA-256 for each platform (Rust's target names).</summary>
    static readonly Dictionary<string, (string File, string Sha256)> RustAnalyzerBuilds = new(StringComparer.Ordinal)
    {
        ["x86_64-unknown-linux-gnu"] = ("rust-analyzer-x86_64-unknown-linux-gnu.gz", "8fde1c9dae81459f98e99eedae3654846a9166abea686306c26e57773be558da"),
        ["aarch64-unknown-linux-gnu"] = ("rust-analyzer-aarch64-unknown-linux-gnu.gz", "d31ee384ab788b9abd8e7aa8e1322f23e6cd4310471d373e9a79ce4656e21896"),
        ["x86_64-apple-darwin"] = ("rust-analyzer-x86_64-apple-darwin.gz", "bdfd46e82731a8d3502d852d28b6462c1f1c13eed0b3e76bbcf1ba839028249c"),
        ["aarch64-apple-darwin"] = ("rust-analyzer-aarch64-apple-darwin.gz", "eedad08a96f7bf92afd0890463787337d17591ea2e3df216033c6bc3ccc9d802"),
        ["x86_64-pc-windows-msvc"] = ("rust-analyzer-x86_64-pc-windows-msvc.zip", "7df6e1a2595d1906e8441cb83fd58170b47d32eb09bc52c1b2184538cfd49a71"),
        ["aarch64-pc-windows-msvc"] = ("rust-analyzer-aarch64-pc-windows-msvc.zip", "dc2b6c35e2be0bf13f4ae2a6c69adca5730c91d011bfac31371279a4e55bcb2b"),
    };

    /// <summary>CodeLLDB's extension for each platform (VS Code's names) and its SHA-256.</summary>
    static readonly Dictionary<string, (string File, string Sha256)> CodeLldbBuilds = new(StringComparer.Ordinal)
    {
        ["linux-x64"] = ("codelldb-linux-x64.vsix", "1cd7f386598022b51a5b93b9ffa23e812b23f519cfe1833384ec4bef4bfd1be1"),
        ["linux-arm64"] = ("codelldb-linux-arm64.vsix", "0887f67d440554617894266f80706b700907c36b95e6e49d23b95a0e05318101"),
        ["darwin-x64"] = ("codelldb-darwin-x64.vsix", "e25cc716b94c62c07fec268ff2785d2b797245b160502baef8b9c970a0c4d8e8"),
        ["darwin-arm64"] = ("codelldb-darwin-arm64.vsix", "2f114a990e1b368dd1dbd33c80c0e719767af2d228391ec0df0571c957f9ac91"),
        ["win32-x64"] = ("codelldb-win32-x64.vsix", "a916e509308dac817732f63ca604a8b93ed29cd16f38a2fa9f0b64ed58e8f51a"),
    };

    static string Exe(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    static string Root(string tool, string version) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "karvon", tool, version);

    public static string RustAnalyzerRoot => Root("rust-analyzer", RustAnalyzerVersion);

    public static string CodeLldbRoot => Root("codelldb", CodeLldbVersion);

    static string? Cpu => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        _ => null,
    };

    static string? RustTarget => (Cpu, OperatingSystem.IsLinux() ? "unknown-linux-gnu" : OperatingSystem.IsMacOS() ? "apple-darwin" : OperatingSystem.IsWindows() ? "pc-windows-msvc" : null) switch
    {
        ({ } cpu, { } os) => $"{(cpu == "x64" ? "x86_64" : "aarch64")}-{os}",
        _ => null,
    };

    static string? VsCodePlatform => Cpu is { } cpu && (OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "darwin" : OperatingSystem.IsWindows() ? "win32" : null) is { } os ? $"{os}-{cpu}" : null;

    /// <summary>cargo's folder when the PATH has none (Karvon started from the desktop): ~/.cargo/bin, as rustup installs it.</summary>
    public static string? DirectoryOffPath(string? path = null)
    {
        path ??= Environment.GetEnvironmentVariable("PATH") ?? "";
        if (TaskVariables.Find(Exe("cargo"), path, "/") is not null)
            return null;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var cargoHome = Environment.GetEnvironmentVariable("CARGO_HOME") is { Length: > 0 } own ? own : Path.Combine(home, ".cargo");
        return new[] { Path.Combine(cargoHome, "bin"), Path.Combine(home, ".local", "bin") }.FirstOrDefault(dir => File.Exists(Path.Combine(dir, Exe("cargo"))));
    }

    /// <summary>The PATH Rust's tools run with: cargo's folder first when the PATH has none.</summary>
    public static string PathWithCargo(string path) => DirectoryOffPath(path) is { } cargo ? cargo + System.IO.Path.PathSeparator + path : path;

    public static string? FindRustAnalyzer()
    {
        if (Environment.GetEnvironmentVariable("KARVON_RUST_ANALYZER") is { Length: > 0 } own)
            return File.Exists(own) ? own : null;
        var exe = Path.Combine(RustAnalyzerRoot, Exe("rust-analyzer"));
        return File.Exists(exe) ? exe : null;
    }

    /// <summary>CodeLLDB's adapter (its folder holds lldb beside it).</summary>
    public static string? FindCodeLldb()
    {
        if (Environment.GetEnvironmentVariable("KARVON_CODELLDB") is { Length: > 0 } own)
            return File.Exists(own) ? own : null;
        var exe = Path.Combine(CodeLldbRoot, "adapter", Exe("codelldb"));
        return File.Exists(exe) ? exe : null;
    }

    /// <summary>The lldb library CodeLLDB loads (the VS Code extension names it, as Karvon does).</summary>
    public static string LibLldb(string adapter)
    {
        var root = Path.GetDirectoryName(Path.GetDirectoryName(adapter))!;
        return OperatingSystem.IsWindows() ? Path.Combine(root, "lldb", "bin", "liblldb.dll")
            : Path.Combine(root, "lldb", "lib", OperatingSystem.IsMacOS() ? "liblldb.dylib" : "liblldb.so");
    }

    public static async Task<string> EnsureRustAnalyzerAsync(Action<string> log, CancellationToken ct)
    {
        if (FindRustAnalyzer() is { } existing)
            return existing;
        if (RustTarget is not { } target || !RustAnalyzerBuilds.TryGetValue(target, out var build))
            throw new InvalidOperationException($"rust-analyzer has no build for {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}).");
        var bytes = await DownloadAsync($"https://github.com/rust-lang/rust-analyzer/releases/download/{RustAnalyzerVersion}/{build.File}", build.Sha256, "rust-analyzer", log, ct);
        await InstallAsync(RustAnalyzerRoot, FindRustAnalyzer, async staging =>
        {
            var exe = Path.Combine(staging, Exe("rust-analyzer"));
            if (build.File.EndsWith(".zip", StringComparison.Ordinal))
            {
                using var zip = new ZipArchive(new MemoryStream(bytes));
                zip.ExtractToDirectory(staging, overwriteFiles: true);
            }
            else
            {
                await using var gzip = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
                await using var file = File.Create(exe);
                await gzip.CopyToAsync(file, ct);
            }
            if (!File.Exists(exe))
                throw new InvalidOperationException("The rust-analyzer download has no rust-analyzer.");
            MakeExecutable(exe);
        });
        log($"rust-analyzer {RustAnalyzerVersion} installed into {RustAnalyzerRoot}");
        return FindRustAnalyzer() ?? throw new InvalidOperationException("rust-analyzer was installed but not found.");
    }

    public static async Task<string> EnsureCodeLldbAsync(Action<string> log, CancellationToken ct)
    {
        if (FindCodeLldb() is { } existing)
            return existing;
        if (VsCodePlatform is not { } platform || !CodeLldbBuilds.TryGetValue(platform, out var build))
            throw new InvalidOperationException($"CodeLLDB has no build for {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}).");
        var bytes = await DownloadAsync($"https://github.com/vadimcn/codelldb/releases/download/v{CodeLldbVersion}/{build.File}", build.Sha256, "CodeLLDB", log, ct);
        await InstallAsync(CodeLldbRoot, FindCodeLldb, staging =>
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
        log($"CodeLLDB {CodeLldbVersion} installed into {CodeLldbRoot}");
        return FindCodeLldb() ?? throw new InvalidOperationException("CodeLLDB was installed but not found.");
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
}
