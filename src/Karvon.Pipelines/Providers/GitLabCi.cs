using System.Text.Json;
using static Karvon.Pipelines.Providers.GitHubActions;

namespace Karvon.Pipelines.Providers;

/// <summary>
/// GitLab CI (gitlab.com's or a company's own): a project's pipelines, their jobs by stage, a job's trace (as it runs),
/// through REST v4 with a personal access token Karvon keeps (read_api to look, api to retry and cancel).
/// </summary>
public sealed class GitLabCi : IPipelineProvider
{
    readonly GitLabApi _api;

    public GitLabCi(RepoRemote remote, string token, HttpMessageHandler? handler = null) : this(new GitLabApi(remote, token, handler))
    {
    }

    public GitLabCi(GitLabApi api) => _api = api;

    public RepoRemote Remote => _api.Remote;

    public bool LogsWhileRunning => true;

    Task<string> GetAsync(string path, CancellationToken ct) => _api.GetAsync(_api.Project + path, ct);

    Task PostAsync(string path, CancellationToken ct) => _api.SendAsync(HttpMethod.Post, _api.Project + path, null, ct);

    public async Task<IReadOnlyList<Pipeline>> PipelinesAsync(string? branch, int count, CancellationToken ct)
    {
        var query = $"pipelines?per_page={count}&order_by=id&sort=desc" + (branch is { Length: > 0 } ? "&ref=" + Uri.EscapeDataString(branch) : "");
        using var json = JsonDocument.Parse(await GetAsync(query, ct));
        return [.. json.RootElement.EnumerateArray().Select(Run)];
    }

    public async Task<Pipeline> PipelineAsync(string id, CancellationToken ct)
    {
        using var json = JsonDocument.Parse(await GetAsync($"pipelines/{id}", ct));
        return Run(json.RootElement);
    }

    public async Task<IReadOnlyList<PipelineJob>> JobsAsync(Pipeline pipeline, CancellationToken ct)
    {
        using var json = JsonDocument.Parse(await GetAsync($"pipelines/{pipeline.Id}/jobs?per_page=100", ct));
        // Oldest first, as the stages run.
        return [.. json.RootElement.EnumerateArray().Reverse().Select(job => new PipelineJob(
            Num(job, "id"), Str(job, "name"), Str(job, "stage"), Status(Str(job, "status")), Date(job, "started_at"), Date(job, "finished_at"),
            Str(job, "web_url"), [], job.TryGetProperty("allow_failure", out var allow) && allow.ValueKind == JsonValueKind.True))];
    }

    public Task<string> LogAsync(PipelineJob job, CancellationToken ct) => GetAsync($"jobs/{job.Id}/trace", ct);

    public Task RetryAsync(Pipeline pipeline, bool failedOnly, CancellationToken ct) =>
        failedOnly ? PostAsync($"pipelines/{pipeline.Id}/retry", ct) : PostAsync("pipeline?ref=" + Uri.EscapeDataString(pipeline.Branch), ct);

    public Task CancelAsync(Pipeline pipeline, CancellationToken ct) => PostAsync($"pipelines/{pipeline.Id}/cancel", ct);

    public Task RetryJobAsync(PipelineJob job, CancellationToken ct) => PostAsync($"jobs/{job.Id}/{(job.CanPlay ? "play" : "retry")}", ct);

    static Pipeline Run(JsonElement run) => new(
        Num(run, "id"), Num(run, "id"), Str(run, "name") is { Length: > 0 } name ? name : "Pipeline", "", Str(run, "ref"), Str(run, "sha"), Str(run, "source"),
        Status(Str(run, "status")), Date(run, "created_at"), Date(run, "started_at") ?? Date(run, "created_at"),
        Status(Str(run, "status")).IsActive() ? null : Date(run, "finished_at") ?? Date(run, "updated_at"), Str(run, "web_url"),
        run.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object ? Str(user, "username") : null);

    internal static PipelineStatus Status(string status) => status switch
    {
        "running" => PipelineStatus.Running,
        "success" => PipelineStatus.Success,
        "failed" => PipelineStatus.Failed,
        "canceled" or "canceling" => PipelineStatus.Canceled,
        "skipped" => PipelineStatus.Skipped,
        "manual" => PipelineStatus.Manual,
        "scheduled" or "waiting_for_resource" or "waiting_for_callback" => PipelineStatus.Waiting,
        _ => PipelineStatus.Queued,
    };

    public void Dispose() => _api.Dispose();
}
