using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Karvon.Git;
using Karvon.Workspace;

namespace Karvon.ViewModels;

/// <summary>A changed file in the Git Changes window, and the repository it is in.</summary>
public sealed record GitFileItem(GitFileChange Change, GitRepoItem? Repository = null)
{
    public string FileName => Path.GetFileName(Change.Path);

    public string Folder => Path.GetDirectoryName(Change.RelativePath) is { Length: > 0 } dir ? dir : "";

    /// <summary>The folder shown after the file name, gray, as in VS.</summary>
    public string FolderSuffix => Folder.Length > 0 ? "  " + Folder : "";

    public string Letter => Change.Letter;

    public string LetterColor => Change.Kind switch
    {
        GitChangeKind.Added or GitChangeKind.Untracked or GitChangeKind.Renamed => "#73C991",
        GitChangeKind.Deleted => "#C74E39",
        GitChangeKind.Conflicted => "#E4676B",
        _ => "#E2C08D",
    };
}

/// <summary>Git integration: Git Changes window, branch status bar, Solution Explorer badges, editor change margin.</summary>
public partial class MainViewModel
{
    /// <summary>The solution's own repository (else the first one found): the status bar's branch and its menu.</summary>
    GitRepository? _git;
    GitWatcher? _gitWatcher;
    Dictionary<string, string> _gitBadges = [];
    Task? _gitRefresh;
    bool _gitRefreshQueued;
    List<GitTreeRow> _stagedTree = [];
    List<GitTreeRow> _unstagedTree = [];
    readonly HashSet<string> _collapsedGitRows = [];

    /// <summary>How deep the solution's folder is searched for repositories of its own.</summary>
    const int GitSearchDepth = 3;

    static readonly HashSet<string> GitSkippedFolders = new(StringComparer.Ordinal) { "bin", "obj", "node_modules", "packages", "TestResults" };

    public OutputChannel GitOutput { get; } = new("Source Control - Git");

    /// <summary>Every repository of the solution, as Git Changes lists them (the solution's own first).</summary>
    public ObservableCollection<GitRepoItem> GitRepositories { get; } = [];

    /// <summary>The changes of the repositories in scope (all of them, or the one chosen in the repository menu).</summary>
    public ObservableCollection<GitFileItem> StagedChanges { get; } = [];
    public ObservableCollection<GitFileItem> UnstagedChanges { get; } = [];

    /// <summary>The same changes as the trees Git Changes draws: repositories, folders and files.</summary>
    public ObservableCollection<GitTreeRow> StagedRows { get; } = [];
    public ObservableCollection<GitTreeRow> UnstagedRows { get; } = [];

    /// <summary>The local branches of the solution's repository (the status bar's branch menu).</summary>
    public ObservableCollection<string> Branches { get; } = [];

    /// <summary>The one repository Git Changes works on; null for all of them.</summary>
    [ObservableProperty]
    public partial GitRepoItem? SelectedGitRepository { get; set; }

    /// <summary>"Name &lt;email&gt;" from git config: who the commits are made as (never Claude).</summary>
    [ObservableProperty]
    public partial string? GitAuthor { get; set; }

    [ObservableProperty]
    public partial bool IsStagedSectionExpanded { get; set; } = true;

    [ObservableProperty]
    public partial bool IsChangesSectionExpanded { get; set; } = true;

    IReadOnlyList<GitRepoItem> GitScope => SelectedGitRepository is { } one ? [one] : GitRepositories;

    public bool IsMultiRepository => GitRepositories.Count > 1;

    /// <summary>"5 Repositories", or the name of the one in scope.</summary>
    public string GitRepositoryLabel => SelectedGitRepository?.Name ?? (GitRepositories.Count == 1 ? GitRepositories[0].Name : $"{GitRepositories.Count} Repositories");

    /// <summary>The branch of the repositories in scope, or "[multiple]" when they differ.</summary>
    public string GitScopeBranch => GitScope.Select(r => r.Branch).Distinct().ToList() is { Count: 1 } one ? one[0] : "[multiple]";

