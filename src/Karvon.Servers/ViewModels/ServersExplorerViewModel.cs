using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Karvon.ViewModels;
using Karvon.Ui;

namespace Karvon.Servers.ViewModels;

/// <summary>What the Servers explorer asks of Karvon: its folder, the document well, the editor, file pickers, Claude.</summary>
public interface IServersHost
{
    /// <summary>Karvon's settings folder: the server list, the host keys Karvon trusts, downloaded copies.</summary>
    string ConfigDirectory { get; }

    IEnumerable<ServerDocument> Documents { get; }

    void ShowDocument(ServerDocument document);

    void CloseDocument(ServerDocument document);

    /// <summary>A downloaded copy of a remote file, in the editor.</summary>
    void OpenFile(string localPath);

    /// <summary>Files to upload; empty when cancelled.</summary>
    Task<IReadOnlyList<string>> PickFilesAsync(string title);

    void AskClaude(string prompt);

    /// <summary>A line in the status bar (an upload, a download).</summary>
    void Status(string text);

    /// <summary>A folder of a machine copied here and opened as the workspace (its tasks, builds and terminal run there).</summary>
    Task OpenRemoteFolderAsync(ServerHost host, string path);
}

public enum ServerStatus
{
    NotConnected,
    Connecting,
    Connected,
    Failed,
}

/// <summary>A machine in the tree: where it is, whether Karvon is connected to it (and how fast), production or not.</summary>
public sealed partial class ServerNode(ServersExplorerViewModel explorer, ServerHost host) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name), nameof(Target), nameof(IsProduction), nameof(ToolTip))]
    public partial ServerHost Host { get; set; } = host;

    public string Name => Host.Name;

    public string Target => Host.Target;

    public bool IsProduction => Host.Production;

    public string ToolTip => $"{Host.Target} · from {Host.Source}{(Host.ProxyJump is { } jump ? $" · through {jump}" : "")}"
        + (Host.Production ? "\nProduction: every change on it asks, yours and Claude's." : "") + (Error is { } error ? "\n" + error : "");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(DotKind), nameof(IsConnected))]
    public partial ServerStatus Status { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(ToolTip))]
    public partial string? Error { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LatencyText), nameof(StatusText))]
    public partial TimeSpan? Latency { get; set; }

    public bool IsConnected => Status == ServerStatus.Connected;

    public string LatencyText => Latency is { } l ? $"{l.TotalMilliseconds:0} ms" : "";

    public string StatusText => Status switch
    {
        ServerStatus.Connected => "Connected" + (Latency is { } l ? $" · {l.TotalMilliseconds:0} ms" : ""),
        ServerStatus.Connecting => "Connecting…",
        ServerStatus.Failed => "Not connected" + (Error is { } e ? ": " + e : ""),
        _ => "Not connected",
    };

    /// <summary>The dot: green connected, amber connecting, red failed, gray not connected.</summary>
    public string DotKind => Status switch
    {
        ServerStatus.Connected => "running",
        ServerStatus.Connecting => "paused",
        ServerStatus.Failed => "failing",
        _ => "stopped",
    };

    public bool CanRemove => !Host.FromSshConfig;

    /// <summary>For the tree's style; a machine has nothing under it.</summary>
    public bool IsExpanded { get; set; }

    [RelayCommand]
    void Open() => explorer.Open(this);

    [RelayCommand]
    void OpenTab(string tab) => explorer.Open(this, tab);

    [RelayCommand]
    Task OpenRemoteFolder() => explorer.OpenRemoteFolderAsync(this);

    [RelayCommand]
    Task Connect() => explorer.ConnectAsync(this);

    [RelayCommand]
    Task ConnectAgain() => explorer.ConnectAgainAsync(this);

    [RelayCommand]
    void Disconnect() => explorer.Disconnect(this);

    [RelayCommand]
    void ToggleProduction() => explorer.SetProduction(this, !IsProduction);

    [RelayCommand]
    Task Edit() => explorer.EditAsync(this);

    [RelayCommand]
    Task Remove() => explorer.RemoveAsync(this);

    [RelayCommand]
    Task ForgetHostKey() => explorer.ForgetHostKeyAsync(this);

    [RelayCommand]
    void ForgetPasswords() => explorer.ForgetPasswords(this);
}

/// <summary>A group of machines: the user's (Work, Home...), or where they come from.</summary>
public sealed partial class ServerGroupNode(string name) : ObservableObject
{
    public string Name { get; } = name;

    public ObservableCollection<ServerNode> Servers { get; } = [];

    [ObservableProperty]
    public partial bool IsExpanded { get; set; } = true;

