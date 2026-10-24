using System.Collections.ObjectModel;
using Avalonia.Threading;
using Bengine.Introspection.Servers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Karvon.Git;
using Karvon.Modules;
using Karvon.Pipelines.Providers;
using Karvon.Ui;
using Karvon.ViewModels;

namespace Karvon.Pipelines.ViewModels;

/// <summary>What the Pipelines window needs of Karvon.</summary>
public interface IPipelinesHost
{
    IReadOnlyList<GitRepositoryState> Repositories { get; }

    event Action? GitChanged;

    event Action<GitRepository>? GitPushed;

    IEnumerable<DocumentTab> Documents { get; }

    void ShowDocument(DocumentTab document);

    void OpenUrl(string url);

    void Status(string text);

    void AskClaude(string prompt);
}

public enum RepoState
{
    /// <summary>Its remote is not a service's (no origin, or a path).</summary>
    NoService,
    Connecting,
    /// <summary>A token is needed.</summary>
    NeedsToken,
    Ready,
    Failed,
}

/// <summary>A repository of the solution's and its CI service.</summary>
public sealed partial class PipelineRepo(GitRepository repository, RepoRemote? remote) : ObservableObject
{
    public GitRepository Repository => repository;

    public RepoRemote? Remote => remote;

    public string Name => remote?.Name ?? Path.GetFileName(repository.Root.TrimEnd('/'));

    public string ProviderName => remote?.ProviderName ?? "No CI service";

    [ObservableProperty]
    public partial string? Branch { get; set; }

    [ObservableProperty]
    public partial RepoState State { get; set; } = RepoState.Connecting;

    [ObservableProperty]
    public partial string? Error { get; set; }

    internal IPipelineProvider? Provider { get; set; }

    public override string ToString() => Name;
}

/// <summary>A run in the window's list (updated in place as it goes).</summary>
public sealed partial class PipelineRow(Pipeline pipeline) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status), nameof(Icon), nameof(IsActive), nameof(IsFailed), nameof(IsPassed), nameof(Heading), nameof(Detail), nameof(Duration))]
    public partial Pipeline Pipeline { get; set; } = pipeline;

    /// <summary>The time now, ticking, so that "3 min ago" and a running duration move.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail), nameof(Duration))]
    public partial DateTimeOffset Now { get; set; } = DateTimeOffset.Now;

    public string Id => Pipeline.Id;

    public PipelineStatus Status => Pipeline.Status;

    public string Icon => Status.Icon();

    public bool IsActive => Status.IsActive();

    public bool IsFailed => Status == PipelineStatus.Failed;

    public bool IsPassed => Status == PipelineStatus.Success;

    public string Heading => Pipeline.Title.Length > 0 ? Pipeline.Title : $"{Pipeline.Name} #{Pipeline.Number}";

    public string Detail => string.Join(" · ", new[]
    {
        $"{Pipeline.Name} #{Pipeline.Number}", Pipeline.Branch, Pipeline.Event, Pipeline.Actor, PipelineStatuses.Ago(Pipeline.Created, Now),
    }.Where(s => s is { Length: > 0 }));

    public string Duration => PipelineStatuses.Duration(Pipeline.Started, Pipeline.Finished, Now);
}

/// <summary>
/// The Pipelines window: the repository's CI runs (GitHub Actions' workflow runs, GitLab's pipelines) of its branch or of
/// all, as they go (read again every 10 s while one runs, every minute else, and right after a push); a run opens as a
/// page. Its latest run on the branch is in the status bar. GitHub is read through the user's gh; a GitLab's token is
/// kept in the keyring.
/// </summary>
public sealed partial class PipelinesViewModel : ObservableObject, IDisposable
{
    /// <summary>How often the list is read again while a run of it is active, and else.</summary>
    internal static TimeSpan ActiveInterval { get; set; } = TimeSpan.FromSeconds(10);

    internal static TimeSpan IdleInterval { get; set; } = TimeSpan.FromSeconds(60);

    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    DateTimeOffset _next = DateTimeOffset.MinValue;
    bool _reading;
    readonly Dictionary<string, PipelineStatus> _seen = [];

    public PipelinesViewModel(IPipelinesHost host, ICredentialStore? keyring, Func<string?>? gh = null)
    {
        Host = host;
        Connector = new PipelineConnector(new PipelineSecrets(keyring), gh ?? GhCliApi.Find);
        host.GitChanged += OnGitChanged;
        host.GitPushed += OnGitPushed;
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        _ = LoadRepositoriesAsync();
    }

