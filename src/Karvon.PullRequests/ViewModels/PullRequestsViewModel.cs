using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Karvon.Git;
using Karvon.Modules;
using Karvon.Pipelines;
using Karvon.Pipelines.Providers;
using Karvon.Pipelines.ViewModels;
using Karvon.PullRequests.Providers;
using Karvon.Ui;
using Karvon.ViewModels;

namespace Karvon.PullRequests.ViewModels;

/// <summary>What the Pull Requests window needs of Karvon.</summary>
public interface IPullRequestsHost
{
    IReadOnlyList<GitRepositoryState> Repositories { get; }

    event Action? GitChanged;

    event Action<GitRepository>? GitPushed;

    IEnumerable<DocumentTab> Documents { get; }

    void ShowDocument(DocumentTab document);

    void ShowDiff(string path, string before, string after, string beforeTitle, string afterTitle, string key);

    void OpenUrl(string url);

    void Status(string text);

    void AskClaude(string prompt);

    /// <summary>The branch pushed as Git Changes pushes it (its upstream set the first time).</summary>
    Task<bool> PushAsync(GitRepository repository);

    /// <summary>A git command run as Git Changes runs one, in Output (Git).</summary>
    Task<bool> RunGitAsync(string title, Func<Task<GitResult>> operation, bool changesFiles);
}

/// <summary>A repository of the solution's and its service's pull requests.</summary>
public sealed partial class PullRequestRepo(GitRepository repository, RepoRemote? remote) : ObservableObject
{
    public GitRepository Repository => repository;

    public RepoRemote? Remote => remote;

    public string Name => remote?.Name ?? Path.GetFileName(repository.Root.TrimEnd('/'));

    public string ServiceName => remote is null ? "No service" : remote.Provider == CiProvider.GitHub ? "GitHub" : "GitLab";

    [ObservableProperty]
    public partial string? Branch { get; set; }

    [ObservableProperty]
    public partial RepoState State { get; set; } = RepoState.Connecting;

    [ObservableProperty]
    public partial string? Error { get; set; }

    internal IPullRequestProvider? Provider { get; set; }

    public override string ToString() => Name;
}

/// <summary>A pull request in the window's list (updated in place).</summary>
public sealed partial class PullRequestRow(PullRequest pullRequest, string prefix) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Icon), nameof(Title), nameof(Detail), nameof(HasComments), nameof(CommentCount), nameof(IsDraft))]
    public partial PullRequest PullRequest { get; set; } = pullRequest;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail))]
    public partial DateTimeOffset Now { get; set; } = DateTimeOffset.Now;

    public string Number => PullRequest.Number;

    public string Icon => PullRequest.State.Icon();

    public string Title => PullRequest.Title;

    public bool IsDraft => PullRequest.State == PullRequestState.Draft;

    public string Detail => string.Join(" · ", new[]
    {
        prefix + PullRequest.Number, PullRequest.Author, $"{PullRequest.SourceBranch} → {PullRequest.TargetBranch}",
        PullRequest.IsOpen ? PipelineStatuses.Ago(PullRequest.Updated, Now) : $"{PullRequest.State.Text()} {PipelineStatuses.Ago(PullRequest.Updated, Now)}",
    }.Where(s => s.Length > 0));

    public bool HasComments => PullRequest.Comments > 0;

    public string CommentCount => PullRequest.Comments.ToString();
}

/// <summary>A choice of the window's filter.</summary>
public sealed record PullRequestFilterChoice(PullRequestFilter Filter, string Text)
{
    public override string ToString() => Text;
}

/// <summary>
/// The Pull Requests window: the repository's pull requests (GitHub's) or merge requests (GitLab's), open, made by the
/// user, waiting for their review, closed or all, read again every two minutes, after a push and when the branch changes;
/// one opens as a page. The checked-out branch's own is in the status bar. A new one is made from the branch (pushed
/// first). Signed in as the Pipelines window is: the user's gh, or the token kept for the host.
/// </summary>
public sealed partial class PullRequestsViewModel : ObservableObject, IDisposable
{
    internal static TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(2);

    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    DateTimeOffset _next = DateTimeOffset.MinValue;
    bool _reading;

    public PullRequestsViewModel(IPullRequestsHost host, PipelineConnector connector)
    {
        Host = host;
        Connector = connector;
        Filter = Filters[0];
        host.GitChanged += OnGitChanged;
        host.GitPushed += OnGitPushed;
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        _ = LoadRepositoriesAsync();
    }

