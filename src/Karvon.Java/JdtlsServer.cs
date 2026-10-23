using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Karvon.Lsp;
using Karvon.Modules;

namespace Karvon.Java;

/// <summary>
/// jdtls, Eclipse's Java language server, for a workspace: its Java files' errors, completion, hover, go to definition,
/// with its Maven or Gradle project (or the files alone); java-debug's plugin in it, which F5 starts its sessions from.
/// </summary>
public sealed class JdtlsServer : LspClient
{
    JdtlsServer(Process process, JsonRpcConnection rpc, string root) : base(process, rpc, root)
    {
    }

    public static bool Serves(string path) => Path.GetExtension(path).Equals(".java", StringComparison.OrdinalIgnoreCase);

    public override string LanguageIdFor(string path) => Serves(path) ? "java" : "plaintext";

    /// <summary>Whether its program still runs.</summary>
    public bool IsRunning => !IsDisposed && !Process.HasExited;

    /// <summary>The workspace built as jdtls builds it (incrementally): whether it built without errors.</summary>
    public async Task<bool> BuildAsync(CancellationToken ct)
    {
        // java/buildWorkspace: 0 failed, 1 succeeded, 2 with errors, 3 cancelled.
        var status = await Rpc.RequestAsync("java/buildWorkspace", false, ct);
        return status.ValueKind == System.Text.Json.JsonValueKind.Number && status.GetInt32() == 1;
    }

    /// <summary>
    /// The test class path of the Java project a file is in (its test output, its libraries, its main output), and the
    /// project's folder; null when the file is in no project jdtls knows (yet).
    /// </summary>
    public async Task<(string Root, IReadOnlyList<string> ClassPath)?> TestClassPathAsync(string file, CancellationToken ct)
    {
        System.Text.Json.JsonElement result;
        try
        {
            result = await ExecuteCommandAsync("java.project.getClasspaths", [ToUri(file), """{"scope":"test"}"""], ct);
        }
        catch (LspException)
        {
            return null;
        }
        if (result.ValueKind != System.Text.Json.JsonValueKind.Object || !result.TryGetProperty("projectRoot", out var root))
            return null;
        IEnumerable<string> Paths(string name) => result.TryGetProperty(name, out var list) && list.ValueKind == System.Text.Json.JsonValueKind.Array
            ? list.EnumerateArray().Select(p => p.GetString()).OfType<string>()
            : [];
        // jdtls's URIs have one slash after the scheme (file:/home/...).
        var projectRoot = root.GetString() is { } r && r.StartsWith("file:", StringComparison.Ordinal) ? Uri.UnescapeDataString(r["file:".Length..].TrimStart('/')) : root.GetString() ?? "";
        if (!Path.IsPathRooted(projectRoot))
            projectRoot = "/" + projectRoot;
        return (projectRoot, [.. Paths("classpaths"), .. Paths("modulepaths")]);
    }

    static string DataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "karvon", "jdtls-data");

    /// <summary>Where jdtls keeps what it knows of a workspace (its -data folder), one for each folder.</summary>
    static string DataFolder(string workspace) => Path.Combine(DataRoot, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(workspace)))[..16]);

    /// <summary>The file in a data folder that says whose it is: the workspace's path.</summary>
    const string WorkspaceFile = "karvon-workspace";

    /// <summary>
    /// A workspace's data folder, marked as its; those of workspaces that are not there any more (a folder deleted, a
    /// test's) go, since jdtls keeps tens of megabytes for each.
    /// </summary>
    static string PrepareDataFolder(string workspace)
    {
        try
        {
            if (Directory.Exists(DataRoot))
                foreach (var folder in Directory.EnumerateDirectories(DataRoot))
                    if (File.Exists(Path.Combine(folder, WorkspaceFile)) && File.ReadAllText(Path.Combine(folder, WorkspaceFile)).Trim() is { Length: > 0 } owner && !Directory.Exists(owner))
                        Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // In use by another jdtls, or not ours to delete: it stays.
        }
        var data = DataFolder(workspace);
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, WorkspaceFile), workspace);
        return data;
    }

    /// <summary>A few of a folder's Java files (its build folders aside), by their URIs.</summary>
    static string[] TriggerFiles(string workspace)
    {
        try
        {
            return [.. Directory.EnumerateFiles(workspace, "*.java", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 8 })
                .Where(f => !Path.GetRelativePath(workspace, f).Split(Path.DirectorySeparatorChar).Any(p => p is "target" or "build" or "bin" or "out" or "node_modules" || p.StartsWith('.')))
                .Take(10).Select(f => new Uri(f).AbsoluteUri)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static async Task<JdtlsServer> StartAsync(LanguageServerStart start)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var java = JavaTools.FindJava(path, 21)
            ?? throw new InvalidOperationException(JavaTools.FindJava(path) is { } old
                ? $"jdtls runs on Java 21 or newer, and {old} is Java {JavaTools.MajorOf(old)}: install a JDK 21 (or set JAVA_HOME to one)."
                : "Java was not found: jdtls runs on a JDK 21 or newer (set JAVA_HOME, or put java on the PATH).");
        var launcher = await JavaTools.EnsureJdtlsAsync(start.Log, start.Cancellation);
        var debug = await JavaTools.EnsureJavaDebugAsync(start.Log, start.Cancellation);
        var psi = new ProcessStartInfo(java,
        [
            "-Declipse.application=org.eclipse.jdt.ls.core.id1", "-Dosgi.bundles.defaultStartLevel=4", "-Declipse.product=org.eclipse.jdt.ls.core.product",
            "-Dlog.level=WARNING", "-Xmx1G", "--add-modules=ALL-SYSTEM", "--add-opens", "java.base/java.util=ALL-UNNAMED", "--add-opens", "java.base/java.lang=ALL-UNNAMED",
            "-jar", launcher, "-configuration", JavaTools.JdtlsConfiguration, "-data", PrepareDataFolder(start.Workspace),
        ])
        { WorkingDirectory = start.Workspace };
        var (process, rpc) = Launch(psi);
        var server = new JdtlsServer(process, rpc, start.Workspace);
        try
        {
            await server.InitializeAsync(start.Workspace, new
            {
                bundles = new[] { debug },
                // A folder no build has becomes a project of its own (its source folders, its lib's jars) from these, as
                // VS Code's Java extension gives the files open when it starts; one with a build is that build's.
                triggerFiles = TriggerFiles(start.Workspace),
                settings = new { java = new { autobuild = new { enabled = true } } },
                extendedClientCapabilities = new { classFileContentsSupport = true },
            }, start.Cancellation);
        }
        catch
        {
            await server.DisposeAsync();
            throw;
        }
        return server;
    }
}
