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

    /// <summary>Where jdtls keeps what it knows of a workspace (its -data folder), one for each folder.</summary>
    static string DataFolder(string workspace) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "karvon", "jdtls-data",
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(workspace)))[..16]);

    public static async Task<JdtlsServer> StartAsync(LanguageServerStart start)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var java = JavaTools.FindJava(path)
            ?? throw new InvalidOperationException("Java was not found: jdtls runs on a JDK 21 or newer (set JAVA_HOME, or put java on the PATH).");
        var launcher = await JavaTools.EnsureJdtlsAsync(start.Log, start.Cancellation);
        var debug = await JavaTools.EnsureJavaDebugAsync(start.Log, start.Cancellation);
        var psi = new ProcessStartInfo(java,
        [
            "-Declipse.application=org.eclipse.jdt.ls.core.id1", "-Dosgi.bundles.defaultStartLevel=4", "-Declipse.product=org.eclipse.jdt.ls.core.product",
            "-Dlog.level=WARNING", "-Xmx1G", "--add-modules=ALL-SYSTEM", "--add-opens", "java.base/java.util=ALL-UNNAMED", "--add-opens", "java.base/java.lang=ALL-UNNAMED",
            "-jar", launcher, "-configuration", JavaTools.JdtlsConfiguration, "-data", DataFolder(start.Workspace),
        ])
        { WorkingDirectory = start.Workspace };
        var (process, rpc) = Launch(psi);
        var server = new JdtlsServer(process, rpc, start.Workspace);
        try
        {
            await server.InitializeAsync(start.Workspace, new
            {
                bundles = new[] { debug },
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
