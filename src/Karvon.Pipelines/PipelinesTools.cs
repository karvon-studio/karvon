using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using Karvon.Claude;
using Karvon.Claude.Tools;
using Karvon.Pipelines.ViewModels;
using static Karvon.Claude.Tools.ToolInput;

namespace Karvon.Pipelines;

/// <summary>
/// Claude Code's tools for the repository's CI: its runs, a run's jobs and steps, a job's log, and (asking the user each
/// time) a run again or canceled. Through Karvon's own sign-in (the user's gh, a kept token), which Claude never sees.
/// </summary>
public static class PipelinesClaudeTools
{
    public static IEnumerable<IClaudeTool> Create(Func<PipelinesViewModel> pipelines) =>
    [
        new PipelineListTool(pipelines), new PipelineJobsTool(pipelines), new PipelineLogTool(pipelines),
        new PipelineRetryTool(pipelines), new PipelineCancelTool(pipelines),
    ];

    internal static string Row(Pipeline p) =>
        $"id {p.Id}: {p.Name} #{p.Number} [{p.Status.Text()}] {p.Title} ({p.Branch}, {p.ShortSha}, {p.Event}"
        + (p.Actor is { } actor ? $", by {actor}" : "") + $", {PipelineStatuses.Ago(p.Created, DateTimeOffset.Now)}"
        + (p.Started is not null ? $", {PipelineStatuses.Duration(p.Started, p.Finished, DateTimeOffset.Now)}" : "") + ")";
}

abstract class PipelinesTool(Func<PipelinesViewModel> pipelines) : IClaudeTool
{
    protected PipelinesViewModel Pipelines => pipelines();

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

    protected static string Required(JsonElement input, string name) =>
        Str(input, name) is { Length: > 0 } value ? value
        : input.TryGetProperty(name, out var number) && number.ValueKind == JsonValueKind.Number ? number.GetRawText()
        : throw new InvalidOperationException($"'{name}' is required.");

    protected static JsonObject PipelineProperty => new() { ["type"] = "string", ["description"] = "The run's id (from pipeline_list), or its number." };

    protected string Service => Pipelines.Repository?.Remote is { } remote ? $"{remote.ProviderName} ({remote.Authority}/{remote.Path})" : "the CI service";
}

/// <summary><c>pipeline_list</c>: the latest runs of the branch, or of all.</summary>
sealed class PipelineListTool(Func<PipelinesViewModel> pipelines) : PipelinesTool(pipelines)
{
    public override string Name => "pipeline_list";

    public override string Description =>
        "Lists the latest CI runs of the open repository (GitHub Actions' workflow runs, GitLab's pipelines): id, workflow and "
        + "number, state, commit, branch, event, who, when, how long. Of the checked-out branch by default.";

    public override JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["branch"] = new JsonObject { ["type"] = "string", ["description"] = "A branch, or \"*\" for every branch (default: the checked-out one)." },
            ["max"] = new JsonObject { ["type"] = "integer", ["description"] = "How many runs at most (default 15)." },
        },
    };

    public override ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Read, "Pipelines", Str(input, "branch"), "");

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var provider = Pipelines.ProviderOrThrow();
        var branch = Str(input, "branch") is { Length: > 0 } b ? b == "*" ? null : b : Pipelines.Repository?.Branch;
        var runs = await provider.PipelinesAsync(branch, Int(input, "max", 15, 1, 100), cancellationToken);
        return new ClaudeToolOutput(runs.Count == 0
            ? $"{Service} has no runs{(branch is null ? "" : $" on {branch}")}."
            : $"{Service}, {(branch is null ? "every branch" : branch)}:\n" + string.Join("\n", runs.Select(PipelinesClaudeTools.Row)));
    }
}

/// <summary><c>pipeline_jobs</c>: a run's jobs, their stages and steps.</summary>
sealed class PipelineJobsTool(Func<PipelinesViewModel> pipelines) : PipelinesTool(pipelines)
{
    public override string Name => "pipeline_jobs";

    public override string Description =>
        "Returns a CI run with its jobs (id, stage, state, duration) and, for GitHub Actions, each job's steps: what failed and where. "
        + "A job's id is what pipeline_log takes.";

    public override JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject { ["pipeline"] = PipelineProperty },
        ["required"] = new JsonArray("pipeline"),
    };

    public override ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Read, "Pipeline Jobs", Str(input, "pipeline"), "");

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var provider = Pipelines.ProviderOrThrow();
        var run = await Pipelines.FindAsync(Required(input, "pipeline"), cancellationToken);
        var jobs = await provider.JobsAsync(run, cancellationToken);
        var text = new StringBuilder(PipelinesClaudeTools.Row(run)).Append('\n').Append(run.Url);
        foreach (var job in jobs)
        {
            text.Append($"\n- job {job.Id}: {(job.Stage.Length > 0 ? job.Stage + " / " : "")}{job.Name} [{job.Status.Text()}{(job.AllowFailure ? ", allowed to fail" : "")}]");
            if (job.Started is not null)
                text.Append(' ').Append(PipelineStatuses.Duration(job.Started, job.Finished, DateTimeOffset.Now));
            foreach (var step in job.Steps.Where(s => s.Status is not PipelineStatus.Success and not PipelineStatus.Skipped))
                text.Append($"\n    step {step.Number} {step.Name} [{step.Status.Text()}]");
        }
        return new ClaudeToolOutput(text.ToString());
    }
}

