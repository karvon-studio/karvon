using Karvon.Build;
using Karvon.Workspace;

namespace Karvon.Go;

/// <summary>
/// The Go toolchain the user has (go on the PATH, or where Go installs itself when the PATH has none), and the tools
/// Karvon installs with it: gopls and Delve, by go install at the versions pinned here (the Go checksum database checks
/// every module they are built from), into ~/.local/share/karvon/&lt;tool&gt;/&lt;version&gt;.
/// </summary>
public static class GoTools
{
    public const string GoplsVersion = "v0.24.0";
    public const string DelveVersion = "v1.27.2";

    static string Exe(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    static string Root(string tool, string version) =>
        Path.Combine(Karvon.Modules.ModuleComponent.DataRoot, tool, version);

    public static string GoplsRoot => Root("gopls", GoplsVersion);

    public static string DelveRoot => Root("dlv", DelveVersion);

    /// <summary>Go's folder when the PATH has none (Karvon started from the desktop): ~/.local/go, /usr/local/go, ~/sdk's newest.</summary>
    public static string? DirectoryOffPath(string? path = null)
    {
        path ??= Environment.GetEnvironmentVariable("PATH") ?? "";
        if (TaskVariables.Find(Exe("go"), path, "/") is not null)
            return null;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        IEnumerable<string> candidates =
        [
            Path.Combine(home, ".local", "go", "bin"),
            "/usr/local/go/bin",
            "/opt/homebrew/bin",
            .. Directory.Exists(Path.Combine(home, "sdk")) ? Directory.EnumerateDirectories(Path.Combine(home, "sdk"), "go*").Order(StringComparer.Ordinal).Reverse().Select(d => Path.Combine(d, "bin")) : [],
        ];
        return candidates.FirstOrDefault(dir => File.Exists(Path.Combine(dir, Exe("go"))));
    }

    /// <summary>The PATH Go's tools run with: Go's folder first when the PATH has none.</summary>
    public static string PathWithGo(string path) => DirectoryOffPath(path) is { } go ? go + System.IO.Path.PathSeparator + path : path;

    public static string? FindGopls() => Find("KARVON_GOPLS", GoplsRoot, "gopls");

    public static string? FindDelve() => Find("KARVON_DLV", DelveRoot, "dlv");

    static string? Find(string variable, string root, string name)
    {
        if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } own)
            return File.Exists(own) ? own : null;
        var exe = Path.Combine(root, Exe(name));
        return File.Exists(exe) ? exe : null;
    }

    public static Task<string> EnsureGoplsAsync(Action<string> log, CancellationToken ct) =>
        InstallAsync("golang.org/x/tools/gopls@" + GoplsVersion, GoplsRoot, "gopls", FindGopls, log, ct);

    public static Task<string> EnsureDelveAsync(Action<string> log, CancellationToken ct) =>
        InstallAsync("github.com/go-delve/delve/cmd/dlv@" + DelveVersion, DelveRoot, "dlv", FindDelve, log, ct);

    static async Task<string> InstallAsync(string package, string root, string name, Func<string?> find, Action<string> log, CancellationToken ct)
    {
        if (find() is { } existing)
            return existing;
        var path = PathWithGo(Environment.GetEnvironmentVariable("PATH") ?? "");
        var go = TaskVariables.Find(Exe("go"), path, "/")
            ?? throw new InvalidOperationException($"Go was not found: {name} is built with it (install Go from go.dev/dl).");
        log($"Installing {name} with go install {package} ...");
        // Another Karvon may be installing it at the same time: each builds on its own, the first to finish wins.
        var staging = $"{root}.tmp-{Environment.ProcessId}";
        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);
        try
        {
            var environment = new Dictionary<string, string> { ["GOBIN"] = staging, ["PATH"] = path, ["GOFLAGS"] = "-trimpath" };
            using var runner = ProcessRunner.Start(go, ["install", package], staging, log, environment);
            if (await runner.WaitAsync(ct) != 0 || !File.Exists(Path.Combine(staging, Exe(name))))
                throw new InvalidOperationException($"go install {package} did not succeed (see Output).");
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
        log($"{name} {Path.GetFileName(root)} installed into {root}");
        return find() ?? throw new InvalidOperationException($"{name} was installed but not found in {root}.");
    }
}
