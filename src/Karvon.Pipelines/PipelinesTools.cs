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
/// Claude Code's tools for the CI of the workspace's repositories (the window's, or one Claude names: a workspace of
/// several): their runs, a run's jobs and steps, a job's log (its failed steps, a step, the lines that match), a run
/// waited for, the account's minutes this month; and, asking the user each time, a run started by hand, again or
/// canceled. Through Karvon's own sign-in (the user's gh, a kept token), which Claude never sees.
/// </summary>
public static class PipelinesClaudeTools
{
    public static IEnumerable<IClaudeTool> Create(Func<PipelinesViewModel> pipelines) =>
    [
        new PipelineListTool(pipelines), new PipelineJobsTool(pipelines), new PipelineLogTool(pipelines), new PipelineWatchTool(pipelines),
        new PipelineUsageTool(pipelines), new PipelineDispatchTool(pipelines), new PipelineRetryTool(pipelines), new PipelineCancelTool(pipelines),
    ];

    internal static string Row(Pipeline p) =>
        $"id {p.Id}: {p.Name} #{p.Number} [{p.Status.Text()}] {p.Title} ({p.Branch}, {p.ShortSha}, {p.Event}"
        + (p.Actor is { } actor ? $", by {actor}" : "") + $", {PipelineStatuses.Ago(p.Created, DateTimeOffset.Now)}"
        + (p.Started is not null ? $", {PipelineStatuses.Duration(p.Started, p.Finished, DateTimeOffset.Now)}" : "") + ")";

    /// <summary>A run, its jobs and their steps that did not succeed, as pipeline_jobs and pipeline_watch tell them.</summary>
    internal static string Jobs(Pipeline run, IReadOnlyList<PipelineJob> jobs)
    {
        var text = new StringBuilder(Row(run)).Append('\n').Append(run.Url);
        foreach (var job in jobs)
        {
            text.Append($"\n- job {job.Id}: {(job.Stage.Length > 0 ? job.Stage + " / " : "")}{job.Name} [{job.Status.Text()}{(job.AllowFailure ? ", allowed to fail" : "")}]");
            if (job.Started is not null)
                text.Append(' ').Append(PipelineStatuses.Duration(job.Started, job.Finished, DateTimeOffset.Now));
            foreach (var step in job.Steps.Where(s => s.Status is not PipelineStatus.Success and not PipelineStatus.Skipped))
                text.Append($"\n    step {step.Number} {step.Name} [{step.Status.Text()}]");
        }
        return text.ToString();
    }
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

    /// <summary>The repository the call names (owner/name, its name, or its folder); the window's when none.</summary>
    protected static JsonObject RepoProperty => new()
    {
        ["type"] = "string",
        ["description"] = "The repository: owner/name, its name or its folder in the workspace (default: the one the Pipelines window shows). "
            + "A workspace of several repositories names each one; pipeline_list says which there are.",
    };

    /// <summary>The schema with the repository beside the call's own properties.</summary>
    protected static JsonObject Schema(JsonObject properties, params string[] required)
    {
        properties["repo"] = RepoProperty;
        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required.Length > 0)
            schema["required"] = new JsonArray([.. required.Select(r => (JsonNode)r)]);
        return schema;
    }

    protected Task<PipelineTarget> TargetAsync(JsonElement input, CancellationToken ct) => Pipelines.TargetAsync(Str(input, "repo"), ct);

    /// <summary>A run of the target by its id or number: the window's runs first when it is the window's repository.</summary>
    protected Task<Pipeline> RunOf(JsonElement input, PipelineTarget target, CancellationToken ct) =>
        Str(input, "repo") is { Length: > 0 } ? PipelinesViewModel.FindAsync(target, Required(input, "pipeline"), ct) : Pipelines.FindAsync(Required(input, "pipeline"), ct);

    protected static string Target(JsonElement input) => Str(input, "repo") is { Length: > 0 } repo ? repo + " " : "";
}

/// <summary><c>pipeline_list</c>: the latest runs of the branch, or of all; and the workspace's other repositories.</summary>
sealed class PipelineListTool(Func<PipelinesViewModel> pipelines) : PipelinesTool(pipelines)
{
    public override string Name => "pipeline_list";