/// <summary><c>pipeline_log</c>: a job's log (its end), plain.</summary>
sealed class PipelineLogTool(Func<PipelinesViewModel> pipelines) : PipelinesTool(pipelines)
{
    public override string Name => "pipeline_log";

    public override string Description =>
        "Returns the end of a CI job's log (plain text, secrets as the service masked them). Without 'job', the run's first failed "
        + "job's. GitHub gives a job's log once it is done; GitLab's while it runs.";

    public override JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["pipeline"] = PipelineProperty,
            ["job"] = new JsonObject { ["type"] = "string", ["description"] = "The job's id or name (from pipeline_jobs; default: the first that failed)." },
            ["lines"] = new JsonObject { ["type"] = "integer", ["description"] = "How many of its last lines (default 200)." },
        },
        ["required"] = new JsonArray("pipeline"),
    };

    public override ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Read, "Job Log", $"{Str(input, "pipeline")} {Str(input, "job")}".Trim(), "");

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var provider = Pipelines.ProviderOrThrow();
        var run = await Pipelines.FindAsync(Required(input, "pipeline"), cancellationToken);
        var jobs = await provider.JobsAsync(run, cancellationToken);
        var wanted = Str(input, "job") ?? (input.TryGetProperty("job", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetRawText() : null);
        var job = wanted is { Length: > 0 }
            ? jobs.FirstOrDefault(j => j.Id == wanted) ?? jobs.FirstOrDefault(j => string.Equals(j.Name, wanted, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"The run has no job {wanted}: {string.Join(", ", jobs.Select(j => $"{j.Id} {j.Name}"))}.")
            : jobs.FirstOrDefault(j => j.Status == PipelineStatus.Failed && !j.AllowFailure) ?? jobs.FirstOrDefault(j => j.Status == PipelineStatus.Failed)
                ?? throw new InvalidOperationException("No job of the run failed: name one ('job').");
        var log = PipelineLog.Plain(PipelineLog.Clean(await provider.LogAsync(job, cancellationToken), provider.Remote.Provider), Int(input, "lines", 200, 10, 2000));
        return new ClaudeToolOutput($"job {job.Id} {job.Name} [{job.Status.Text()}] of {run.Name} #{run.Number}:\n" + string.Join('\n', log.Split('\n').Select(Redaction.Text)));
    }
}

/// <summary><c>pipeline_retry</c>: a run again, its failed jobs or all (asks: it uses the service's CI time).</summary>
sealed class PipelineRetryTool(Func<PipelinesViewModel> pipelines) : PipelinesTool(pipelines)
{
    public override string Name => "pipeline_retry";

    public override string Description => "Runs a CI run again: its failed jobs (default), or all of them. The user approves each call.";

    public override JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["pipeline"] = PipelineProperty,
            ["failed_only"] = new JsonObject { ["type"] = "boolean", ["description"] = "Only the failed jobs (default true)." },
        },
        ["required"] = new JsonArray("pipeline"),
    };

    static bool FailedOnly(JsonElement input) => !(input.TryGetProperty("failed_only", out var v) && v.ValueKind == JsonValueKind.False);

    public override ClaudeToolPlan Plan(JsonElement input) =>
        new(ToolAccess.Write, "Re-run Pipeline", Str(input, "pipeline"), FailedOnly(input)
            ? $"Allow Claude to run the failed jobs of run {Str(input, "pipeline")} again?"
            : $"Allow Claude to run all of run {Str(input, "pipeline")} again?")
        {
            Warning = $"It runs on {Service} again, and uses its CI time.",
        };

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var provider = Pipelines.ProviderOrThrow();
        var run = await Pipelines.FindAsync(Required(input, "pipeline"), cancellationToken);
        await provider.RetryAsync(run, FailedOnly(input), cancellationToken);
        _ = Pipelines.RefreshAsync();
        return new ClaudeToolOutput($"{run.Name} #{run.Number} runs again ({(FailedOnly(input) ? "its failed jobs" : "all of it")}): see pipeline_jobs for how it goes.");
    }
}

/// <summary><c>pipeline_cancel</c>: a run that goes, stopped (asks).</summary>
sealed class PipelineCancelTool(Func<PipelinesViewModel> pipelines) : PipelinesTool(pipelines)
{
    public override string Name => "pipeline_cancel";

    public override string Description => "Cancels a CI run that is queued or running. The user approves each call.";

    public override JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject { ["pipeline"] = PipelineProperty },
        ["required"] = new JsonArray("pipeline"),
    };

    public override ClaudeToolPlan Plan(JsonElement input) =>
        new(ToolAccess.Write, "Cancel Pipeline", Str(input, "pipeline"), $"Allow Claude to cancel run {Str(input, "pipeline")}?");

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var provider = Pipelines.ProviderOrThrow();
        var run = await Pipelines.FindAsync(Required(input, "pipeline"), cancellationToken);
        if (!run.Status.IsActive())
            return ClaudeToolOutput.Error($"{run.Name} #{run.Number} is {run.Status.Text()}: there is nothing to cancel.");
        await provider.CancelAsync(run, cancellationToken);
        _ = Pipelines.RefreshAsync();
        return new ClaudeToolOutput($"{run.Name} #{run.Number} is being canceled.");
    }
}
