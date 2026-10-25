using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Karvon.Git;

namespace Karvon.ViewModels;

/// <summary>
/// Merge conflicts: a conflicted file opens in the merge editor; Git Changes says when a merge (a cherry-pick, a revert,
/// a rebase) is under way, to abort it or go on.
/// </summary>
public partial class MainViewModel
{
    /// <summary>A conflicted file's merge editor: the one open, else a new one.</summary>
    public MergeEditorViewModel? OpenMergeEditor(string path)
    {
        if (RepositoryOf(path)?.Repository is not { } repository || !File.Exists(path))
            return null;
        if (Documents.OfType<MergeEditorViewModel>().FirstOrDefault(d => d.FilePath == path) is not { } editor)
        {
            editor = new MergeEditorViewModel(this, repository, path);
            Documents.Add(editor);
        }
        ActiveDocument = editor;
        return editor;
    }

    internal void CloseMergeEditor(MergeEditorViewModel editor)
    {
        Documents.Remove(editor);
        editor.Dispose();
        CheckExternalChanges();
        _ = RefreshGitAsync();
    }

    internal void AskClaudeToResolve(MergeEditorViewModel editor) => ClaudeService?.Ask(
        $"Resolve the merge conflicts in {editor.Repository.Relative(editor.FilePath)} ({editor.Remaining} left; {editor.CurrentLabel} against {editor.IncomingLabel}). "
        + "Read both sides of each conflict and what each branch meant by its change, then edit the file so it keeps both intents "
        + "and has no conflict markers left; build or test it if that tells you it is right. Then tell me, conflict by conflict, what "
        + "you kept. Do not stage or commit: I review it in Karvon's merge editor and accept it there.");

    /// <summary>The merge editors of files that changed on disk read them again (Claude resolved one).</summary>
    void FollowMergeEditors(IReadOnlyCollection<string> files)
    {
        foreach (var editor in Documents.OfType<MergeEditorViewModel>().Where(e => files.Contains(e.FilePath)).ToList())
            editor.ReloadIfChanged();
    }

    /// <summary>What git is in the middle of in the repository in scope (merging, rebasing...), said in Git Changes; null when nothing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGitOperationInProgress))]
    public partial GitOperation? GitInProgress { get; private set; }

    public bool IsGitOperationInProgress => GitInProgress is not null;

    public string GitInProgressText => GitInProgress switch
    {
        GitOperation.Merge => ConflictCount > 0 ? $"Merging: {Conflicted}. Resolve them, then commit." : "Merging: commit to finish it.",
        GitOperation.CherryPick => ConflictCount > 0 ? $"Cherry-picking: {Conflicted}. Resolve them, then continue." : "Cherry-picking: continue to finish it.",
        GitOperation.Revert => ConflictCount > 0 ? $"Reverting: {Conflicted}. Resolve them, then continue." : "Reverting: continue to finish it.",
        GitOperation.Rebase => ConflictCount > 0 ? $"Rebasing: {Conflicted}. Resolve them, then continue." : "Rebasing: continue to the next commit.",
        _ => "",
    };

    string Conflicted => ConflictCount == 1 ? "1 conflicted file" : $"{ConflictCount} conflicted files";

    /// <summary>A merge is finished by a commit; the others by --continue.</summary>
    public bool CanContinueGitOperation => GitInProgress is GitOperation.CherryPick or GitOperation.Revert or GitOperation.Rebase && ConflictCount == 0;

    int ConflictCount => UnstagedChanges.Count(c => c.Change.Kind == GitChangeKind.Conflicted);

    void ShowGitInProgress()
    {
        GitInProgress = (SelectedGitRepository?.Repository ?? _git)?.InProgress();
        OnPropertyChanged(nameof(GitInProgressText));
        OnPropertyChanged(nameof(CanContinueGitOperation));
    }

    [RelayCommand]
    async Task AbortGitOperation()
    {
        if ((SelectedGitRepository?.Repository ?? _git) is not { } repository || repository.InProgress() is not { } operation)
            return;
        if (Dialogs is not null && await Dialogs.ConfirmAsync("Abort", $"Abort the {operation.Verb()}? The branch goes back to where it was before it, and the conflicts' resolutions are lost.") != true)
            return;
        await HistoryOperationAsync($"Abort the {operation.Verb()}", () => repository.AbortAsync(operation), workingTree: true);
        foreach (var editor in Documents.OfType<MergeEditorViewModel>().ToList())
            CloseMergeEditor(editor);
    }

    [RelayCommand]
    async Task ContinueGitOperation()
    {
        if ((SelectedGitRepository?.Repository ?? _git) is not { } repository || repository.InProgress() is not { } operation)
            return;
        await HistoryOperationAsync($"Continue the {operation.Verb()}", () => repository.ContinueAsync(operation), workingTree: true);
    }
}
