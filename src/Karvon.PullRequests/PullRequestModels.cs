using Karvon.Pipelines;

namespace Karvon.PullRequests;

public enum PullRequestState
{
    Open,
    Draft,
    Merged,
    Closed,
}

/// <summary>Which of the repository's pull requests the window lists.</summary>
public enum PullRequestFilter
{
    Open,
    /// <summary>The open ones the signed-in user made.</summary>
    Mine,
    /// <summary>The open ones the signed-in user is asked to review.</summary>
    ReviewRequested,
    Closed,
    All,
}

public enum ReviewVerdict
{
    Comment,
    Approve,
    RequestChanges,
}

public enum MergeMethod
{
    Merge,
    Squash,
    Rebase,
}

/// <summary>
/// A GitHub pull request or a GitLab merge request: its number (GitLab's iid), what it asks (its source branch into the
/// target), who, when, and how it stands.
/// </summary>
public sealed record PullRequest(string Number, string Title, string Author, PullRequestState State, string SourceBranch, string TargetBranch,
    DateTimeOffset? Created, DateTimeOffset? Updated, string Url)
{
    /// <summary>Its description, Markdown.</summary>
    public string Body { get; init; } = "";

    public string HeadSha { get; init; } = "";

    public string BaseSha { get; init; } = "";

    /// <summary>Its branch is in another repository (a fork's), so it is fetched by the service's own ref.</summary>
    public bool IsFromFork { get; init; }

    public int Comments { get; init; }

    /// <summary>Who is asked to review it.</summary>
    public IReadOnlyList<string> Reviewers { get; init; } = [];

    public IReadOnlyList<string> Labels { get; init; } = [];

    /// <summary>Whether it can be merged as it is; null while the service still works it out (or the list did not say).</summary>
    public bool? Mergeable { get; init; }

    /// <summary>Why it cannot be merged yet, in the service's words ("conflicts", "blocked"), when it says.</summary>
    public string? MergeNote { get; init; }

    public int Additions { get; init; }

    public int Deletions { get; init; }

    public int ChangedFiles { get; init; }

    public bool IsOpen => State is PullRequestState.Open or PullRequestState.Draft;
}

/// <summary>A file the pull request changes: its letter (A, M, D, R), its counts, its patch (the service's unified diff of it).</summary>
public sealed record PullRequestFile(string Path, string? OldPath, string Letter, int Additions, int Deletions, string Patch);

/// <summary>A comment of the conversation, or one on a line of a file (a review's).</summary>
public sealed record PullRequestComment(string Id, string Author, string Body, DateTimeOffset? Created)
{
    public string? Path { get; init; }

    public int? Line { get; init; }
}

/// <summary>A reviewer's say: approved, changes requested, or a comment.</summary>
public sealed record PullRequestReview(string Author, ReviewVerdict Verdict, DateTimeOffset? At);

/// <summary>A check on its head commit (a CI job).</summary>
public sealed record PullRequestCheck(string Name, PipelineStatus Status, string Url);

/// <summary>A pull request whole, as its page shows it.</summary>
public sealed record PullRequestDetails(PullRequest PullRequest, IReadOnlyList<PullRequestComment> Comments, IReadOnlyList<PullRequestReview> Reviews,
    IReadOnlyList<PullRequestCheck> Checks);

public static class PullRequestStates
{
    public static string Text(this PullRequestState state) => state switch
    {
        PullRequestState.Open => "open",
        PullRequestState.Draft => "draft",
        PullRequestState.Merged => "merged",
        _ => "closed",
    };

    /// <summary>The <c>VsIcon</c> that shows it.</summary>
    public static string Icon(this PullRequestState state) => state switch
    {
        PullRequestState.Open => "PullRequest",
        PullRequestState.Draft => "PullRequestDraft",
        PullRequestState.Merged => "PullRequestMerged",
        _ => "PullRequestClosed",
    };

    public static string Text(this ReviewVerdict verdict) => verdict switch
    {
        ReviewVerdict.Approve => "approved",
        ReviewVerdict.RequestChanges => "requested changes",
        _ => "commented",
    };
}
