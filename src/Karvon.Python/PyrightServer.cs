using System.Diagnostics;
using Karvon.Lsp;
using Karvon.Modules;
using Karvon.Workspace;

namespace Karvon.Python;

/// <summary>
/// Pyright's language server for a workspace's Python files: their errors as they are typed, completion, hover, go to
/// definition, with the workspace's interpreter (its virtual environment's packages known).
/// </summary>
public sealed class PyrightServer : LspClient
{
    readonly string? _interpreter;

    PyrightServer(Process process, JsonRpcConnection rpc, string root, string? interpreter) : base(process, rpc, root) => _interpreter = interpreter;

    public static bool Serves(string path) => Path.GetExtension(path).ToLowerInvariant() is ".py" or ".pyi";

    public override string LanguageIdFor(string path) => Serves(path) ? "python" : "plaintext";

    /// <summary>The interpreter whose packages it reads (python.pythonPath, as VS Code gives it).</summary>
    protected override object? ConfigurationFor(string? section) => section switch
    {
        "python" when _interpreter is not null => new { pythonPath = _interpreter },
        "python.analysis" => new { diagnosticMode = "openFilesOnly", autoSearchPaths = true, useLibraryCodeForTypes = true },
        _ => null,
    };

    /// <summary>Installs Pyright the first time, then starts its server on Node.js in the workspace.</summary>
    public static async Task<LspClient> StartAsync(LanguageServerStart start)
    {
        var path = PythonEnvironment.PathFor(start.Workspace, string.Join(System.IO.Path.PathSeparator,
            Build.NodeLocator.DirectoryOffPath() is { } nodeDir ? [nodeDir, Environment.GetEnvironmentVariable("PATH") ?? ""] : new[] { Environment.GetEnvironmentVariable("PATH") ?? "" }));
        var node = TaskVariables.Find(OperatingSystem.IsWindows() ? "node.exe" : "node", path, start.Workspace)
            ?? throw new InvalidOperationException("Node.js was not found: Pyright, Python's language server, runs on it (install it from nodejs.org, or with nvm).");
        var langserver = await PyrightLocator.EnsureInstalledAsync(start.Log, start.Cancellation);
        var psi = new ProcessStartInfo(node, [langserver, "--stdio"]) { WorkingDirectory = start.Workspace };
        psi.Environment["PATH"] = path;
        var (process, rpc) = Launch(psi);
        var server = new PyrightServer(process, rpc, start.Workspace, PythonEnvironment.Interpreter(start.Workspace, path));
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
