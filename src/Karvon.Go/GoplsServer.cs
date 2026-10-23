using System.Diagnostics;
using Karvon.Lsp;
using Karvon.Modules;

namespace Karvon.Go;

/// <summary>gopls, Go's language server, for a workspace's .go files and its go.mod: their errors, completion, hover, go to definition.</summary>
public sealed class GoplsServer : LspClient
{
    GoplsServer(Process process, JsonRpcConnection rpc, string root) : base(process, rpc, root)
    {
    }

    public static bool Serves(string path) => Path.GetExtension(path).Equals(".go", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(path) == "go.mod";

    public override string LanguageIdFor(string path) => Path.GetFileName(path) == "go.mod" ? "go.mod" : Serves(path) ? "go" : "plaintext";

    /// <summary>Installs gopls the first time, then starts it on stdio in the workspace, with Go on its PATH.</summary>
    public static async Task<LspClient> StartAsync(LanguageServerStart start)
    {
        var gopls = await GoTools.EnsureGoplsAsync(start.Log, start.Cancellation);
        var psi = new ProcessStartInfo(gopls) { WorkingDirectory = start.Workspace };
        psi.Environment["PATH"] = GoTools.PathWithGo(Environment.GetEnvironmentVariable("PATH") ?? "");
        var (process, rpc) = Launch(psi);
        var server = new GoplsServer(process, rpc, start.Workspace);
        try
        {
            await server.InitializeAsync(start.Workspace, null, start.Cancellation);
        }
        catch
        {
            await server.DisposeAsync();
            throw;
        }
        return server;
    }
}