    /// <summary>Outgoing / incoming commits of the repositories in scope, as Git Changes shows them.</summary>
    public string GitScopeSync => $"{GitScope.Sum(r => r.Ahead)} / {GitScope.Sum(r => r.Behind)}";

    public bool HasNoGitChanges => HasGit && StagedChanges.Count == 0 && UnstagedChanges.Count == 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand), nameof(FetchCommand), nameof(PullCommand), nameof(PushCommand), nameof(SyncCommand), nameof(NewBranchCommand))]
    public partial bool HasGit { get; set; }

    [ObservableProperty]
    public partial string GitBranch { get; set; } = "";

    [ObservableProperty]
    public partial string GitSyncText { get; set; } = "";

    [ObservableProperty]
    public partial int GitChangeCount { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand))]
    public partial string CommitMessage { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand), nameof(FetchCommand), nameof(PullCommand), nameof(PushCommand), nameof(SyncCommand), nameof(NewBranchCommand))]
    public partial bool IsGitBusy { get; set; }

    /// <summary>"Commit Staged" with something staged, else "Commit All" ("Commit All Repos" across repositories), like VS.</summary>
    public string CommitButtonText => StagedChanges.Count > 0 ? "Commit Staged"
        : GitScope.Count(r => r.Changes.Count > 0) > 1 ? "Commit All Repos" : "Commit All";

    async Task OpenGitAsync(SolutionInfo solution)
    {
        foreach (var repository in await FindRepositoriesAsync(solution))
            GitRepositories.Add(new GitRepoItem(repository));
        _git = GitRepositories.FirstOrDefault()?.Repository;
        HasGit = _git is not null;
        OnPropertyChanged(nameof(IsMultiRepository));
        ExplorerNode.GitStatusProvider = path => _gitBadges.GetValueOrDefault(path);
        if (_git is not null)
        {
            GitAuthor = await _git.AuthorAsync();
            await RefreshGitAsync();
        }
        // Watched with or without a repository: Solution Explorer and open editors follow files changed on disk.
        try
        {
            var watcher = new GitWatcher(solution.Directory, _git?.GitDir, TimeSpan.FromMilliseconds(400));
            watcher.Changed += (repository, files) => Dispatcher.UIThread.Post(() => _ = OnRepositoryChangedAsync(watcher, repository, files));
            _gitWatcher = watcher;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            GitOutput.Append("Not watching the repository for outside changes: " + ex.Message);
        }
    }

    /// <summary>A commit, checkout or edit made outside Karvon (e.g. in the terminal): follow it right away.</summary>
    async Task OnRepositoryChangedAsync(GitWatcher watcher, bool repository, IReadOnlyCollection<string> files)
    {
        if (_gitWatcher != watcher)
            return;
        // The solution or a project file changed outside Solution Explorer (an editor, git, dotnet sln, Claude): read again.
        if (ChangesStructure(files))
            await ReloadSolutionAsync();
        ReloadExplorerFolders(files);
        await RefreshGitAsync();
        foreach (var doc in Editors.Where(d => files.Contains(d.FilePath)).ToList())
            if (doc.Host.ReloadIfChangedOnDisk())
                SetStatus($"{doc.FileName} was reloaded because it changed on disk.");
        if (repository)
            await RefreshAllGitBaselinesAsync();
    }

    /// <summary>
    /// Every repository of the solution, as Git Changes finds them: the one its folder is in, those in its folders (a
    /// few levels down), and those of projects linked from folders outside them.
    /// </summary>
    static async Task<List<GitRepository>> FindRepositoriesAsync(SolutionInfo solution)
    {
        var found = new List<GitRepository>();
        bool Known(string directory) => found.Any(r => directory == r.Root || directory.StartsWith(r.Root + "/", StringComparison.Ordinal));
        async Task AddAsync(string directory)
        {
            if (await GitRepository.OpenAsync(directory) is { } repository && found.All(r => r.Root != repository.Root))
                found.Add(repository);
        }
        await AddAsync(solution.Directory);
        var nested = await Task.Run(() =>
        {
            var folders = new List<string>();
            SearchRepositories(solution.Directory, 0, folders);
            return folders;
        });
        var others = new List<GitRepository>();
        foreach (var folder in nested)
            if (await GitRepository.OpenAsync(folder) is { } repository && repository.Root == folder)
                others.Add(repository);
        found.AddRange(others.Where(r => found.All(f => f.Root != r.Root)));
        // Projects outside every repository found so far say where theirs is (once per repository, not per project).
        foreach (var project in solution.Projects)
            if (!Known(project.Directory))
                await AddAsync(project.Directory);
        return [.. found.Take(1), .. found.Skip(1).OrderBy(r => Path.GetFileName(r.Root), StringComparer.OrdinalIgnoreCase)];
    }

    static void SearchRepositories(string directory, int depth, List<string> found)
    {
        if (depth >= GitSearchDepth)
            return;
        string[] children;
        try
        {
            children = Directory.GetDirectories(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }
        foreach (var child in children)
        {
            var name = Path.GetFileName(child);
            if (name.StartsWith('.') || GitSkippedFolders.Contains(name))
                continue;
            // A .git folder, or a .git file (a submodule or a worktree).
            var git = Path.Combine(child, ".git");
            if (Directory.Exists(git) || File.Exists(git))
                found.Add(child);
            SearchRepositories(child, depth + 1, found);
        }
    }

    /// <summary>The repository a file of the disk is in (the innermost one).</summary>
    GitRepoItem? RepositoryOf(string path) =>
        GitRepositories.Where(r => r.Contains(path)).OrderByDescending(r => r.Root.Length).FirstOrDefault();

    void CloseGit()
    {
        _gitWatcher?.Dispose();
        _gitWatcher = null;
        _git = null;
        GitRepositories.Clear();
        SelectedGitRepository = null;
        GitAuthor = null;
        StagedRows.Clear();
        UnstagedRows.Clear();
        _collapsedGitRows.Clear();
        _gitBadges = [];
        HasGit = false;
        GitBranch = GitSyncText = "";
        GitChangeCount = 0;
        StagedChanges.Clear();
        UnstagedChanges.Clear();
        Branches.Clear();
        CommitMessage = "";
    }

    /// <summary>
    /// Re-reads status; coalesces bursts (saves, file events, focus changes) into at most one extra run. A caller
    /// that arrives while a read is in flight waits for the next one, so after a git command the state is current.
    /// </summary>
    public Task RefreshGitAsync()
    {
        if (_git is null)
            return Task.CompletedTask;
        if (_gitRefresh is { IsCompleted: false } running)
        {
            _gitRefreshQueued = true;
            return running;
        }
        return _gitRefresh = RefreshGitLoopAsync(GitRepositories.ToList());
    }

    async Task RefreshGitLoopAsync(List<GitRepoItem> repositories)
    {
        try
        {
            do
            {
                _gitRefreshQueued = false;
                var statuses = await Task.WhenAll(repositories.Select(r => r.Repository.StatusAsync()));
                if (!repositories.SequenceEqual(GitRepositories))
                    return;
                for (var i = 0; i < repositories.Count; i++)
                    repositories[i].Status = statuses[i];
                ApplyGitStatus();
                if (_git is { } git)
                {
                    var branches = await git.BranchesAsync();
                    Branches.Clear();
                    foreach (var b in branches)
                        Branches.Add(b);
                }
            }
            while (_gitRefreshQueued);
        }
        catch (Exception ex)
        {
            GitOutput.Append("git status failed: " + ex.Message);
        }
    }

    void ApplyGitStatus()
    {
        var primary = GitRepositories.FirstOrDefault(r => r.Repository == _git);
        GitBranch = primary?.Branch ?? "";
        GitSyncText = primary?.Status?.Branch.Upstream is null ? "" : $"↑{primary.Ahead} ↓{primary.Behind}";
        ShowGitScope();

        // Working-tree status wins over the index for the badge, matching what the editor shows.
        _gitBadges = GitRepositories.SelectMany(r => r.Changes).OrderBy(c => c.Staged ? 0 : 1).GroupBy(c => c.Path)
            .ToDictionary(g => g.Key, g => g.Last().Letter);
        foreach (var root in ExplorerRoots)
            root.RefreshGitBadges();
    }

    /// <summary>Fills both sections from the repositories in scope, as lists and as trees.</summary>
    void ShowGitScope()
    {
        StagedChanges.Clear();
        UnstagedChanges.Clear();
        foreach (var repository in GitScope)
            foreach (var c in repository.Changes.OrderBy(c => c.RelativePath, StringComparer.Ordinal))
                (c.Staged ? StagedChanges : UnstagedChanges).Add(new GitFileItem(c, repository));
        GitChangeCount = GitScope.Sum(r => r.Changes.Select(c => c.Path).Distinct().Count());
        var showRoot = IsMultiRepository && GitScope.Count > 1;
        _stagedTree = GitTreeRow.Build(StagedChanges, staged: true, showRoot);
        _unstagedTree = GitTreeRow.Build(UnstagedChanges, staged: false, showRoot);
        foreach (var row in GitTreeRow.All(_stagedTree.Concat(_unstagedTree)))
            row.IsExpanded = !_collapsedGitRows.Contains(row.Key);
        ShowGitRows();
        foreach (var name in new[] { nameof(CommitButtonText), nameof(GitRepositoryLabel), nameof(GitScopeBranch), nameof(GitScopeSync),
                     nameof(IsMultiRepository), nameof(HasNoGitChanges) })
            OnPropertyChanged(name);
        CommitCommand.NotifyCanExecuteChanged();
        WriteCommitMessageCommand.NotifyCanExecuteChanged();
    }

    void ShowGitRows()
    {
        StagedRows.Clear();
        UnstagedRows.Clear();
        foreach (var row in GitTreeRow.Visible(_stagedTree))
            StagedRows.Add(row);
        foreach (var row in GitTreeRow.Visible(_unstagedTree))
            UnstagedRows.Add(row);
    }

    /// <summary>A folder, repository or the shared folder opens or closes; a file opens its diff.</summary>
    [RelayCommand]
    async Task ActivateGitRow(GitTreeRow? row)
    {
        if (row is { IsExpandable: true })
        {
            row.IsExpanded = !row.IsExpanded;
            if (row.IsExpanded)
                _collapsedGitRows.Remove(row.Key);
            else
                _collapsedGitRows.Add(row.Key);
            ShowGitRows();
        }
        else if (row?.Item is { } item)
            await OpenDiff(item);
    }

    [RelayCommand]
    void ToggleGitSection(string? section)
    {
        if (section == "staged")
            IsStagedSectionExpanded = !IsStagedSectionExpanded;
        else
            IsChangesSectionExpanded = !IsChangesSectionExpanded;
    }

    /// <summary>The repository menu: one repository for Git Changes to work on, or all of them (null).</summary>
    [RelayCommand]
    async Task SelectGitRepository(GitRepoItem? repository)
    {
        SelectedGitRepository = repository is not null && GitRepositories.Contains(repository) ? repository : null;
        foreach (var r in GitRepositories)
            r.IsSelected = r == SelectedGitRepository;
        ShowGitScope();
        if ((SelectedGitRepository ?? GitRepositories.FirstOrDefault()) is { } shown)
            GitAuthor = await shown.Repository.AuthorAsync();
    }

    /// <summary>Points each open editor's change margin at the file's HEAD content.</summary>
    async Task RefreshGitBaselineAsync(DocumentViewModel doc)
    {
        if (RepositoryOf(doc.FilePath) is not { } repository)
        {
            doc.Host.SetGitBaseline(null);
            return;
        }
        var head = await repository.Repository.ShowHeadAsync(doc.FilePath);
        var tracked = repository.Changes.Any(c => c.Path == doc.FilePath && c.Kind == GitChangeKind.Untracked) != true;
        doc.Host.SetGitBaseline(head ?? (tracked ? null : ""));
    }

    async Task RefreshAllGitBaselinesAsync()
    {
        foreach (var doc in Editors.ToList())
            await RefreshGitBaselineAsync(doc);
    }

    // ---- Commands ------------------------------------------------------------------------------------------

    bool CanCommit => HasGit && !IsGitBusy && !string.IsNullOrWhiteSpace(CommitMessage) && GitChangeCount > 0;

    /// <summary>The repository a change is in (the solution's for a change made before there were several).</summary>
    GitRepository? RepositoryOf(GitFileItem item) => item.Repository?.Repository ?? RepositoryOf(item.Change.Path)?.Repository ?? _git;

    /// <summary>
    /// git commit in every repository in scope that has something to commit, with the user's own identity; the message
    /// goes in as written. With something staged only that is committed, otherwise every change (Commit All).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCommit))]
    async Task Commit()
    {
        SaveAll();
        var stagedOnly = StagedChanges.Count > 0;
        var repositories = GitScope.Where(r => stagedOnly ? r.HasStaged : r.Changes.Count > 0).ToList();
        var message = CommitMessage.Trim();
        var committed = 0;
        foreach (var repository in repositories)
            if (await GitOperationAsync(repositories.Count > 1 ? $"Commit ({repository.Name})" : "Commit",
                    () => repository.Repository.CommitAsync(message, all: !stagedOnly)))
                committed++;
        if (committed > 0 && committed == repositories.Count)
            CommitMessage = "";
        await RefreshAllGitBaselinesAsync();
    }

    [RelayCommand]
    async Task Stage(GitFileItem? item)
    {
        if (item is not null && RepositoryOf(item) is { } git)
            await GitOperationAsync(null, () => git.StageAsync([item.Change.Path]));
    }

    [RelayCommand]
    async Task Unstage(GitFileItem? item)
    {
        if (item is not null && RepositoryOf(item) is { } git)
            await GitOperationAsync(null, () => git.UnstageAsync([item.Change.Path]));
    }

    /// <summary>Stages a file, a folder, a repository or everything under the shared folder.</summary>
    [RelayCommand]
    Task StageRow(GitTreeRow? row) => row is null ? Task.CompletedTask : StageItemsAsync(row.Files().ToList(), stage: true);

    [RelayCommand]
    Task UnstageRow(GitTreeRow? row) => row is null ? Task.CompletedTask : StageItemsAsync(row.Files().ToList(), stage: false);

    [RelayCommand]
    Task StageAll() => StageItemsAsync(UnstagedChanges.ToList(), stage: true);

    [RelayCommand]
    Task UnstageAll() => StageItemsAsync(StagedChanges.ToList(), stage: false);

    async Task StageItemsAsync(IReadOnlyList<GitFileItem> items, bool stage)
    {
        foreach (var group in items.GroupBy(RepositoryOf).Where(g => g.Key is not null))
        {
            var paths = group.Select(i => i.Change.Path).Distinct().ToList();
            await GitOperationAsync(null, () => stage ? group.Key!.StageAsync(paths) : group.Key!.UnstageAsync(paths));
        }
    }

    [RelayCommand]
    async Task Discard(GitFileItem? item)
    {
        if (item is null || RepositoryOf(item) is not { } git)
            return;
        if (Dialogs is not null && await Dialogs.ConfirmAsync("Undo Changes", $"Undo the changes to {item.FileName}? This cannot be undone.") != true)
            return;
        await GitOperationAsync(null, () => git.DiscardAsync(item.Change));
        CheckExternalChanges();
        await RefreshAllGitBaselinesAsync();
        if (item.Change.Kind == GitChangeKind.Untracked)
            await RefreshExplorer();
    }

    bool CanSync => HasGit && !IsGitBusy;

    /// <summary>Runs a git command in each repository in scope, one after another (each one's output in the Git pane).</summary>
    async Task<bool> InEachRepositoryAsync(string title, Func<GitRepoItem, Task<GitResult>> operation)
    {
        var scope = GitScope.ToList();
        var ok = true;
        foreach (var repository in scope)
            ok &= await GitOperationAsync(scope.Count > 1 ? $"{title} ({repository.Name})" : title, () => operation(repository));
        return ok;
    }

    [RelayCommand(CanExecute = nameof(CanSync))]
    Task Fetch() => InEachRepositoryAsync("Fetch", r => r.Repository.FetchAsync(GitOutput.Append));

    [RelayCommand(CanExecute = nameof(CanSync))]
    async Task Pull()
    {
        SaveAll();
        await InEachRepositoryAsync("Pull", r => r.Repository.PullAsync(GitOutput.Append));
        await AfterWorkingTreeChangedAsync();
    }

    [RelayCommand(CanExecute = nameof(CanSync))]
    Task Push() => InEachRepositoryAsync("Push", r => r.Status is { } status ? r.Repository.PushAsync(status.Branch, GitOutput.Append)
        : Task.FromResult(new GitResult(0, "")));

    /// <summary>Pull, then push, in each repository in scope (Git Changes' Sync).</summary>
    [RelayCommand(CanExecute = nameof(CanSync))]
    async Task Sync()
    {
        SaveAll();
        await InEachRepositoryAsync("Sync", async r =>
        {
            var pull = await r.Repository.PullAsync(GitOutput.Append);
            return pull.Success && r.Status is { } status ? await r.Repository.PushAsync(status.Branch, GitOutput.Append) : pull;
        });
        await AfterWorkingTreeChangedAsync();
    }

    /// <summary>Switches to a local branch in the repositories in scope that have it; the others stay as they are.</summary>
    [RelayCommand]
    async Task SwitchBranch(string? branch)
    {
        if (string.IsNullOrEmpty(branch) || GitScope.Count == 1 && branch == GitScope[0].Branch)
            return;
        SaveAll();
        var targets = new List<GitRepoItem>();
        foreach (var repository in GitScope)
            if (repository.Branch != branch && await repository.Repository.HasRefAsync("refs/heads/" + branch))
                targets.Add(repository);
        foreach (var repository in targets)
            await GitOperationAsync(targets.Count > 1 ? $"Checkout {branch} ({repository.Name})" : $"Checkout {branch}", () => repository.Repository.SwitchAsync(branch));
        await AfterWorkingTreeChangedAsync();
    }

    /// <summary>A remote branch ("origin/feature"): its local branch, made to track it where there is none yet.</summary>
    [RelayCommand]
    async Task SwitchRemoteBranch(string? remote)
    {
        var slash = remote?.IndexOf('/') ?? -1;
        if (remote is null || slash < 1)
            return;
        var local = remote[(slash + 1)..];
        SaveAll();
        foreach (var repository in GitScope)
        {
            if (await repository.Repository.HasRefAsync("refs/heads/" + local))
                await GitOperationAsync($"Checkout {local}", () => repository.Repository.SwitchAsync(local));
            else if (await repository.Repository.HasRefAsync("refs/remotes/" + remote))
                await GitOperationAsync($"Checkout {local} tracking {remote}", () => repository.Repository.SwitchTrackingAsync(local, remote));
        }
        await AfterWorkingTreeChangedAsync();
    }

    /// <summary>The branches of the repositories in scope, for the branch menu: local ones, then remote ones.</summary>
    public async Task<(IReadOnlyList<string> Local, IReadOnlyList<string> Remote)> ScopeBranchesAsync()
    {
        var local = new SortedSet<string>(StringComparer.Ordinal);
        var remote = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var repository in GitScope)
        {
            local.UnionWith(await repository.Repository.BranchesAsync());
            remote.UnionWith(await repository.Repository.RemoteBranchesAsync());
        }
        return (local.ToList(), remote.ToList());
    }

    [RelayCommand(CanExecute = nameof(CanSync))]
    async Task NewBranch()
    {
        if (Dialogs is null || await Dialogs.PromptAsync("Create a New Branch", "Branch name:", "") is not { Length: > 0 } name)
            return;
        await CreateBranchAsync(name.Trim().Replace(' ', '-'));
    }

    /// <summary>A new branch from the current commit, switched to, in each repository in scope.</summary>
    public Task CreateBranchAsync(string name) => InEachRepositoryAsync($"Create branch {name}", r => r.Repository.CreateBranchAsync(name));

    [RelayCommand]
    async Task OpenDiff(GitFileItem? item)
    {
        if (item is null || RepositoryOf(item) is not { } git)
            return;
        var change = item.Change;
        string left, right, leftLabel, rightLabel, key;
        if (change.Staged)
        {
            // Staged change: what the commit would record (index) against HEAD.
            left = await git.ShowHeadAsync(change.Path) ?? "";
            right = await git.ShowIndexAsync(change.Path) ?? "";
            (leftLabel, rightLabel, key) = ($"{item.FileName};HEAD", $"{item.FileName};Staged", "staged");
        }
        else
        {
            // Working-tree change: the file on disk against the index (HEAD when nothing is staged).
            left = change.Kind == GitChangeKind.Untracked ? "" : await git.ShowIndexAsync(change.Path) ?? await git.ShowHeadAsync(change.Path) ?? "";
            right = change.Kind == GitChangeKind.Deleted || !File.Exists(change.Path) ? "" : await File.ReadAllTextAsync(change.Path);
            (leftLabel, rightLabel, key) = ($"{item.FileName};Index", $"{item.FileName};Working Tree", "working");
        }
        ShowDiff(new DiffViewModel(change.Path, left, right, leftLabel, rightLabel, key));
    }

    /// <summary>Compare with Unmodified: the editor's current text against HEAD.</summary>
    public async Task CompareWithUnmodifiedAsync(DocumentViewModel doc)
    {
        if (RepositoryOf(doc.FilePath)?.Repository is not { } git)
            return;
        var head = await git.ShowHeadAsync(doc.FilePath) ?? "";
        ShowDiff(new DiffViewModel(doc.FilePath, head, doc.Host.Document.Text, $"{doc.FileName};HEAD", $"{doc.FileName};Current", "head"));
    }

    void ShowDiff(DiffViewModel diff)
    {
        // Reopen so the tab shows the latest content instead of a stale comparison.
        if (Documents.FirstOrDefault(d => d.Key == diff.Key) is { } stale)
        {
            Documents.Remove(stale);
            stale.Dispose();
        }
        Documents.Add(diff);
        ActiveDocument = diff;
    }

    [RelayCommand]
    Task CompareActiveWithUnmodified() => ActiveEditor is { } doc ? CompareWithUnmodifiedAsync(doc) : Task.CompletedTask;

    [RelayCommand]
    void ShowGitChanges() => ShowToolWindow(ToolWindows.GitChanges);

    /// <summary>Runs a git command, logs it to the Git output pane and refreshes status. Returns success.</summary>
    async Task<bool> GitOperationAsync(string? title, Func<Task<GitResult>> operation, Action? success = null)
    {
        IsGitBusy = true;
        try
        {
            if (title is not null)
            {
                SetStatus($"Git: {title}...", StatusKind.Busy);
                GitOutput.Append($"> {title}");
            }
            var result = await operation();
            if (!string.IsNullOrWhiteSpace(result.Output) && (title is not null || !result.Success))
                GitOutput.Append(result.Output.TrimEnd());
            if (result.Success)
            {
                success?.Invoke();
                if (title is not null)
                    SetStatus($"Git: {title} succeeded", StatusKind.Ready);
            }
            else
            {
                SelectedOutput = GitOutput;
                ShowToolWindow(ToolWindows.Output);
                SetStatus($"Git: {title ?? "operation"} failed (see Output)", StatusKind.Ready);
            }
            return result.Success;
        }
        catch (Exception ex)
        {
            GitOutput.Append(ex.Message);
            SetStatus("Git: " + ex.Message, StatusKind.Ready);
            return false;
        }
        finally
        {
            IsGitBusy = false;
            await RefreshGitAsync();
        }
    }

    /// <summary>After pull/checkout the files on disk changed: reload editors, margins and the tree.</summary>
    async Task AfterWorkingTreeChangedAsync()
    {
        CheckExternalChanges();
        await RefreshAllGitBaselinesAsync();
        await RefreshExplorer();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }
}
