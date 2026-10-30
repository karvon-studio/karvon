using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Karvon.Claude;
using Karvon.ViewModels;
using Karvon.Ui;

namespace Karvon.Mcp.ViewModels;

/// <summary>What the MCP Servers explorer asks of Karvon: its folders, Karvon's own tools, the document well, Claude.</summary>
public interface IMcpHost
{
    /// <summary>Karvon's settings folder: its own servers, and which ones the Claude Code sessions get.</summary>
    string ConfigDirectory { get; }

    /// <summary>The solution's folder (its .mcp.json, Claude Code's servers for it), or null.</summary>
    string? Workspace { get; }

    /// <summary>The Claude Code account's settings file; null for ~/.claude.json.</summary>
    string? ClaudeFile { get; }

    /// <summary>The tools Karvon gives the Claude Code sessions it starts; empty when they are off.</summary>
    IReadOnlyList<IClaudeTool> BuiltInTools { get; }

    /// <summary>Whether the sessions Karvon starts use its tools before the shell (a setting, for the sessions started after).</summary>
    bool KarvonToolsFirst { get; set; }

    /// <summary>Whether Claude Code's own tools are kept away from the secrets on the machine (a setting, for the sessions started after).</summary>
    bool KeepSecretsAway { get => true; set { } }

    /// <summary>Whether Claude Code's shell runs in its sandbox (a setting, for the sessions started after).</summary>
    bool Sandbox { get => false; set { } }

    /// <summary>Why the sandbox cannot start on this machine; null when nothing stops it.</summary>
    string? SandboxProblem => null;

    /// <summary>Whether what Claude Code sends the model goes through Karvon's gateway, which hides secrets (for the sessions started after).</summary>
    bool Gateway { get => true; set { } }

    /// <summary>The user's rule for one of Karvon's tools ("allow", "ask", "deny"); null for none (as its access says).</summary>
    string? ToolRule(string tool) => null;

    /// <summary>Sets it; null takes it away. For the calls from now on.</summary>
    void SetToolRule(string tool, string? rule)
    {
    }

    IEnumerable<DocumentTab> Documents { get; }

    void ShowDocument(DocumentTab document);

    void CloseDocument(DocumentTab document);

    void AskClaude(string prompt);

    /// <summary>A server's sign-in page, in the browser.</summary>
    void OpenUrl(string url);

    /// <summary>Claude Code signs in to one of its servers (the apps a server lets in may be Claude Code and not Karvon).</summary>
    Task<string> SignInWithClaudeAsync(string name);

    /// <summary>Where the servers' sign-in tokens are kept; null for this session only.</summary>
    Bengine.Introspection.Servers.ICredentialStore? Keyring { get; }

    /// <summary>A line in the status bar ("Saved in ...").</summary>
    void Status(string text);
}

/// <summary>A row of the MCP Servers tree.</summary>
public abstract partial class McpTreeNode : ObservableObject
{
    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    partial void OnIsExpandedChanged(bool value) => Expanded(value);

    protected virtual void Expanded(bool value)
    {
    }
}

/// <summary>Where servers are defined (Claude Code, Codex, this folder's .mcp.json...), or Karvon's own tools.</summary>
public sealed partial class McpGroupNode : McpTreeNode
{
    readonly McpExplorerViewModel _explorer;

    public McpGroupNode(McpExplorerViewModel explorer, string title, string toolTip, McpOrigin? origin)
    {
        (_explorer, Title, ToolTip, Origin) = (explorer, title, toolTip, origin);
        IsExpanded = true;
    }

    public string Title { get; }

    /// <summary>The file it is read from.</summary>
    public string ToolTip { get; }

    /// <summary>Null for Karvon's own tools.</summary>
    public McpOrigin? Origin { get; }

    public bool IsBuiltIn => Origin is null;

    public ObservableCollection<McpTreeNode> Items { get; } = [];

    [ObservableProperty]
    public partial string CountText { get; set; } = "";

    [RelayCommand]
    Task AddServer() => _explorer.AddServerAsync(Origin);

    [RelayCommand]
    void Open()
    {
        if (IsBuiltIn)
            _explorer.OpenBuiltIn();
    }
}

