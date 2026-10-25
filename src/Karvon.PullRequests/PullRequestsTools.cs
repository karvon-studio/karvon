using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using Karvon.Claude;
using Karvon.Claude.Tools;
using Karvon.Pipelines;
using Karvon.PullRequests.ViewModels;
using static Karvon.Claude.Tools.ToolInput;

namespace Karvon.PullRequests;

/// <summary>
/// Claude Code's tools for the repository's pull requests (GitHub's) and merge requests (GitLab's): the list, one whole,
/// its changes; and, asking the user each time, a new one from the branch, a comment, a review, a merge. Through Karvon's
/// own sign-in (the user's gh, a kept token), which Claude never sees.
/// </summary>
public static class PullRequestsClaudeTools
{
    /// <summary>How much of a pull request's patches one call returns at most.</summary>
    internal const int MaxDiff = 60_000;

    public static IEnumerable<IClaudeTool> Create(Func<PullRequestsViewModel> window) =>
    [
        new PrListTool(window), new PrViewTool(window), new PrDiffTool(window),
        new PrCreateTool(window), new PrCommentTool(window), new PrReviewTool(window), new PrMergeTool(window),
    ];

    internal static string Row(PullRequest p, string prefix) =>
        $"{prefix}{p.Number} [{p.State.Text()}] {p.Title} ({p.Author}, {p.SourceBranch} -> {p.TargetBranch}, updated {PipelineStatuses.Ago(p.Updated, DateTimeOffset.Now)}"
        + (p.Comments > 0 ? $", {p.Comments} comments" : "") + ")";
}

abstract class PullRequestsTool(Func<PullRequestsViewModel> window) : IClaudeTool
{
    protected PullRequestsViewModel Window => window();

    public abstract string Name { get; }

    public abstract string Description { get; }

    public abstract JsonObject InputSchema { get; }

    public abstract ClaudeToolPlan Plan(JsonElement input);

    public async Task<ClaudeToolOutput> CallAsync(JsonElement input, CancellationToken cancellationToken)
    {
        try
        {
            return await Dispatcher.UIThread.InvokeAsync(() => RunAsync(input, cancellationToken));
        }
        catch (Exception ex) when (ex is PipelineException or InvalidOperationException or IOException or HttpRequestException or JsonException)
        {
            return ClaudeToolOutput.Error(Redaction.Text(ex.Message));
        }
    }

    protected abstract Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken);

    protected static string Number(JsonElement input) =>
        Str(input, "number") is { Length: > 0 } value ? value.TrimStart('#', '!')
        : input.TryGetProperty("number", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetRawText()
        : throw new InvalidOperationException("'number' is required.");

    protected static string Required(JsonElement input, string name) =>
        Str(input, name) is { Length: > 0 } value ? value : throw new InvalidOperationException($"'{name}' is required.");

    protected static JsonObject NumberProperty => new() { ["type"] = "string", ["description"] = "Its number (GitHub's #, GitLab's !iid)." };

    protected string Service => Window.Repository?.Remote is { } remote ? $"{remote.Authority}/{remote.Path}" : "the repository's service";
}

/// <summary><c>pr_list</c>: the repository's pull requests.</summary>
sealed class PrListTool(Func<PullRequestsViewModel> window) : PullRequestsTool(window)
{
    public override string Name => "pr_list";

    public override string Description =>
        "Lists the open repository's pull requests (GitHub) or merge requests (GitLab), the latest updated first: number, state, title, "
        + "author, its branch into the target, comments. Open ones by default.";

