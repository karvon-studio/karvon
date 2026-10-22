using Karvon.Modules;

namespace Karvon.TypeScript;

/// <summary>
/// TypeScript and JavaScript: TypeScript 7's own language server in the editor of their files (errors as you type,
/// completion, hover, go to definition, references, rename, formatting), installed the first time one of them opens; and a
/// folder's package.json scripts as its tasks (build, test, the app).
/// </summary>
public sealed class TypeScriptModule : KarvonModule
{
    public override string Id => "typescript";

    public override string Name => "TypeScript and JavaScript";

    public override string Description =>
        "TypeScript 7's language server for .ts, .tsx, .js and .jsx files: errors as you type, completion, hover, go to definition, "
        + "references, rename and formatting, installed the first time one of them opens (no Node.js needed). In a folder, the "
        + "package.json scripts build, run and test it (with npm, pnpm, yarn or bun).";

    public override IReadOnlyList<LanguageServerInfo> LanguageServers =>
        [new("typescript", "TypeScript language server", TypeScriptServer.Serves, StartAsync)];

    /// <summary>The package.json scripts: build, test, the app (dev or start), and the rest in Build > Run Task.</summary>
    public override IReadOnlyList<WorkspaceTask> WorkspaceTasks(string workspace) => PackageScripts.Read(workspace);

    public override IReadOnlyList<string> WorkspaceTaskFiles => ["package.json"];

    /// <summary>Node.js's folder when Karvon's PATH has none (started from the desktop, with Node from nvm).</summary>
    public override IEnumerable<string> ToolDirectories => NodeLocator.DirectoryOffPath() is { } node ? [node] : [];

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