    public IPullRequestsHost Host { get; }

    public PipelineConnector Connector { get; }

    /// <summary>The checked-out branch's open pull request, in the status bar.</summary>
    public ModuleStatusItem StatusItem { get; } = new();

    public ObservableCollection<PullRequestRepo> Repositories { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRepository), nameof(NeedsToken), nameof(IsReady), nameof(Heading), nameof(HasManyRepositories), nameof(NewText))]
    public partial PullRequestRepo? Repository { get; set; }

    public bool HasRepository => Repository is not null;

    public bool HasManyRepositories => Repositories.Count > 1;

    public bool NeedsToken => Repository?.State == RepoState.NeedsToken;

    public bool IsReady => Repository?.State == RepoState.Ready;

    public string Heading => Repository is { Remote: { } remote } ? $"{Repository.ServiceName} · {remote.Authority}/{remote.Path}" : "";

    /// <summary>"New Pull Request" or "New Merge Request".</summary>
    public string NewText => Repository?.Remote?.Provider == CiProvider.GitLab ? "New Merge Request" : "New Pull Request";

    public IReadOnlyList<PullRequestFilterChoice> Filters { get; } =
    [
        new(PullRequestFilter.Open, "Open"), new(PullRequestFilter.Mine, "Created by Me"), new(PullRequestFilter.ReviewRequested, "Review Requested"),
        new(PullRequestFilter.Closed, "Closed"), new(PullRequestFilter.All, "All"),
    ];

    [ObservableProperty]
    public partial PullRequestFilterChoice Filter { get; set; }

    partial void OnFilterChanged(PullRequestFilterChoice value)
    {
        Rows.Clear();
        _ = RefreshAsync();
    }

    partial void OnRepositoryChanged(PullRequestRepo? value)
    {
        Rows.Clear();
        _ = RefreshAsync();
    }

    public ObservableCollection<PullRequestRow> Rows { get; } = [];

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

    internal async Task<bool> ConfirmAsync(string title, string message, string confirm, bool danger = false) =>
        await ShowAsync(new ConfirmDialog(title, message, confirm, danger)) is not null;

    async Task LoadRepositoriesAsync()
    {
        var keep = Repository?.Repository.Root;
        foreach (var repo in Repositories)
            repo.Provider?.Dispose();
        Repositories.Clear();
        foreach (var state in Host.Repositories)
        {
            var remote = await state.Repository.RemoteUrlAsync() is { } url ? RepoRemote.Parse(url) : null;
            var repo = new PullRequestRepo(state.Repository, remote) { Branch = state.Branch, State = remote is null ? RepoState.NoService : RepoState.Connecting };
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

    async Task ConnectAsync(PullRequestRepo repo)
    {
        try
        {
            repo.Provider?.Dispose();
            repo.Provider = repo.Remote!.Provider == CiProvider.GitHub
                ? await Connector.GitHubAsync(repo.Remote, default) is { } github ? new GitHubPullRequests(repo.Remote, github) : null
                : Connector.GitLab(repo.Remote) is { } gitlab ? new GitLabMergeRequests(gitlab) : null;
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
        foreach (var (state, repo) in states.Zip(Repositories))
            if (repo.Branch != state.Branch)
            {
                repo.Branch = state.Branch;
                if (repo == Repository)
                    UpdateStatusItem();
            }
    });

    /// <summary>A push: the branch's pull request has new commits (or the branch can have one now).</summary>
    void OnGitPushed(GitRepository repository) => Dispatcher.UIThread.Post(() =>
    {
        if (Repository?.Repository.Root == repository.Root)
            _next = DateTimeOffset.Now + TimeSpan.FromSeconds(2);
    });

    void Tick()
    {
        var now = DateTimeOffset.Now;
        if (now.Second == 0)
            foreach (var row in Rows)
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
                RepoState.Failed => repo.Error,
                _ => null,
            };
            UpdateStatusItem();
            return;
        }
        _reading = true;
        IsBusy = true;
        var filter = Filter.Filter;
        try
        {
            var list = await provider.ListAsync(filter, 50, default);
            if (filter is PullRequestFilter.Mine or PullRequestFilter.ReviewRequested)
            {
                var user = await provider.UserAsync(default);
                list = [.. list.Where(p => filter == PullRequestFilter.Mine
                    ? string.Equals(p.Author, user, StringComparison.OrdinalIgnoreCase)
                    : p.Reviewers.Contains(user, StringComparer.OrdinalIgnoreCase))];
            }
            if (Repository != repo || Filter.Filter != filter)
                return;
            Show(list, provider.Prefix);
            Message = list.Count == 0 ? $"No {Filter.Text.ToLowerInvariant()} {provider.Noun}s." : null;
        }
        catch (Exception ex) when (ex is PipelineException or IOException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Message = ex.Message;
        }
        finally
        {
            _reading = false;
            IsBusy = false;
            _next = DateTimeOffset.Now + Interval;
            UpdateStatusItem();
        }
    }

    void Show(IReadOnlyList<PullRequest> list, string prefix)
    {
        var rows = Rows.ToDictionary(r => r.Number);
        for (var i = 0; i < list.Count; i++)
        {
            if (rows.Remove(list[i].Number, out var row))
            {
                row.PullRequest = list[i];
                var at = Rows.IndexOf(row);
                if (at != i)
                    Rows.Move(at, i);
            }
            else
                Rows.Insert(i, new PullRequestRow(list[i], prefix));
        }
        foreach (var gone in rows.Values)
            Rows.Remove(gone);
    }

    /// <summary>The open pull request of the checked-out branch, as listed.</summary>
    public PullRequestRow? CurrentRow => Repository is { Branch: { } branch } ? Rows.FirstOrDefault(r => r.PullRequest.IsOpen && r.PullRequest.SourceBranch == branch) : null;

    void UpdateStatusItem()
    {
        var current = CurrentRow;
        StatusItem.IsVisible = current is not null;
        if (current is null || Repository?.Provider is not { } provider)
            return;
        var pr = current.PullRequest;
        StatusItem.Icon = pr.State.Icon();
        StatusItem.Text = $"{provider.Short} {provider.Prefix}{pr.Number}";
        StatusItem.ToolTip = $"{pr.SourceBranch}'s {provider.Noun}: {pr.Title} ({pr.State.Text()}, into {pr.TargetBranch})";
        StatusItem.Click = () => Open(current);
    }

    [RelayCommand]
    async Task SignInAsync()
    {
        if (Repository is not { Remote: { } remote } repo)
            return;
        var explanation = remote.Provider == CiProvider.GitHub
            ? "Karvon reads the pull requests through your gh CLI when it is signed in (gh auth login). Without it, give a token with the repo scope."
            : $"A personal access token of {remote.Authority} with the api scope, to read the merge requests, comment, approve and merge. "
              + "It is the Pipelines window's token too.";
        var dialog = new PipelineTokenDialog(remote, Connector.Secrets.HasKeyring, Connector.Secrets.Get(remote) is not null, explanation);
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

    /// <summary>A pull request's page: the one open, else a new one.</summary>
    [RelayCommand]
    public void Open(PullRequestRow? row)
    {
        if (row is not null)
            Open(row.PullRequest);
    }

    public PullRequestDocument? Open(PullRequest pr)
    {
        if (Repository is not { Provider: { } } repo)
            return null;
        var document = Host.Documents.OfType<PullRequestDocument>().FirstOrDefault(d => d.Page.Repo == repo && d.Page.PullRequest.Number == pr.Number)
            ?? new PullRequestDocument(new PullRequestPageViewModel(this, repo, pr));
        Host.ShowDocument(document);
        _ = document.Page.RefreshAsync();
        return document;
    }

    /// <summary>A pull request by its number (Claude's call), read whole from the service.</summary>
    public Task<PullRequest> FindAsync(string number, CancellationToken ct) => ProviderOrThrow().GetAsync(number.TrimStart('#', '!'), ct);

    internal IPullRequestProvider ProviderOrThrow() => Repository switch
    {
        null => throw new InvalidOperationException("No Git repository is open in Karvon."),
        { State: RepoState.NoService } r => throw new InvalidOperationException($"{r.Name}'s remote is not a GitHub or GitLab repository."),
        { State: RepoState.NeedsToken, Remote: { } remote } => throw new InvalidOperationException(
            $"Karvon has no sign-in for {remote.Authority}: the user signs in in View > Pull Requests (gh auth login for GitHub, or a token)."),
        { Provider: { } provider } => provider,
        var r => throw new InvalidOperationException(r.Error ?? $"{r.Name}'s service could not be reached."),
    };

    [RelayCommand]
    void OpenInBrowser(PullRequestRow? row)
    {
        if (row?.PullRequest.Url is { Length: > 0 } url)
            Host.OpenUrl(url);
    }

    /// <summary>
    /// A new pull request from the checked-out branch into the default one (as asked in its dialog): the branch pushed
    /// first, then made, and its page opened. A branch that has one already opens it.
    /// </summary>
    [RelayCommand]
    async Task NewAsync()
    {
        try
        {
            var provider = ProviderOrThrow();
            var repo = Repository!;
            if (repo.Branch is not { } branch)
            {
                Message = "HEAD is detached: check out the branch to propose first.";
                return;
            }
            var target = await provider.DefaultBranchAsync(default);
            if (branch == target)
            {
                Message = $"{branch} is the default branch: make a branch of the changes to propose first.";
                return;
            }
            if (CurrentRow is { } open)
            {
                Open(open);
                return;
            }
            var subjects = await repo.Repository.SubjectsSinceAsync("origin/" + target) ?? [];
            var dialog = new NewPullRequestDialog(provider.Noun, branch, target,
                subjects.Count > 0 ? subjects[^1] : branch, subjects.Count > 1 ? string.Join("\n", subjects.Select(s => "- " + s)) : "");
            if (await ShowAsync(dialog) is not { } form)
                return;
            await CreateAsync(form.Title.Trim(), form.Body.Trim(), form.Target.Trim(), form.Draft, default);
        }
        catch (Exception ex) when (ex is PipelineException or InvalidOperationException or IOException or HttpRequestException or System.Text.Json.JsonException)
        {
            Message = ex.Message;
        }
    }

    /// <summary>The branch pushed, then its pull request made and opened (Claude's pr_create too).</summary>
    public async Task<PullRequest> CreateAsync(string title, string body, string? target, bool draft, CancellationToken ct)
    {
        var provider = ProviderOrThrow();
        var repo = Repository!;
        if (repo.Branch is not { } branch)
            throw new InvalidOperationException("HEAD is detached: there is no branch to propose.");
        target = target is { Length: > 0 } ? target : await provider.DefaultBranchAsync(ct);
        if (!await Host.PushAsync(repo.Repository))
            throw new InvalidOperationException($"{branch} could not be pushed (see Output, Git): the {provider.Noun} needs it on the remote.");
        var made = await provider.CreateAsync(branch, target, title, body, draft, ct);
        Host.Status($"{provider.Short} {provider.Prefix}{made.Number} made: {made.Title}");
        Open(made);
        _ = RefreshAsync();
        return made;
    }

    /// <summary>Its branch checked out here: the branch itself (fetched), or a fork's as pr/N (mr/N).</summary>
    public async Task<bool> CheckoutAsync(PullRequest pr)
    {
        var provider = ProviderOrThrow();
        var repo = Repository!.Repository;
        var local = pr.IsFromFork ? $"{provider.Short.ToLowerInvariant()}/{pr.Number}" : pr.SourceBranch;
        if (Repository.Branch == local)
        {
            Host.Status($"{local} is checked out already.");
            return true;
        }
        return await Host.RunGitAsync($"Checkout {provider.Prefix}{pr.Number} ({local})", async () =>
        {
            var fetch = await repo.FetchRefAsync("origin", pr.IsFromFork ? $"{provider.HeadRef(pr)}:{local}" : pr.SourceBranch);
            return fetch.Success ? await repo.SwitchAsync(local) : fetch;
        }, changesFiles: true);
    }

    [RelayCommand]
    async Task CheckoutRowAsync(PullRequestRow? row)
    {
        if (row is null)
            return;
        try
        {
            await CheckoutAsync(row.PullRequest);
        }
        catch (InvalidOperationException ex)
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

/// <summary>A pull request's page in the document well.</summary>
public sealed class PullRequestDocument(PullRequestPageViewModel page) : DocumentTab
{
    public PullRequestPageViewModel Page => page;

    public override string Key => $"pull-request:{page.Repo.Remote?.Authority}/{page.Repo.Name}{page.Prefix}{page.PullRequest.Number}";

    public override string Title => $"{page.Short} {page.Prefix}{page.PullRequest.Number}";

    public override string ToolTip => $"{page.Repo.Name}: {page.PullRequest.Title}";

    public override void Dispose() => page.Dispose();
}
