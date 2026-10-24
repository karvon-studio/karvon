using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Karvon.Pipelines.Providers;
using Karvon.ViewModels;

namespace Karvon.Pipelines.ViewModels;

/// <summary>A job of a run's page: its stage's heading before the first of the stage (GitLab's).</summary>
public sealed partial class JobRow(PipelineJob job) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Icon), nameof(IsActive), nameof(IsFailed), nameof(IsPassed), nameof(StatusText), nameof(Steps), nameof(CanRetry), nameof(CanPlay))]
    public partial PipelineJob Job { get; set; } = job;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Duration))]
    public partial DateTimeOffset Now { get; set; } = DateTimeOffset.Now;

    /// <summary>The first job of its stage: the stage's name shows above it.</summary>
    [ObservableProperty]
    public partial bool ShowsStage { get; set; }

    public string Id => Job.Id;

    public string Name => Job.Name;

    public string Stage => Job.Stage;

    public string Icon => Job.Status.Icon();

    public bool IsActive => Job.Status.IsActive();

    public bool IsFailed => Job.Status == PipelineStatus.Failed;

    public bool IsPassed => Job.Status == PipelineStatus.Success;

    public string StatusText => Job.AllowFailure && IsFailed ? "failed (allowed)" : Job.Status.Text();

    public string Duration => PipelineStatuses.Duration(Job.Started, Job.Finished, Now);

    public bool CanRetry => !IsActive && !Job.CanPlay;

    public bool CanPlay => Job.CanPlay;

    public IReadOnlyList<StepRow> Steps => [.. Job.Steps.Select(s => new StepRow(s, Now))];
}

/// <summary>A GitHub Actions step: its state and how long it took.</summary>
public sealed record StepRow(PipelineStep Step, DateTimeOffset Now)
{
    public string Name => Step.Name;

    public string Icon => Step.Status.Icon();

    public bool IsFailed => Step.Status == PipelineStatus.Failed;

    public bool IsPassed => Step.Status == PipelineStatus.Success;

    public bool IsActive => Step.Status.IsActive();

    public string Duration => PipelineStatuses.Duration(Step.Started, Step.Finished, Now);
}

/// <summary>
/// A run's page: its state, commit, branch, who started it, how long it took; its jobs (by stage on GitLab), a job's steps
/// (GitHub's) and log (GitLab's as it runs, GitHub's once the job is done); Re-run failed jobs, Re-run all, Cancel, Open in
/// browser, Ask Claude (a failed run as the task). While the run goes it is read again every 5 s.
/// </summary>
public sealed partial class PipelinePageViewModel : ObservableObject, IDisposable
{
    internal static TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(5);

    readonly PipelinesViewModel _owner;
    readonly DispatcherTimer _timer;
    bool _reading;
    string _logJob = "";
    int _logLength;

