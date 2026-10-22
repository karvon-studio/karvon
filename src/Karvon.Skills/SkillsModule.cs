using Avalonia.Controls.Templates;
using CommunityToolkit.Mvvm.ComponentModel;
using Karvon.Mcp;
using Karvon.Modules;
using Karvon.Skills.ViewModels;
using Karvon.Skills.Views;
using Karvon.ViewModels;

namespace Karvon.Skills;

/// <summary>
/// Skills: the skills, subagents, commands and hooks Claude Code reads, and the memory files (see
/// <see cref="SkillsExplorerViewModel"/>), its explorer made the first time the tool window shows.
/// </summary>
public sealed partial class SkillsModule : KarvonModule, ISkillsHost
{
    public const string ToolWindowId = "Skills";

    IClaudeService? _claude;

    public override string Id => "skills";

    public override string Name => "Skills";

    public override string Description =>
        "The skills, subagents, commands and hooks Claude Code reads, a marketplace to find more, and memory files kept in "
        + "step for the other agents.";

    public override IReadOnlyList<ToolWindowInfo> ToolWindows =>
        [new(ToolWindowId, "Skills", "S_kills", "ToolSkills", DockSide.Right, () => new SkillsExplorerView { DataContext = EnsureExplorer() })];

    public override IReadOnlyList<IDataTemplate> DocumentTemplates =>
    [
        Document<SkillDocument>(document => new SkillPageView { DataContext = document.Page }),
        Document<SkillsMarketplaceDocument>(document => new SkillsMarketplaceView { DataContext = document.Marketplace }),
    ];

    public override IDataTemplate OverlayTemplate => Template<SkillsExplorerViewModel>(_ => new SkillsOverlay());

    /// <summary>The explorer, once the tool window has shown.</summary>
    [ObservableProperty]
    public partial SkillsExplorerViewModel? Explorer { get; private set; }

    public SkillsExplorerViewModel EnsureExplorer()
    {
        if (Explorer is null)
            Overlay = Explorer = new SkillsExplorerViewModel(this);
        return Explorer;
    }

    protected override void Started()
    {
        // Another folder has its own skills; another account its own folder.
        Workbench.WorkspaceChanged += Refresh;
        _claude = Workbench.Service<IClaudeService>();
        if (_claude is not null)
            _claude.Changed += Refresh;
    }

    void Refresh() => Explorer?.Refresh();

    public override void Dispose()
    {
        Explorer?.Dispose();
        Overlay = Explorer = null;
    }

    // ---- ISkillsHost ----------------------------------------------------------------------------------------------

    string? ISkillsHost.Workspace => Workbench.Workspace;

    /// <summary>The chosen account's folder; with Claude Code off, the CLI's own (CLAUDE_CONFIG_DIR, else ~/.claude).</summary>
    string ISkillsHost.ClaudeDirectory => _claude?.ConfigDirectory
        ?? (Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir ? dir : Path.Combine(Services.UserHome.Path, ".claude"));

    Services.Settings ISkillsHost.Settings => Workbench.Settings;

    void ISkillsHost.SaveSettings() => Workbench.SaveSettings();

    IEnumerable<DocumentTab> ISkillsHost.Documents => Workbench.Documents;

    void ISkillsHost.ShowDocument(DocumentTab document) => Workbench.ShowDocument(document);

    void ISkillsHost.CloseDocument(DocumentTab document) => Workbench.CloseDocument(document);

    void ISkillsHost.OpenFile(string path, int line) => Workbench.OpenFile(path, line);

    void ISkillsHost.AskClaude(string prompt) => _claude?.Ask(prompt);

    void ISkillsHost.OpenUrl(string url) => Workbench.OpenUrl(url);

    /// <summary>Claude Code has it already (in any of its settings, at that URL's host), or `claude mcp add --scope user` adds it.</summary>
    async Task<string?> ISkillsHost.EnsureClaudeMcpServerAsync(string name, string url)
    {
        if (_claude is null)
            return "Claude Code is not available.";
        var host = new Uri(url).Host;
        var configs = new McpConfigs(Services.Settings.ConfigDirectory, Workbench.Workspace, _claude.SettingsFile);
        if (configs.Read().Servers.Any(s => McpConfigs.ClaudeReadsItself(s.Origin) && s.Url is { } u && Uri.TryCreate(u, UriKind.Absolute, out var at) && at.Host == host))
            return null;
        return await _claude.AddMcpServerAsync(name, "http", url, "user");
    }

    void ISkillsHost.Status(string text) => Workbench.Status(text);
}
