using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Karvon.ViewModels;

namespace Karvon.Git;

/// <summary>A commit of the history: its graph row, its refs, when.</summary>
public sealed class GitHistoryRow(GitCommit commit, GitGraphRow graph)
{
    public GitCommit Commit => commit;

    public GitGraphRow Graph => graph;

    public string Subject => commit.Subject;

    public string Author => commit.Author;

    public string ShortHash => commit.ShortHash;

    public string Date => commit.Date.ToString("yyyy-MM-dd HH:mm");

    public IReadOnlyList<GitRef> Refs => commit.Refs;

    public bool HasRefs => commit.Refs.Count > 0;

    public bool IsHead => commit.Refs.Any(r => r.IsCurrent);

    public bool IsMerge => commit.IsMerge;
}

/// <summary>
/// A repository's history, as Visual Studio's Git Repository window and Git Graph show it: the commits with their
/// graph (HEAD's, or every branch's), their branches and tags, who and when; a commit's message, committer and files
/// (each opens as a diff against the parent); and what can be done at a commit: a branch or a tag made there, checked
/// out, cherry-picked, reverted, the branch reset to it. One file's history (through renames) the same way.
/// </summary>
public sealed partial class GitHistoryViewModel : DocumentTab
{
    const int PageSize = 200;

    readonly MainViewModel _vm;
    GitGraph _graph = new();
    bool _end;
    string? _refs;
    Task _load = Task.CompletedTask;
    Task _reloading = Task.CompletedTask;
    Task _following = Task.CompletedTask;

    public GitHistoryViewModel(MainViewModel vm, GitRepository repository, string? file)
    {
        _vm = vm;
        Repository = repository;
        File = file;
    }

    public GitRepository Repository { get; }

    /// <summary>The file whose history this is, or null for the repository's.</summary>
    public string? File { get; }

    public bool IsFileHistory => File is not null;

    public override string Key => File is null ? $"git-history:{Repository.Root}" : $"git-history:{File}";

    public override string Title => File is null ? $"History - {Path.GetFileName(Repository.Root.TrimEnd('/', '\\'))}" : $"History - {Path.GetFileName(File)}";

    public override string ToolTip => File ?? Repository.Root;

    public ObservableCollection<GitHistoryRow> Rows { get; } = [];

    /// <summary>The rows shown: all of them, or those the filter finds (then without the graph, which they would break).</summary>
    public ObservableCollection<GitHistoryRow> Visible { get; } = [];

    [ObservableProperty]
    public partial bool AllBranches { get; set; }

