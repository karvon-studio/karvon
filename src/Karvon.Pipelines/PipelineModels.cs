namespace Karvon.Pipelines;

/// <summary>A run's, a job's or a step's state, as both services have it (GitHub's status and conclusion in one).</summary>
public enum PipelineStatus
{
    Queued,
    Waiting,
    Running,
    Success,
    Failed,
    Canceled,
    Skipped,
    Manual,
}

public static class PipelineStatuses
{
    public static bool IsActive(this PipelineStatus status) => status is PipelineStatus.Queued or PipelineStatus.Waiting or PipelineStatus.Running;

    /// <summary>As the services' pages say it.</summary>
    public static string Text(this PipelineStatus status) => status switch
    {
        PipelineStatus.Queued => "queued",
        PipelineStatus.Waiting => "waiting",
        PipelineStatus.Running => "running",
        PipelineStatus.Success => "passed",
        PipelineStatus.Failed => "failed",
        PipelineStatus.Canceled => "canceled",
        PipelineStatus.Skipped => "skipped",
        _ => "manual",
    };

    /// <summary>The <c>VsIcon</c> that shows it.</summary>
    public static string Icon(this PipelineStatus status) => status switch
    {
        PipelineStatus.Success => "Ok",
        PipelineStatus.Failed => "Error",
        PipelineStatus.Running => "Running",
        PipelineStatus.Canceled or PipelineStatus.Skipped => "Discard",
        PipelineStatus.Manual => "DockerPlay",
        _ => "Pending",
    };

    /// <summary>How long from <paramref name="start"/> to <paramref name="end"/> (or now, while it runs), as "1m 05s".</summary>
    public static string Duration(DateTimeOffset? start, DateTimeOffset? end, DateTimeOffset now)
    {
        if (start is not { } from)
            return "";
        var span = (end ?? now) - from;
        if (span < TimeSpan.Zero)
            span = TimeSpan.Zero;
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes:00}m" : span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes}m {span.Seconds:00}s" : $"{span.Seconds}s";
    }

    /// <summary>"3 min ago", "yesterday".</summary>
    public static string Ago(DateTimeOffset? at, DateTimeOffset now)
    {
        if (at is not { } when)
            return "";
        var span = now - when;
        return span.TotalSeconds < 60 ? "just now"
            : span.TotalMinutes < 60 ? $"{(int)span.TotalMinutes} min ago"
            : span.TotalHours < 24 ? $"{(int)span.TotalHours} h ago"
            : span.TotalDays < 2 ? "yesterday"
            : $"{(int)span.TotalDays} days ago";
    }
}

/// <summary>
/// A run of the repository's CI: GitHub Actions' workflow run, GitLab's pipeline. <paramref name="Title"/> is its commit's
/// subject (or what the service shows for it); <paramref name="Name"/> the workflow's.
/// </summary>
public sealed record Pipeline(string Id, string Number, string Name, string Title, string Branch, string Sha, string Event, PipelineStatus Status,
    DateTimeOffset? Created, DateTimeOffset? Started, DateTimeOffset? Finished, string Url, string? Actor = null, int Attempt = 1)
{
    public string ShortSha => Sha.Length > 8 ? Sha[..8] : Sha;
}

/// <summary>A job of a run; <paramref name="Stage"/> is GitLab's (GitHub's jobs have none).</summary>
public sealed record PipelineJob(string Id, string Name, string Stage, PipelineStatus Status, DateTimeOffset? Started, DateTimeOffset? Finished, string Url,
    IReadOnlyList<PipelineStep> Steps, bool AllowFailure = false)
{
    /// <summary>A manual job, to be started by hand (GitLab's play).</summary>
    public bool CanPlay => Status == PipelineStatus.Manual;
}

/// <summary>A step of a GitHub Actions job.</summary>
public sealed record PipelineStep(int Number, string Name, PipelineStatus Status, DateTimeOffset? Started, DateTimeOffset? Finished);

/// <summary>What the service said, as it said it (a token refused, a run not found).</summary>
public sealed class PipelineException(string message) : Exception(message);
