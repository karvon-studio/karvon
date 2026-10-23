using System.Text.Json;
using Avalonia.Controls.Templates;
using Bengine.Introspection.Servers;
using CommunityToolkit.Mvvm.ComponentModel;
using Karvon.Claude;
using Karvon.Claude.Tools;
using Karvon.Modules;
using Karvon.Servers.ViewModels;
using Karvon.Servers.Views;
using Karvon.ViewModels;
using static Karvon.Claude.Tools.ToolInput;

namespace Karvon.Servers;

/// <summary>
/// Servers: the machines of ~/.ssh/config and those added in Karvon (see <see cref="ServersExplorerViewModel"/>), its
/// explorer made the first time the tool window shows. A machine's page is a document; its files open in the editor, and
/// saving one puts it back on the machine. Claude Code gets the server tools.
/// </summary>
public sealed partial class ServersModule : KarvonModule, IServersHost
{
    public const string ToolWindowId = "Servers";

    IClaudeService? _claude;

    public override string Id => "servers";

    public override string Name => "Servers";


    public override string Icon => "folder-server";

    public override string Description =>
        "The machines of ~/.ssh/config and those added in Karvon, over SSH: their services, logs, files and a terminal; "
        + "Claude Code's server tools.";

    public override IReadOnlyList<ToolWindowInfo> ToolWindows =>
        [new(ToolWindowId, "Servers", "Ser_vers", "ToolServers", DockSide.Right, () => new ServersExplorerView { DataContext = EnsureExplorer() })];

    public override IReadOnlyList<IDataTemplate> DocumentTemplates => [Document<ServerDocument>(document => new ServerPageView { DataContext = document.Page })];

    public override IDataTemplate OverlayTemplate => Template<ServersExplorerViewModel>(_ => new ServersOverlay());

    /// <summary>The explorer, once it has been needed (no ~/.ssh/config is read before).</summary>
    [ObservableProperty]
    public partial ServersExplorerViewModel? Explorer { get; private set; }

    public ServersExplorerViewModel EnsureExplorer()
    {
        if (Explorer is null)
            Overlay = Explorer = new ServersExplorerViewModel(this, CredentialStores.System);
        return Explorer;
    }

    protected override void Started()
    {
        _claude = Workbench.Service<IClaudeService>();
        // A remote file's copy saved in the editor goes back to its machine.
        Workbench.FileSaved += path => _ = Explorer?.OnFileSavedAsync(path);
    }

    /// <summary>Karvon closes: the connections end.</summary>
    public override void Dispose()
    {
        Explorer?.Dispose();
        Overlay = Explorer = null;
    }

    public override IEnumerable<IClaudeTool> ClaudeTools() => ServerClaudeTools.Create(EnsureExplorer);

    /// <summary>ssh to one of the Servers' machines: Karvon's own connection (its keys and passwords) runs the command instead.</summary>
    public override IEnumerable<ClaudeShellRule> ClaudeShellRules() =>
        [new("ssh", command => ServerOf(command, EnsureExplorer().Servers) is not null,
            "Karvon connects to this server itself (the Servers window, with its keys and passwords): use mcp__karvon__server_exec (a "
            + "command, which the user approves) or server_logs, not ssh; server_list names the servers.")];

    /// <summary>The one of <paramref name="servers"/> an ssh command goes to (its destination: a name, a host or user@host), or null.</summary>
    internal static ServerNode? ServerOf(string command, IEnumerable<ServerNode> servers)
    {
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(command, @"(?:^|[;&|(\n`]|\$\()\s*(?:sudo\s+)?ssh\s+([^;&|\n]*)"))
        {
            // Its options (and their values), then its destination.
            var words = m.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < words.Length; i++)
            {
                if (words[i].StartsWith('-'))
                {
                    if (words[i].Length == 2 && "bcDEeFIiJLlmOoPpQRSWw".Contains(words[i][1]))
                        i++;
                    continue;
                }
                var destination = words[i].Trim('"', '\'');
                var host = destination[(destination.IndexOf('@') + 1)..];
                return servers.FirstOrDefault(s => s.Name == destination || s.Name == host || s.Host.Address == host);
            }
        }
        return null;
    }

    public override Task ShowClaudeToolAsync(string tool, JsonElement input, ClaudeToolOutput output)
    {
        switch (tool)
        {
            case "server_list":
                EnsureExplorer();
                Workbench.ShowToolWindowBesideClaude(ToolWindowId);
                break;
            case "server_logs" or "server_exec":
                ShowServer(Str(input, "server"), tool == "server_logs" ? input : null);
                break;
        }
        return Task.CompletedTask;
    }

    /// <summary>A server's page; for its logs, on its Logs tab with what Claude read (a unit, a file, how many lines).</summary>
    void ShowServer(string? name, JsonElement? logs)
    {
        if (name is null || EnsureExplorer().Find(name) is not { } node)
            return;
        var page = Explorer!.Open(node, logs is null ? null : "logs").Page;
        if (logs is { } input)
        {
            (page.LogUnit, page.LogFile, page.LogLines) = (Str(input, "unit") ?? "", Str(input, "file") ?? "", Int(input, "lines", 200, 10, 10_000));
            page.RefreshLogsCommand.Execute(null);
        }
    }

    // ---- IServersHost -------------------------------------------------------------------------------------------

    string IServersHost.ConfigDirectory => Services.Settings.ConfigDirectory;

    IEnumerable<ServerDocument> IServersHost.Documents => Workbench.Documents.OfType<ServerDocument>();

    void IServersHost.ShowDocument(ServerDocument document) => Workbench.ShowDocument(document);

    void IServersHost.CloseDocument(ServerDocument document) => Workbench.CloseDocument(document);

    void IServersHost.OpenFile(string localPath) => Workbench.OpenFile(localPath);

    Task<IReadOnlyList<string>> IServersHost.PickFilesAsync(string title) => Workbench.PickFilesAsync(title);

    void IServersHost.AskClaude(string prompt) => _claude?.Ask(prompt);

    void IServersHost.Status(string text) => Workbench.Status(text);
}
