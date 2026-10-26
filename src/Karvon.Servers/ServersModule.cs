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
            Overlay = Explorer = new ServersExplorerViewModel(this, Karvon.Services.Keyring.System);
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

    // ---- Remote folders -----------------------------------------------------------------------------------------

    /// <summary>Where the machines' folders are copied: &lt;config&gt;/remote-folders.</summary>
    internal static string RemoteFoldersRoot => Path.Combine(Services.Settings.ConfigDirectory, "remote-folders");

    /// <summary>The remote folder open as the workspace.</summary>
    RemoteFolderSession? _remote;

    /// <summary>The remote folder being opened: its machine, and how it reads the files there (SFTP, the tests' own).</summary>
    (string LocalRoot, ServerHost Host, IRemoteFiles Files)? _opening;

    readonly ModuleStatusItem _remoteStatus = new() { Icon = "ToolServers" };

    /// <summary>"SSH: web-1" while a remote folder is the workspace; a click pulls what changed there.</summary>
    public override ModuleStatusItem? StatusItem => _remoteStatus;

    public override RemoteWorkspace? RemoteWorkspaceOf(string workspace) =>
        _remote is { } open && open.Folder.LocalRoot == Path.GetFullPath(workspace).TrimEnd(Path.DirectorySeparatorChar) ? open.Workspace : null;

    /// <summary>The remote folder open, if any (the tests').</summary>
    internal RemoteFolderSession? OpenRemoteFolder => _remote;

    Task IServersHost.OpenRemoteFolderAsync(ServerHost host, string path) => OpenRemoteFolderAsync(host, path, null);

    /// <summary>
    /// A machine's folder copied here (only what changed since, when it was before) and opened as the workspace; its
    /// files read over the Servers' SFTP connection, or <paramref name="files"/>.
    /// </summary>
    internal async Task OpenRemoteFolderAsync(ServerHost host, string path, IRemoteFiles? files)
    {
        var folder = RemoteFolder.For(RemoteFoldersRoot, host.Name, path);
        Workbench.Status($"Copying {host.Name}:{path}...");
        try
        {
            files ??= new SftpRemoteFiles(await EnsureExplorer().Connections.SftpAsync(host));
            var pulled = await folder.PullAsync(files, CancellationToken.None);
            if (!Directory.EnumerateFileSystemEntries(folder.LocalRoot).Any())
            {
                Workbench.Status($"{host.Name}:{path} has no files to open" + (pulled.Left.Count > 0 ? $" (left there: {string.Join(", ", pulled.Left.Take(3))})." : "."));
                return;
            }
        }
        catch (Exception ex) when (ex is ServerException or Renci.SshNet.Common.SshException or IOException or UnauthorizedAccessException or System.Net.Sockets.SocketException)
        {
            Workbench.Status($"{host.Name}:{path} could not be copied: {ex.Message}");
            return;
        }
        _opening = (folder.LocalRoot, host, files);
        await Workbench.OpenWorkspaceAsync(folder.LocalRoot);
    }

    /// <summary>
    /// The workspace changed: a remote folder that was open sends what is left and closes; one opened (now, or from the
    /// recent list) is watched, and what changed there since is pulled when it was not just copied.
    /// </summary>
    public override async Task WorkspaceChangedAsync()
    {
        var dir = Workbench.Solution?.Directory is { } d ? Path.GetFullPath(d).TrimEnd(Path.DirectorySeparatorChar) : null;
        if (_remote is { } open && open.Folder.LocalRoot != dir)
        {
            open.Dispose();
            _remote = null;
            _remoteStatus.IsVisible = false;
        }
        // The one being opened, once it is the workspace (the modules may hear of a change before it is).
        var opening = _opening is { } o && o.LocalRoot == dir ? o : ((string LocalRoot, ServerHost Host, IRemoteFiles Files)?)null;
        if (opening is not null)
            _opening = null;
        if (dir is null || _remote is not null || RemoteFolder.Of(dir) is not { } folder)
            return;
        if ((opening?.Host ?? EnsureExplorer().Find(folder.Server)?.Host) is not { } host)
        {
            Workbench.Status($"{Path.GetFileName(dir)} is a copy of a folder of {folder.Server}, which is no longer one of the Servers: it is opened as a folder here.");
            return;
        }
        Func<CancellationToken, Task<IRemoteFiles>> files = opening is { } given
            ? _ => Task.FromResult(given.Files)
            : async ct => new SftpRemoteFiles(await EnsureExplorer().Connections.SftpAsync(host, ct));
        _remote = new RemoteFolderSession(folder, host, files, Workbench.Status, (title, message, confirm) => EnsureExplorer().ConfirmAsync(title, message, confirm, danger: true));
        _remote.Watch();
        _remoteStatus.Text = $"SSH: {host.Name}";
        _remoteStatus.ToolTip = $"A copy of {host.Name}:{folder.RemoteRoot}: what changes here goes there; tasks, builds and the terminal run there. Click to pull what changed there.";
        _remoteStatus.Click = () => _ = _remote?.PullAsync();
        _remoteStatus.IsVisible = true;
        if (opening is null)
            await _remote.PullAsync();
    }

    /// <summary>The workspace closes: what changed in a remote folder's copy goes to its machine first.</summary>
    public override async Task<bool> CloseWorkspaceAsync()
    {
        if (_remote is { } open)
            await open.PushAsync();
        return true;
    }
}