    public override string Description =>
        "Lists the latest CI runs of a repository (GitHub Actions' workflow runs, GitLab's pipelines): id, workflow and number, state, "
        + "commit, branch, event, who, when, how long. Of its checked-out branch by default. It ends with the workspace's other "
        + "repositories, which repo names.";

    public override JsonObject InputSchema => Schema(new JsonObject
    {
        ["branch"] = new JsonObject { ["type"] = "string", ["description"] = "A branch, or \"*\" for every branch (default: the checked-out one)." },
        ["max"] = new JsonObject { ["type"] = "integer", ["description"] = "How many runs at most (default 15)." },
    });

    public override ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Read, "Pipelines", $"{Target(input)}{Str(input, "branch")}".Trim(), "");

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var target = await TargetAsync(input, cancellationToken);
        var branch = Str(input, "branch") is { Length: > 0 } b ? b == "*" ? null : b : target.Branch;
        var runs = await target.Provider.PipelinesAsync(branch, Int(input, "max", 15, 1, 100), cancellationToken);
        var text = new StringBuilder(runs.Count == 0
            ? $"{target.Service} has no runs{(branch is null ? "" : $" on {branch}")}."
            : $"{target.Service}, {(branch is null ? "every branch" : branch)}:\n" + string.Join("\n", runs.Select(PipelinesClaudeTools.Row)));
        var others = (await Pipelines.WorkspaceRepositoriesAsync()).Where(r => r.Folder != target.Folder && r.Remote is not null).ToList();
        if (others.Count > 0)
            text.Append($"\nThe workspace's other repositories (repo): {string.Join(", ", others.Select(r => $"{Path.GetFileName(r.Folder)} ({r.Remote!.Path})"))}.");
        return new ClaudeToolOutput(text.ToString());
    }
}

/// <summary><c>pipeline_jobs</c>: a run's jobs, their stages and steps.</summary>
sealed class PipelineJobsTool(Func<PipelinesViewModel> pipelines) : PipelinesTool(pipelines)
{
    public override string Name => "pipeline_jobs";

    public override string Description =>
        "Returns a CI run with its jobs (id, stage, state, duration) and, for GitHub Actions, each job's steps that did not succeed: "
        + "what failed and where. A job's id is what pipeline_log takes.";

    public override JsonObject InputSchema => Schema(new JsonObject { ["pipeline"] = PipelineProperty }, "pipeline");

    public override ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Read, "Pipeline Jobs", Target(input) + Str(input, "pipeline"), "");

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var target = await TargetAsync(input, cancellationToken);
        var run = await RunOf(input, target, cancellationToken);
        return new ClaudeToolOutput(PipelinesClaudeTools.Jobs(run, await target.Provider.JobsAsync(run, cancellationToken)));
    }
}

/// <summary><c>pipeline_log</c>: a job's log, plain: its end, its failed steps or one step, or the lines that match.</summary>
sealed class PipelineLogTool(Func<PipelinesViewModel> pipelines) : PipelinesTool(pipelines)
{
    public override string Name => "pipeline_log";

    public override string Description =>
        "Returns a CI job's log as plain text (secrets as the service masked them): its end; or only its failed steps (failed) or one "
        + "step (step, GitHub Actions); or only the lines that match grep, each with context lines around it. Without 'job', the run's "
        + "first failed job's. GitHub gives a job's log once it is done; GitLab's while it runs.";

    public override JsonObject InputSchema => Schema(new JsonObject
    {
        ["pipeline"] = PipelineProperty,
        ["job"] = new JsonObject { ["type"] = "string", ["description"] = "The job's id or name (from pipeline_jobs; default: the first that failed)." },
        ["failed"] = new JsonObject { ["type"] = "boolean", ["description"] = "Only the steps that failed (GitHub Actions)." },
        ["step"] = new JsonObject { ["type"] = "string", ["description"] = "Only this step, by its number or name (GitHub Actions)." },
        ["grep"] = new JsonObject { ["type"] = "string", ["description"] = "Only the lines that match this (a regular expression, case aside), with their line numbers." },
        ["context"] = new JsonObject { ["type"] = "integer", ["description"] = "Lines around each match (default 3)." },
        ["lines"] = new JsonObject { ["type"] = "integer", ["description"] = "How many lines at most (default 200: the last ones; with grep, the first matches)." },
    }, "pipeline");

