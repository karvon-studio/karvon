using Avalonia.Threading;
using AvaloniaEdit;
using Karvon.Git;

namespace Karvon.Editor;

/// <summary>Git > Blame (Annotate): the commit each line last changed in, in a margin, following edits and commits.</summary>
public sealed partial class EditorHost
{
    IReadOnlyList<GitBlameCommit>? _blame;
    DispatcherTimer? _blameTimer;
    int _blameRead;

    /// <summary>Reads the blame of the text given (the editor's, unsaved edits included); null when the file is not tracked.</summary>
    public Func<string, Task<IReadOnlyList<GitBlameCommit>?>>? BlameProvider { get; set; }

    public bool IsBlameShown { get; private set; }

    /// <summary>Each line's commit, as last read.</summary>
    public IReadOnlyList<GitBlameCommit>? Blame => _blame;

    /// <summary>A commit clicked in the margin: show it in the history.</summary>
    public event Action<GitBlameCommit>? BlameCommitRequested;

    public Task ToggleBlameAsync()
    {
        if (!IsBlameShown)
            return ShowBlameAsync();
        HideBlame();
        return Task.CompletedTask;
    }

    public async Task ShowBlameAsync()
    {
        if (BlameProvider is null)
        {
            StatusMessage?.Invoke("Blame: the file is not in a Git repository.");
            return;
        }
        IsBlameShown = true;
        foreach (var view in Views)
            AddBlameMargin(view);
        await RefreshBlameAsync();
    }

    public void HideBlame()
    {
        IsBlameShown = false;
        _blame = null;
        _blameTimer?.Stop();
        foreach (var view in Views)
            foreach (var margin in view.TextArea.LeftMargins.OfType<GitBlameMargin>().ToList())
                view.TextArea.LeftMargins.Remove(margin);
    }

    /// <summary>The blame read again (after an edit, a commit, a checkout); the last read wins.</summary>
    public async Task RefreshBlameAsync()
    {
        if (!IsBlameShown || BlameProvider is not { } provider)
            return;
        var read = ++_blameRead;
        var blame = await provider(Document.Text);
        if (read != _blameRead || !IsBlameShown)
            return;
        if (blame is null)
        {
            HideBlame();
            StatusMessage?.Invoke("Blame: the file is not tracked by Git.");
            return;
        }
        _blame = blame;
        foreach (var margin in Views.SelectMany(v => v.TextArea.LeftMargins.OfType<GitBlameMargin>()))
            margin.InvalidateVisual();
    }

    void AddBlameMargin(TextEditor view)
    {
        if (!IsBlameShown || view.TextArea.LeftMargins.OfType<GitBlameMargin>().Any())
            return;
        var margin = new GitBlameMargin(() => _blame);
        margin.CommitClicked += commit => BlameCommitRequested?.Invoke(commit);
        // Left of the line numbers, right of the breakpoints, as in VS.
        var margins = view.TextArea.LeftMargins;
        margins.Insert(margins.OfType<BreakpointMargin>().Any() ? 1 : 0, margin);
    }

    void ScheduleBlame()
    {
        if (!IsBlameShown)
            return;
        _blameTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, (_, _) =>
        {
            _blameTimer!.Stop();
            _ = RefreshBlameAsync();
        });
        _blameTimer.Stop();
        _blameTimer.Start();
    }
}
