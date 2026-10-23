using Avalonia.Threading;
using Karvon.Lsp;
using Karvon.Modules;

namespace Karvon.ViewModels;

/// <summary>
/// The modules' language servers (TypeScript's, Python's...): each starts for the open workspace when a file it serves
/// first opens in the editor, serves every editor of its files from then on, and stops with the workspace. One that is
/// for a kind of workspace (.NET's Roslyn: a solution) starts as that opens, its progress on the status bar. A file has
/// one server, the first that serves it, and any linters beside it (ESLint), each adding its diagnostics.
/// </summary>
public partial class MainViewModel
{
    readonly Dictionary<string, Task<LspClient?>> _moduleServers = [];

    /// <summary>The editor waits for these servers' colors and blocks in their files.</summary>
    void RegisterModuleLanguageServers()
    {
        foreach (var server in Modules.SelectMany(m => m.LanguageServers))
            LanguageServers.Register(server.Serves);
    }

    /// <summary>The running module servers (for the tests and Claude's code_diagnostics).</summary>
    internal IEnumerable<LspClient> ModuleLanguageServers =>
        _moduleServers.Values.Where(t => t.IsCompletedSuccessfully && t.Result is not null).Select(t => t.Result!);

    /// <summary>The running server of a file, or null (Claude's code_diagnostics).</summary>
    internal LspClient? LanguageServerFor(string path) =>
        _moduleServers.Where(s => !IsLinter(s.Key) && s.Value.IsCompletedSuccessfully).Select(s => s.Value.Result).FirstOrDefault(l => l?.Handles(path) == true);

    /// <summary>The running linters of a file (Claude's code_diagnostics).</summary>
    internal IEnumerable<LspClient> LintersFor(string path) =>
        _moduleServers.Where(s => IsLinter(s.Key) && s.Value.IsCompletedSuccessfully).Select(s => s.Value.Result).OfType<LspClient>().Where(l => l.Handles(path));

    bool IsLinter(string id) => Modules.SelectMany(m => m.LanguageServers).Any(s => s.Id == id && s.Lints);

    /// <summary>A server on an editor: as its file's server, or as one of its linters.</summary>
    static void AttachTo(DocumentViewModel doc, LanguageServerInfo info, LspClient lsp)
    {
        if (info.Lints)
            doc.Host.AttachLinter(lsp);
        else
            doc.Host.Attach(lsp);
    }

    /// <summary>The workspace opened: the servers that are for it start, and attach to the files open already.</summary>
    async Task StartWorkspaceLanguageServersAsync()
    {
        if (Solution is not { } solution)
            return;
        var starting = Modules.SelectMany(m => m.LanguageServers)
            .Where(s => s.ForWorkspace?.Invoke(solution) == true && !_moduleServers.ContainsKey(s.Id))
            .Select(s => (Info: s, Start: _moduleServers[s.Id] = StartModuleLanguageServerAsync(s, solution, null))).ToList();
        foreach (var (info, start) in starting)
            if (await start is { } lsp && Solution == solution)
                foreach (var d in Editors.Where(d => lsp.Handles(d.FilePath)))
                    AttachTo(d, info, lsp);
    }

    /// <summary>
    /// The modules' servers for an editor's file, its own and its linters: each started the first time one of its files
    /// opens (one that is for a kind of workspace only with it), then attached.
    /// </summary>
    void AttachModuleLanguageServer(DocumentViewModel doc)
    {
        if (Solution is not { } solution)
            return;
        var servers = Modules.SelectMany(m => m.LanguageServers).Where(s => s.Serves(doc.FilePath)).ToList();
        foreach (var info in servers.Where(s => s.Lints).Prepend(servers.FirstOrDefault(s => !s.Lints)).OfType<LanguageServerInfo>())
            AttachModuleLanguageServer(doc, info, solution);
    }

    async void AttachModuleLanguageServer(DocumentViewModel doc, LanguageServerInfo info, Workspace.SolutionInfo solution)
    {
        if (!_moduleServers.TryGetValue(info.Id, out var starting))
        {
            if (info.ForWorkspace is not null)
                return;
            _moduleServers[info.Id] = starting = StartModuleLanguageServerAsync(info, solution, doc.FilePath);
        }
        if (await starting is { } lsp && Solution == solution && Editors.Contains(doc))
            AttachTo(doc, info, lsp);
    }

