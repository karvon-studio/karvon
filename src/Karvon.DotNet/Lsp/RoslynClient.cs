using System.Diagnostics;
using System.Text.Json;
using Karvon.Build;
using Karvon.Lsp;

namespace Karvon.DotNet.Lsp;

/// <summary>
/// Microsoft's Roslyn language server (roslyn-language-server, the one VS Code's C# Dev Kit uses) for a solution: C# and,
/// through Razor cohosting, .razor and .cshtml (their HTML half served here). It loads the solution's projects and says
/// when they are in; it asks for a restore when a project needs one.
/// </summary>
public sealed class RoslynClient : LspClient
{
    readonly HtmlLanguageService _html = new();

    RoslynClient(Process process, JsonRpcConnection rpc, string solutionDir) : base(process, rpc, solutionDir)
    {
    }

    public event Action? ProjectsLoaded;
    public event Action<IReadOnlyList<string>>? RestoreRequested;

    public static string LanguageIdOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".cs" => "csharp",
        ".razor" or ".cshtml" => "razor",
        _ => "plaintext",
    };

    /// <summary>The files Roslyn serves: C#, Razor.</summary>
    public static bool Serves(string path) => LanguageIdOf(path) != "plaintext";

    public override string LanguageIdFor(string path) => LanguageIdOf(path);

    /// <summary>Roslyn is ready once the solution's projects are loaded, not when it has started.</summary>
    protected override bool ReadyOnceInitialized => false;

    /// <param name="root">Its workspace's folder (a folder opened as it is); null, the solution's or the project's.</param>
    public static async Task<RoslynClient> StartAsync(RoslynServerInstall install, string solutionPath, string logDir, CancellationToken ct, string? root = null)
    {
        Directory.CreateDirectory(logDir);
        // Build hosts a server left behind (Karvon killed, or one that exited by itself before Karvon ended them).
        _ = Task.Run(() => Services.ProcessTree.KillOrphans(RoslynServerLocator.InstallRoot, "BuildHost", "Microsoft.CodeAnalysis.LanguageServer"));
        root ??= Path.GetDirectoryName(solutionPath)!;
        var psi = new ProcessStartInfo(install.Executable) { WorkingDirectory = root };
        foreach (var a in new[]
                 {
                     "--stdio", "--logLevel", "Information", "--extensionLogDirectory", logDir,
                     "--csharpDesignTimePath", install.CSharpDesignTimeTargets,
                     $"--clientProcessId={Environment.ProcessId}",
                 })
            psi.ArgumentList.Add(a);
        DotnetCli.ApplyEnvironment(psi);
        // The server is configured for server GC, which on a many-core desktop keeps gigabytes of per-core heap
        // reserve on top of the solution's live data. Workstation (background) GC stays close to what is live;
        // DOTNET_gcServer=1 in Karvon's environment brings server GC back.
        psi.Environment.TryAdd("DOTNET_gcServer", "0");

        var (process, rpc) = Launch(psi);
        var client = new RoslynClient(process, rpc, root);
        await client.InitializeAsync(root, null, ct);
        if (Workspace.SolutionLoader.IsProjectFile(solutionPath))
            await client.Rpc.NotifyAsync("project/open", new { projects = new[] { ToUri(solutionPath) } });
        else
            await client.Rpc.NotifyAsync("solution/open", new { solution = ToUri(solutionPath) });
        return client;
    }

    /// <summary>Projects the solution has now (added, or a project file renamed): Roslyn loads them beside those it has.</summary>
    public Task OpenProjectsAsync(IEnumerable<string> projects) =>
        Rpc.NotifyAsync("project/open", new { projects = projects.Select(ToUri).ToArray() });

    protected override void WireServer()
    {
        // Razor cohosting: the HTML half of .razor files is served by the client.
        Rpc.OnRequest("razor/updateHtml", p =>
        {
            _html.Update(p);
            return Task.FromResult<object?>(null);
        });
        Rpc.OnNotification("razor/updateHtml", _html.Update);
        foreach (var method in new[]
                 {
                     "textDocument/completion", "textDocument/hover", "textDocument/formatting", "textDocument/rangeFormatting",
                     "textDocument/onTypeFormatting", "textDocument/foldingRange", "textDocument/documentHighlight",
                     "textDocument/documentSymbol", "textDocument/documentColor", "textDocument/codeAction",
                     "textDocument/inlayHint", "textDocument/diagnostic", "textDocument/signatureHelp",
                 })
            Rpc.OnRequest(method, p => Task.FromResult(_html.Handle(method, p)));
        Rpc.OnRequest("workspace/_roslyn_projectNeedsRestore", p =>
        {
            var paths = p.TryGetProperty("projectFilePaths", out var arr)
                ? arr.EnumerateArray().Select(x => x.GetString()!).ToList()
                : [];
            RestoreRequested?.Invoke(paths);
            return Task.FromResult<object?>(null);
        });
        Rpc.OnNotification("window/_roslyn_showToast", p =>
            OnLog("[toast] " + (p.TryGetProperty("message", out var m) ? m.GetString() : p.GetRawText())));
        Rpc.OnNotification("workspace/projectInitializationComplete", _ =>
        {
            IsReady = true;
            ProjectsLoaded?.Invoke();
        });
    }
}
