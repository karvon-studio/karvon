using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Karvon.Workspace;

namespace Karvon.Pipelines.Providers;

/// <summary>How GitHub's REST API is reached: the user's gh CLI (its own sign-in), or a token Karvon keeps.</summary>
public interface IGitHubApi : IDisposable
{
    Task<string> GetAsync(string path, CancellationToken ct);

    Task PostAsync(string path, CancellationToken ct);

    /// <summary>A request with a method of its own (POST, PUT, PATCH, DELETE) and a JSON body, if any: what GitHub answers.</summary>
    Task<string> SendAsync(string method, string path, string? json, CancellationToken ct);
}

/// <summary>
/// GitHub's API through the user's own <c>gh</c> (gh api): signed in as gh is, its token never Karvon's. Its errors are
/// gh's ("HTTP 404: Not Found").
/// </summary>
public sealed class GhCliApi(string gh, string host) : IGitHubApi
{
    /// <summary>
    /// The gh CLI: KARVON_GH_PATH's (off: none, as the tests have it), else on PATH or where its installers put it (a
    /// desktop launcher's PATH may not have ~/.local/bin).
    /// </summary>
    public static string? Find()
    {
        if (Environment.GetEnvironmentVariable("KARVON_GH_PATH") is { Length: > 0 } given)
            return given == "off" ? null : given;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] more = [Path.Combine(home, ".local", "bin"), "/usr/local/bin", "/opt/homebrew/bin", "/usr/bin"];
        var path = string.Join(Path.PathSeparator, [Environment.GetEnvironmentVariable("PATH") ?? "", .. more]);
        return TaskVariables.Find("gh", path, "/");
    }

    /// <summary>Whether gh is signed in to the host.</summary>
    public static async Task<bool> SignedInAsync(string gh, string host, CancellationToken ct) =>
        (await RunAsync(gh, ["auth", "status", "--hostname", host], ct)).Code == 0;

    public async Task<string> GetAsync(string path, CancellationToken ct)
    {
        var (code, output, error) = await RunAsync(gh, ["api", "--hostname", host, "-H", "Accept: application/vnd.github+json", path], ct);
        return code == 0 ? output : throw new PipelineException(Message(error, output));
    }

    public async Task PostAsync(string path, CancellationToken ct)
    {
        var (code, output, error) = await RunAsync(gh, ["api", "--hostname", host, "--method", "POST", "-H", "Accept: application/vnd.github+json", path], ct);
        if (code != 0)
            throw new PipelineException(Message(error, output));
    }

    public async Task<string> SendAsync(string method, string path, string? json, CancellationToken ct)
    {
        List<string> args = ["api", "--hostname", host, "--method", method, "-H", "Accept: application/vnd.github+json", path];
        if (json is not null)
            args.AddRange(["--input", "-"]);
        var (code, output, error) = await RunAsync(gh, args, ct, json);
        return code == 0 ? output : throw new PipelineException(Message(error, output));
    }

    /// <summary>gh's error, and GitHub's message when the body says one.</summary>
    static string Message(string error, string output)
    {
        var text = error.Trim().Replace("gh: ", "");
        try
        {
            if (JsonDocument.Parse(output).RootElement.TryGetProperty("message", out var message) && message.GetString() is { Length: > 0 } said && !text.Contains(said))
                text = $"{text} ({said})".Trim();
        }
        catch (JsonException)
        {
        }
        return text.Length > 0 ? "GitHub: " + text : "gh failed.";
    }

    internal static async Task<(int Code, string Output, string Error)> RunAsync(string gh, IReadOnlyList<string> args, CancellationToken ct, string? input = null)
    {
        var psi = new ProcessStartInfo(gh)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = input is not null,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetTempPath(),
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        // Never a prompt the user cannot see, nor colors or an update notice in what is read.
        psi.Environment["GH_PROMPT_DISABLED"] = "1";
        psi.Environment["GH_NO_UPDATE_NOTIFIER"] = "1";
        psi.Environment["NO_COLOR"] = "1";
        using var process = Process.Start(psi) ?? throw new PipelineException("gh could not be started.");
        if (input is not null)
        {
            await process.StandardInput.BaseStream.WriteAsync(new UTF8Encoding(false).GetBytes(input), ct);
            process.StandardInput.Close();
        }
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            throw;
        }
        return (process.ExitCode, await output, await error);
    }

    public void Dispose()
    {
    }
}

