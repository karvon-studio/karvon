using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using Karvon.Git;

namespace Karvon.Editor;

/// <summary>
/// Visual Studio's Blame (Annotate) margin, left of the line numbers: each run of lines from one commit says when, who
/// and why at its first line, with a bar as warm as the commit is new; hovering tells the commit whole, clicking opens
/// it in the history.
/// </summary>
public sealed class GitBlameMargin(Func<IReadOnlyList<GitBlameCommit>?> blame) : AbstractMargin
{
    const double Characters = 44;
    static readonly IBrush Background = new SolidColorBrush(Color.Parse("#252526"));
    static readonly IBrush HoverBrush = new SolidColorBrush(Color.Parse("#2D2D30"));
    static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#9D9D9D"));
    static readonly IBrush HoverTextBrush = new SolidColorBrush(Color.Parse("#DCDCDC"));
    static readonly IBrush UncommittedBrush = new SolidColorBrush(Color.Parse("#1B81A8"));
    static readonly IPen Separator = new Pen(new SolidColorBrush(Color.Parse("#3F3F46")), 1);
    static readonly Color Old = Color.Parse("#3F3F46"), New = Color.Parse("#E2A355");

    GitBlameCommit? _hovered;

    /// <summary>A commit clicked (not a line yet to be committed).</summary>
    public event Action<GitBlameCommit>? CommitClicked;

    double FontSize => GetValue(TextElement.FontSizeProperty) * 0.9;

    Typeface Typeface => new(GetValue(TextElement.FontFamilyProperty));

    protected override Size MeasureOverride(Size availableSize) =>
        new(Math.Ceiling(Characters * Measure("0").Width + 16), 0);

    FormattedText Measure(string text, IBrush? brush = null) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface, FontSize, brush ?? TextBrush);

    protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
    {
        if (oldTextView is not null)
            oldTextView.VisualLinesChanged -= OnVisualLinesChanged;
        base.OnTextViewChanged(oldTextView, newTextView);
        if (newTextView is not null)
            newTextView.VisualLinesChanged += OnVisualLinesChanged;
        InvalidateVisual();
    }

    void OnVisualLinesChanged(object? sender, EventArgs e) => InvalidateVisual();

    /// <summary>The commit of a line (1-based), when the blame has it.</summary>
    GitBlameCommit? At(IReadOnlyList<GitBlameCommit> lines, int line) => line >= 1 && line <= lines.Count ? lines[line - 1] : null;

    public override void Render(DrawingContext dc)
    {
        dc.FillRectangle(Background, new Rect(Bounds.Size));
        var view = TextView;
        if (view is null || !view.VisualLinesValid || blame() is not { Count: > 0 } lines)
            return;
        var committed = lines.Where(c => !c.IsUncommitted).ToList();
        var (oldest, newest) = committed.Count == 0 ? (default, default) : (committed.Min(c => c.Date), committed.Max(c => c.Date));
        var first = true;
        foreach (var visual in view.VisualLines)
        {
            var line = visual.FirstDocumentLine.LineNumber;
            if (At(lines, line) is not { } commit)
                continue;
            var top = visual.VisualTop - view.VerticalOffset;
            var height = visual.Height;
            if (ReferenceEquals(commit, _hovered))
                dc.FillRectangle(HoverBrush, new Rect(0, top, Bounds.Width, height));
            dc.FillRectangle(commit.IsUncommitted ? UncommittedBrush : new SolidColorBrush(Heat(commit.Date, oldest, newest)), new Rect(0, top, 3, height));
            // A run's first line tells it (the first one shown too, when the run starts above).
            var starts = !ReferenceEquals(At(lines, line - 1), commit);
            if (starts && line > 1)
                dc.DrawLine(Separator, new Point(0, top + 0.5), new Point(Bounds.Width, top + 0.5));
            if (starts || first)
            {
                var text = Measure(Describe(commit), ReferenceEquals(commit, _hovered) ? HoverTextBrush : TextBrush);
                text.MaxTextWidth = Math.Max(1, Bounds.Width - 16);
                text.MaxLineCount = 1;
                text.Trimming = TextTrimming.CharacterEllipsis;
                dc.DrawText(text, new Point(9, top + (height - text.Height) / 2));
            }
            first = false;
        }
    }

    static string Describe(GitBlameCommit c) =>
        c.IsUncommitted ? "Not committed yet" : $"{c.Date:yyyy-MM-dd}  {c.Author}  {c.Summary}";

    /// <summary>The newest commit the warmest; the oldest grey.</summary>
    static Color Heat(DateTimeOffset date, DateTimeOffset oldest, DateTimeOffset newest)
    {
        var span = (newest - oldest).TotalSeconds;
        var t = span <= 0 ? 1 : (date - oldest).TotalSeconds / span;
        byte Mix(byte a, byte b) => (byte)(a + (b - a) * t);
        return Color.FromRgb(Mix(Old.R, New.R), Mix(Old.G, New.G), Mix(Old.B, New.B));
    }

    GitBlameCommit? CommitAt(Point point)
    {
        if (TextView is not { VisualLinesValid: true } view || blame() is not { } lines)
            return null;
        var line = view.GetDocumentLineByVisualTop(point.Y + view.VerticalOffset);
        return line is null ? null : At(lines, line.LineNumber);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var commit = CommitAt(e.GetPosition(this));
        if (ReferenceEquals(commit, _hovered))
            return;
        _hovered = commit;
        Cursor = commit is { IsUncommitted: false } ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
        ToolTip.SetTip(this, commit is null ? null : commit.IsUncommitted
            ? "Not committed yet"
            : $"{commit.Hash}\n{commit.Author} <{commit.Email}>, {commit.Date:yyyy-MM-dd HH:mm}\n\n{commit.Summary}\n\nClick to show it in the history.");
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hovered = null;
        ToolTip.SetTip(this, null);
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || CommitAt(e.GetPosition(this)) is not { IsUncommitted: false } commit)
            return;
        e.Handled = true;
        CommitClicked?.Invoke(commit);
    }
}
