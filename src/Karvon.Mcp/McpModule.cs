using System.Text.Json.Nodes;
using Avalonia.Controls.Templates;
using CommunityToolkit.Mvvm.ComponentModel;
using Karvon.Claude;
using Karvon.Mcp.ViewModels;
using Karvon.Mcp.Views;
using Karvon.Modules;
using Karvon.ViewModels;

namespace Karvon.Mcp;

/// <summary>
/// MCP Servers: the servers of every agent's configuration and Karvon's own (see <see cref="McpExplorerViewModel"/>), its
/// explorer made the first time the tool window shows. The Claude Code sessions Karvon starts get those switched on for them.
/// </summary>
public sealed partial class McpModule : KarvonModule, IMcpHost
{
    public const string ToolWindowId = "Mcp";

    IClaudeService? _claude;
    string? _claudeMcpFile;

    public override string Id => "mcp";

    public override string Name => "MCP Servers";


    public override string Icon => "folder-robot";

    public override string Description =>
        "The MCP servers of Claude Code, Codex, Gemini CLI and Karvon's own: their tools, resources and prompts to try, sign-in, "
        + "and which ones the Claude Code sessions get.";

    public override IReadOnlyList<ToolWindowInfo> ToolWindows =>
        [new(ToolWindowId, "MCP Servers", "_MCP Servers", "ToolMcp", DockSide.Right, () => new McpExplorerView { DataContext = EnsureExplorer() })];

    public override IReadOnlyList<IDataTemplate> DocumentTemplates =>
    [
        Document<McpServerDocument>(document => new McpServerPageView { DataContext = document.Page }),
        Document<McpBuiltInDocument>(_ => new McpBuiltInView()),
    ];

    public override IDataTemplate OverlayTemplate => Template<McpExplorerViewModel>(_ => new McpOverlay());

    /// <summary>The explorer, once the tool window has shown (no server is started before one is opened).</summary>
    [ObservableProperty]
    public partial McpExplorerViewModel? Explorer { get; private set; }

    public McpExplorerViewModel EnsureExplorer()
    {
        if (Explorer is null)
            Overlay = Explorer = new McpExplorerViewModel(this);
        return Explorer;
    }

    protected override void Started()
    {
        // Another folder has its own .mcp.json and its own servers in Claude Code's settings; another account its own .claude.json.
        Workbench.WorkspaceChanged += Refresh;
        _claude = Workbench.Service<IClaudeService>();
        if (_claude is not null)
        {
            _claude.Changed += Refresh;
            _claude.ExtraMcpServers = ClaudeMcpServers;
        }
    }

    void Refresh() => Explorer?.Refresh();

    /// <summary>Karvon closes: the servers it started stop.</summary>
    public override void Dispose()
    {
        Explorer?.Dispose();
        Overlay = Explorer = null;
        if (_claudeMcpFile is { } file && File.Exists(file))
            File.Delete(file);
    }

    /// <summary>
    /// The servers a Claude Code session gets beside its own (those of Codex, Gemini CLI and Karvon switched on for it),
    /// in a file of the user's alone (their environment may hold tokens); null when there are none.
    /// </summary>
    string? ClaudeMcpServers(string workingDirectory)
    {
        var servers = new McpConfigs(Services.Settings.ConfigDirectory, workingDirectory, _claude?.SettingsFile).ForClaude();
        var file = _claudeMcpFile ??= Path.Combine(Services.Settings.ConfigDirectory, "mcp", $"claude-{Environment.ProcessId}.json");
        if (servers.Count == 0)
        {
            if (File.Exists(file))
                File.Delete(file);
            return null;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.WriteAllText(file, new JsonObject { ["mcpServers"] = servers }.ToJsonString());
        return file;
    }

    // ---- IMcpHost -------------------------------------------------------------------------------------------------

    string IMcpHost.ConfigDirectory => Services.Settings.ConfigDirectory;

    string? IMcpHost.Workspace => Workbench.Workspace;

    string? IMcpHost.ClaudeFile => _claude?.SettingsFile;

    IReadOnlyList<IClaudeTool> IMcpHost.BuiltInTools => _claude?.BuiltInTools ?? [];

    IEnumerable<DocumentTab> IMcpHost.Documents => Workbench.Documents;

    void IMcpHost.ShowDocument(DocumentTab document) => Workbench.ShowDocument(document);

    void IMcpHost.CloseDocument(DocumentTab document) => Workbench.CloseDocument(document);

    void IMcpHost.AskClaude(string prompt) => _claude?.Ask(prompt);

    void IMcpHost.OpenUrl(string url) => Workbench.OpenUrl(url);

    Task<string> IMcpHost.SignInWithClaudeAsync(string name) =>
        _claude?.AuthenticateMcpAsync(name) ?? Task.FromResult("Claude Code is not available.");

    Bengine.Introspection.Servers.ICredentialStore? IMcpHost.Keyring => Bengine.Introspection.Servers.CredentialStores.System;

    void IMcpHost.Status(string text) => Workbench.Status(text);
}
