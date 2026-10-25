using System.Globalization;
using Avalonia;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;
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

    // ---- The caret line's commit at its end (Git > Show Line Blame) --------------------------------------------

    IReadOnlyList<GitBlameCommit>? _lineBlame;
    bool _lineBlameStale = true;
    bool _showLineBlame;
    DispatcherTimer? _lineBlameTimer;
    int _lineBlameRead;

    /// <summary>The caret line's commit after its text, as GitLens shows it (who, when, why); read again after edits.</summary>
    public bool ShowLineBlame
    {
        get => _showLineBlame;
        set
        {
            if (_showLineBlame == value)
                return;
            _showLineBlame = value;
            foreach (var view in Views)
                AddLineBlame(view);
            ScheduleLineBlame(TimeSpan.FromMilliseconds(200));
            RedrawLineBlame();
        }
    }

    /// <summary>What the caret line's annotation says; null when there is none (not tracked, not read yet, being edited).</summary>
    public string? LineBlameText
    {
        get
        {
            if (!_showLineBlame || BlameProvider is null || !HasGitBaseline)
                return null;
            var lines = IsBlameShown ? _blame : _lineBlameStale ? null : _lineBlame;
            var line = Editor.TextArea.Caret.Line;
            if (lines is null || line < 1 || line > lines.Count)
                return null;
            var c = lines[line - 1];
            return c.IsUncommitted ? "Not committed yet" : $"{c.Author}, {Ago(c.Date)} · {c.Summary}";
        }
    }

    static string Ago(DateTimeOffset at)
    {
        var span = DateTimeOffset.Now - at;
        return span.TotalMinutes < 1 ? "just now" : span.TotalHours < 1 ? $"{(int)span.TotalMinutes} minutes ago"
            : span.TotalDays < 1 ? $"{(int)span.TotalHours} hours ago" : span.TotalDays < 2 ? "yesterday"
            : span.TotalDays < 60 ? $"{(int)span.TotalDays} days ago" : span.TotalDays < 730 ? $"{(int)(span.TotalDays / 30)} months ago" : $"{(int)(span.TotalDays / 365)} years ago";
    }

    void AddLineBlame(TextEditor view)
    {
        if (_showLineBlame && !view.TextArea.TextView.BackgroundRenderers.OfType<LineBlameRenderer>().Any())
            view.TextArea.TextView.BackgroundRenderers.Add(new LineBlameRenderer(this));
    }

    void ScheduleLineBlame(TimeSpan delay)
    {
        if (!_showLineBlame || IsBlameShown)
            return;
        _lineBlameTimer ??= new DispatcherTimer(DispatcherPriority.Background);
        _lineBlameTimer.Stop();
        _lineBlameTimer.Interval = delay;
        _lineBlameTimer.Tick -= OnLineBlameTick;
        _lineBlameTimer.Tick += OnLineBlameTick;
        _lineBlameTimer.Start();
    }

    void OnLineBlameTick(object? sender, EventArgs e)
    {
        _lineBlameTimer!.Stop();
        _ = ReadLineBlameAsync();
    }

    async Task ReadLineBlameAsync()
    {
        if (!_showLineBlame || BlameProvider is not { } provider || !HasGitBaseline)
            return;
        var read = ++_lineBlameRead;
        var blame = await provider(Document.Text);
        if (read != _lineBlameRead)
            return;
        _lineBlame = blame;
        _lineBlameStale = false;
        RedrawLineBlame();
    }

    void RedrawLineBlame()
    {
        foreach (var view in Views)
            view.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
    }

    /// <summary>The caret moved: the annotation follows it (read once, when it has not been).</summary>
    public void RefreshLineBlame()
    {
        if (!_showLineBlame)
            return;
        if (_lineBlame is null && !IsBlameShown)
            ScheduleLineBlame(TimeSpan.FromMilliseconds(300));
        RedrawLineBlame();
    }

    /// <summary>A commit or a checkout: the lines' commits read again.</summary>
    public void ReadLineBlameAgain()
    {
        if (_showLineBlame && _lineBlame is not null)
            ScheduleLineBlame(TimeSpan.FromMilliseconds(200));
    }

    /// <summary>An edit: the annotation goes until the blame of the edited text is read.</summary>
    void LineBlameEdited()
    {
        if (!_showLineBlame)
            return;
        _lineBlameStale = true;
        ScheduleLineBlame(TimeSpan.FromSeconds(1));
    }

    /// <summary>The caret line's annotation, after its text.</summary>
    sealed class LineBlameRenderer(EditorHost host) : IBackgroundRenderer
    {
        static readonly IBrush Brush = new SolidColorBrush(Color.Parse("#6A6A6A"));

        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView view, DrawingContext dc)
        {
            if (host.LineBlameText is not { } text || !view.VisualLinesValid || view.Document is not { } document)
                return;
            var line = host.Editor.TextArea.Caret.Line;
            if (line < 1 || line > document.LineCount || view.GetVisualLine(line) is not { } visual || visual.TextLines.Count == 0)
                return;
            var last = visual.TextLines[^1];
            var x = visual.GetTextLineVisualXPosition(last, visual.VisualLengthWithEndOfLineMarker) - view.HorizontalOffset + 32;
            var size = view.GetValue(TextElement.FontSizeProperty) * 0.92;
            var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(view.GetValue(TextElement.FontFamilyProperty), FontStyle.Italic), size, Brush);
            var y = visual.VisualTop - view.VerticalOffset + (last.Height - formatted.Height) / 2;
            dc.DrawText(formatted, new Point(x, y));
        }
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