    public IPipelinesHost Host { get; }

    public PipelineConnector Connector { get; }

    /// <summary>The branch's latest run, in the status bar.</summary>
    public ModuleStatusItem StatusItem { get; } = new();

    public ObservableCollection<PipelineRepo> Repositories { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRepository), nameof(NeedsToken), nameof(IsReady), nameof(Heading), nameof(HasManyRepositories))]
    public partial PipelineRepo? Repository { get; set; }

    public bool HasRepository => Repository is not null;

    public bool HasManyRepositories => Repositories.Count > 1;

    public bool NeedsToken => Repository?.State == RepoState.NeedsToken;

    public bool IsReady => Repository?.State == RepoState.Ready;

    public string Heading => Repository is { Remote: { } remote } ? $"{remote.ProviderName} · {remote.Authority}/{remote.Path}" : "";

    public ObservableCollection<PipelineRow> Runs { get; } = [];

    /// <summary>Every branch's runs, not the checked-out branch's only.</summary>
    [ObservableProperty]
    public partial bool AllBranches { get; set; }

    partial void OnAllBranchesChanged(bool value) => _ = RefreshAsync();

    partial void OnRepositoryChanged(PipelineRepo? value)
    {
        Runs.Clear();
        _seen.Clear();
        _ = RefreshAsync();
    }

    [ObservableProperty]
    public partial string? Message { get; private set; }

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDialog))]
    public partial OverlayDialog? Dialog { get; set; }

    public bool HasDialog => Dialog is not null;

    public async Task<T?> ShowAsync<T>(T dialog) where T : OverlayDialog
    {
        while (Dialog is { } open)
            await open.Result;
        Dialog = dialog;
        try
        {
            return await dialog.Result ? dialog : null;
        }
        finally
        {
            Dialog = null;
        }
    }

    /// <summary>The solution's repositories, each with its service (connected as it can be, never asking).</summary>
    async Task LoadRepositoriesAsync()
    {
        var states = Host.Repositories;
        var keep = Repository?.Repository.Root;
        foreach (var repo in Repositories)
            repo.Provider?.Dispose();
        Repositories.Clear();
        foreach (var state in states)
        {
            var remote = await state.Repository.RemoteUrlAsync() is { } url ? RepoRemote.Parse(url) : null;
            var repo = new PipelineRepo(state.Repository, remote) { Branch = state.Branch, State = remote is null ? RepoState.NoService : RepoState.Connecting };
            Repositories.Add(repo);
            if (remote is not null)
                await ConnectAsync(repo);
        }
        OnPropertyChanged(nameof(HasManyRepositories));
        Repository = Repositories.FirstOrDefault(r => r.Repository.Root == keep) ?? Repositories.FirstOrDefault(r => r.Remote is not null) ?? Repositories.FirstOrDefault();
        if (Repository is null)
        {
            Message = "No Git repository is open.";
            StatusItem.IsVisible = false;
        }
    }

    async Task ConnectAsync(PipelineRepo repo)
    {
        try
        {
            repo.Provider?.Dispose();
            repo.Provider = await Connector.ConnectAsync(repo.Remote!, default);
            repo.State = repo.Provider is null ? RepoState.NeedsToken : RepoState.Ready;
            repo.Error = null;
        }
        catch (Exception ex) when (ex is PipelineException or IOException or System.ComponentModel.Win32Exception or HttpRequestException)
        {
            repo.State = RepoState.Failed;
            repo.Error = ex.Message;
        }
        if (repo == Repository)
        {
            OnPropertyChanged(nameof(NeedsToken));
            OnPropertyChanged(nameof(IsReady));
        }
    }

    void OnGitChanged() => Dispatcher.UIThread.Post(() =>
    {
        var states = Host.Repositories;
        if (!states.Select(s => s.Repository.Root).SequenceEqual(Repositories.Select(r => r.Repository.Root)))
        {
            _ = LoadRepositoriesAsync();
            return;
        }
        var switched = false;
        foreach (var (state, repo) in states.Zip(Repositories))
            if (repo.Branch != state.Branch)
            {
                repo.Branch = state.Branch;
                switched |= repo == Repository;
            }
        if (switched)
        {
            // The other branch's runs are not this one's.
            if (!AllBranches)
                Runs.Clear();
            _ = RefreshAsync();
        }
    });

    /// <summary>A push: its run comes in a few seconds; read again soon, then as it goes.</summary>
    void OnGitPushed(GitRepository repository) => Dispatcher.UIThread.Post(() =>
    {
        if (Repository?.Repository.Root == repository.Root)
            _next = DateTimeOffset.Now + TimeSpan.FromSeconds(3);
    });

    void Tick()
    {
        var now = DateTimeOffset.Now;
        foreach (var row in Runs)
            if (row.IsActive || now.Second == 0)
                row.Now = now;
        if (now >= _next && !_reading && IsReady)
            _ = RefreshAsync();
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (Repository is not { } repo || _reading)
            return;
        if (repo.State != RepoState.Ready || repo.Provider is not { } provider)
        {
            Message = repo.State switch
            {
                RepoState.NoService => "This repository's remote is not a GitHub or GitLab repository.",
                RepoState.NeedsToken => null,
                RepoState.Failed => repo.Error,
                _ => null,
            };
            UpdateStatusItem();
            return;
        }
        _reading = true;
        IsBusy = true;
        try
        {
            var runs = await provider.PipelinesAsync(AllBranches ? null : repo.Branch, 30, default);
            runs = await WithTitlesAsync(repo, runs);
            if (Repository != repo)
                return;
            Show(runs);
            Message = runs.Count == 0 ? AllBranches || repo.Branch is null ? "No runs yet." : $"No runs on {repo.Branch} yet." : null;
            Notify(repo, runs);
        }
        catch (Exception ex) when (ex is PipelineException or IOException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Message = ex.Message;
        }
        finally
        {
            _reading = false;
            IsBusy = false;
            _next = DateTimeOffset.Now + (Runs.Any(r => r.IsActive) ? ActiveInterval : IdleInterval);
            UpdateStatusItem();
        }
    }

    /// <summary>GitLab's pipelines have no commit subject: the repository's own log says it.</summary>
    static async Task<IReadOnlyList<Pipeline>> WithTitlesAsync(PipelineRepo repo, IReadOnlyList<Pipeline> runs)
    {
        if (runs.All(r => r.Title.Length > 0))
            return runs;
        var subjects = await repo.Repository.SubjectsAsync(runs.Where(r => r.Title.Length == 0).Select(r => r.Sha));
        return [.. runs.Select(r => r.Title.Length > 0 || !subjects.TryGetValue(r.Sha, out var subject) ? r : r with { Title = subject })];
    }

    void Show(IReadOnlyList<Pipeline> runs)
    {
        var rows = Runs.ToDictionary(r => r.Id);
        for (var i = 0; i < runs.Count; i++)
        {
            if (rows.Remove(runs[i].Id, out var row))
            {
                row.Pipeline = runs[i];
                var at = Runs.IndexOf(row);
                if (at != i)
                    Runs.Move(at, i);
            }
            else
                Runs.Insert(i, new PipelineRow(runs[i]));
        }
        foreach (var gone in rows.Values)
            Runs.Remove(gone);
    }

    /// <summary>A run of the branch that was going is done: said in the status bar.</summary>
    void Notify(PipelineRepo repo, IReadOnlyList<Pipeline> runs)
    {
        foreach (var run in runs)
        {
            if (_seen.TryGetValue(run.Id, out var was) && was.IsActive() && !run.Status.IsActive() && run.Branch == repo.Branch)
                Host.Status($"{repo.Remote!.ProviderName}: {run.Name} #{run.Number} {run.Status.Text()} on {run.Branch}.");
            _seen[run.Id] = run.Status;
        }
    }

    /// <summary>The checked-out branch's latest run: its state in the status bar, a click opens it.</summary>
    void UpdateStatusItem()
    {
        var latest = Repository is { Branch: { } branch } ? Runs.FirstOrDefault(r => r.Pipeline.Branch == branch) : null;
        StatusItem.IsVisible = latest is not null;
        if (latest is null)
            return;
        var run = latest.Pipeline;
        StatusItem.Icon = run.Status.Icon();
        StatusItem.Text = $"{run.Name} {run.Status.Text()}";
        StatusItem.ToolTip = $"{Repository!.Remote!.ProviderName}: {run.Name} #{run.Number} on {run.Branch} {run.Status.Text()} · {latest.Heading}";
        StatusItem.Click = () => Open(latest);
    }

    /// <summary>A token for the repository's service (GitLab's, or GitHub's without gh).</summary>
    [RelayCommand]
    async Task SignInAsync()
    {
        if (Repository is not { Remote: { } remote } repo)
            return;
        var dialog = new PipelineTokenDialog(remote, Connector.Secrets.HasKeyring, Connector.Secrets.Get(remote) is not null);
        dialog.OpenPage += Host.OpenUrl;
        if (await ShowAsync(dialog) is not { } form)
            return;
        if (form.Forget)
            Connector.Secrets.Forget(remote);
        else
            Connector.Secrets.Set(remote, form.Token.Trim(), form.Keep);
        await ConnectAsync(repo);
        await RefreshAsync();
    }

    /// <summary>A run's page: the one open, else a new one.</summary>
    [RelayCommand]
    public void Open(PipelineRow? row)
    {
        if (row is null || Repository is not { Provider: { } } repo)
            return;
        var document = Host.Documents.OfType<PipelineDocument>().FirstOrDefault(d => d.Page.Repo == repo && d.Page.Pipeline.Id == row.Id)
            ?? new PipelineDocument(new PipelinePageViewModel(this, repo, row.Pipeline));
        Host.ShowDocument(document);
        _ = document.Page.RefreshAsync();
    }

    /// <summary>A run by its id or number (Claude's call), read from the service.</summary>
    public async Task<Pipeline> FindAsync(string id, CancellationToken ct)
    {
        var provider = ProviderOrThrow();
        var known = Runs.FirstOrDefault(r => r.Id == id || r.Pipeline.Number == id)?.Pipeline;
        return await provider.PipelineAsync(known?.Id ?? id, ct);
    }

    internal IPipelineProvider ProviderOrThrow() => Repository switch
    {
        null => throw new InvalidOperationException("No Git repository is open in Karvon."),
        { State: RepoState.NoService } r => throw new InvalidOperationException($"{r.Name}'s remote is not a GitHub or GitLab repository."),
        { State: RepoState.NeedsToken, Remote: { } remote } => throw new InvalidOperationException(
            $"Karvon has no token for {remote.ProviderName} on {remote.Authority}: the user gives one in View > Pipelines (Sign In)."),
        { Provider: { } provider } => provider,
        var r => throw new InvalidOperationException(r.Error ?? $"{r.Name}'s CI service could not be reached."),
    };

    [RelayCommand]
    void OpenInBrowser(PipelineRow? row)
    {
        if (row?.Pipeline.Url is { Length: > 0 } url)
            Host.OpenUrl(url);
    }

    [RelayCommand]
    async Task RetryAsync(PipelineRow? row)
    {
        if (row is null)
            return;
        await ActAsync(p => p.RetryAsync(row.Pipeline, failedOnly: true, default), $"{row.Pipeline.Name} #{row.Pipeline.Number}: its failed jobs run again.");
    }

    [RelayCommand]
    async Task CancelAsync(PipelineRow? row)
    {
        if (row is null)
            return;
        await ActAsync(p => p.CancelAsync(row.Pipeline, default), $"{row.Pipeline.Name} #{row.Pipeline.Number} is being canceled.");
    }

    internal async Task ActAsync(Func<IPipelineProvider, Task> action, string done)
    {
        try
        {
            await action(ProviderOrThrow());
            Host.Status(done);
            _next = DateTimeOffset.Now + TimeSpan.FromSeconds(2);
        }
        catch (Exception ex) when (ex is PipelineException or InvalidOperationException or IOException or HttpRequestException)
        {
            Message = ex.Message;
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        Host.GitChanged -= OnGitChanged;
        Host.GitPushed -= OnGitPushed;
        foreach (var repo in Repositories)
            repo.Provider?.Dispose();
    }
}

/// <summary>A run's page in the document well.</summary>
public sealed class PipelineDocument(PipelinePageViewModel page) : DocumentTab
{
    public PipelinePageViewModel Page => page;

    public override string Key => $"pipeline:{page.Repo.Remote?.Authority}/{page.Repo.Name}#{page.Pipeline.Id}";

    public override string Title => $"{page.Pipeline.Name} #{page.Pipeline.Number}";

    public override string ToolTip => $"{page.Repo.Name}: {page.Pipeline.Title}";

    public override void Dispose() => page.Dispose();
}
