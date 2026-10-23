using System.Text.Json;
using Avalonia.Controls.Templates;
using Bengine.Introspection.Servers;
using CommunityToolkit.Mvvm.ComponentModel;
using Karvon.Claude;
using Karvon.Jira.ViewModels;
using Karvon.Jira.Views;
using Karvon.Modules;
using Karvon.ViewModels;
using static Karvon.Claude.Tools.ToolInput;

namespace Karvon.Jira;

/// <summary>
/// Jira: the sites added in Karvon (Cloud, Server and Data Center) in a tool window (see
/// <see cref="JiraExplorerViewModel"/>); an issue, a board and a JQL search open as documents. Claude Code gets the Jira
/// tools, which work through Karvon's own sign-in: its token or password stays in the keyring.
/// </summary>
public sealed partial class JiraModule : KarvonModule, IJiraHost
{
    public const string ToolWindowId = "Jira";

    IClaudeService? _claude;

    public override string Id => "jira";

    public override string Name => "Jira";

    public override string Icon => "todo";

    public override string Description =>
        "Jira Cloud, Server and Data Center sites: your issues, filters and boards, an issue's page (its fields, description, "
        + "comments and transitions), JQL search, new issues; Claude Code's Jira tools, signed in by Karvon (the token stays "
        + "in the system keyring, Claude never sees it).";

    public override IReadOnlyList<ToolWindowInfo> ToolWindows =>
        [new(ToolWindowId, "Jira", "_Jira", "ToolJira", DockSide.Right, () => new JiraExplorerView { DataContext = EnsureExplorer() })];

    public override IReadOnlyList<IDataTemplate> DocumentTemplates =>
    [
        Document<JiraIssueDocument>(document => new JiraIssuePageView { DataContext = document.Page }),
        Document<JiraSearchDocument>(document => new JiraSearchPageView { DataContext = document.Page }),
        Document<JiraBoardDocument>(document => new JiraBoardPageView { DataContext = document.Page }),
    ];

    public override IDataTemplate OverlayTemplate => Template<JiraExplorerViewModel>(_ => new JiraOverlay());

    /// <summary>The explorer, once it has been needed.</summary>
    [ObservableProperty]
    public partial JiraExplorerViewModel? Explorer { get; private set; }

    /// <summary>How the explorer makes a site's client (the tests' fake site).</summary>
    internal Func<JiraSite, string, JiraClient>? Clients { get; set; }

    internal ICredentialStore? Keyring { get; set; } = CredentialStores.System;

    public JiraExplorerViewModel EnsureExplorer()
    {
        if (Explorer is null)
            Overlay = Explorer = new JiraExplorerViewModel(this, Keyring, Clients);
        return Explorer;
    }

    protected override void Started() => _claude = Workbench.Service<IClaudeService>();

    public override void Dispose()
    {
        Explorer?.Dispose();
        Overlay = Explorer = null;
    }

    public override IEnumerable<IClaudeTool> ClaudeTools() => JiraClaudeTools.Create(EnsureExplorer);

    /// <summary>What Claude did shows in Karvon: the issue it read or changed, the search it made.</summary>
    public override Task ShowClaudeToolAsync(string tool, JsonElement input, ClaudeToolOutput output)
    {
        if (output.IsError || EnsureExplorer().Find(Str(input, "site")) is not { } site)
            return Task.CompletedTask;
        switch (tool)
        {
            case "jira_sites":
                Workbench.ShowToolWindowBesideClaude(ToolWindowId);
                break;
            case "jira_search" when Str(input, "jql") is { } jql:
                Explorer!.OpenSearch(site, jql);
                break;
            case "jira_issue" or "jira_transition" or "jira_comment" or "jira_assign" when Str(input, "key") is { } key:
                Explorer!.OpenIssue(site, key);
                break;
            case "jira_create" when output.Text.Split(' ', 2)[0] is { Length: > 0 } made:
                Explorer!.OpenIssue(site, made);
                break;
        }
        return Task.CompletedTask;
    }

    // ---- IJiraHost ----------------------------------------------------------------------------------------------

    string IJiraHost.ConfigDirectory => Services.Settings.ConfigDirectory;

    IEnumerable<DocumentTab> IJiraHost.Documents => Workbench.Documents;

    void IJiraHost.ShowDocument(DocumentTab document) => Workbench.ShowDocument(document);

    void IJiraHost.OpenUrl(string url) => Workbench.OpenUrl(url);

    void IJiraHost.AskClaude(string prompt) => _claude?.Ask(prompt);

    void IJiraHost.Status(string text) => Workbench.Status(text);
}