/// <summary>A server: where it runs (a command, a URL), whether Karvon is connected to it, and what it offers once it is.</summary>
public sealed partial class McpServerNode : McpTreeNode
{
    readonly McpExplorerViewModel _explorer;

    public McpServerNode(McpExplorerViewModel explorer, McpServerDefinition definition, string? workingDirectory)
    {
        _explorer = explorer;
        Connection = new McpConnection(definition, workingDirectory, explorer.SignIn);
        Definition = definition;
        Connection.Changed += () => Dispatcher.UIThread.Post(Update);
        Update();
    }

    public McpConnection Connection { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name), nameof(Transport), nameof(ToolTip), nameof(DefinedIn))]
    public partial McpServerDefinition Definition { get; set; }

    partial void OnDefinitionChanged(McpServerDefinition value) => Connection.Server = value;

    public string Name => Definition.Name;

    public string Transport => Definition.Transport;

    public string ToolTip => Transport + (Connection.Error is { } error ? "\n" + error : "");

    public string DefinedIn => $"Defined in {McpConfigs.Describe(Definition.Origin)}: {_explorer.Configs.FileOf(Definition.Origin)}";

    public string StatusText => Connection.StatusText;

    public bool IsConnected => Connection.IsConnected;

    /// <summary>The dot: green connected, amber connecting or signing in, red failed, gray not connected.</summary>
    public string DotKind => Connection.Status switch
    {
        McpStatus.Connected => "running",
        McpStatus.Connecting or McpStatus.NeedsSignIn => "paused",
        McpStatus.Failed => "failing",
        _ => "stopped",
    };

    public bool HasFailed => Connection.Status is McpStatus.Failed or McpStatus.NeedsSignIn;

    public bool NeedsSignIn => Connection.Status == McpStatus.NeedsSignIn;

    /// <summary>A URL: it can be signed in to (again) from the tree.</summary>
    public bool CanSignIn => Connection.CanSignIn && !Connection.IsSigningIn;

    public bool IsSignedIn => Connection.IsSignedIn;

    /// <summary>One of Claude Code's own servers, at a URL: Claude Code can sign in to it (when Karvon may not).</summary>
    public bool CanSignInWithClaude => McpConfigs.ClaudeReadsItself(Definition.Origin) && Definition.IsHttp && HasFailed;

    /// <summary>Its tools, resources and prompts once connected; before, a row that says how it is (and lets it expand).</summary>
    public ObservableCollection<McpTreeNode> Children { get; } = [];

    internal McpExplorerViewModel Explorer => _explorer;

    protected override void Expanded(bool value)
    {
        if (value)
            _ = _explorer.ExpandAsync(this);
    }

    internal void Update()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(DotKind));
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(HasFailed));
        OnPropertyChanged(nameof(NeedsSignIn));
        OnPropertyChanged(nameof(CanSignIn));
        OnPropertyChanged(nameof(IsSignedIn));
        OnPropertyChanged(nameof(CanSignInWithClaude));
        OnPropertyChanged(nameof(ToolTip));
        var rows = new List<McpTreeNode>();
        if (Connection.IsConnected)
        {
            rows.AddRange(Connection.Tools.Select(t => new McpItemNode(this, "tool", t.Name, t.Description, t.Description)));
            rows.AddRange(Connection.Resources.Select(r => new McpItemNode(this, "resource", r.Name, r.Uri, r.Uri)));
            rows.AddRange(Connection.Prompts.Select(p => new McpItemNode(this, "prompt", p.Name, p.Description, p.Description)));
        }
        if (rows.Count == 0)
            rows.Add(new McpItemNode(this, "status", Connection.IsConnected ? "It offers nothing" : Connection.Status == McpStatus.NotConnected ? "Expand to connect" : StatusText,
                null, Connection.Error));
        Children.Clear();
        foreach (var row in rows)
            Children.Add(row);
    }

    [RelayCommand]
    void Open() => _explorer.Open(this);

    [RelayCommand]
    Task ConnectAgain() => _explorer.ConnectAsync(this);

    [RelayCommand]
    Task Disconnect() => Connection.DisconnectAsync();

    [RelayCommand]
    Task SignIn() => Connection.SignInAsync();

    [RelayCommand]
    Task SignOut() => Connection.SignOutAsync();

    [RelayCommand]
    async Task SignInWithClaude() => _explorer.Host.Status(await _explorer.Host.SignInWithClaudeAsync(Name));

    [RelayCommand]
    Task Edit() => _explorer.EditAsync(this);

    [RelayCommand]
    Task Remove() => _explorer.RemoveAsync(this);

    [RelayCommand]
    void AskClaude() => _explorer.AskClaude(this);
}