    public override ClaudeToolPlan Plan(JsonElement input) =>
        new(ToolAccess.Read, "Job Log", $"{Target(input)}{Str(input, "pipeline")} {Str(input, "job")} {Str(input, "grep")}".Trim(), "");

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var target = await TargetAsync(input, cancellationToken);
        var provider = target.Provider;
        var run = await RunOf(input, target, cancellationToken);
        var jobs = await provider.JobsAsync(run, cancellationToken);
        var wanted = Str(input, "job") ?? (input.TryGetProperty("job", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetRawText() : null);
        var job = wanted is { Length: > 0 }
            ? jobs.FirstOrDefault(j => j.Id == wanted) ?? jobs.FirstOrDefault(j => string.Equals(j.Name, wanted, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"The run has no job {wanted}: {string.Join(", ", jobs.Select(j => $"{j.Id} {j.Name}"))}.")
            : jobs.FirstOrDefault(j => j.Status == PipelineStatus.Failed && !j.AllowFailure) ?? jobs.FirstOrDefault(j => j.Status == PipelineStatus.Failed)
                ?? throw new InvalidOperationException("No job of the run failed: name one ('job').");
        var raw = await provider.LogAsync(job, cancellationToken);
        var heading = $"job {job.Id} {job.Name} [{job.Status.Text()}] of {run.Name} #{run.Number}";

        // Its failed steps, or one step: by their times (GitHub marks no step in the log).
        var stepWanted = Str(input, "step") ?? (input.TryGetProperty("step", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetRawText() : null);
        if (Bool(input, "failed") || stepWanted is { Length: > 0 })
        {
            if (provider.Remote.Provider != CiProvider.GitHub)
                return ClaudeToolOutput.Error("GitLab's job has no steps: read its log whole, or with grep.");
            var steps = stepWanted is { Length: > 0 }
                ? job.Steps.Where(st => st.Number.ToString(System.Globalization.CultureInfo.InvariantCulture) == stepWanted
                    || string.Equals(st.Name, stepWanted, StringComparison.OrdinalIgnoreCase)).ToList()
                : job.Steps.Where(st => st.Status == PipelineStatus.Failed).ToList();
            if (steps.Count == 0)
                return ClaudeToolOutput.Error(stepWanted is { Length: > 0 }
                    ? $"The job has no step {stepWanted}: {string.Join(", ", job.Steps.Select(st => $"{st.Number} {st.Name}"))}."
                    : "No step of the job failed: read its log whole, or with grep.");
            var spans = steps.Where(st => st.Started is not null).Select(st => (st.Started!.Value, st.Finished ?? DateTimeOffset.MaxValue)).ToList();
            raw = PipelineLog.Slice(raw, spans);
            heading += $", step {string.Join(", ", steps.Select(st => $"{st.Number} {st.Name}"))}";
        }
        var lines = PipelineLog.PlainLines(PipelineLog.Clean(raw, provider.Remote.Provider));
        string text;
        if (Str(input, "grep") is { Length: > 0 } pattern)
            text = PipelineLog.Grep(lines, pattern, Int(input, "context", 3, 0, 50), Int(input, "lines", 400, 10, 5000));
        else
        {
            var max = Int(input, "lines", 200, 10, 5000);
            text = lines.Length <= max ? string.Join('\n', lines) : $"(the last {max} of {lines.Length} lines)\n" + string.Join('\n', lines[^max..]);
        }
        return new ClaudeToolOutput($"{heading}:\n" + string.Join('\n', text.Split('\n').Select(Redaction.Text)))
        {
            Outside = $"the log of job {job.Name}",
        };
    }
}

/// <summary><c>pipeline_watch</c>: a run (or one of its jobs) waited for until it ends, then what came of it.</summary>
sealed class PipelineWatchTool(Func<PipelinesViewModel> pipelines) : PipelinesTool(pipelines)
{
    /// <summary>How often the service is asked (the tests' is shorter).</summary>
    internal static TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(15);

    public override string Name => "pipeline_watch";

    public override string Description =>
        "Waits until a CI run (or one of its jobs) ends, asking the service every 15 seconds, then returns it with its jobs and the "
        + "steps that failed, as pipeline_jobs does. It waits up to timeout_minutes (default 15, at most 60); a run still going then "
        + "is said so, and the call can be made again.";

    public override JsonObject InputSchema => Schema(new JsonObject
    {
        ["pipeline"] = PipelineProperty,
        ["job"] = new JsonObject { ["type"] = "string", ["description"] = "Wait for this job only (its id or name)." },
        ["timeout_minutes"] = new JsonObject { ["type"] = "integer", ["description"] = "How long to wait at most (default 15, at most 60)." },
    }, "pipeline");

    public override ClaudeToolPlan Plan(JsonElement input) =>
        new(ToolAccess.Read, "Watch Pipeline", $"{Target(input)}{Str(input, "pipeline")} {Str(input, "job")}".Trim(), "");

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var target = await TargetAsync(input, cancellationToken);
        var wanted = Str(input, "job");
        var until = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(Int(input, "timeout_minutes", 15, 1, 60));
        var run = await RunOf(input, target, cancellationToken);
        while (true)
        {
            var jobs = await target.Provider.JobsAsync(run, cancellationToken);
            var job = wanted is { Length: > 0 }
                ? jobs.FirstOrDefault(j => j.Id == wanted || string.Equals(j.Name, wanted, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"The run has no job {wanted}: {string.Join(", ", jobs.Select(j => $"{j.Id} {j.Name}"))}.")
                : null;
            var done = job is not null ? !job.Status.IsActive() : !run.Status.IsActive();
            if (done)
                return new ClaudeToolOutput((job is null ? "It ended:\n" : $"Job {job.Name} ended [{job.Status.Text()}]:\n") + PipelinesClaudeTools.Jobs(run, jobs));
            if (DateTimeOffset.UtcNow + Interval > until)
                return new ClaudeToolOutput($"Still {(job ?? (object)run) switch { PipelineJob j => j.Status.Text(), _ => run.Status.Text() }} after waiting: call "
                    + "pipeline_watch again to wait on.\n" + PipelinesClaudeTools.Jobs(run, jobs));
            await Task.Delay(Interval, cancellationToken);
            run = await target.Provider.PipelineAsync(run.Id, cancellationToken);
        }
    }
}

/// <summary><c>pipeline_usage</c>: the account's CI minutes this month, as the service tells them.</summary>
sealed class PipelineUsageTool(Func<PipelinesViewModel> pipelines) : PipelinesTool(pipelines)
{
    public override string Name => "pipeline_usage";

    public override string Description =>
        "The CI minutes the repository's owner (its organization, or the user) used this month, by runner and by repository, and what it "
        + "cost (GitHub's billing usage report). Before starting runs that take long, to see how much is used.";

    public override JsonObject InputSchema => Schema([]);

    public override ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Read, "CI Usage", Target(input).Trim(), "");

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken) =>
        new((await (await TargetAsync(input, cancellationToken)).Provider.UsageAsync(cancellationToken)));
}

