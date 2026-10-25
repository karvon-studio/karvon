using CommunityToolkit.Mvvm.Input;
using Karvon.Git;

namespace Karvon.ViewModels;

/// <summary>
/// Git's history: the repository's (Git > View Branch History) and a file's (View History), as documents; and Blame
/// (Annotate), the commit of each line of an editor, which opens in the history.
/// </summary>
public partial class MainViewModel
{
    /// <summary>The repository's history (the one in scope in Git Changes): the open one, else a new one.</summary>
    [RelayCommand(CanExecute = nameof(HasGit))]
    void ShowHistory()
    {
        if ((SelectedGitRepository?.Repository ?? _git) is { } repository)
            OpenHistory(repository, null);
    }

    /// <summary>A file's history, through renames.</summary>
    public void ShowFileHistory(string path)
    {
        if (RepositoryOf(path)?.Repository is { } repository)
            OpenHistory(repository, path);
    }

    public GitHistoryViewModel OpenHistory(GitRepository repository, string? file)
    {
        var key = file is null ? $"git-history:{repository.Root}" : $"git-history:{file}";
        if (Documents.OfType<GitHistoryViewModel>().FirstOrDefault(d => d.Key == key) is not { } history)
        {
            history = new GitHistoryViewModel(this, repository, file);
            Documents.Add(history);
            _ = history.LoadAsync();
        }
        ActiveDocument = history;
        return history;
    }

    /// <summary>The histories open follow the refs (a commit, a fetch, a checkout).</summary>
    void FollowHistories()
    {
        foreach (var history in Documents.OfType<GitHistoryViewModel>().ToList())
            _ = history.RefsChangedAsync();
    }

    /// <summary>A commit shown and selected in its repository's history.</summary>
    public async Task RevealCommitAsync(string path, string hash)
    {
        if (RepositoryOf(path)?.Repository is not { } repository)
            return;
        if (!await OpenHistory(repository, null).RevealAsync(hash))
            SetStatus($"Commit {hash[..Math.Min(8, hash.Length)]} is not in the history.");
    }

    /// <summary>Git > Blame (Annotate) for the active editor: on, or off again.</summary>
    [RelayCommand]
    Task ToggleBlame() => ActiveEditor?.Host.ToggleBlameAsync() ?? Task.CompletedTask;

    /// <summary>Git > Show Line Blame: the caret line's commit after its text, in every editor (kept).</summary>
    public bool LineBlame
    {
        get => Settings.LineBlame;
        set
        {
            if (Settings.LineBlame == value)
                return;
            Settings.LineBlame = value;
            Settings.Save();
            foreach (var editor in Editors)
                editor.Host.ShowLineBlame = value;
            OnPropertyChanged();
        }
    }

    [RelayCommand]
    void ToggleLineBlame() => LineBlame = !LineBlame;

    void WireBlame(DocumentViewModel doc)
    {
        doc.Host.BlameProvider = text => RepositoryOf(doc.FilePath)?.Repository is { } repository
            ? repository.BlameAsync(doc.FilePath, text)
            : Task.FromResult<IReadOnlyList<GitBlameCommit>?>(null);
        doc.Host.BlameCommitRequested += commit => _ = RevealCommitAsync(doc.FilePath, commit.Hash);
        doc.Host.CaretMoved += () => doc.Host.RefreshLineBlame();
        doc.Host.ShowLineBlame = Settings.LineBlame;
    }

    /// <summary>A file a commit changed, against the commit's first parent (its first version against nothing).</summary>
    public async Task ShowCommitDiffAsync(GitRepository repository, GitCommit commit, GitCommitFile file)
    {
        var parent = commit.Parents.Count > 0 ? commit.Parents[0] : null;
        var before = parent is null || file.Letter == "A" ? "" : await repository.ShowAtAsync(parent, file.OldPath ?? file.Path) ?? "";
        var after = file.Letter == "D" ? "" : await repository.ShowAtAsync(commit.Hash, file.Path) ?? "";
        var name = Path.GetFileName(file.Path);
        ShowDiff(new DiffViewModel(Path.Combine(repository.Root, file.Path), before, after,
            $"{Path.GetFileName(file.OldPath ?? file.Path)};{(parent is null ? "(none)" : parent[..Math.Min(8, parent.Length)])}", $"{name};{commit.ShortHash}", "commit:" + commit.Hash));
    }

    /// <summary>A history's command (a branch made, a cherry-pick, a reset), in Output (Git); the files on disk followed when it changed them.</summary>
    internal async Task<bool> HistoryOperationAsync(string title, Func<Task<GitResult>> operation, bool workingTree = false)
    {
        SaveAll();
        var done = await GitOperationAsync(title, operation);
        if (workingTree)
            await AfterWorkingTreeChangedAsync();
        return done;
    }
}
