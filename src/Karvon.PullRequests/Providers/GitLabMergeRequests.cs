using System.Text.Json;
using System.Text.Json.Nodes;
using Karvon.Pipelines;
using Karvon.Pipelines.Providers;
using static Karvon.Pipelines.Providers.GitHubActions;

namespace Karvon.PullRequests.Providers;

/// <summary>
/// A GitLab's merge requests (gitlab.com's or a company's own) through REST v4 with the kept token: the list, one whole
/// with its notes, approvals and its pipeline's jobs, its changes, and what a reviewer does: comment, approve, merge
/// (squashed or not), close.
/// </summary>
public sealed class GitLabMergeRequests(GitLabApi api) : IPullRequestProvider
{
    string? _user;

    public RepoRemote Remote => api.Remote;

    public string Noun => "merge request";

    public string Short => "MR";

    public string Prefix => "!";

    public bool CanRebase => false;

    string P => api.Project;

    public async Task<string> UserAsync(CancellationToken ct)
    {
        if (_user is null)
        {
            using var json = JsonDocument.Parse(await api.GetAsync("user", ct));
            _user = Str(json.RootElement, "username");
        }
        return _user;
    }

    public async Task<string> DefaultBranchAsync(CancellationToken ct)
    {
        using var json = JsonDocument.Parse(await api.GetAsync(P.TrimEnd('/'), ct));
        return Str(json.RootElement, "default_branch") is { Length: > 0 } branch ? branch : "main";
    }

    public async Task<IReadOnlyList<PullRequest>> ListAsync(PullRequestFilter state, int count, CancellationToken ct)
    {
        // GitLab's closed are not its merged: both are what the window calls closed.
        var s = state is PullRequestFilter.Closed or PullRequestFilter.All ? "all" : "opened";
        using var json = JsonDocument.Parse(await api.GetAsync($"{P}merge_requests?state={s}&per_page={count}&order_by=updated_at&sort=desc", ct));
        var all = json.RootElement.EnumerateArray().Select(Merge);
        return [.. state == PullRequestFilter.Closed ? all.Where(m => !m.IsOpen) : all];
    }

    public async Task<PullRequest> GetAsync(string number, CancellationToken ct)
    {
        using var json = JsonDocument.Parse(await api.GetAsync($"{P}merge_requests/{number}", ct));
        return Merge(json.RootElement);
    }

