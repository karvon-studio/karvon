using Karvon.Modules;

namespace Karvon.Python;

/// <summary>
/// Python: Pyright's language server in the editor of .py files (errors as you type, completion, hover, go to
/// definition), installed the first time one of them opens, with the workspace's interpreter (its .venv first).
/// </summary>
public sealed class PythonModule : KarvonModule
{
    public override string Id => "python";

    public override string Name => "Python";

    public override string Description =>
        "Pyright's language server for .py files: errors as you type, completion, hover, go to definition, references and "
        + "rename, with the workspace's interpreter (its .venv first), installed the first time one of them opens (it runs on Node.js).";

    public override IReadOnlyList<LanguageServerInfo> LanguageServers =>
        [new("pyright", "Python language server (Pyright)", PyrightServer.Serves, StartAsync)];

    public override IReadOnlyList<ModuleComponent> Components =>
    [
        new("pyright", "Python language server (Pyright)", PyrightLocator.Version, PyrightLocator.InstallRoot,
            () => PyrightLocator.Find() is not null, PyrightLocator.EnsureInstalledAsync)
        {
            Description = "Pyright (MIT), Microsoft's Python type checker and language server, from the npm registry; it runs on Node.js.",
        },
    ];

    async Task<Lsp.LspClient> StartAsync(LanguageServerStart start)
    {
        var installing = PyrightLocator.Find() is null;
        if (installing)
            Workbench.Status($"Installing Pyright {PyrightLocator.Version}...");
        var server = await PyrightServer.StartAsync(start);
        if (installing)
            Workbench.Status($"Pyright {PyrightLocator.Version} installed.");
        return server;
    }
}
