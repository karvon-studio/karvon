using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Karvon.Workspace;

namespace Karvon.Java;

/// <summary>
/// Java's tools: the JDK the user has (JAVA_HOME's, else java on the PATH), and what Karvon installs beside it, each
/// checked against the hash pinned here: jdtls (EPL-2.0), Eclipse's Java language server, and java-debug's plugin for
/// it (EPL-1.0, from VS Code's Java debugger, MIT), into ~/.local/share/karvon/&lt;tool&gt;/&lt;version&gt;.
/// </summary>
public static class JavaTools
{
    /// <summary>The last jdtls that runs on Java 21 (1.62 needs Java 25).</summary>
    public const string JdtlsVersion = "1.61.0";
    const string JdtlsBuild = "jdt-language-server-1.61.0-202609031315.tar.gz";
    const string JdtlsSha256 = "338e7e73d61836651ba2453919a0d34fa763eb4e7c03342092309bffb8934c64";

    public const string JavaDebugVersion = "0.60.0";
    const string JavaDebugSha256 = "f09ebe358a7b08b3c17b21b8d8207b0649acad120eb6f1604dc4f15379f59d66";

    static string Root(string tool, string version) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "karvon", tool, version);

    public static string JdtlsRoot => Root("jdtls", JdtlsVersion);

    public static string JavaDebugRoot => Root("java-debug", JavaDebugVersion);

    static string JavaName => OperatingSystem.IsWindows() ? "java.exe" : "java";

    /// <summary>A JDK's java: JAVA_HOME's, else the PATH's; null when there is none.</summary>
    public static string? FindJava(string path)
    {
        if (Environment.GetEnvironmentVariable("JAVA_HOME") is { Length: > 0 } home && File.Exists(Path.Combine(home, "bin", JavaName)))
            return Path.Combine(home, "bin", JavaName);
        return TaskVariables.Find(JavaName, path, "/");
    }

    /// <summary>
    /// A java of at least <paramref name="major"/> (jdtls needs 21): JAVA_HOME's or the PATH's when it is, else one of the
    /// other JDKs installed where they go (/usr/lib/jvm, SDKMAN's, macOS's), the newest; null when none is.
    /// </summary>
    public static string? FindJava(string path, int major)
    {
        if (FindJava(path) is { } own && MajorOf(own) >= major)
            return own;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        IEnumerable<string> roots = ["/usr/lib/jvm", Path.Combine(home, ".sdkman", "candidates", "java"), "/Library/Java/JavaVirtualMachines", Path.Combine(home, ".jdks")];
        return roots.Where(Directory.Exists).SelectMany(Directory.EnumerateDirectories)
            .Select(jdk => Path.Combine(jdk, OperatingSystem.IsMacOS() && File.Exists(Path.Combine(jdk, "Contents", "Home", "bin", JavaName)) ? Path.Combine("Contents", "Home", "bin") : "bin", JavaName))
            .Where(File.Exists)
            .Select(java => (Java: java, Major: MajorOf(java)))
            .Where(j => j.Major >= major)
            .OrderByDescending(j => j.Major)
            .Select(j => j.Java)
            .FirstOrDefault();
    }

    /// <summary>A java's major version, read from its JDK's release file (JAVA_VERSION="21.0.5"); 0 when it cannot be.</summary>
    public static int MajorOf(string java)
    {
        try
        {
            // The PATH's java is a link (to /etc/alternatives, then to the JDK's).
            var file = new FileInfo(java);
            var target = file.LinkTarget is null ? file.FullName : file.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? file.FullName;
            var release = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(target)!)!, "release");
            if (!File.Exists(release))
                return 0;
            var line = File.ReadLines(release).FirstOrDefault(l => l.StartsWith("JAVA_VERSION=", StringComparison.Ordinal));
            var version = line?["JAVA_VERSION=".Length..].Trim('"');
            var first = version?.Split('.')[0];
            // Java 8's is "1.8.0_x".
            return first == "1" ? 8 : int.TryParse(first, out var n) ? n : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>jdtls's launcher jar (Equinox's), or null.</summary>
    public static string? FindJdtls()
    {
        var plugins = Path.Combine(JdtlsRoot, "plugins");
        return Directory.Exists(plugins) ? Directory.EnumerateFiles(plugins, "org.eclipse.equinox.launcher_*.jar").FirstOrDefault() : null;
    }

    /// <summary>jdtls's configuration for this platform (config_linux, config_mac_arm...).</summary>
    public static string JdtlsConfiguration => Path.Combine(JdtlsRoot,
        "config_" + (OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "mac" : "linux") + (RuntimeInformation.OSArchitecture == Architecture.Arm64 && !OperatingSystem.IsWindows() ? "_arm" : ""));

    /// <summary>java-debug's plugin jar, which jdtls loads as a bundle, or null.</summary>
    public static string? FindJavaDebug() =>
        Directory.Exists(JavaDebugRoot) ? Directory.EnumerateFiles(JavaDebugRoot, "com.microsoft.java.debug.plugin-*.jar").FirstOrDefault() : null;

    public static async Task<string> EnsureJdtlsAsync(Action<string> log, CancellationToken ct)
    {
        if (FindJdtls() is { } existing)
            return existing;
        var bytes = await DownloadAsync($"https://download.eclipse.org/jdtls/milestones/{JdtlsVersion}/{JdtlsBuild}", JdtlsSha256, "jdtls", log, ct);
        await InstallAsync(JdtlsRoot, FindJdtls, async staging =>
        {
            await using var gzip = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
            await TarFile.ExtractToDirectoryAsync(gzip, staging, overwriteFiles: true, ct);
        });
        log($"jdtls {JdtlsVersion} installed into {JdtlsRoot}");
        return FindJdtls() ?? throw new InvalidOperationException("jdtls was installed but its launcher was not found.");
    }

    public static async Task<string> EnsureJavaDebugAsync(Action<string> log, CancellationToken ct)
    {
        if (FindJavaDebug() is { } existing)
            return existing;
        var bytes = await DownloadAsync($"https://open-vsx.org/api/vscjava/vscode-java-debug/{JavaDebugVersion}/file/vscjava.vscode-java-debug-{JavaDebugVersion}.vsix",
            JavaDebugSha256, "java-debug", log, ct);
        await InstallAsync(JavaDebugRoot, FindJavaDebug, staging =>
        {
            // The plugin is in the extension's server folder.
            using var vsix = new ZipArchive(new MemoryStream(bytes));
            var jar = vsix.Entries.FirstOrDefault(e => e.FullName.StartsWith("extension/server/com.microsoft.java.debug.plugin-", StringComparison.Ordinal) && e.Name.EndsWith(".jar", StringComparison.Ordinal))
                ?? throw new InvalidOperationException("The java-debug extension has no plugin.");
            jar.ExtractToFile(Path.Combine(staging, jar.Name));
            return Task.CompletedTask;
        });
        log($"java-debug {JavaDebugVersion} installed into {JavaDebugRoot}");
        return FindJavaDebug() ?? throw new InvalidOperationException("java-debug was installed but its plugin was not found.");
    }

    static async Task<byte[]> DownloadAsync(string url, string sha256, string name, Action<string> log, CancellationToken ct)
    {
        log($"Downloading {name} from {url} ...");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
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
}