    public override JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["state"] = new JsonObject
            {
                ["type"] = "string", ["enum"] = new JsonArray("open", "mine", "review_requested", "closed", "all"),
                ["description"] = "open (default), mine (open, made by the user), review_requested (open, the user asked to review), closed, all.",
            },
            ["max"] = new JsonObject { ["type"] = "integer", ["description"] = "How many at most (default 20)." },
        },
    };

    public override ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Read, "Pull Requests", Str(input, "state"), "");

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var provider = Window.ProviderOrThrow();
        var state = Str(input, "state") switch
        {
            "mine" => PullRequestFilter.Mine,
            "review_requested" => PullRequestFilter.ReviewRequested,
            "closed" => PullRequestFilter.Closed,
            "all" => PullRequestFilter.All,
            _ => PullRequestFilter.Open,
        };
        var list = await provider.ListAsync(state, Int(input, "max", 20, 1, 100), cancellationToken);
        if (state is PullRequestFilter.Mine or PullRequestFilter.ReviewRequested)
        {
            var user = await provider.UserAsync(cancellationToken);
            list = [.. list.Where(p => state == PullRequestFilter.Mine
                ? string.Equals(p.Author, user, StringComparison.OrdinalIgnoreCase)
                : p.Reviewers.Contains(user, StringComparer.OrdinalIgnoreCase))];
        }
        return new ClaudeToolOutput(list.Count == 0
            ? $"{Service} has no {Str(input, "state") ?? "open"} {provider.Noun}s."
            : $"{Service}'s {provider.Noun}s:\n" + string.Join("\n", list.Select(p => PullRequestsClaudeTools.Row(p, provider.Prefix))));
    }
}

/// <summary><c>pr_view</c>: one whole: its description, reviews, checks and conversation.</summary>
sealed class PrViewTool(Func<PullRequestsViewModel> window) : PullRequestsTool(window)
{
    public override string Name => "pr_view";

    public override string Description =>
        "Returns a pull request (merge request) whole: title, state, branches, author, whether it can be merged, its description, "
        + "the reviewers' say, its checks (CI jobs) and its conversation, comments on lines with their file and line. Its changes are pr_diff's.";

    public override JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject { ["number"] = NumberProperty },
        ["required"] = new JsonArray("number"),
    };

    public override ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Read, "Pull Request", Str(input, "number"), "");

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var provider = Window.ProviderOrThrow();
        var pr = await Window.FindAsync(Number(input), cancellationToken);
        var details = await provider.DetailsAsync(pr, cancellationToken);
        var text = new StringBuilder();
        text.AppendLine($"{provider.Prefix}{pr.Number} {pr.Title}");
        text.AppendLine($"State: {pr.State.Text()}; {pr.Author} wants to merge {pr.SourceBranch} into {pr.TargetBranch}{(pr.IsFromFork ? " (from a fork)" : "")}.");
        if (pr.ChangedFiles > 0 || pr.Additions + pr.Deletions > 0)
            text.AppendLine($"Changes: {pr.ChangedFiles} files, +{pr.Additions} -{pr.Deletions}.");
        if (pr.IsOpen)
            text.AppendLine("Mergeable: " + (pr.Mergeable switch { true => "yes", false => "no", _ => "not known yet" }) + (pr.MergeNote is { } note ? $" ({note})" : ""));
        if (pr.Labels.Count > 0)
            text.AppendLine("Labels: " + string.Join(", ", pr.Labels));
        if (pr.Reviewers.Count > 0)
            text.AppendLine("Review requested: " + string.Join(", ", pr.Reviewers));
        foreach (var review in details.Reviews)
            text.AppendLine($"Review: {review.Author} {review.Verdict.Text()}");
        if (details.Checks.Count > 0)
            text.AppendLine("Checks: " + string.Join("; ", details.Checks.Select(c => $"{c.Name} {c.Status.Text()}")));
        text.AppendLine().AppendLine("Description:").AppendLine(pr.Body.Length > 0 ? pr.Body : "(none)");
        if (details.Comments.Count > 0)
        {
            text.AppendLine().AppendLine("Conversation:");
            foreach (var c in details.Comments)
                text.AppendLine($"- {c.Author}{(c.Path is { Length: > 0 } path ? $" on {path}{(c.Line is { } line ? ":" + line : "")}" : "")}: {c.Body}");
        }
        text.AppendLine().Append(pr.Url);
        return new ClaudeToolOutput(text.ToString());
    }
}

/// <summary><c>pr_diff</c>: its changes, file by file.</summary>
sealed class PrDiffTool(Func<PullRequestsViewModel> window) : PullRequestsTool(window)
{
    public override string Name => "pr_diff";

    public override string Description =>
        "Returns a pull request's (merge request's) changes: each file with its status and counts, and its unified diff (one file's only, when "
        + "given). Long changes are cut; ask for a file by its path then.";