    partial void OnAllBranchesChanged(bool value) => _ = ReloadAsync();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsGraph))]
    public partial string Filter { get; set; } = "";

    partial void OnFilterChanged(string value) => ShowVisible();

    public bool ShowsGraph => Filter.Trim().Length == 0 && !IsFileHistory;

    /// <summary>As wide as the most lanes a row has.</summary>
    [ObservableProperty]
    public partial double GraphWidth { get; private set; } = 24;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial GitHistoryRow? Selected { get; set; }

    public bool HasSelection => Selected is not null;

    partial void OnSelectedChanged(GitHistoryRow? value) => _ = LoadDetailsAsync(value);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetails), nameof(Parents))]
    public partial GitCommitDetails? Details { get; private set; }

    public bool HasDetails => Details is not null;

    public string Parents => Details is { } d ? string.Join(", ", d.Commit.Parents.Select(p => p[..Math.Min(8, p.Length)])) : "";

    public bool HasMore => !_end;

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    /// <summary>The history from its newest commit, read as the refs are now.</summary>
    public Task LoadAsync() => RefsChangedAsync();

    /// <summary>The history again from its newest commit (the selected one kept when it is still there), after any reload under way.</summary>
    [RelayCommand]
    public Task ReloadAsync() => _reloading = ReloadAfterAsync(_reloading);

    async Task ReloadAfterAsync(Task previous)
    {
        await previous;
        // A page still being read belongs to the history before.
        await _load;
        var keep = Selected?.Commit.Hash;
        Selected = null;
        _graph = new GitGraph();
        _end = false;
        Visible.Clear();
        Rows.Clear();
        GraphWidth = 24;
        await LoadMoreAsync();
        Selected = Rows.FirstOrDefault(r => r.Commit.Hash == keep) ?? Rows.FirstOrDefault();
    }

    /// <summary>The next page of commits (the one being read, if one is).</summary>
    [RelayCommand]
    public Task LoadMoreAsync() => !_load.IsCompleted || _end ? _load : _load = LoadPageAsync();

    async Task LoadPageAsync()
    {
        IsBusy = true;
        try
        {
            var commits = await Repository.LogAsync(Rows.Count, PageSize, AllBranches, File);
            _end = commits.Count < PageSize;
            var filter = Filter.Trim();
            foreach (var commit in commits)
            {
                var row = new GitHistoryRow(commit, _graph.Add(commit));
                Rows.Add(row);
                if (filter.Length == 0 || Matches(commit, filter))
                    Visible.Add(row);
                GraphWidth = Math.Max(GraphWidth, row.Graph.Lanes * GitGraphCell.LaneWidth + 8);
            }
            OnPropertyChanged(nameof(HasMore));
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>A commit selected (the filter cleared), the pages read until it is there; false when the history does not have it.</summary>
    public async Task<bool> RevealAsync(string hash)
    {
        await _following;
        await _reloading;
        await _load;
        while (true)
        {
            if (Rows.FirstOrDefault(r => r.Commit.Hash == hash) is { } row)
            {
                Filter = "";
                Selected = row;
                return true;
            }
            if (_end)
                return false;
            await LoadMoreAsync();
        }
    }

    /// <summary>The filter changed: the rows it finds, the selected one kept when it is among them.</summary>
    void ShowVisible()
    {
        var keep = Selected;
        var filter = Filter.Trim();
        Visible.Clear();
        foreach (var row in filter.Length == 0 ? Rows : Rows.Where(r => Matches(r.Commit, filter)))
            Visible.Add(row);
        if (keep is not null && Visible.Contains(keep))
            Selected = keep;
        OnPropertyChanged(nameof(ShowsGraph));
    }

    static bool Matches(GitCommit c, string filter) =>
        c.Subject.Contains(filter, StringComparison.CurrentCultureIgnoreCase) || c.Author.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
        || c.Hash.StartsWith(filter, StringComparison.OrdinalIgnoreCase) || c.Refs.Any(r => r.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase));

    async Task LoadDetailsAsync(GitHistoryRow? row)
    {
        if (row is null)
        {
            Details = null;
            return;
        }
        var details = await Repository.CommitDetailsAsync(row.Commit);
        if (Selected == row)
            Details = details;
    }

    /// <summary>
    /// Git's state was read again: the history again, when a ref moved (a commit, a fetch, a checkout). One look at a
    /// time, so a burst of them reloads once.
    /// </summary>
    public Task RefsChangedAsync() => _following = FollowAfterAsync(_following);

    async Task FollowAfterAsync(Task previous)
    {
        await previous;
        var refs = await Repository.RefsSignatureAsync();
        if (refs == _refs)
            return;
        _refs = refs;
        await ReloadAsync();
    }

    /// <summary>A file of the selected commit against the commit's first parent.</summary>
    [RelayCommand]
    async Task OpenFileAsync(GitCommitFile? file)
    {
        if (file is null || Details is not { } details)
            return;
        await _vm.ShowCommitDiffAsync(Repository, details.Commit, file);
    }

    [RelayCommand]
    void CopyHash(GitHistoryRow? row)
    {
        if ((row ?? Selected) is { } r)
            CopyRequested?.Invoke(r.Commit.Hash);
    }

    /// <summary>Text to put on the clipboard (the view does it).</summary>
    public event Action<string>? CopyRequested;

    [RelayCommand]
    async Task CreateBranchAsync(GitHistoryRow? row)
    {
        if ((row ?? Selected) is not { } r || _vm.Dialogs is not { } dialogs
            || await dialogs.PromptAsync("Create Branch", $"A branch at {r.ShortHash} ({r.Subject})", "") is not { Length: > 0 } name)
            return;
        await _vm.HistoryOperationAsync($"Create branch {name.Trim()}", () => Repository.BranchAtAsync(name.Trim(), r.Commit.Hash));
    }

    [RelayCommand]
    async Task CreateTagAsync(GitHistoryRow? row)
    {
        if ((row ?? Selected) is not { } r || _vm.Dialogs is not { } dialogs
            || await dialogs.PromptAsync("Create Tag", $"A tag at {r.ShortHash} ({r.Subject})", "") is not { Length: > 0 } name)
            return;
        await _vm.HistoryOperationAsync($"Create tag {name.Trim()}", () => Repository.TagAsync(name.Trim(), r.Commit.Hash));
    }

    [RelayCommand]
    async Task CheckoutAsync(GitHistoryRow? row)
    {
        if ((row ?? Selected) is not { } r || !await ConfirmAsync("Checkout",
                $"Check out {r.ShortHash} by itself (a detached HEAD)? New commits there belong to no branch until one is made."))
            return;
        await _vm.HistoryOperationAsync($"Checkout {r.ShortHash}", () => Repository.CheckoutDetachedAsync(r.Commit.Hash), workingTree: true);
    }

    [RelayCommand]
    async Task CherryPickAsync(GitHistoryRow? row)
    {
        if ((row ?? Selected) is not { } r || !await ConfirmAsync("Cherry-Pick", $"Apply {r.ShortHash} ({r.Subject}) on the current branch as a new commit?"))
            return;
        await _vm.HistoryOperationAsync($"Cherry-pick {r.ShortHash}", () => Repository.CherryPickAsync(r.Commit.Hash), workingTree: true);
    }

    [RelayCommand]
    async Task RevertAsync(GitHistoryRow? row)
    {
        if ((row ?? Selected) is not { } r || !await ConfirmAsync("Revert", $"Undo {r.ShortHash} ({r.Subject}) with a new commit on the current branch?"))
            return;
        await _vm.HistoryOperationAsync($"Revert {r.ShortHash}", () => Repository.RevertAsync(r.Commit), workingTree: true);
    }

    /// <summary>An interactive rebase of the branch's commits after this one (it must be one of HEAD's).</summary>
    [RelayCommand]
    async Task RebaseFromAsync(GitHistoryRow? row)
    {
        if ((row ?? Selected) is not { } r)
            return;
        if (!await Repository.IsAncestorOfHeadAsync(r.Commit.Hash))
        {
            _vm.SetStatus($"{r.ShortHash} is not one of the checked-out branch's commits: rebase from one of them.");
            return;
        }
        await _vm.OpenRebaseAsync(Repository, r.Commit);
    }

    /// <summary>The current branch reset to the commit: soft, mixed or hard (which loses the changes after it).</summary>
    [RelayCommand]
    async Task ResetAsync(string? mode)
    {
        if (Selected is not { } r || mode is not ("soft" or "mixed" or "hard"))
            return;
        var what = mode switch
        {
            "soft" => "the commits after it stay as staged changes",
            "mixed" => "the commits after it stay as changes, unstaged",
            _ => "the commits after it and every uncommitted change are lost",
        };
        if (!await ConfirmAsync($"Reset ({mode})", $"Reset the current branch to {r.ShortHash} ({r.Subject})? {char.ToUpper(what[0])}{what[1..]}."))
            return;
        await _vm.HistoryOperationAsync($"Reset --{mode} {r.ShortHash}", () => Repository.ResetAsync(r.Commit.Hash, mode), workingTree: true);
    }

    async Task<bool> ConfirmAsync(string title, string message) => _vm.Dialogs is { } dialogs && await dialogs.ConfirmAsync(title, message) == true;
}
