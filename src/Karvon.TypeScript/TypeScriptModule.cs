using Karvon.Modules;

namespace Karvon.TypeScript;

/// <summary>
/// TypeScript and JavaScript: TypeScript 7's own language server in the editor of their files (errors as you type,
/// completion, hover, go to definition, references, rename, formatting). It is installed the first time one of them opens.
/// </summary>
public sealed class TypeScriptModule : KarvonModule
{
    public override string Id => "typescript";

    public override string Name => "TypeScript and JavaScript";

    public override string Description =>
        "TypeScript 7's language server for .ts, .tsx, .js and .jsx files: errors as you type, completion, hover, go to definition, "
        + "references, rename and formatting. Installed the first time one of them opens (no Node.js needed).";

    public override IReadOnlyList<LanguageServerInfo> LanguageServers =>
        [new("typescript", "TypeScript language server", TypeScriptServer.Serves, StartAsync)];

    async Task<Lsp.LspClient> StartAsync(LanguageServerStart start)
    {
        var installing = TypeScriptServerLocator.Find() is null;
        if (installing)
            Workbench.Status($"Installing TypeScript {TypeScriptServerLocator.Version}...");
        var server = await TypeScriptServer.StartAsync(start);
        if (installing)
            Workbench.Status($"TypeScript {TypeScriptServerLocator.Version} installed.");
        return server;
    }
}