    public override JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["number"] = NumberProperty,
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "One file's path, to read only its diff." },
        },
        ["required"] = new JsonArray("number"),
    };

    public override ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Read, "Pull Request Diff", Str(input, "number"), "");

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var provider = Window.ProviderOrThrow();
        var pr = await Window.FindAsync(Number(input), cancellationToken);
        var files = await provider.FilesAsync(pr, cancellationToken);
        if (Str(input, "path") is { Length: > 0 } only)
            files = [.. files.Where(f => f.Path == only || f.OldPath == only)];
        if (files.Count == 0)
            return ClaudeToolOutput.Error(Str(input, "path") is { Length: > 0 } p ? $"{provider.Prefix}{pr.Number} does not change {p}." : $"{provider.Prefix}{pr.Number} changes no files.");
        var text = new StringBuilder($"{provider.Prefix}{pr.Number} {pr.Title}: {files.Count} files\n");
        foreach (var f in files)
            text.AppendLine($"{f.Letter} {(f.OldPath is { } old ? old + " -> " : "")}{f.Path} (+{f.Additions} -{f.Deletions})");
        var cut = new List<string>();
        foreach (var f in files)
        {
            if (f.Patch.Length == 0)
                continue;
            if (text.Length + f.Patch.Length > PullRequestsClaudeTools.MaxDiff)
            {
                cut.Add(f.Path);
                continue;
            }
            text.AppendLine().AppendLine($"--- {f.OldPath ?? f.Path}").AppendLine($"+++ {f.Path}").AppendLine(f.Patch.TrimEnd());
        }
        if (cut.Count > 0)
            text.AppendLine().Append("Not shown (too long together; ask with path): " + string.Join(", ", cut));
        return new ClaudeToolOutput(text.ToString());
    }
}

/// <summary><c>pr_create</c>: a new one from the checked-out branch (pushed first; asks).</summary>
sealed class PrCreateTool(Func<PullRequestsViewModel> window) : PullRequestsTool(window)
{
    public override string Name => "pr_create";

    public override string Description =>
        "Makes a pull request (GitHub) or merge request (GitLab) from the checked-out branch, pushed first, into the default branch (or the "
        + "target given). The user approves each call.";

    public override JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["title"] = new JsonObject { ["type"] = "string" },
            ["body"] = new JsonObject { ["type"] = "string", ["description"] = "Its description, Markdown." },
            ["target"] = new JsonObject { ["type"] = "string", ["description"] = "The branch to merge into (default: the repository's default)." },
            ["draft"] = new JsonObject { ["type"] = "boolean" },
        },
        ["required"] = new JsonArray("title"),
    };

    public override ClaudeToolPlan Plan(JsonElement input) =>
        new(ToolAccess.Write, "New Pull Request", Str(input, "title"),
            $"Allow Claude to push {Window.Repository?.Branch ?? "the branch"} and propose it as \"{Str(input, "title")}\"?")
        {
            Warning = $"It pushes the branch to {Service} and opens the request there, where others see it.",
            Code = Str(input, "body"),
        };

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var provider = Window.ProviderOrThrow();
        var made = await Window.CreateAsync(Required(input, "title"), Str(input, "body") ?? "", Str(input, "target"),
            input.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True, cancellationToken);
        return new ClaudeToolOutput($"{provider.Prefix}{made.Number} made: {made.Title} ({made.SourceBranch} -> {made.TargetBranch}) {made.Url}");
    }
}

/// <summary><c>pr_comment</c>: a comment on the conversation (asks).</summary>
sealed class PrCommentTool(Func<PullRequestsViewModel> window) : PullRequestsTool(window)
{
    public override string Name => "pr_comment";

    public override string Description => "Comments on a pull request's (merge request's) conversation, as the user. The user approves each call.";

    public override JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject { ["number"] = NumberProperty, ["body"] = new JsonObject { ["type"] = "string", ["description"] = "Markdown." } },
        ["required"] = new JsonArray("number", "body"),
    };

    public override ClaudeToolPlan Plan(JsonElement input) =>
        new(ToolAccess.Write, "Comment", Str(input, "number"), $"Allow Claude to comment on {Str(input, "number")} as you?") { Code = Str(input, "body") };

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var provider = Window.ProviderOrThrow();
        var pr = await Window.FindAsync(Number(input), cancellationToken);
        await provider.CommentAsync(pr, Required(input, "body"), cancellationToken);
        return new ClaudeToolOutput($"Commented on {provider.Prefix}{pr.Number}.");
    }
}