/// <summary>A tool, a resource or a prompt of a server (or a row saying it is not connected).</summary>
public sealed partial class McpItemNode(McpServerNode server, string kind, string name, string? detail, string? toolTip) : McpTreeNode
{
    public McpServerNode Server { get; } = server;

    /// <summary>"tool", "resource", "prompt", or "status".</summary>
    public string Kind { get; } = kind;

    public string Name { get; } = name;

    public string Detail { get; } = kind == "status" ? "" : kind;

    public string? ToolTip { get; } = toolTip ?? detail;

    public bool IsStatus => Kind == "status";

    public string Icon => Kind switch { "tool" => "Method", "resource" => "file", "prompt" => "Snippet", _ => "Info" };

    [RelayCommand]
    void Open() => Server.Explorer.Open(Server, IsStatus ? null : Kind, IsStatus ? null : Name);
}

/// <summary>One of Karvon's own tools: its name, how much it may change, and what it does.</summary>
public sealed class McpBuiltInToolNode(McpExplorerViewModel explorer, IClaudeTool tool) : McpTreeNode
{
    public string Name { get; } = tool.Name;

    /// <summary>The rules a tool can have, as the list shows them; the first is none.</summary>
    public static IReadOnlyList<string> Rules { get; } = ["As its access says", "Allow", "Ask", "Deny"];

    /// <summary>The user's rule for it, enforced by Karvon's tool server whatever Claude Code's permission mode.</summary>
    public string Rule
    {
        get => ClaudeToolPolicy.Parse(explorer.Host.ToolRule(Name)) switch
        {
            Karvon.Claude.ToolRule.Allow => "Allow",
            Karvon.Claude.ToolRule.Ask => "Ask",
            Karvon.Claude.ToolRule.Deny => "Deny",
            _ => Rules[0],
        };
        set
        {
            if (value == Rule)
                return;
            explorer.Host.SetToolRule(Name, value is "Allow" or "Ask" or "Deny" ? value.ToLowerInvariant() : null);
            OnPropertyChanged();
        }
    }

    public string Access { get; } = AccessOf(tool);

    public string Description { get; } = tool.Description;

    public IRelayCommand OpenCommand { get; } = new RelayCommand(explorer.OpenBuiltIn);

    /// <summary>What a call does without arguments ("reads", "asks"); some depend on them.</summary>
    static string AccessOf(IClaudeTool tool)
    {
        try
        {
            using var none = JsonDocument.Parse("{}");
            return tool.Plan(none.RootElement).Access switch
            {
                ToolAccess.Read => "reads",
                ToolAccess.Run => "runs",
                ToolAccess.Write => "changes, asks",
                _ => "destructive, asks",
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or JsonException or ArgumentException or FormatException)
        {
            return "per call";
        }
    }
}

/// <summary>A configuration that could not be read, and why.</summary>
public sealed class McpProblemNode(string text) : McpTreeNode
{
    public string Text { get; } = text;
}

/// <summary>A server's page in the document well (Tools, Resources, Prompts, Log).</summary>
public sealed class McpServerDocument(McpServerPageViewModel page) : DocumentTab
{
    public McpServerPageViewModel Page { get; } = page;

    public override string Key => "mcp:" + Page.Node.Definition.Key;

    public override string Title => Page.Node.Name;

    public override string ToolTip => Page.Node.Transport;

    public override void Dispose() => Page.Dispose();
}

/// <summary>Karvon's own tools, as the Claude Code sessions Karvon starts have them.</summary>
public sealed class McpBuiltInDocument(IReadOnlyList<McpBuiltInToolNode> tools, IMcpHost host) : DocumentTab
{
    public IReadOnlyList<McpBuiltInToolNode> Tools { get; } = tools;

    public bool IsOff => Tools.Count == 0;

