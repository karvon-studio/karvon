using Karvon.Pipelines;

namespace Karvon.PullRequests.Providers;

/// <summary>A repository's pull requests (GitHub's) or merge requests (GitLab's), as the window, the pages and Claude's tools use them.</summary>
public interface IPullRequestProvider : IDisposable
{
    RepoRemote Remote { get; }

    /// <summary>"pull request" or "merge request".</summary>
    string Noun { get; }

    /// <summary>"PR" or "MR".</summary>
    string Short { get; }

    /// <summary>"#" (GitHub's) or "!" (GitLab's), before a number.</summary>
    string Prefix { get; }

    /// <summary>Whether it merges by rebasing too (GitHub); GitLab merges by the project's method, squashed or not.</summary>
    bool CanRebase { get; }

    /// <summary>The signed-in user's login.</summary>
    Task<string> UserAsync(CancellationToken ct);

    /// <summary>The repository's default branch: where a new one goes.</summary>
    Task<string> DefaultBranchAsync(CancellationToken ct);

    /// <summary>The latest updated first: open, closed (merged too) or all.</summary>
    Task<IReadOnlyList<PullRequest>> ListAsync(PullRequestFilter state, int count, CancellationToken ct);

    /// <summary>One whole: its description, counts and whether it can be merged.</summary>
    Task<PullRequest> GetAsync(string number, CancellationToken ct);

    Task<PullRequestDetails> DetailsAsync(PullRequest pullRequest, CancellationToken ct);

    Task<IReadOnlyList<PullRequestFile>> FilesAsync(PullRequest pullRequest, CancellationToken ct);

    /// <summary>A file's content at a commit (the base's or the head's), or null when it is not there.</summary>
    Task<string?> FileAtAsync(string sha, string path, CancellationToken ct);

    Task<PullRequest> CreateAsync(string source, string target, string title, string body, bool draft, CancellationToken ct);

    Task CommentAsync(PullRequest pullRequest, string body, CancellationToken ct);

    /// <summary>Approved, changes requested (GitLab: a comment saying so), or a comment; with what the reviewer says.</summary>
    Task ReviewAsync(PullRequest pullRequest, ReviewVerdict verdict, string body, CancellationToken ct);

    Task MergeAsync(PullRequest pullRequest, MergeMethod method, bool deleteBranch, CancellationToken ct);

    Task CloseAsync(PullRequest pullRequest, CancellationToken ct);

    /// <summary>The ref its head is fetched by when it is a fork's ("pull/7/head", "merge-requests/7/head").</summary>
    string HeadRef(PullRequest pullRequest);
}
