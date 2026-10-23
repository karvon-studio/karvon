using System.Diagnostics;
using Karvon.Lsp;
using Karvon.Modules;

namespace Karvon.Cpp;

/// <summary>
/// clangd for a workspace's C and C++ files: their errors, completion, hover, go to definition, by the compile commands
/// CMake writes (build/compile_commands.json, which the CMake tasks ask it for) or a compile_flags.txt.
/// </summary>
public sealed class ClangdServer : LspClient
{
    ClangdServer(Process process, JsonRpcConnection rpc, string root) : base(process, rpc, root)
    {
    }

    static readonly HashSet<string> CppExtensions = new(StringComparer.OrdinalIgnoreCase) { ".cpp", ".cc", ".cxx", ".c++", ".hpp", ".hh", ".hxx", ".h", ".ipp", ".inl" };

    public static bool Serves(string path) => Path.GetExtension(path).Equals(".c", StringComparison.OrdinalIgnoreCase) || CppExtensions.Contains(Path.GetExtension(path));

    public override string LanguageIdFor(string path) => Path.GetExtension(path).Equals(".c", StringComparison.OrdinalIgnoreCase) ? "c" : Serves(path) ? "cpp" : "plaintext";

    public static async Task<LspClient> StartAsync(LanguageServerStart start)
    {
        var clangd = await ClangdLocator.EnsureInstalledAsync(start.Log, start.Cancellation);
        // Its own diagnostics as you type, its index in the background (in .cache/clangd, as it keeps it).
        var (process, rpc) = Launch(new ProcessStartInfo(clangd, ["--background-index", "--header-insertion=never"]) { WorkingDirectory = start.Workspace });
        var server = new ClangdServer(process, rpc, start.Workspace);
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