    /// <summary>
    /// The sessions use these before the shell: their system prompt says so, and a Bash command one of them does is sent to it
    /// once. For the sessions started after.
    /// </summary>
    public bool ToolsFirst
    {
        get => host.KarvonToolsFirst;
        set
        {
            if (host.KarvonToolsFirst == value)
                return;
            host.KarvonToolsFirst = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Claude Code's own tools kept from the files that hold secrets and the secret stores. For the sessions started after.</summary>
    public bool KeepSecretsAway
    {
        get => host.KeepSecretsAway;
        set
        {
            if (host.KeepSecretsAway == value)
                return;
            host.KeepSecretsAway = value;
            OnPropertyChanged();
        }
    }

    /// <summary>What Claude Code sends the model through Karvon's gateway, its secrets hidden. For the sessions started after.</summary>
    public bool Gateway
    {
        get => host.Gateway;
        set
        {
            if (host.Gateway == value)
                return;
            host.Gateway = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Claude Code's shell in its sandbox. For the sessions started after.</summary>
    public bool Sandbox
    {
        get => host.Sandbox;
        set
        {
            if (host.Sandbox == value)
                return;
            host.Sandbox = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SandboxProblem));
        }
    }

    /// <summary>Why the sandbox, turned on, cannot start here.</summary>
    public string? SandboxProblem => host.Sandbox ? host.SandboxProblem : null;

    public override string Key => "mcp:" + KarvonToolNames.Server;

    public override string Title => KarvonToolNames.Server;

    public override string ToolTip => "Karvon's tools for Claude Code";
}

/// <summary>
/// The MCP Servers tool window: the servers of every agent's configuration (this folder's .mcp.json, Claude Code's,
/// Codex's, Gemini CLI's) and Karvon's own, in groups, with Karvon's built-in tools first. A server connects when it is
/// expanded or opened, never at start; its page runs its tools with a form made from their schema, reads its resources
/// and prompts, and logs all that is said. Servers are added and changed in their file's own format.
/// </summary>
public sealed partial class McpExplorerViewModel : ObservableObject, IDisposable
{
    readonly Dictionary<string, McpServerNode> _nodes = new(StringComparer.Ordinal);
    IReadOnlyList<string> _problems = [];

    public McpExplorerViewModel(IMcpHost host)
    {
        Host = host;
        SignIn = new McpSignIn(uri => host.OpenUrl(uri.ToString()), host.Keyring);
        Configs = new McpConfigs(host.ConfigDirectory, host.Workspace, host.ClaudeFile);
        Refresh();
    }

    internal IMcpHost Host { get; }

    /// <summary>The browser and the keyring the servers sign in with.</summary>
    internal McpSignIn SignIn { get; }

    internal McpConfigs Configs { get; private set; }

    public ObservableCollection<McpTreeNode> Roots { get; } = [];

    public IEnumerable<McpServerNode> Servers => _nodes.Values;

    public bool IsEmpty => _nodes.Count == 0;

    [ObservableProperty]
    public partial string Filter { get; set; } = "";

    partial void OnFilterChanged(string value) => Regroup();

    /// <summary>The dialog over the window (the server form, a confirmation), or null.</summary>
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

    internal McpServerNode? Find(string name, McpOrigin? origin = null) =>
        _nodes.Values.FirstOrDefault(n => n.Name == name && (origin is null || n.Definition.Origin == origin));

    /// <summary>Reads every configuration again (another folder, another account, a file changed); connections stay.</summary>
    [RelayCommand]
    public void Refresh()
    {
        var before = Configs;
        Configs = new McpConfigs(Host.ConfigDirectory, Host.Workspace, Host.ClaudeFile);
        var read = Configs.Read();
        _problems = read.Problems;
        var keys = read.Servers.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        // Another folder's "echo" (or another account's) is another server, under the same key.
        bool Moved(McpServerNode n) => McpConfigs.IsInWorkspace(n.Definition.Origin) && before.Workspace != Configs.Workspace
            || n.Definition.Origin is McpOrigin.ClaudeUser or McpOrigin.ClaudeLocal && before.ClaudeFile != Configs.ClaudeFile;
        foreach (var gone in _nodes.Values.Where(n => !keys.Contains(n.Definition.Key) || Moved(n)).ToList())
        {
            foreach (var document in Host.Documents.OfType<McpServerDocument>().Where(d => d.Page.Node == gone).ToList())
                Host.CloseDocument(document);
            _nodes.Remove(gone.Definition.Key);
            _ = gone.Connection.DisposeAsync().AsTask();
        }
        foreach (var server in read.Servers)
            if (_nodes.TryGetValue(server.Key, out var node))
                node.Definition = server;
            else
                _nodes.TryAdd(server.Key, new McpServerNode(this, server, Configs.Workspace ?? McpConfigs.Home));
        Regroup();
        OnPropertyChanged(nameof(IsEmpty));
    }

    void Regroup()
    {
        var filter = Filter.Trim();
        var roots = new List<McpTreeNode>();
        var builtIn = Roots.OfType<McpGroupNode>().FirstOrDefault(g => g.IsBuiltIn)
            ?? new McpGroupNode(this, "Karvon (built in)", $"The tools Karvon gives the Claude Code sessions it starts ({KarvonToolNames.Prefix}…)", null);
        var all = Host.BuiltInTools;
        var tools = all.Where(t => filter.Length == 0 || t.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Select(t => builtIn.Items.OfType<McpBuiltInToolNode>().FirstOrDefault(n => n.Name == t.Name) ?? new McpBuiltInToolNode(this, t))
            .Cast<McpTreeNode>().ToList();
        ObservableSync.Sync(builtIn.Items, tools);
        builtIn.CountText = all.Count == 0 ? "off" : $"{all.Count} tools";
        if (filter.Length == 0 || tools.Count > 0)
            roots.Add(builtIn);
        foreach (var group in _nodes.Values.Where(n => filter.Length == 0 || n.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                     .GroupBy(n => n.Definition.Origin).OrderBy(g => g.Key))
        {
            var node = Roots.OfType<McpGroupNode>().FirstOrDefault(g => g.Origin == group.Key)
                ?? new McpGroupNode(this, McpConfigs.Describe(group.Key), Configs.FileOf(group.Key), group.Key);
            ObservableSync.Sync(node.Items, group.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase).Cast<McpTreeNode>().ToList());
            var count = node.Items.Count;
            node.CountText = count == 1 ? "1 server" : $"{count} servers";
            roots.Add(node);
        }
        roots.AddRange(_problems.Select(p => Roots.OfType<McpProblemNode>().FirstOrDefault(n => n.Text == p) ?? new McpProblemNode(p)));
        ObservableSync.Sync(Roots, roots);
    }

    [RelayCommand]
    void CollapseAll()
    {
        foreach (var node in Roots)
            node.IsExpanded = false;
    }

    // ---- Connections -------------------------------------------------------------------------------------------

    /// <summary>A server opened in the tree: connected once (a failed one waits for Connect Again).</summary>
    internal Task ExpandAsync(McpServerNode node) =>
        node.Connection.Status == McpStatus.NotConnected ? ConnectAsync(node) : Task.CompletedTask;

    internal Task ConnectAsync(McpServerNode node) => node.Connection.ConnectAsync();

    [RelayCommand]
    Task ConnectAll() => Task.WhenAll(_nodes.Values.Where(n => !n.IsConnected).Select(ConnectAsync));

    // ---- Pages -------------------------------------------------------------------------------------------------

    /// <summary>The server's page (connecting to it), with a tool, a resource or a prompt chosen.</summary>
    public McpServerDocument Open(McpServerNode node, string? kind = null, string? name = null)
    {
        var document = Host.Documents.OfType<McpServerDocument>().FirstOrDefault(d => d.Page.Node == node)
            ?? new McpServerDocument(new McpServerPageViewModel(this, node));
        Host.ShowDocument(document);
        document.Page.Select(kind, name);
        _ = ExpandAsync(node);
        return document;
    }

    internal void OpenBuiltIn()
    {
        var document = Host.Documents.OfType<McpBuiltInDocument>().FirstOrDefault()
            ?? new McpBuiltInDocument([.. Host.BuiltInTools.Select(t => new McpBuiltInToolNode(this, t))], Host);
        Host.ShowDocument(document);
    }

    internal void AskClaude(McpServerNode node)
    {
        var log = node.Connection.LogText.Split('\n').TakeLast(40);
        Host.AskClaude($"The MCP server {node.Name} ({node.Transport}) is {node.StatusText}. It is defined in {Configs.FileOf(node.Definition.Origin)}"
            + $" ({McpConfigs.Describe(node.Definition.Origin)}). Find out why and fix its configuration. Karvon's log of it:\n\n```\n{string.Join("\n", log)}\n```");
    }

    // ---- Servers -----------------------------------------------------------------------------------------------

    /// <summary>Where a new server can go: this folder's .mcp.json, each agent's own file, or Karvon's.</summary>
    IReadOnlyList<McpWhere> Places() =>
    [
        .. Configs.Workspace is null ? [] : new[] { new McpWhere(McpOrigin.Project, "This folder: .mcp.json, which Claude Code (and other agents) read here", Configs.FileOf(McpOrigin.Project)) },
        new McpWhere(McpOrigin.ClaudeUser, "Claude Code, every project", Configs.FileOf(McpOrigin.ClaudeUser)),
        new McpWhere(McpOrigin.Codex, "Codex", Configs.FileOf(McpOrigin.Codex)),
        new McpWhere(McpOrigin.GeminiUser, "Gemini CLI", Configs.FileOf(McpOrigin.GeminiUser)),
        new McpWhere(McpOrigin.Karvon, "Karvon: the Claude Code sessions Karvon starts", Configs.FileOf(McpOrigin.Karvon)),
    ];

    [RelayCommand]
    Task AddServer() => AddServerAsync(null);

    internal async Task AddServerAsync(McpOrigin? origin)
    {
        var places = Places();
        var form = new McpServerFormDialog(null, places, origin is { } o ? places.FirstOrDefault(p => p.Origin == o) : null, (name, where) => Find(name, where) is null);
        if (await ShowAsync(form) is null)
            return;
        var server = form.ToDefinition(form.Where.Origin);
        Save(server, null);
        if (Find(server.Name, server.Origin) is { } node)
            Open(node);
    }

    internal async Task EditAsync(McpServerNode node)
    {
        var old = node.Definition;
        var form = new McpServerFormDialog(old, [new McpWhere(old.Origin, McpConfigs.Describe(old.Origin), Configs.FileOf(old.Origin))], null,
            (name, where) => name == old.Name || Find(name, where) is null);
        if (await ShowAsync(form) is null)
            return;
        var server = form.ToDefinition(old.Origin);
        Save(server, old.Name);
        if (server.Name != old.Name && Find(server.Name, server.Origin) is { } renamed)
            Open(renamed);
        else
            await ConnectAsync(node);
    }

    void Save(McpServerDefinition server, string? replacing)
    {
        try
        {
            var file = Configs.Write(server, replacing);
            Host.Status($"Saved {server.Name} in {file}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or Tomlyn.TomlException)
        {
            Error = $"{server.Name} was not saved: {ex.Message}";
        }
        Refresh();
    }

    internal async Task RemoveAsync(McpServerNode node)
    {
        var file = Configs.FileOf(node.Definition.Origin);
        if (!await ConfirmAsync($"Remove {node.Name}?", $"It goes from {file} ({McpConfigs.Describe(node.Definition.Origin)}): the agents that read it there do not have it any more.", "Remove", danger: true))
            return;
        try
        {
            Configs.Remove(node.Definition);
            Host.Status($"Removed {node.Name} from {file}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Error = $"{node.Name} was not removed: {ex.Message}";
        }
        Refresh();
    }

    internal void SetForClaude(McpServerNode node, bool on)
    {
        try
        {
            Configs.SetForClaude(node.Definition, on);
            Host.Status(on ? $"The Claude Code sessions Karvon starts get {node.Name} (a new session)." : $"The Claude Code sessions Karvon starts do not get {node.Name}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = ex.Message;
        }
    }

    // ---- Dialogs -----------------------------------------------------------------------------------------------

    internal async Task<T?> ShowAsync<T>(T dialog) where T : OverlayDialog
    {
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

    internal async Task<bool> ConfirmAsync(string title, string message, string confirm, bool danger = false) =>
        await ShowAsync(new ConfirmDialog(title, message, confirm, danger)) is not null;

    public void Dispose()
    {
        Dialog?.Cancel();
        foreach (var node in _nodes.Values)
            _ = node.Connection.DisposeAsync().AsTask();
    }
}
