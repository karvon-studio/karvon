using System.Diagnostics;
using Karvon.Lsp;
using Karvon.Modules;

namespace Karvon.TypeScript;

/// <summary>
/// TypeScript 7's language server (<c>tsc --lsp --stdio</c>) for a workspace: TypeScript and JavaScript, with JSX, by the
/// workspace's tsconfig.json or jsconfig.json (or the defaults when it has none).
/// </summary>
public sealed class TypeScriptServer : LspClient
{
    TypeScriptServer(Process process, JsonRpcConnection rpc, string root) : base(process, rpc, root)
    {
    }

    public static string LanguageIdOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".ts" or ".mts" or ".cts" => "typescript",
        ".tsx" => "typescriptreact",
        ".js" or ".mjs" or ".cjs" => "javascript",
        ".jsx" => "javascriptreact",
        _ => "plaintext",
    };

    /// <summary>The files it serves: .ts, .tsx, .js, .jsx and their module kinds (.mts, .cjs...).</summary>
    public static bool Serves(string path) => LanguageIdOf(path) != "plaintext";

    public override string LanguageIdFor(string path) => LanguageIdOf(path);

    /// <summary>Installs TypeScript the first time, then starts its server in the workspace.</summary>
    public static async Task<LspClient> StartAsync(LanguageServerStart start)
    {
        var tsc = await TypeScriptServerLocator.EnsureInstalledAsync(start.Log, start.Cancellation);
        var (process, rpc) = Launch(new ProcessStartInfo(tsc, ["--lsp", "--stdio"]) { WorkingDirectory = start.Workspace });
        var server = new TypeScriptServer(process, rpc, start.Workspace);
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
