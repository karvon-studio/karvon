using Avalonia.Threading;
using Karvon.Lsp;
using Karvon.Modules;

namespace Karvon.ViewModels;

/// <summary>
/// The modules' language servers (TypeScript's, Python's...): each starts for the open workspace when a file it serves
/// first opens in the editor, serves every editor of its files from then on, and stops with the workspace. Roslyn, the
/// core's own, starts with a solution (see StartLanguageServerAsync).
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

    /// <summary>A module's server for an editor's file: started the first time one of its files opens, then attached.</summary>
    async void AttachModuleLanguageServer(DocumentViewModel doc)
    {
        if (Solution is not { } solution || Modules.SelectMany(m => m.LanguageServers).FirstOrDefault(s => s.Serves(doc.FilePath)) is not { } info)
            return;
        if (!_moduleServers.TryGetValue(info.Id, out var starting))
            _moduleServers[info.Id] = starting = StartModuleLanguageServerAsync(info, solution);
        if (await starting is { } lsp && Solution == solution && Editors.Contains(doc))
            doc.Host.Attach(lsp);
    }

    async Task<LspClient?> StartModuleLanguageServerAsync(LanguageServerInfo info, Workspace.SolutionInfo solution)
    {
        ServerOutput.Append($"Starting {info.Name}...");
        try
        {
            var lsp = await info.StartAsync(new LanguageServerStart(solution.Directory, ServerOutput.Append, CancellationToken.None));
            if (Solution != solution)
            {
                await lsp.DisposeAsync();
                return null;
            }
            lsp.Log += ServerOutput.Append;
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
                if (!_moduleServers.Remove(info.Id))
                    return;
                foreach (var d in Editors.Where(d => lsp.Handles(d.FilePath)))
                    d.Host.Detach();
                ServerOutput.Append($"{info.Name} stopped.");
            });
            ServerOutput.Append($"{info.Name} started.");
            return lsp;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or LspException or TimeoutException)
        {
            ServerOutput.Append($"{info.Name} could not start: {ex.Message}");
            SetStatus($"{info.Name} could not start: see Output > Language Server.");
            _moduleServers.Remove(info.Id);
            return null;
        }
    }

    /// <summary>The workspace closes: its module servers stop.</summary>
    async Task StopModuleLanguageServersAsync()
    {
        var servers = _moduleServers.Values.ToList();
        _moduleServers.Clear();
        foreach (var starting in servers)
            if (await starting is { } lsp)
                await lsp.DisposeAsync();
    }
}