/// <summary><c>pipeline_dispatch</c>: a workflow run by hand (asks: it uses the service's CI time).</summary>
sealed class PipelineDispatchTool(Func<PipelinesViewModel> pipelines) : PipelinesTool(pipelines)
{
    public override string Name => "pipeline_dispatch";

    public override string Description =>
        "Starts a CI run by hand on a branch or tag: a GitHub Actions workflow that has workflow_dispatch (by its file, ci.yml, or id), "
        + "with its inputs; on GitLab, the project's pipeline, the inputs its variables. The user approves each call. Returns the run "
        + "it started, for pipeline_watch.";

    public override JsonObject InputSchema => Schema(new JsonObject
    {
        ["workflow"] = new JsonObject { ["type"] = "string", ["description"] = "GitHub: the workflow's file in .github/workflows (ci.yml) or its id." },
        ["ref"] = new JsonObject { ["type"] = "string", ["description"] = "The branch or tag (default: the checked-out branch)." },
        ["inputs"] = new JsonObject { ["type"] = "object", ["description"] = "The workflow's inputs (GitLab: variables), name to value.", ["additionalProperties"] = new JsonObject { ["type"] = "string" } },
    });

    public override ClaudeToolPlan Plan(JsonElement input) =>
        new(ToolAccess.Write, "Run Pipeline", $"{Target(input)}{Str(input, "workflow")} {Str(input, "ref")}".Trim(),
            $"Allow Claude to start {(Str(input, "workflow") is { Length: > 0 } w ? "the workflow " + w : "a pipeline")}{(Str(input, "ref") is { Length: > 0 } r ? " on " + r : "")}?")
        {
            Warning = "It runs on the CI service and uses its minutes.",
            Code = input.TryGetProperty("inputs", out var inputs) && inputs.ValueKind == JsonValueKind.Object ? inputs.GetRawText() : null,
        };

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var target = await TargetAsync(input, cancellationToken);
        var reference = Str(input, "ref") is { Length: > 0 } r ? r : target.Branch ?? throw new InvalidOperationException("Name the branch or tag (ref).");
        var inputs = input.TryGetProperty("inputs", out var given) && given.ValueKind == JsonValueKind.Object
            ? given.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText())
            : [];
        var run = await target.Provider.DispatchAsync(Str(input, "workflow"), reference, inputs, cancellationToken);
        _ = Pipelines.RefreshAsync();
        return new ClaudeToolOutput(run is null
            ? $"Started on {reference}; {target.Service} does not show the run yet: pipeline_list shows it in a moment."
            : $"Started: {PipelinesClaudeTools.Row(run)}\npipeline_watch waits for it.");
    }
}

