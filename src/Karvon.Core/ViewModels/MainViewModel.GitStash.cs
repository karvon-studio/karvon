using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Karvon.Git;

namespace Karvon.ViewModels;

/// <summary>A stash in Git Changes: what it says and when; opened, its files.</summary>
public sealed partial class GitStashItem(GitRepository repository, GitStash stash) : ObservableObject
{
    public GitRepository Repository => repository;

    public GitStash Stash => stash;

    public string Title => stash.Message;

    public string Detail => $"{stash.Ref} · {Ago(stash.Date)}";

    public ObservableCollection<GitCommitFile> Files { get; } = [];

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    static string Ago(DateTimeOffset at)
    {
        var span = DateTimeOffset.Now - at;
        return span.TotalMinutes < 1 ? "just now" : span.TotalHours < 1 ? $"{(int)span.TotalMinutes} min ago"
            : span.TotalDays < 1 ? $"{(int)span.TotalHours} h ago" : span.TotalDays < 2 ? "yesterday" : $"{(int)span.TotalDays} days ago";
    }
}

/// <summary>
/// Git Changes' stashes, as Visual Studio's: Stash All (untracked files too), Stash All and Keep Staged, Stash Staged
/// (the message box's text as the stash's message); each stash applied, popped or dropped, its files each a diff
/// against the commit it was made on.
/// </summary>
public partial class MainViewModel
{
    public ObservableCollection<GitStashItem> Stashes { get; } = [];

    public bool HasStashes => Stashes.Count > 0;

    string? _stashesRead;

    GitRepository? StashRepository => SelectedGitRepository?.Repository ?? _git;

    /// <summary>The repository in scope's stashes read again (when they changed).</summary>
    async Task LoadStashesAsync()
    {
        if (StashRepository is not { } repository)
        {
            Stashes.Clear();
            OnPropertyChanged(nameof(HasStashes));
            return;
        }
        var stashes = await repository.StashesAsync();
        var read = repository.Root + "\n" + string.Join('\n', stashes.Select(s => s.Hash));
        if (read == _stashesRead)
            return;
        _stashesRead = read;
        Stashes.Clear();
        foreach (var stash in stashes)
            Stashes.Add(new GitStashItem(repository, stash));
        OnPropertyChanged(nameof(HasStashes));
    }

    Task StashAsync(GitStashMode mode, string title)
    {
        if (StashRepository is not { } repository)
            return Task.CompletedTask;
        SaveAll();
        var message = CommitMessage.Trim() is { Length: > 0 } m ? m : null;
        return HistoryOperationAsync(title, async () =>
        {
            var result = await repository.StashAsync(message, mode);
            if (result.Success && message is not null)
                CommitMessage = "";
            return result;
        }, workingTree: true);
    }

    [RelayCommand]
    Task StashAll() => StashAsync(GitStashMode.All, "Stash all");

    [RelayCommand]
    Task StashKeepStaged() => StashAsync(GitStashMode.KeepStaged, "Stash all and keep staged");

    [RelayCommand]
    Task StashStaged() => StashAsync(GitStashMode.Staged, "Stash staged");

    [RelayCommand]
    Task ApplyStash(GitStashItem? item) => item is null ? Task.CompletedTask
        : HistoryOperationAsync($"Apply {item.Stash.Ref}", () => item.Repository.ApplyStashAsync(item.Stash.Ref, pop: false), workingTree: true);

    [RelayCommand]
    Task PopStash(GitStashItem? item) => item is null ? Task.CompletedTask
        : HistoryOperationAsync($"Pop {item.Stash.Ref}", () => item.Repository.ApplyStashAsync(item.Stash.Ref, pop: true), workingTree: true);

    [RelayCommand]
    async Task DropStash(GitStashItem? item)
    {
        if (item is null || Dialogs is not null && await Dialogs.ConfirmAsync("Drop Stash", $"Drop {item.Stash.Ref} ({item.Title})? Its changes are gone.") != true)
            return;
        await HistoryOperationAsync($"Drop {item.Stash.Ref}", () => item.Repository.DropStashAsync(item.Stash.Ref));
    }

    /// <summary>A stash opened (its files read) or closed.</summary>
    [RelayCommand]
    async Task ToggleStash(GitStashItem? item)
    {
        if (item is null)
            return;
        item.IsExpanded = !item.IsExpanded;
        if (!item.IsExpanded || item.Files.Count > 0)
            return;
        if (await item.Repository.CommitDetailsAsync(item.Stash.Commit) is { } details)
            foreach (var file in details.Files)
                item.Files.Add(file);
    }

    /// <summary>Git > Manage Worktrees: the repository in scope's worktrees, as a document.</summary>
    [RelayCommand(CanExecute = nameof(HasGit))]
    async Task ManageWorktrees()
    {
        if (StashRepository is not { } repository)
            return;
        if (Documents.OfType<GitWorktreesViewModel>().FirstOrDefault(d => d.Repository.Root == repository.Root) is not { } worktrees)
        {
            worktrees = new GitWorktreesViewModel(this, repository);
            Documents.Add(worktrees);
        }
        ActiveDocument = worktrees;
        await worktrees.RefreshAsync();
    }

    /// <summary>An interactive rebase from a commit (onto it), as a document; the one open is replaced.</summary>
    public async Task<GitRebaseViewModel> OpenRebaseAsync(GitRepository repository, GitCommit? onto)
    {
        if (Documents.OfType<GitRebaseViewModel>().FirstOrDefault(d => d.Repository.Root == repository.Root) is { } open)
        {
            Documents.Remove(open);
            open.Dispose();
        }
        var rebase = new GitRebaseViewModel(this, repository, onto);
        Documents.Add(rebase);
        ActiveDocument = rebase;
        await rebase.LoadAsync();
        return rebase;
    }

    /// <summary>A stash's file opened (the view gives the stash and the file).</summary>
    public Task OpenStashFileAsync(GitStashItem item, GitCommitFile file) => ShowCommitDiffAsync(item.Repository, item.Stash.Commit, file);
}
