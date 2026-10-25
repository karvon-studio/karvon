using System.Text.Json;
using Avalonia.Controls.Templates;
using Bengine.Introspection.Servers;
using CommunityToolkit.Mvvm.ComponentModel;
using Karvon.Claude;
using Karvon.Git;
using Karvon.Modules;
using Karvon.Pipelines;
using Karvon.Pipelines.Providers;
using Karvon.PullRequests.ViewModels;
using Karvon.PullRequests.Views;
using Karvon.ViewModels;

namespace Karvon.PullRequests;

/// <summary>
/// Pull Requests: the open repository's GitHub pull requests and GitLab merge requests in a tool window (see
/// <see cref="PullRequestsViewModel"/>) and the branch's own in the status bar; one opens as a page with its conversation,
/// files and checks, to comment, review, merge or check out; a new one from the branch. Signed in as the Pipelines are
/// (the user's gh, a token in the keyring). Claude Code gets the pull request tools.
/// </summary>
public sealed partial class PullRequestsModule : KarvonModule, IPullRequestsHost
{
    public const string ToolWindowId = "PullRequests";

    IClaudeService? _claude;

    public override string Id => "pull-requests";

    public override string Name => "Pull Requests";

    public override string Icon => "folder-git";

    public override string Description =>
        "The repository's GitHub pull requests and GitLab merge requests: open, yours, waiting for your review; one's conversation, files "
        + "(each a diff) and checks; comment, approve, request changes, merge, close, check out; a new one from the branch; Claude Code's "
        + "pull request tools. Signed in by your gh or a token in the system keyring, as the Pipelines are.";

    public override IReadOnlyList<ToolWindowInfo> ToolWindows =>
        [new(ToolWindowId, "Pull Requests", "Pull _Requests", "ToolPullRequests", DockSide.Right, () => new PullRequestsView { DataContext = EnsureWindow() })];

    public override IReadOnlyList<IDataTemplate> DocumentTemplates => [Document<PullRequestDocument>(document => new PullRequestPageView { DataContext = document.Page })];

    public override IDataTemplate OverlayTemplate => Template<PullRequestsViewModel>(_ => new PullRequestsOverlay());

    /// <summary>The branch's open pull request: made with the window's view model, which starts with Karvon (it never asks for anything).</summary>
    public override ModuleStatusItem? StatusItem => EnsureWindow().StatusItem;

    [ObservableProperty]
    public partial PullRequestsViewModel? Window { get; private set; }

    internal ICredentialStore? Keyring { get; set; } = CredentialStores.System;

    /// <summary>How GitHub's gh is found (the tests' fake one).</summary>
    internal Func<string?> Gh { get; set; } = GhCliApi.Find;

    public PullRequestsViewModel EnsureWindow()
    {
        if (Window is null)
        {
            // The Pipelines window's sign-in when it is on: a token given there for this session counts here too.
            var connector = Workbench.Service<PipelinesModule>()?.EnsurePipelines().Connector ?? new PipelineConnector(new PipelineSecrets(Keyring), Gh);
            Overlay = Window = new PullRequestsViewModel(this, connector);
        }
        return Window;
    }

    protected override void Started()
    {
        _claude = Workbench.Service<IClaudeService>();
        Workbench.GitChanged += OnGitChanged;
        Workbench.GitPushed += OnGitPushed;
    }

    void OnGitChanged() => GitChanged?.Invoke();

    void OnGitPushed(GitRepository repository) => GitPushed?.Invoke(repository);

    public override void Dispose()
    {
        Workbench.GitChanged -= OnGitChanged;
        Workbench.GitPushed -= OnGitPushed;
        Window?.Dispose();
        Overlay = Window = null;
    }

    public override IEnumerable<IClaudeTool> ClaudeTools() => PullRequestsClaudeTools.Create(EnsureWindow);

    /// <summary>gh pr, glab mr, the services' pull request APIs by curl: Karvon's tools instead (its sign-in, its window).</summary>
    public override IEnumerable<ClaudeShellRule> ClaudeShellRules() =>
    [
        ClaudeShellRule.Command("pr cli", @"gh\s+pr\s+(list|view|diff|create|comment|review|merge|close|checks|status)\b|glab\s+mr\b", Use),
        ClaudeShellRule.Command("pr api", @"(curl|wget|http|https|xh)\b[^;&|\n]*(/repos/[^\s;&|]+/pulls\b|/merge_requests\b)", Use),
    ];

    const string Use =
        "Karvon reads the repository's pull requests for the user (its Pull Requests window, the user's gh or a kept token): use "
        + "mcp__karvon__pr_list, pr_view and pr_diff, and pr_create, pr_comment, pr_review or pr_merge (the user approves them), not "
        + "the CLI or curl; no token is needed. To check one out, use git as usual.";

    /// <summary>What Claude looked at or did shows in Karvon: the list, or the pull request's page.</summary>
    public override Task ShowClaudeToolAsync(string tool, JsonElement input, ClaudeToolOutput output)
    {
        if (output.IsError || Window is not { } window)
            return Task.CompletedTask;
        if (tool == "pr_list")
            Workbench.ShowToolWindowBesideClaude(ToolWindowId);
        else if (tool is "pr_view" or "pr_diff" or "pr_comment" or "pr_review" or "pr_merge" && Number(input) is { } number)
            _ = OpenAsync(window, number);
        else if (tool == "pr_create" && output.Text.Split(' ', 2)[0].TrimStart('#', '!') is { Length: > 0 } made)
            _ = OpenAsync(window, made);
        return Task.CompletedTask;
    }

    static string? Number(JsonElement input) =>
        input.TryGetProperty("number", out var n) ? (n.ValueKind == JsonValueKind.String ? n.GetString() : n.GetRawText())?.TrimStart('#', '!') : null;

    static async Task OpenAsync(PullRequestsViewModel window, string number)
    {
        if (window.Rows.FirstOrDefault(r => r.Number == number) is { } row)
        {
            window.Open(row);
            return;
        }
        try
        {
            window.Open(await window.FindAsync(number, default));
        }
        catch (Exception ex) when (ex is PipelineException or InvalidOperationException or IOException or HttpRequestException)
        {
        }
    }

    // ---- IPullRequestsHost -----------------------------------------------------------------------------------

    IReadOnlyList<GitRepositoryState> IPullRequestsHost.Repositories => Workbench.Repositories;

    public event Action? GitChanged;

    public event Action<GitRepository>? GitPushed;

    IEnumerable<DocumentTab> IPullRequestsHost.Documents => Workbench.Documents;

    void IPullRequestsHost.ShowDocument(DocumentTab document) => Workbench.ShowDocument(document);

    void IPullRequestsHost.ShowDiff(string path, string before, string after, string beforeTitle, string afterTitle, string key) =>
        Workbench.ShowDiff(path, before, after, beforeTitle, afterTitle, key);

    void IPullRequestsHost.OpenUrl(string url) => Workbench.OpenUrl(url);

    void IPullRequestsHost.Status(string text) => Workbench.Status(text);

    void IPullRequestsHost.AskClaude(string prompt) => _claude?.Ask(prompt);

    Task<bool> IPullRequestsHost.PushAsync(GitRepository repository) => Workbench.PushAsync(repository);

    Task<bool> IPullRequestsHost.RunGitAsync(string title, Func<Task<GitResult>> operation, bool changesFiles) =>
        Workbench.RunGitAsync(title, operation, changesFiles);
}
