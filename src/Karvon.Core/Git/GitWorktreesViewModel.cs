using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Karvon.ViewModels;

namespace Karvon.Git;

/// <summary>
/// A repository's worktrees (Git > Manage Worktrees): each one's folder and branch; a new one of a branch (or of a new
/// branch) beside the repository, opened in this window, removed, the gone ones pruned.
/// </summary>
public sealed partial class GitWorktreesViewModel(MainViewModel vm, GitRepository repository) : DocumentTab
{
    public GitRepository Repository => repository;

    public override string Key => "git-worktrees:" + repository.Root;

    public override string Title => $"Worktrees - {Path.GetFileName(repository.Root.TrimEnd('/', '\\'))}";

    public override string ToolTip => repository.Root;

    public ObservableCollection<GitWorktree> Worktrees { get; } = [];

    [ObservableProperty]
    public partial GitWorktree? Selected { get; set; }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        var keep = Selected?.Path;
        Worktrees.Clear();
        foreach (var worktree in await repository.WorktreesAsync())
            Worktrees.Add(worktree);
        Selected = Worktrees.FirstOrDefault(w => w.Path == keep);
    }

    /// <summary>A worktree of a branch there is, or of a new one; in a folder beside the repository's by default.</summary>
    [RelayCommand]
    async Task AddAsync()
    {
        if (vm.Dialogs is not { } dialogs
            || await dialogs.PromptAsync("Add Worktree", "Its branch (one there is, or a new one's name):", "") is not { Length: > 0 } branch)
            return;
        branch = branch.Trim();
        var root = repository.Root.TrimEnd('/', '\\');
        var suggested = Path.Combine(Path.GetDirectoryName(root) ?? root, $"{Path.GetFileName(root)}-{branch.Replace('/', '-')}");
        if (await dialogs.PromptAsync("Add Worktree", "Its folder:", suggested) is not { Length: > 0 } folder)
            return;
        var exists = await repository.HasRefAsync("refs/heads/" + branch);
        await vm.HistoryOperationAsync($"Add worktree {branch}", () => repository.AddWorktreeAsync(folder.Trim(), branch, newBranch: !exists));
        await RefreshAsync();
    }

    /// <summary>The worktree's folder opened in this window (the solution here closes).</summary>
    [RelayCommand]
    async Task OpenAsync(GitWorktree? worktree)
    {
        if ((worktree ?? Selected) is not { } w || vm.Dialogs is not { } dialogs
            || await dialogs.ConfirmAsync("Open Worktree", $"Open {w.Path} in this window? What is open here closes.") != true)
            return;
        await vm.OpenSolutionAsync(w.Path);
    }

    [RelayCommand]
    async Task RemoveAsync(GitWorktree? worktree)
    {
        if ((worktree ?? Selected) is not { IsMain: false } w || vm.Dialogs is not { } dialogs
            || await dialogs.ConfirmAsync("Remove Worktree", $"Remove the worktree {w.Path}? Its folder is deleted; its branch stays.") != true)
            return;
        if (!await vm.HistoryOperationAsync($"Remove worktree {Path.GetFileName(w.Path)}", () => repository.RemoveWorktreeAsync(w.Path, force: false))
            && await dialogs.ConfirmAsync("Remove Worktree", $"{w.Path} has changes not committed. Remove it with them?") == true)
            await vm.HistoryOperationAsync($"Remove worktree {Path.GetFileName(w.Path)}", () => repository.RemoveWorktreeAsync(w.Path, force: true));
        await RefreshAsync();
    }

    [RelayCommand]
    async Task PruneAsync()
    {
        await vm.HistoryOperationAsync("Prune worktrees", repository.PruneWorktreesAsync);
        await RefreshAsync();
    }
}
