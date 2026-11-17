namespace Karvon.Pipelines.Providers;

/// <summary>A repository's CI service, as the Pipelines window and Claude's tools use it.</summary>
public interface IPipelineProvider : IDisposable
{
    RepoRemote Remote { get; }

    /// <summary>A job's log while it still runs (GitLab's trace); GitHub gives it once the job is done.</summary>
    bool LogsWhileRunning { get; }

    /// <summary>The latest runs, of a branch or of all.</summary>
    Task<IReadOnlyList<Pipeline>> PipelinesAsync(string? branch, int count, CancellationToken ct);

    Task<Pipeline> PipelineAsync(string id, CancellationToken ct);

    Task<IReadOnlyList<PipelineJob>> JobsAsync(Pipeline pipeline, CancellationToken ct);

    /// <summary>A job's log as the service has it (its own markers in it; see <see cref="PipelineLog"/>).</summary>
    Task<string> LogAsync(PipelineJob job, CancellationToken ct);

    /// <summary>The run again: its failed jobs, or all of it (GitLab: a new pipeline of its branch).</summary>
    Task RetryAsync(Pipeline pipeline, bool failedOnly, CancellationToken ct);

    Task CancelAsync(Pipeline pipeline, CancellationToken ct);

    /// <summary>One job again; a manual one started.</summary>
    Task RetryJobAsync(PipelineJob job, CancellationToken ct);

    /// <summary>
    /// A run started by hand on a branch or tag, with its inputs (GitHub: a workflow's, by its file or id; GitLab: the
    /// project's pipeline, the inputs its variables): the run it made, once the service shows it (null when not yet).
    /// </summary>
    Task<Pipeline?> DispatchAsync(string? workflow, string reference, IReadOnlyDictionary<string, string> inputs, CancellationToken ct);

    /// <summary>What the account (the repository's owner) used of the service's CI this month, as the service tells it.</summary>
    Task<string> UsageAsync(CancellationToken ct);
}
