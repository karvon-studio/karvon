using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Karvon.Pipelines;
using Karvon.Pipelines.Providers;
using static Karvon.Pipelines.Providers.GitHubActions;

namespace Karvon.PullRequests.Providers;

/// <summary>
/// GitHub's pull requests through its REST API (the user's gh, or a kept token): the list, one whole with its
/// conversation, reviews and checks, its files, and what a reviewer does: comment, approve or ask for changes, merge
/// (merge, squash or rebase), close.
/// </summary>
public sealed class GitHubPullRequests(RepoRemote remote, IGitHubApi api) : IPullRequestProvider
{
    string? _user;

    public RepoRemote Remote => remote;

    public string Noun => "pull request";

    public string Short => "PR";

    public string Prefix => "#";

    public bool CanRebase => true;

    string Repo => "repos/" + remote.Path;

    public async Task<string> UserAsync(CancellationToken ct)
    {
        if (_user is null)
        {
            using var json = JsonDocument.Parse(await api.GetAsync("user", ct));
            _user = Str(json.RootElement, "login");
        }
        return _user;
    }

    public async Task<string> DefaultBranchAsync(CancellationToken ct)
    {
        using var json = JsonDocument.Parse(await api.GetAsync(Repo, ct));
        return Str(json.RootElement, "default_branch") is { Length: > 0 } branch ? branch : "main";
    }

    public async Task<IReadOnlyList<PullRequest>> ListAsync(PullRequestFilter state, int count, CancellationToken ct)
    {
        var s = state switch { PullRequestFilter.Closed => "closed", PullRequestFilter.All => "all", _ => "open" };
        using var json = JsonDocument.Parse(await api.GetAsync($"{Repo}/pulls?state={s}&per_page={count}&sort=updated&direction=desc", ct));
        return [.. json.RootElement.EnumerateArray().Select(Pull)];
    }

    public async Task<PullRequest> GetAsync(string number, CancellationToken ct)
    {
        using var json = JsonDocument.Parse(await api.GetAsync($"{Repo}/pulls/{number}", ct));
        return Pull(json.RootElement);
    }

    public async Task<PullRequestDetails> DetailsAsync(PullRequest pr, CancellationToken ct)
    {
        var comments = new List<PullRequestComment>();
        using (var json = JsonDocument.Parse(await api.GetAsync($"{Repo}/issues/{pr.Number}/comments?per_page=100", ct)))
            comments.AddRange(json.RootElement.EnumerateArray().Select(c => new PullRequestComment(Num(c, "id"), Login(c), Str(c, "body"), Date(c, "created_at"))));
        using (var json = JsonDocument.Parse(await api.GetAsync($"{Repo}/pulls/{pr.Number}/comments?per_page=100", ct)))
            comments.AddRange(json.RootElement.EnumerateArray().Select(c => new PullRequestComment(Num(c, "id"), Login(c), Str(c, "body"), Date(c, "created_at"))
            {
                Path = Str(c, "path"),
                Line = Int(c, "line") ?? Int(c, "original_line"),
            }));
        var reviews = new List<(string Author, ReviewVerdict Verdict, DateTimeOffset? At)>();
        using (var json = JsonDocument.Parse(await api.GetAsync($"{Repo}/pulls/{pr.Number}/reviews?per_page=100", ct)))
            foreach (var r in json.RootElement.EnumerateArray())
            {
                ReviewVerdict? verdict = Str(r, "state") switch
                {
                    "APPROVED" => ReviewVerdict.Approve,
                    "CHANGES_REQUESTED" => ReviewVerdict.RequestChanges,
                    "COMMENTED" => ReviewVerdict.Comment,
                    _ => null,
                };
                if (verdict is not { } v)
                    continue;
                reviews.Add((Login(r), v, Date(r, "submitted_at")));
                // What a reviewer said with their review is part of the conversation, as GitHub's page has it.
                if (Str(r, "body") is { Length: > 0 } said)
                    comments.Add(new PullRequestComment("review-" + Num(r, "id"), Login(r), said, Date(r, "submitted_at")));
            }
        return new PullRequestDetails(pr, [.. comments.OrderBy(c => c.Created)], Latest(reviews), await ChecksAsync(pr, ct));
    }

    /// <summary>Each reviewer's last say; a comment does not undo an approval or a request for changes.</summary>
    internal static IReadOnlyList<PullRequestReview> Latest(IEnumerable<(string Author, ReviewVerdict Verdict, DateTimeOffset? At)> reviews) =>
    [
        .. reviews.GroupBy(r => r.Author)
            .Select(g => g.Where(r => r.Verdict != ReviewVerdict.Comment).OrderBy(r => r.At).LastOrDefault() is { Author: not null } decided
                ? decided : g.OrderBy(r => r.At).Last())
            .Select(r => new PullRequestReview(r.Author, r.Verdict, r.At)),
    ];

    async Task<IReadOnlyList<PullRequestCheck>> ChecksAsync(PullRequest pr, CancellationToken ct)
    {
        if (pr.HeadSha.Length == 0)
            return [];
        try
        {
            using var json = JsonDocument.Parse(await api.GetAsync($"{Repo}/commits/{pr.HeadSha}/check-runs?per_page=100", ct));
            return [.. json.RootElement.GetProperty("check_runs").EnumerateArray()
                .Select(c => new PullRequestCheck(Str(c, "name"), Status(Str(c, "status"), Str(c, "conclusion")), Str(c, "html_url")))];
        }
        catch (PipelineException)
        {
            // A token without the checks' scope: the rest of the page still shows.
            return [];
        }
    }