    /// <param name="file">The file it starts for; null, as its workspace opens.</param>
    async Task<LspClient?> StartModuleLanguageServerAsync(LanguageServerInfo info, Workspace.SolutionInfo solution, string? file)
    {
        // One that asks first (ESLint: may it run the folder's code?): no, and it stays off while the workspace is open.
        try
        {
            if (info.MayStartAsync is { } may && !await may(solution.Directory, file ?? solution.Directory))
                return null;
        }
        catch (Exception ex)
        {
            ServerOutput.Append($"{info.Name} could not start: {ex.Message}");
            return null;
        }
        if (Solution != solution)
            return null;
        ServerOutput.Append($"Starting {info.Name}...");
        // A workspace's server says how it is doing on the status bar, as Visual Studio's IntelliSense does.
        var workspace = info.ForWorkspace is not null;
        if (workspace)
            ServerStatus = "IntelliSense: starting...";
        try
        {
            var lsp = await info.StartAsync(new LanguageServerStart(solution.Directory, ServerOutput.Append, CancellationToken.None));
            if (Solution != solution)
            {
                await lsp.DisposeAsync();
                return null;
            }
            lsp.Log += ServerOutput.Append;
            if (workspace)
            {
                lsp.Progress += text => Dispatcher.UIThread.Post(() =>
                {
                    if (text is not null && !lsp.IsReady && IsServing(info, lsp))
                        ServerStatus = "IntelliSense: " + text;
                });
                lsp.Ready += () => Dispatcher.UIThread.Post(() =>
                {
                    if (IsServing(info, lsp))
                        ServerStatus = "IntelliSense: ready";
                });
                ServerStatus = lsp.IsReady ? "IntelliSense: ready" : "IntelliSense: loading projects...";
            }
            lsp.SemanticTokensRefreshRequested += () => Dispatcher.UIThread.Post(() =>
            {
                foreach (var d in Editors.Where(d => lsp.Handles(d.FilePath)))
                    d.Host.ScheduleSemanticTokens(300);
            });
            lsp.DiagnosticsRefreshRequested += () => Dispatcher.UIThread.Post(() =>
            {
                foreach (var d in Editors.Where(d => lsp.Handles(d.FilePath)))
                    d.Host.ScheduleDiagnostics(300);
            });
            lsp.Exited += _ => Dispatcher.UIThread.Post(() =>
            {
                if (!IsServing(info, lsp))
                    return;
                _moduleServers.Remove(info.Id);
                foreach (var d in Editors.Where(d => lsp.Handles(d.FilePath)))
                    if (info.Lints)
                        d.Host.DetachLinter(lsp);
                    else
                        d.Host.Detach();
                ServerOutput.Append($"{info.Name} stopped.");
                if (workspace)
                    ServerStatus = "IntelliSense: stopped (Tools > Restart Language Server)";
            });
            ServerOutput.Append($"{info.Name} started.");
            return lsp;
        }
        catch (Exception ex)
        {
            // A download that failed, a server that would not start...: the workspace stays open without it.
            ServerOutput.Append($"{info.Name} could not start: {ex.Message}");
            SetStatus($"{info.Name} could not start: see Output > Language Server.");
            if (workspace)
                ServerStatus = "IntelliSense: unavailable";
            _moduleServers.Remove(info.Id);
            return null;
        }
    }

    /// <summary>Whether this server is still the one of its id (not stopped, nor replaced by a restart).</summary>
    bool IsServing(LanguageServerInfo info, LspClient lsp) =>
        _moduleServers.TryGetValue(info.Id, out var running) && running.IsCompletedSuccessfully && running.Result == lsp;

    /// <summary>The workspace closes, or the servers restart: they stop.</summary>
    async Task StopModuleLanguageServersAsync()
    {
        var servers = _moduleServers.Values.ToList();
        _moduleServers.Clear();
        ServerStatus = "";
        foreach (var starting in servers)
            if (await starting is { } lsp)
                await lsp.DisposeAsync();
    }
}