/// <summary>GitHub's API with a token Karvon keeps (the keyring): when there is no gh, or it is not signed in.</summary>
public sealed class GitHubTokenApi : IGitHubApi
{
    readonly HttpClient _http;

    public GitHubTokenApi(string host, string token, HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? new HttpClientHandler(), disposeHandler: true)
        {
            BaseAddress = new Uri(host == "github.com" ? "https://api.github.com/" : $"https://{host}/api/v3/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Karvon-Studio");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public async Task<string> GetAsync(string path, CancellationToken ct)
    {
        using var response = await _http.GetAsync(path, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        return response.IsSuccessStatusCode ? body : throw new PipelineException(Message(response.StatusCode, body));
    }

    public async Task PostAsync(string path, CancellationToken ct)
    {
        using var response = await _http.PostAsync(path, null, ct);
        if (!response.IsSuccessStatusCode)
            throw new PipelineException(Message(response.StatusCode, await response.Content.ReadAsStringAsync(ct)));
    }

    public async Task<string> SendAsync(string method, string path, string? json, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (json is not null)
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        return response.IsSuccessStatusCode ? body : throw new PipelineException(Message(response.StatusCode, body));
    }

    static string Message(HttpStatusCode status, string body)
    {
        var said = "";
        try
        {
            said = JsonDocument.Parse(body).RootElement.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
        }
        catch (JsonException)
        {
        }
        return status switch
        {
            HttpStatusCode.Unauthorized => "GitHub did not accept the token (401): give a new one.",
            HttpStatusCode.Forbidden or HttpStatusCode.NotFound when said.Length == 0 => $"GitHub: HTTP {(int)status}.",
            _ => $"GitHub: HTTP {(int)status}" + (said.Length > 0 ? $": {said}" : "") + ".",
        };
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>GitHub Actions: a repository's workflow runs, their jobs and steps, their logs (once a job is done).</summary>
public sealed class GitHubActions(RepoRemote remote, IGitHubApi api) : IPipelineProvider
{
    public RepoRemote Remote => remote;

    public bool LogsWhileRunning => false;

    string Repo => "repos/" + remote.Path;

    public async Task<IReadOnlyList<Pipeline>> PipelinesAsync(string? branch, int count, CancellationToken ct)
    {
        var query = $"{Repo}/actions/runs?per_page={count}" + (branch is { Length: > 0 } ? "&branch=" + Uri.EscapeDataString(branch) : "");
        using var json = JsonDocument.Parse(await api.GetAsync(query, ct));
        return [.. json.RootElement.GetProperty("workflow_runs").EnumerateArray().Select(Run)];
    }

    public async Task<Pipeline> PipelineAsync(string id, CancellationToken ct)
    {
        using var json = JsonDocument.Parse(await api.GetAsync($"{Repo}/actions/runs/{id}", ct));
        return Run(json.RootElement);
    }

    public async Task<IReadOnlyList<PipelineJob>> JobsAsync(Pipeline pipeline, CancellationToken ct)
    {
        using var json = JsonDocument.Parse(await api.GetAsync($"{Repo}/actions/runs/{pipeline.Id}/jobs?per_page=100", ct));
        return [.. json.RootElement.GetProperty("jobs").EnumerateArray().Select(job => new PipelineJob(
            Num(job, "id"), Str(job, "name"), "", Status(Str(job, "status"), Str(job, "conclusion")), Date(job, "started_at"), Date(job, "completed_at"),
            Str(job, "html_url"),
            job.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array
                ? [.. steps.EnumerateArray().Select(s => new PipelineStep(s.GetProperty("number").GetInt32(), Str(s, "name"),
                    Status(Str(s, "status"), Str(s, "conclusion")), Date(s, "started_at"), Date(s, "completed_at")))]
                : []))];
    }

    /// <summary>
    /// A finished job's log; one with none (it never started: a billing block, a runner that never came; or its log
    /// expired) says why by its check run's annotations, as GitHub's page does.
    /// </summary>
    public async Task<string> LogAsync(PipelineJob job, CancellationToken ct)
    {
        if (job.Status.IsActive())
            throw new PipelineException("GitHub gives a job's log once the job is done; its steps show how far it is.");
        try
        {
            return await api.GetAsync($"{Repo}/actions/jobs/{job.Id}/logs", ct);
        }
        catch (PipelineException)
        {
            using var json = JsonDocument.Parse(await api.GetAsync($"{Repo}/check-runs/{job.Id}/annotations", ct));
            var notes = json.RootElement.EnumerateArray()
                .Select(a => (Level: Str(a, "annotation_level"), Text: string.Join(": ", new[] { Str(a, "title"), Str(a, "message") }.Where(t => t.Length > 0))))
                .Where(a => a.Text.Length > 0)
                .Select(a => (a.Level == "failure" ? "##[error]" : a.Level == "warning" ? "##[warning]" : "##[notice]") + a.Text)
                .ToList();
            return notes.Count > 0
                ? "GitHub has no log of this job; what it says of it:\n" + string.Join('\n', notes) + "\n"
                : throw new PipelineException("GitHub has no log of this job: it did not run, or its log has expired.");
        }
    }

    public Task RetryAsync(Pipeline pipeline, bool failedOnly, CancellationToken ct) =>
        api.PostAsync($"{Repo}/actions/runs/{pipeline.Id}/{(failedOnly ? "rerun-failed-jobs" : "rerun")}", ct);

    public Task CancelAsync(Pipeline pipeline, CancellationToken ct) => api.PostAsync($"{Repo}/actions/runs/{pipeline.Id}/cancel", ct);

    public Task RetryJobAsync(PipelineJob job, CancellationToken ct) => api.PostAsync($"{Repo}/actions/jobs/{job.Id}/rerun", ct);

    static Pipeline Run(JsonElement run) => new(
        Num(run, "id"), Num(run, "run_number"), Str(run, "name"),
        Str(run, "display_title") is { Length: > 0 } title ? title : run.TryGetProperty("head_commit", out var commit) && commit.ValueKind == JsonValueKind.Object ? Str(commit, "message").Split('\n')[0] : "",
        Str(run, "head_branch"), Str(run, "head_sha"), Str(run, "event"), Status(Str(run, "status"), Str(run, "conclusion")),
        Date(run, "created_at"), Date(run, "run_started_at"), Str(run, "status") == "completed" ? Date(run, "updated_at") : null, Str(run, "html_url"),
        run.TryGetProperty("triggering_actor", out var actor) && actor.ValueKind == JsonValueKind.Object ? Str(actor, "login")
            : run.TryGetProperty("actor", out var a) && a.ValueKind == JsonValueKind.Object ? Str(a, "login") : null,
        run.TryGetProperty("run_attempt", out var attempt) && attempt.ValueKind == JsonValueKind.Number ? attempt.GetInt32() : 1);

    /// <summary>GitHub's status, and its conclusion once completed.</summary>
    internal static PipelineStatus Status(string status, string conclusion) => status switch
    {
        "completed" => conclusion switch
        {
            "success" => PipelineStatus.Success,
            "cancelled" => PipelineStatus.Canceled,
            "skipped" or "neutral" => PipelineStatus.Skipped,
            "action_required" => PipelineStatus.Waiting,
            _ => PipelineStatus.Failed,
        },
        "in_progress" => PipelineStatus.Running,
        "waiting" => PipelineStatus.Waiting,
        _ => PipelineStatus.Queued,
    };

    internal static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    internal static string Num(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetRawText() : "";

    internal static DateTimeOffset? Date(JsonElement e, string name) => DateTimeOffset.TryParse(Str(e, name), out var at) ? at : null;

    public void Dispose() => api.Dispose();
}