/// <summary><c>pr_review</c>: an approval, changes asked for, or a review comment (asks).</summary>
sealed class PrReviewTool(Func<PullRequestsViewModel> window) : PullRequestsTool(window)
{
    public override string Name => "pr_review";

    public override string Description =>
        "Reviews a pull request (merge request) as the user: approve, request_changes or comment, with what the review says. "
        + "The user approves each call.";

    public override JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["number"] = NumberProperty,
            ["verdict"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("approve", "request_changes", "comment") },
            ["body"] = new JsonObject { ["type"] = "string", ["description"] = "What the review says (needed but for an approval)." },
        },
        ["required"] = new JsonArray("number", "verdict"),
    };

    static ReviewVerdict Verdict(JsonElement input) => Str(input, "verdict") switch
    {
        "approve" => ReviewVerdict.Approve,
        "request_changes" => ReviewVerdict.RequestChanges,
        _ => ReviewVerdict.Comment,
    };

    public override ClaudeToolPlan Plan(JsonElement input) =>
        new(ToolAccess.Write, "Review", Str(input, "number"), Verdict(input) switch
        {
            ReviewVerdict.Approve => $"Allow Claude to approve {Str(input, "number")} as you?",
            ReviewVerdict.RequestChanges => $"Allow Claude to request changes on {Str(input, "number")} as you?",
            _ => $"Allow Claude to review {Str(input, "number")} as you?",
        })
        { Code = Str(input, "body") };

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var provider = Window.ProviderOrThrow();
        var verdict = Verdict(input);
        var body = Str(input, "body") ?? "";
        if (verdict != ReviewVerdict.Approve && body.Trim().Length == 0)
            return ClaudeToolOutput.Error("A review that is not an approval needs a body: what it says.");
        var pr = await Window.FindAsync(Number(input), cancellationToken);
        await provider.ReviewAsync(pr, verdict, body, cancellationToken);
        return new ClaudeToolOutput($"{provider.Prefix}{pr.Number}: {verdict.Text()}.");
    }
}

/// <summary><c>pr_merge</c>: merged (asks).</summary>
sealed class PrMergeTool(Func<PullRequestsViewModel> window) : PullRequestsTool(window)
{
    public override string Name => "pr_merge";

    public override string Description =>
        "Merges a pull request (merge request): merge (default), squash, or rebase (GitHub's only); its branch deleted on the remote when asked. "
        + "The user approves each call.";

    public override JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["number"] = NumberProperty,
            ["method"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("merge", "squash", "rebase") },
            ["delete_branch"] = new JsonObject { ["type"] = "boolean", ["description"] = "Delete its branch on the remote after (default false)." },
        },
        ["required"] = new JsonArray("number"),
    };

    static MergeMethod Method(JsonElement input) => Str(input, "method") switch
    {
        "squash" => MergeMethod.Squash,
        "rebase" => MergeMethod.Rebase,
        _ => MergeMethod.Merge,
    };

    static bool DeleteBranch(JsonElement input) => input.TryGetProperty("delete_branch", out var d) && d.ValueKind == JsonValueKind.True;

    public override ClaudeToolPlan Plan(JsonElement input) =>
        new(ToolAccess.Write, "Merge", Str(input, "number"),
            $"Allow Claude to merge {Str(input, "number")} ({Method(input).ToString().ToLowerInvariant()}){(DeleteBranch(input) ? " and delete its branch" : "")}?")
        {
            Warning = $"Its changes go into the target branch on {Service}.",
        };

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var provider = Window.ProviderOrThrow();
        var pr = await Window.FindAsync(Number(input), cancellationToken);
        if (!pr.IsOpen)
            return ClaudeToolOutput.Error($"{provider.Prefix}{pr.Number} is {pr.State.Text()}: there is nothing to merge.");
        await provider.MergeAsync(pr, Method(input), DeleteBranch(input), cancellationToken);
        _ = Window.RefreshAsync();
        return new ClaudeToolOutput($"{provider.Prefix}{pr.Number} merged into {pr.TargetBranch}.");
    }
}