    public PipelinePageViewModel(PipelinesViewModel owner, PipelineRepo repo, Pipeline pipeline)
    {
        _owner = owner;
        Repo = repo;
        Pipeline = pipeline;
        _timer = new DispatcherTimer { Interval = Interval };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    public PipelineRepo Repo { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Icon), nameof(IsActive), nameof(IsFailed), nameof(IsPassed), nameof(StatusText), nameof(Heading), nameof(Facts), nameof(CanRetry))]
    public partial Pipeline Pipeline { get; private set; }

    public string Icon => Pipeline.Status.Icon();

    public bool IsActive => Pipeline.Status.IsActive();

    public bool IsFailed => Pipeline.Status == PipelineStatus.Failed;

    public bool IsPassed => Pipeline.Status == PipelineStatus.Success;

    public bool CanRetry => !IsActive;

    public string StatusText => Pipeline.Status.Text();

    public string Heading => Pipeline.Title.Length > 0 ? Pipeline.Title : $"{Pipeline.Name} #{Pipeline.Number}";

    /// <summary>The run's facts in a line: its workflow and number, branch, commit, event, who, when, how long.</summary>
    public string Facts => string.Join("  ·  ", new[]
    {
        $"{Pipeline.Name} #{Pipeline.Number}" + (Pipeline.Attempt > 1 ? $" (attempt {Pipeline.Attempt})" : ""), Pipeline.Branch, Pipeline.ShortSha,
        Pipeline.Event, Pipeline.Actor, PipelineStatuses.Ago(Pipeline.Created, DateTimeOffset.Now),
        PipelineStatuses.Duration(Pipeline.Started, Pipeline.Finished, DateTimeOffset.Now),
    }.Where(s => s is { Length: > 0 }));

    public ObservableCollection<JobRow> Jobs { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasJob), nameof(HasSteps))]
    public partial JobRow? SelectedJob { get; set; }

    public bool HasJob => SelectedJob is not null;

    public bool HasSteps => SelectedJob?.Job.Steps.Count > 0;

    partial void OnSelectedJobChanged(JobRow? value) => _ = LoadLogAsync(fresh: true);

    /// <summary>The selected job's log, colored as Output colors its own.</summary>
    public OutputChannel Log { get; } = new("Job log");

    [ObservableProperty]
    public partial string? LogNote { get; private set; }

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial string? Error { get; private set; }

    void Tick()
    {
        var now = DateTimeOffset.Now;
        foreach (var job in Jobs)
            if (job.IsActive)
                job.Now = now;
        if (IsActive || Jobs.Any(j => j.IsActive))
            _ = RefreshAsync();
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (_reading || Repo.Provider is not { } provider)
            return;
        _reading = true;
        IsBusy = true;
        try
        {
            var pipeline = await provider.PipelineAsync(Pipeline.Id, default);
            Pipeline = pipeline with { Title = pipeline.Title.Length > 0 ? pipeline.Title : Pipeline.Title };
            ShowJobs(await provider.JobsAsync(Pipeline, default));
            Error = null;
            if (SelectedJob is { } job && (job.IsActive && provider.LogsWhileRunning || _logJob != job.Id || _logLength == 0 && !job.IsActive))
                await LoadLogAsync(fresh: _logJob != job.Id);
        }
        catch (Exception ex) when (ex is PipelineException or IOException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Error = ex.Message;
        }
        finally
        {
            _reading = false;
            IsBusy = false;
        }
    }

    void ShowJobs(IReadOnlyList<PipelineJob> jobs)
    {
        var rows = Jobs.ToDictionary(j => j.Id);
        Jobs.Clear();
        string? stage = null;
        foreach (var job in jobs)
        {
            var row = rows.TryGetValue(job.Id, out var known) ? known : new JobRow(job);
            row.Job = job;
            row.ShowsStage = job.Stage.Length > 0 && job.Stage != stage;
            stage = job.Stage;
            Jobs.Add(row);
        }
        // The failed job first, as the services open a failed run; else the running one, else the first.
        if (SelectedJob is null || !Jobs.Contains(SelectedJob))
            SelectedJob = Jobs.FirstOrDefault(j => j.IsFailed && !j.Job.AllowFailure) ?? Jobs.FirstOrDefault(j => j.IsActive) ?? Jobs.FirstOrDefault();
        OnPropertyChanged(nameof(HasSteps));
    }

    /// <summary>The selected job's log: all of it, or what came since (a GitLab job's trace grows as it runs).</summary>
    async Task LoadLogAsync(bool fresh)
    {
        if (SelectedJob is not { } job || Repo.Provider is not { } provider)
        {
            Log.Clear();
            return;
        }
        if (job.IsActive && !provider.LogsWhileRunning)
        {
            Log.Clear();
            _logJob = job.Id;
            _logLength = 0;
            LogNote = "GitHub gives a job's log once the job is done: its steps show how far it is.";
            return;
        }
        try
        {
            var log = PipelineLog.Clean(await provider.LogAsync(job.Job, default), Repo.Remote!.Provider);
            if (SelectedJob != job)
                return;
            LogNote = log.Trim().Length == 0 ? "The job has written no log yet." : null;
            if (fresh || _logJob != job.Id || log.Length < _logLength)
            {
                Log.Clear();
                Log.AppendRaw(log);
            }
            else if (log.Length > _logLength)
                Log.AppendRaw(log[_logLength..]);
            _logJob = job.Id;
            _logLength = log.Length;
        }
        catch (Exception ex) when (ex is PipelineException or IOException or HttpRequestException or TaskCanceledException)
        {
            LogNote = ex.Message;
        }
    }

    [RelayCommand]
    Task RetryFailedAsync() => ActAsync(p => p.RetryAsync(Pipeline, failedOnly: true, default), $"{Pipeline.Name} #{Pipeline.Number}: its failed jobs run again.");

    [RelayCommand]
    Task RetryAllAsync() => ActAsync(p => p.RetryAsync(Pipeline, failedOnly: false, default), $"{Pipeline.Name} #{Pipeline.Number} runs again.");

    [RelayCommand]
    Task CancelAsync() => ActAsync(p => p.CancelAsync(Pipeline, default), $"{Pipeline.Name} #{Pipeline.Number} is being canceled.");

    [RelayCommand]
    Task RetryJobAsync(JobRow? job) => job is null ? Task.CompletedTask
        : ActAsync(p => p.RetryJobAsync(job.Job, default), job.CanPlay ? $"{job.Name} started." : $"{job.Name} runs again.");

    async Task ActAsync(Func<IPipelineProvider, Task> action, string done)
    {
        await _owner.ActAsync(action, done);
        await Task.Delay(TimeSpan.FromSeconds(1));
        await RefreshAsync();
    }

    [RelayCommand]
    void OpenInBrowser() => _owner.Host.OpenUrl(Pipeline.Url);

    [RelayCommand]
    void OpenJobInBrowser(JobRow? job)
    {
        if (job?.Job.Url is { Length: > 0 } url)
            _owner.Host.OpenUrl(url);
    }

    /// <summary>The run as Claude's task: it reads the failed job's log with pipeline_log, through Karvon.</summary>
    [RelayCommand]
    void AskClaude()
    {
        var failed = Jobs.Where(j => j.IsFailed && !j.Job.AllowFailure).Select(j => j.Name).ToList();
        _owner.Host.AskClaude(failed.Count > 0
            ? $"The CI run {Pipeline.Name} #{Pipeline.Number} of {Repo.Name} (pipeline id {Pipeline.Id}, branch {Pipeline.Branch}) failed in {string.Join(", ", failed)}: "
                + "read its log with pipeline_log, find the cause and fix it. "
            : $"Look at the CI run {Pipeline.Name} #{Pipeline.Number} of {Repo.Name} (pipeline id {Pipeline.Id}) with pipeline_jobs. ");
    }

    public void Dispose() => _timer.Stop();
}