    [ObservableProperty]
    public partial int Count { get; set; }
}

/// <summary>A machine's page in the document well (Overview, Terminal, Files, Services, Logs).</summary>
public sealed class ServerDocument(ServerPageViewModel page) : DocumentTab
{
    public ServerPageViewModel Page { get; } = page;

    public override string Key => "server:" + Page.Node.Name;

    public override string Title => Page.Node.Name;

    public override string ToolTip => Page.Node.Host.Target;

    public override void Dispose() => Page.Dispose();
}

/// <summary>
/// The Servers tool window: the machines of ~/.ssh/config and those added in Karvon, in groups, with whether Karvon is connected
/// to each and how fast it answers. A machine opens as a page (its state, a terminal, its files, services and logs); a
/// production machine asks before every change. Connections ask for passwords and new host keys through dialogs over the
/// window.
/// </summary>
public sealed partial class ServersExplorerViewModel : ObservableObject, IServerPrompts, IDisposable
{
    readonly Dictionary<string, ServerNode> _nodes = [];
    readonly Dictionary<string, (ServerHost Host, string Remote, DateTime? Modified)> _remoteCopies = new(StringComparer.Ordinal);

    public ServersExplorerViewModel(IServersHost host, Bengine.Introspection.Servers.ICredentialStore? keyring = null)
    {
        Host = host;
        List = new ServerList(Path.Combine(host.ConfigDirectory, "servers.json"));
        Keyring = keyring;
        Connections = new SshConnections(Path.Combine(host.ConfigDirectory, "ssh_known_hosts.json"), this, FindHost, keyring);
        Connections.Changed += name => Dispatcher.UIThread.Post(() => UpdateStatus(name));
        Refresh();
    }

    internal IServersHost Host { get; }

    internal ServerList List { get; }

    internal SshConnections Connections { get; }

    internal Bengine.Introspection.Servers.ICredentialStore? Keyring { get; }

    public ObservableCollection<ServerGroupNode> Groups { get; } = [];

    public IEnumerable<ServerNode> Servers => _nodes.Values;

    public bool IsEmpty => _nodes.Count == 0;

    [ObservableProperty]
    public partial string Filter { get; set; } = "";

    partial void OnFilterChanged(string value) => Regroup();

    /// <summary>The dialog over the window (a password, a host key, the server form, a confirmation), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDialog))]
    public partial OverlayDialog? Dialog { get; set; }

    public bool HasDialog => Dialog is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    public bool HasError => Error is not null;

    [RelayCommand]
    void DismissError() => Error = null;

    internal ServerHost? FindHost(string name) => _nodes.TryGetValue(name, out var node) ? node.Host : null;