    public async Task<PullRequestDetails> DetailsAsync(PullRequest mr, CancellationToken ct)
    {
        List<PullRequestComment> comments;
        using (var json = JsonDocument.Parse(await api.GetAsync($"{P}merge_requests/{mr.Number}/notes?sort=asc&order_by=created_at&per_page=100", ct)))
            comments = [.. json.RootElement.EnumerateArray()
                // GitLab's own notes ("added 2 commits", "approved this merge request") are not the conversation.
                .Where(n => !(n.TryGetProperty("system", out var system) && system.ValueKind == JsonValueKind.True))
                .Select(n => new PullRequestComment(Num(n, "id"), Username(n, "author"), Str(n, "body"), Date(n, "created_at"))
                {
                    Path = n.TryGetProperty("position", out var at) && at.ValueKind == JsonValueKind.Object ? Str(at, "new_path") : null,
                    Line = n.TryGetProperty("position", out var line) && line.ValueKind == JsonValueKind.Object
                        && line.TryGetProperty("new_line", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : null,
                })];
        IReadOnlyList<PullRequestReview> reviews = [];
        try
        {
            using var json = JsonDocument.Parse(await api.GetAsync($"{P}merge_requests/{mr.Number}/approvals", ct));
            if (json.RootElement.TryGetProperty("approved_by", out var by) && by.ValueKind == JsonValueKind.Array)
                reviews = [.. by.EnumerateArray().Select(a => new PullRequestReview(Username(a, "user"), ReviewVerdict.Approve, null))];
        }
        catch (PipelineException)
        {
        }
        return new PullRequestDetails(mr, comments, reviews, await ChecksAsync(mr, ct));
    }

    /// <summary>Its latest pipeline's jobs.</summary>
    async Task<IReadOnlyList<PullRequestCheck>> ChecksAsync(PullRequest mr, CancellationToken ct)
    {
        try
        {
            using var pipelines = JsonDocument.Parse(await api.GetAsync($"{P}merge_requests/{mr.Number}/pipelines", ct));
            if (pipelines.RootElement.EnumerateArray().FirstOrDefault() is not { ValueKind: JsonValueKind.Object } latest)
                return [];
            using var jobs = JsonDocument.Parse(await api.GetAsync($"{P}pipelines/{Num(latest, "id")}/jobs?per_page=100", ct));
            return [.. jobs.RootElement.EnumerateArray().Reverse()
                .Select(j => new PullRequestCheck(Str(j, "name"), GitLabCi.Status(Str(j, "status")), Str(j, "web_url")))];
        }
        catch (PipelineException)
        {
            return [];
        }
    }

    public async Task<IReadOnlyList<PullRequestFile>> FilesAsync(PullRequest mr, CancellationToken ct)
    {
        JsonElement[] changes;
        try
        {
            using var json = JsonDocument.Parse(await api.GetAsync($"{P}merge_requests/{mr.Number}/diffs?per_page=100", ct));
            changes = [.. json.RootElement.EnumerateArray().Select(c => c.Clone())];
        }
        catch (PipelineException)
        {
            // A GitLab older than 15.7 has its changes only.
            using var json = JsonDocument.Parse(await api.GetAsync($"{P}merge_requests/{mr.Number}/changes", ct));
            changes = [.. json.RootElement.GetProperty("changes").EnumerateArray().Select(c => c.Clone())];
        }
        return [.. changes.Select(c =>
        {
            var diff = Str(c, "diff");
            var lines = diff.Split('\n');
            var letter = True(c, "new_file") ? "A" : True(c, "deleted_file") ? "D" : True(c, "renamed_file") ? "R" : "M";
            return new PullRequestFile(Str(c, "new_path"), letter == "R" ? Str(c, "old_path") : null, letter,
                lines.Count(l => l.StartsWith('+') && !l.StartsWith("+++")), lines.Count(l => l.StartsWith('-') && !l.StartsWith("---")), diff);
        })];
    }

    public async Task<string?> FileAtAsync(string sha, string path, CancellationToken ct)
    {
        try
        {
            return await api.GetAsync($"{P}repository/files/{Uri.EscapeDataString(path)}/raw?ref={sha}", ct);
        }
        catch (PipelineException)
        {
            return null;
        }
    }

    public async Task<PullRequest> CreateAsync(string source, string target, string title, string body, bool draft, CancellationToken ct)
    {
        var made = await api.SendAsync(HttpMethod.Post, $"{P}merge_requests", new JsonObject
        {
            ["source_branch"] = source, ["target_branch"] = target, ["title"] = draft ? "Draft: " + title : title, ["description"] = body,
        }.ToJsonString(), ct);
        using var json = JsonDocument.Parse(made);
        return Merge(json.RootElement);
    }

    public Task CommentAsync(PullRequest mr, string body, CancellationToken ct) =>
        api.SendAsync(HttpMethod.Post, $"{P}merge_requests/{mr.Number}/notes", new JsonObject { ["body"] = body }.ToJsonString(), ct);

    /// <summary>GitLab approves; changes asked for and comments are notes.</summary>
    public async Task ReviewAsync(PullRequest mr, ReviewVerdict verdict, string body, CancellationToken ct)
    {
        if (verdict == ReviewVerdict.Approve)
            await api.SendAsync(HttpMethod.Post, $"{P}merge_requests/{mr.Number}/approve", null, ct);
        var note = verdict == ReviewVerdict.RequestChanges ? "**Changes requested**" + (body.Length > 0 ? "\n\n" + body : "") : body;
        if (note.Length > 0)
            await CommentAsync(mr, note, ct);
    }

    public async Task MergeAsync(PullRequest mr, MergeMethod method, bool deleteBranch, CancellationToken ct)
    {
        if (method == MergeMethod.Rebase)
            throw new PipelineException("GitLab merges by the project's own method: merge it, or squash it.");
        await api.SendAsync(HttpMethod.Put, $"{P}merge_requests/{mr.Number}/merge", new JsonObject
        {
            ["squash"] = method == MergeMethod.Squash, ["should_remove_source_branch"] = deleteBranch,
        }.ToJsonString(), ct);
    }

    public Task CloseAsync(PullRequest mr, CancellationToken ct) =>
        api.SendAsync(HttpMethod.Put, $"{P}merge_requests/{mr.Number}", new JsonObject { ["state_event"] = "close" }.ToJsonString(), ct);

    public string HeadRef(PullRequest mr) => $"merge-requests/{mr.Number}/head";

    static bool True(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    static string Username(JsonElement e, string name) => e.TryGetProperty(name, out var who) && who.ValueKind == JsonValueKind.Object ? Str(who, "username") : "";

    static PullRequest Merge(JsonElement m)
    {
        var draft = True(m, "draft") || True(m, "work_in_progress");
        var state = Str(m, "state") switch
        {
            "merged" => PullRequestState.Merged,
            "closed" or "locked" => PullRequestState.Closed,
            _ => draft ? PullRequestState.Draft : PullRequestState.Open,
        };
        var refs = m.TryGetProperty("diff_refs", out var r) && r.ValueKind == JsonValueKind.Object ? r : default;
        var status = Str(m, "detailed_merge_status") is { Length: > 0 } detailed ? detailed : Str(m, "merge_status");
        return new PullRequest(Num(m, "iid"), Str(m, "title"), Username(m, "author"), state, Str(m, "source_branch"), Str(m, "target_branch"),
            Date(m, "created_at"), Date(m, "updated_at"), Str(m, "web_url"))
        {
            Body = Str(m, "description"),
            HeadSha = refs.ValueKind == JsonValueKind.Object ? Str(refs, "head_sha") : Str(m, "sha"),
            BaseSha = refs.ValueKind == JsonValueKind.Object ? Str(refs, "base_sha") : "",
            IsFromFork = Num(m, "source_project_id") is { Length: > 0 } from && from != Num(m, "target_project_id"),
            Comments = m.TryGetProperty("user_notes_count", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetInt32() : 0,
            Reviewers = m.TryGetProperty("reviewers", out var rv) && rv.ValueKind == JsonValueKind.Array ? [.. rv.EnumerateArray().Select(x => Str(x, "username"))] : [],
            Labels = m.TryGetProperty("labels", out var l) && l.ValueKind == JsonValueKind.Array ? [.. l.EnumerateArray().Select(x => x.GetString() ?? "")] : [],
            Mergeable = status switch
            {
                "mergeable" or "can_be_merged" => true,
                "checking" or "unchecked" or "preparing" or "" => null,
                _ => false,
            },
            MergeNote = status switch
            {
                "conflict" or "cannot_be_merged" => "It conflicts with " + Str(m, "target_branch") + ".",
                "not_approved" => "It needs approval.",
                "ci_must_pass" or "ci_still_running" => "Its pipeline must pass.",
                "discussions_not_resolved" => "Its threads must be resolved.",
                "draft_status" => "It is a draft.",
                "need_rebase" => "It needs a rebase onto " + Str(m, "target_branch") + ".",
                _ => null,
            },
            ChangedFiles = int.TryParse(Str(m, "changes_count").TrimEnd('+'), out var files) ? files : 0,
        };
    }

    public void Dispose() => api.Dispose();
}
