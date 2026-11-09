using System.Diagnostics;
using Karvon.Lsp;
using Karvon.Modules;

namespace Karvon.Arche;

/// <summary>
/// arche-lsp, Arche's language server, for a workspace's .arche files: errors as they are typed, hover, go to definition, find
/// references, completion, Format Document and the compiler's fixes. It runs the compiler for each request, so a file's package
/// (the nearest package.arche above it) is its own business, and the workspace need not be one.
/// </summary>
public sealed class ArcheServer : LspClient
{
    ArcheServer(Process process, JsonRpcConnection rpc, string root) : base(process, rpc, root)
    {
    }

    public static bool Serves(string path) => Path.GetExtension(path).Equals(".arche", StringComparison.OrdinalIgnoreCase);

    public override string LanguageIdFor(string path) => Serves(path) ? "arche" : "plaintext";

    /// <summary>Starts it on stdio in the workspace, told which compiler goes with it.</summary>
    public static async Task<LspClient> StartAsync(LanguageServerStart start)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var server = ArcheTools.Find(ArcheTools.Server, path) ?? throw new InvalidOperationException(ArcheTools.NotFound);
        var psi = new ProcessStartInfo(server) { WorkingDirectory = start.Workspace };
        if (ArcheTools.Find(ArcheTools.Compiler, path) is { } compiler)
            psi.Environment["ARCHE"] = compiler;
        var (process, rpc) = Launch(psi);
        var client = new ArcheServer(process, rpc, start.Workspace);
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