    internal ServerNode? Find(string name) => _nodes.Values.FirstOrDefault(n => n.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Reads ~/.ssh/config and Karvon's list again; the machines keep their connections.</summary>
    [RelayCommand]
    public void Refresh()
    {
        var hosts = List.Hosts();
        foreach (var gone in _nodes.Keys.Where(k => hosts.All(h => h.Name != k)).ToList())
        {
            Connections.Drop(gone);
            _nodes.Remove(gone);
        }
        foreach (var host in hosts)
            if (_nodes.TryGetValue(host.Name, out var node))
                node.Host = host;
            else
                _nodes[host.Name] = new ServerNode(this, host);
        Regroup();
        OnPropertyChanged(nameof(IsEmpty));
    }

    void Regroup()
    {
        var filter = Filter.Trim();
        var shown = _nodes.Values.Where(n => filter.Length == 0 || n.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || n.Host.Address.Contains(filter, StringComparison.OrdinalIgnoreCase));
        var groups = shown.GroupBy(n => n.Host.Group is { Length: > 0 } g ? g : n.Host.FromSshConfig ? "SSH config" : "Servers")
            .OrderBy(g => g.Key is "SSH config" or "Servers" ? 1 : 0).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ToList();
        var nodes = new List<ServerGroupNode>();
        foreach (var group in groups)
        {
            var node = Groups.FirstOrDefault(g => g.Name == group.Key) ?? new ServerGroupNode(group.Key);
            ObservableSync.Sync(node.Servers, group.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToList());
            node.Count = node.Servers.Count;
            nodes.Add(node);
        }
        ObservableSync.Sync(Groups, nodes);
    }

    [RelayCommand]
    void CollapseAll()
    {
        foreach (var group in Groups)
            group.IsExpanded = false;
    }

    void UpdateStatus(string name)
    {
        if (!_nodes.TryGetValue(name, out var node))
            return;
        var connected = Connections.IsConnected(name);
        if (connected)
        {
            node.Status = ServerStatus.Connected;
            node.Error = null;
        }
        else if (node.Status == ServerStatus.Connected)
            node.Status = ServerStatus.NotConnected;
        node.Latency = Connections.Latency(name);
    }

    // ---- Connections ------------------------------------------------------------------------------------------------

    /// <summary>Connects when it is not (asking for a password or a host key as needed); false when it could not.</summary>
    internal async Task<bool> ConnectAsync(ServerNode node, CancellationToken cancellationToken = default)
    {
        if (Connections.IsConnected(node.Name))
            return true;
        node.Status = ServerStatus.Connecting;
        try
        {
            await Connections.ClientAsync(node.Host, cancellationToken);
            UpdateStatus(node.Name);
            return true;
        }
        catch (ServerException ex)
        {
            node.Error = ex.Message;
            node.Status = ServerStatus.Failed;
            return false;
        }
        catch (OperationCanceledException)
        {
            node.Status = ServerStatus.NotConnected;
            return false;
        }
    }

    internal Task ConnectAgainAsync(ServerNode node)
    {
        Connections.Drop(node.Name);
        return ConnectAsync(node);
    }

    internal void Disconnect(ServerNode node)
    {
        Connections.Drop(node.Name);
        node.Status = ServerStatus.NotConnected;
    }

    async Task<(string Secret, bool Save)?> IServerPrompts.SecretAsync(ServerHost host, string question)
    {
        var dialog = await Dispatcher.UIThread.InvokeAsync(() => ShowAsync(new SecretDialog($"Sign in to {host.Name}", question, Keyring is not null)));
        return dialog is { } d && d.Secret.Length > 0 ? (d.Secret, d.Save) : null;
    }

    async Task<bool> IServerPrompts.TrustAsync(ServerHost host, string endpoint, string keyType, string fingerprint) =>
        await Dispatcher.UIThread.InvokeAsync(() => ShowAsync(new TrustDialog(host.Name, endpoint, keyType, fingerprint))) is not null;

    internal async Task<T?> ShowAsync<T>(T dialog) where T : OverlayDialog
    {
        // One at a time: a second machine asking waits for the first dialog.
        while (Dialog is { } open)
            await open.Result;
        Dialog = dialog;
        try
        {
            return await dialog.Result ? dialog : null;
        }
        finally
        {
            if (Dialog == dialog)
                Dialog = null;
        }
    }

    /// <summary>
    /// Open Remote Folder: a folder of the machine (its home when none is given, ~/ in front of one under it) copied here
    /// and opened as the workspace.
    /// </summary>
    internal async Task OpenRemoteFolderAsync(ServerNode node)
    {
        if (await ShowAsync(new InputDialog($"Open a folder of {node.Name}", "Folder", "/srv/app, or ~/projects/api (empty: the home folder)", "Open")) is not { } dialog
            || !await ConnectAsync(node))
            return;
        var path = dialog.Text.Trim();
        if (path.Length == 0 || path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || !path.StartsWith('/'))
        {
            var home = (await Connections.SftpAsync(node.Host)).WorkingDirectory.TrimEnd('/');
            path = path is "" or "~" ? home : home + "/" + (path.StartsWith("~/", StringComparison.Ordinal) ? path[2..] : path);
        }
        await Host.OpenRemoteFolderAsync(node.Host, path);
    }

    internal async Task<bool> ConfirmAsync(string title, string message, string confirm, bool danger = false) =>
        await ShowAsync(new ConfirmDialog(title, message, confirm, danger)) is not null;

    // ---- Machines -------------------------------------------------------------------------------------------------

    [RelayCommand]
    async Task AddServer()
    {
        if (await ShowAsync(new ServerFormDialog(null, GroupNames())) is not { } form)
            return;
        var name = form.Name.Trim();
        if (_nodes.ContainsKey(name))
        {
            Error = $"There is a server named {name} already.";
            return;
        }
        List.Update(name, s => Fill(s, form));
        Refresh();
        if (Find(name) is { } node)
            Open(node);
    }

    internal async Task EditAsync(ServerNode node)
    {
        if (await ShowAsync(new ServerFormDialog(node.Host, GroupNames())) is not { } form)
            return;
        var name = form.Name.Trim();
        if (!node.Host.FromSshConfig && name != node.Name)
        {
            List.Remove(node.Name);
            Connections.Drop(node.Name);
        }
        List.Update(node.Host.FromSshConfig ? node.Name : name, s =>
        {
            if (node.Host.FromSshConfig)
                (s.Group, s.Production) = (Blank(form.Group), form.Production);
            else
                Fill(s, form);
        });
        if (!node.Host.FromSshConfig)
            Connections.Drop(node.Name);
        Refresh();
    }

    static void Fill(ServerSettings settings, ServerFormDialog form)
    {
        settings.Address = form.Address.Trim();
        settings.Port = int.Parse(form.Port, System.Globalization.CultureInfo.InvariantCulture);
        settings.User = form.User.Trim();
        settings.IdentityFile = Blank(form.IdentityFile);
        settings.Group = Blank(form.Group);
        settings.Production = form.Production;
    }

    static string? Blank(string text) => text.Trim() is { Length: > 0 } t ? t : null;

    IReadOnlyList<string> GroupNames() => _nodes.Values.Select(n => n.Host.Group).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList();

    internal void SetProduction(ServerNode node, bool production)
    {
        List.Update(node.Name, s => s.Production = production);
        Refresh();
    }

    internal async Task RemoveAsync(ServerNode node)
    {
        if (node.Host.FromSshConfig)
        {
            Error = $"{node.Name} is in ~/.ssh/config: remove it there.";
            return;
        }
        if (!await ConfirmAsync($"Remove {node.Name}?", $"It goes from Karvon's list ({node.Target}); nothing changes on the machine.", "Remove", danger: true))
            return;
        foreach (var document in Host.Documents.Where(d => d.Page.Node == node).ToList())
            Host.CloseDocument(document);
        Connections.ForgetSecrets(node.Host);
        List.Remove(node.Name);
        Refresh();
    }

    internal async Task ForgetHostKeyAsync(ServerNode node)
    {
        if (!await ConfirmAsync($"Forget {node.Name}'s host key?", "The next connection shows the machine's key again, to be trusted (after a reinstall, for one).", "Forget"))
            return;
        Connections.ForgetHostKey(node.Host);
        Disconnect(node);
    }

    internal void ForgetPasswords(ServerNode node)
    {
        Connections.ForgetSecrets(node.Host);
        Host.Status($"Karvon forgot the passwords of {node.Name}.");
    }

    // ---- Pages --------------------------------------------------------------------------------------------------

    /// <summary>The machine's page (connecting to it), on a tab: "overview", "terminal", "files", "services", "logs".</summary>
    public ServerDocument Open(ServerNode node, string? tab = null)
    {
        var document = Host.Documents.FirstOrDefault(d => d.Page.Node == node) ?? new ServerDocument(new ServerPageViewModel(this, node));
        Host.ShowDocument(document);
        if (tab is not null)
            document.Page.Tab = tab;
        _ = document.Page.ActivateAsync();
        return document;
    }

    // ---- Remote files in the editor -----------------------------------------------------------------------------

    /// <summary>Where a machine's file is copied to be opened in the editor: &lt;config&gt;/remote/&lt;machine&gt;/&lt;its path&gt;.</summary>
    internal string LocalCopyOf(ServerHost host, string remote) =>
        Path.Combine(Host.ConfigDirectory, "remote", host.Name, remote.TrimStart('/').Replace("..", "_", StringComparison.Ordinal));

    internal void RememberCopy(string local, ServerHost host, string remote, DateTime? modified) => _remoteCopies[local] = (host, remote, modified);

    /// <summary>
    /// A copy of a machine's file was saved in the editor: it goes back to the machine. When the file changed there since it
    /// was opened, the user is asked first.
    /// </summary>
    public async Task OnFileSavedAsync(string local)
    {
        if (!_remoteCopies.TryGetValue(local, out var copy))
            return;
        try
        {
            var sftp = await Connections.SftpAsync(copy.Host);
            var now = sftp.Exists(copy.Remote) ? sftp.GetLastWriteTimeUtc(copy.Remote) : (DateTime?)null;
            if (copy.Modified is { } then && now is { } changed && changed > then
                && !await ConfirmAsync($"{Path.GetFileName(copy.Remote)} changed on {copy.Host.Name}", $"{copy.Remote} was changed there since you opened it. Overwrite it with yours?", "Overwrite", danger: true))
                return;
            await using (var stream = File.OpenRead(local))
                await sftp.UploadFileAsync(stream, copy.Remote);
            _remoteCopies[local] = copy with { Modified = sftp.GetLastWriteTimeUtc(copy.Remote) };
            Host.Status($"Saved {copy.Remote} on {copy.Host.Name}.");
        }
        catch (Exception ex) when (ex is ServerException or Renci.SshNet.Common.SshException or IOException)
        {
            Error = $"{copy.Remote} was not saved on {copy.Host.Name}: {ex.Message} Your changes are in {local}.";
        }
    }

    public void Dispose()
    {
        Dialog?.Cancel();
        Connections.Dispose();
    }
}
