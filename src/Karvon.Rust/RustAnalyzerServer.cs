using System.Diagnostics;
using Karvon.Lsp;
using Karvon.Modules;

namespace Karvon.Rust;

/// <summary>rust-analyzer for a workspace's .rs files: their errors (cargo check's too), completion, hover, go to definition.</summary>
public sealed class RustAnalyzerServer : LspClient
{
    RustAnalyzerServer(Process process, JsonRpcConnection rpc, string root) : base(process, rpc, root)
    {
    }

    public static bool Serves(string path) => Path.GetExtension(path).Equals(".rs", StringComparison.OrdinalIgnoreCase);

    public override string LanguageIdFor(string path) => Serves(path) ? "rust" : "plaintext";

    /// <summary>Installs rust-analyzer the first time, then starts it in the workspace, with cargo on its PATH.</summary>
    public static async Task<LspClient> StartAsync(LanguageServerStart start)
    {
        var server = await RustTools.EnsureRustAnalyzerAsync(start.Log, start.Cancellation);
        var psi = new ProcessStartInfo(server) { WorkingDirectory = start.Workspace };
        psi.Environment["PATH"] = RustTools.PathWithCargo(Environment.GetEnvironmentVariable("PATH") ?? "");
        var (process, rpc) = Launch(psi);
        var client = new RustAnalyzerServer(process, rpc, start.Workspace);
        try
        {
            await client.InitializeAsync(start.Workspace, null, start.Cancellation);
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
        return client;
    }
}