/// <summary><c>pipeline_retry</c>: a run again, its failed jobs or all (asks: it uses the service's CI time).</summary>
sealed class PipelineRetryTool(Func<PipelinesViewModel> pipelines) : PipelinesTool(pipelines)
{
    public override string Name => "pipeline_retry";

    public override string Description => "Runs a CI run again: its failed jobs (default), or all of them. The user approves each call.";

    public override JsonObject InputSchema => Schema(new JsonObject
    {
        ["pipeline"] = PipelineProperty,
        ["failed_only"] = new JsonObject { ["type"] = "boolean", ["description"] = "Only the failed jobs (default true)." },
    }, "pipeline");

    static bool FailedOnly(JsonElement input) => !(input.TryGetProperty("failed_only", out var v) && v.ValueKind == JsonValueKind.False);

    public override ClaudeToolPlan Plan(JsonElement input) =>
        new(ToolAccess.Write, "Re-run Pipeline", Target(input) + Str(input, "pipeline"), FailedOnly(input)
            ? $"Allow Claude to run the failed jobs of run {Str(input, "pipeline")} again?"
            : $"Allow Claude to run all of run {Str(input, "pipeline")} again?")
        {
            Warning = "It runs on the CI service again, and uses its minutes.",
        };

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var target = await TargetAsync(input, cancellationToken);
        var run = await RunOf(input, target, cancellationToken);
        await target.Provider.RetryAsync(run, FailedOnly(input), cancellationToken);
        _ = Pipelines.RefreshAsync();
        return new ClaudeToolOutput($"{run.Name} #{run.Number} runs again ({(FailedOnly(input) ? "its failed jobs" : "all of it")}): pipeline_watch waits for it.");
    }
}

/// <summary><c>pipeline_cancel</c>: a run that goes, stopped (asks).</summary>
sealed class PipelineCancelTool(Func<PipelinesViewModel> pipelines) : PipelinesTool(pipelines)
{
    public override string Name => "pipeline_cancel";

    public override string Description => "Cancels a CI run that is queued or running. The user approves each call.";

    public override JsonObject InputSchema => Schema(new JsonObject { ["pipeline"] = PipelineProperty }, "pipeline");

    public override ClaudeToolPlan Plan(JsonElement input) =>
        new(ToolAccess.Write, "Cancel Pipeline", Target(input) + Str(input, "pipeline"), $"Allow Claude to cancel run {Str(input, "pipeline")}?");

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var target = await TargetAsync(input, cancellationToken);
        var run = await RunOf(input, target, cancellationToken);
        if (!run.Status.IsActive())
            return ClaudeToolOutput.Error($"{run.Name} #{run.Number} is {run.Status.Text()}: there is nothing to cancel.");
        await target.Provider.CancelAsync(run, cancellationToken);
        _ = Pipelines.RefreshAsync();
        return new ClaudeToolOutput($"{run.Name} #{run.Number} is being canceled.");
    }
}
