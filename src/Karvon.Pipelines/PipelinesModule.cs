using System.Text.Json;
using Avalonia.Controls.Templates;
using Bengine.Introspection.Servers;
using CommunityToolkit.Mvvm.ComponentModel;
using Karvon.Claude;
using Karvon.Git;
using Karvon.Modules;
using Karvon.Pipelines.ViewModels;
using Karvon.Pipelines.Views;
using Karvon.ViewModels;

namespace Karvon.Pipelines;

/// <summary>
/// Pipelines: the open repository's CI runs, GitHub Actions' (through the user's gh) and GitLab CI's (a token in the
/// keyring), in a tool window (see <see cref="PipelinesViewModel"/>) and the status bar; a run opens as a page with its
/// jobs, steps and logs. Claude Code gets the pipeline tools.
/// </summary>
public sealed partial class PipelinesModule : KarvonModule, IPipelinesHost
{
    public const string ToolWindowId = "Pipelines";

    IClaudeService? _claude;

    public override string Id => "pipelines";

    public override string Name => "Pipelines";

    public override string Icon => "folder-ci";

    public override string Description =>
        "The repository's CI: GitHub Actions' workflow runs (through your own gh) and GitLab CI's pipelines (a token in the system "
        + "keyring), as they go and in the status bar; a run's jobs, steps and logs; re-run and cancel; Claude Code's pipeline tools.";

    public override IReadOnlyList<ToolWindowInfo> ToolWindows =>
        [new(ToolWindowId, "Pipelines", "P_ipelines", "ToolPipelines", DockSide.Right, () => new PipelinesView { DataContext = EnsurePipelines() })];

    public override IReadOnlyList<IDataTemplate> DocumentTemplates => [Document<PipelineDocument>(document => new PipelinePageView { DataContext = document.Page })];

    public override IDataTemplate OverlayTemplate => Template<PipelinesViewModel>(_ => new PipelinesOverlay());

    /// <summary>The branch's latest run: made with the window's view model, which starts with Karvon (it never asks for anything).</summary>
    public override ModuleStatusItem? StatusItem => EnsurePipelines().StatusItem;

    [ObservableProperty]
    public partial PipelinesViewModel? Pipelines { get; private set; }

    internal ICredentialStore? Keyring { get; set; } = CredentialStores.System;

    public PipelinesViewModel EnsurePipelines()
    {
        if (Pipelines is null)
            Overlay = Pipelines = new PipelinesViewModel(this, Keyring);
        return Pipelines;
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
        Pipelines?.Dispose();
        Overlay = Pipelines = null;
    }

    public override IEnumerable<IClaudeTool> ClaudeTools() => PipelinesClaudeTools.Create(EnsurePipelines);

    /// <summary>gh run, glab ci, GitLab's pipelines API by curl: Karvon's tools instead (its sign-in, its window).</summary>
    public override IEnumerable<ClaudeShellRule> ClaudeShellRules() =>
    [
        ClaudeShellRule.Command("ci cli", @"gh\s+run\s+(list|view|watch|rerun|cancel)\b|glab\s+(ci|pipeline|job)\b", Use),
        ClaudeShellRule.Command("ci api", @"(curl|wget|http|https|xh)\b[^;&|\n]*/api/v4/projects/[^\s;&|]*/(pipelines|jobs)\b", Use),
    ];

    const string Use =
        "Karvon reads the repository's CI for the user (its Pipelines window, the user's gh or a kept token): use "
        + "mcp__karvon__pipeline_list, pipeline_jobs and pipeline_log, and pipeline_retry or pipeline_cancel (the user approves "
        + "them), not the CLI or curl; no token is needed.";

    /// <summary>What Claude looked at shows in Karvon: the run's page.</summary>
    public override Task ShowClaudeToolAsync(string tool, JsonElement input, ClaudeToolOutput output)
    {
        if (output.IsError || Pipelines is not { } pipelines)
            return Task.CompletedTask;
        if (tool == "pipeline_list")
            Workbench.ShowToolWindowBesideClaude(ToolWindowId);
        else if (tool is "pipeline_jobs" or "pipeline_log" or "pipeline_retry" && input.TryGetProperty("pipeline", out var id)
            && pipelines.Runs.FirstOrDefault(r => r.Id == (id.ValueKind == JsonValueKind.String ? id.GetString() : id.GetRawText())) is { } row)
            pipelines.Open(row);
        return Task.CompletedTask;
    }

    // ---- IPipelinesHost -----------------------------------------------------------------------------------------

    IReadOnlyList<GitRepositoryState> IPipelinesHost.Repositories => Workbench.Repositories;

    public event Action? GitChanged;

    public event Action<GitRepository>? GitPushed;

    IEnumerable<DocumentTab> IPipelinesHost.Documents => Workbench.Documents;

    void IPipelinesHost.ShowDocument(DocumentTab document) => Workbench.ShowDocument(document);

    void IPipelinesHost.OpenUrl(string url) => Workbench.OpenUrl(url);

    void IPipelinesHost.Status(string text) => Workbench.Status(text);

    void IPipelinesHost.AskClaude(string prompt) => _claude?.Ask(prompt);
}