    public async Task<IReadOnlyList<PullRequestFile>> FilesAsync(PullRequest pr, CancellationToken ct)
    {
        using var json = JsonDocument.Parse(await api.GetAsync($"{Repo}/pulls/{pr.Number}/files?per_page=100", ct));
        return [.. json.RootElement.EnumerateArray().Select(f => new PullRequestFile(
            Str(f, "filename"), Str(f, "previous_filename") is { Length: > 0 } old ? old : null,
            Str(f, "status") switch { "added" => "A", "removed" => "D", "renamed" => "R", "copied" => "C", _ => "M" },
            Int(f, "additions") ?? 0, Int(f, "deletions") ?? 0, Str(f, "patch")))];
    }

    public async Task<string?> FileAtAsync(string sha, string path, CancellationToken ct)
    {
        try
        {
            var escaped = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
            using var json = JsonDocument.Parse(await api.GetAsync($"{Repo}/contents/{escaped}?ref={sha}", ct));
            var content = Str(json.RootElement, "content");
            return Str(json.RootElement, "encoding") == "base64" ? Encoding.UTF8.GetString(Convert.FromBase64String(content.Replace("\n", ""))) : content;
        }
        catch (PipelineException)
        {
            return null;
        }
    }

    public async Task<PullRequest> CreateAsync(string source, string target, string title, string body, bool draft, CancellationToken ct)
    {
        var made = await api.SendAsync("POST", $"{Repo}/pulls", new JsonObject
        {
            ["title"] = title, ["head"] = source, ["base"] = target, ["body"] = body, ["draft"] = draft,
        }.ToJsonString(), ct);
        using var json = JsonDocument.Parse(made);
        return Pull(json.RootElement);
    }

    public Task CommentAsync(PullRequest pr, string body, CancellationToken ct) =>
        api.SendAsync("POST", $"{Repo}/issues/{pr.Number}/comments", new JsonObject { ["body"] = body }.ToJsonString(), ct);

    public Task ReviewAsync(PullRequest pr, ReviewVerdict verdict, string body, CancellationToken ct) =>
        api.SendAsync("POST", $"{Repo}/pulls/{pr.Number}/reviews", new JsonObject
        {
            ["event"] = verdict switch { ReviewVerdict.Approve => "APPROVE", ReviewVerdict.RequestChanges => "REQUEST_CHANGES", _ => "COMMENT" },
            ["body"] = body,
        }.ToJsonString(), ct);

    public async Task MergeAsync(PullRequest pr, MergeMethod method, bool deleteBranch, CancellationToken ct)
    {
        await api.SendAsync("PUT", $"{Repo}/pulls/{pr.Number}/merge",
            new JsonObject { ["merge_method"] = method.ToString().ToLowerInvariant() }.ToJsonString(), ct);
        // A fork's branch is not the repository's to delete.
        if (deleteBranch && !pr.IsFromFork)
            await api.SendAsync("DELETE", $"{Repo}/git/refs/heads/{string.Join('/', pr.SourceBranch.Split('/').Select(Uri.EscapeDataString))}", null, ct);
    }

    public Task CloseAsync(PullRequest pr, CancellationToken ct) =>
        api.SendAsync("PATCH", $"{Repo}/pulls/{pr.Number}", new JsonObject { ["state"] = "closed" }.ToJsonString(), ct);

    public string HeadRef(PullRequest pr) => $"pull/{pr.Number}/head";

    static string Login(JsonElement e) => e.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object ? Str(user, "login") : "";

    static int? Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    static PullRequest Pull(JsonElement p)
    {
        var head = p.GetProperty("head");
        var @base = p.GetProperty("base");
        var merged = Str(p, "merged_at").Length > 0 || p.TryGetProperty("merged", out var m) && m.ValueKind == JsonValueKind.True;
        var draft = p.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True;
        var state = merged ? PullRequestState.Merged : Str(p, "state") == "closed" ? PullRequestState.Closed : draft ? PullRequestState.Draft : PullRequestState.Open;
        string RepoOf(JsonElement side) => side.TryGetProperty("repo", out var r) && r.ValueKind == JsonValueKind.Object ? Str(r, "full_name") : "";
        return new PullRequest(Num(p, "number"), Str(p, "title"), Login(p), state, Str(head, "ref"), Str(@base, "ref"), Date(p, "created_at"),
            Date(p, "updated_at"), Str(p, "html_url"))
        {
            Body = Str(p, "body"),
            HeadSha = Str(head, "sha"),
            BaseSha = Str(@base, "sha"),
            IsFromFork = RepoOf(head) is { Length: > 0 } from && from != RepoOf(@base),
            Comments = (Int(p, "comments") ?? 0) + (Int(p, "review_comments") ?? 0),
            Reviewers = p.TryGetProperty("requested_reviewers", out var rr) && rr.ValueKind == JsonValueKind.Array ? [.. rr.EnumerateArray().Select(r => Str(r, "login"))] : [],
            Labels = p.TryGetProperty("labels", out var l) && l.ValueKind == JsonValueKind.Array ? [.. l.EnumerateArray().Select(x => Str(x, "name"))] : [],
            Mergeable = p.TryGetProperty("mergeable", out var ok) && ok.ValueKind is JsonValueKind.True or JsonValueKind.False ? ok.GetBoolean() : null,
            MergeNote = Str(p, "mergeable_state") switch
            {
                "dirty" => "It conflicts with " + Str(@base, "ref") + ".",
                "blocked" => "It is blocked: a review or a check it needs is missing.",
                "behind" => "It is behind " + Str(@base, "ref") + ".",
                "unstable" => "Some checks are failing.",
                _ => null,
            },
            Additions = Int(p, "additions") ?? 0,
            Deletions = Int(p, "deletions") ?? 0,
            ChangedFiles = Int(p, "changed_files") ?? 0,
        };
    }

    public void Dispose() => api.Dispose();
}
