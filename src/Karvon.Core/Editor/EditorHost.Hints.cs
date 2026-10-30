using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Karvon.Lsp;

namespace Karvon.Editor;

/// <summary>
/// What the language server says beside the code, as Visual Studio shows it: inlay hints (a parameter's name before its
/// argument, a type after a var) in the line, and CodeLens (a member's "3 references") on a row of its own above the
/// member's line, as indented as it is, which finds them. Both follow edits (their places are anchors) and are read again a
/// moment after.
/// </summary>
public sealed partial class EditorHost
{
    sealed record PlacedHint(TextAnchor Anchor, LspInlayHint Hint);

    sealed record PlacedLens(TextAnchor Anchor, LspCodeLens Lens);

    IReadOnlyList<PlacedHint> _hints = [];
    IReadOnlyList<PlacedLens> _lenses = [];
    DispatcherTimer? _hintsTimer;
    CancellationTokenSource? _hintsCts;
    bool _showInlayHints = true;
    bool _showCodeLens = true;

    /// <summary>Inlay hints in the code (View > Inlay Hints).</summary>
    public bool ShowInlayHints
    {
        get => _showInlayHints;
        set
        {
            if (_showInlayHints == value)
                return;
            _showInlayHints = value;
            ScheduleHints(0);
            RedrawHints();
        }
    }

    /// <summary>CodeLens above the members' lines (View > CodeLens).</summary>
    public bool ShowCodeLens
    {
        get => _showCodeLens;
        set
        {
            if (_showCodeLens == value)
                return;
            _showCodeLens = value;
            ScheduleHints(0);
            RedrawHints();
        }
    }

    /// <summary>The hints read last, each at its place now.</summary>
    public IReadOnlyList<(int Offset, string Label)> InlayHints => [.. _hints.Where(h => !h.Anchor.IsDeleted).Select(h => (h.Anchor.Offset, h.Hint.Label))];

    /// <summary>The code lenses read last, each on its line now.</summary>
    public IReadOnlyList<(int Line, string Title)> CodeLenses => [.. _lenses.Where(l => !l.Anchor.IsDeleted && l.Lens.Title is not null).Select(l => (l.Anchor.Line, l.Lens.Title!))];

    void InstallHints(TextEditor view)
    {
        view.TextArea.TextView.ElementGenerators.Add(new InlayHintGenerator(this));
        view.TextArea.TextView.ElementGenerators.Add(new CodeLensGenerator(this));
        // The line numbers beside the code, not beside a code lens's row above it.
        var margins = view.TextArea.LeftMargins;
        if (margins.OfType<AvaloniaEdit.Editing.LineNumberMargin>().FirstOrDefault() is { } numbers)
        {
            var lensAware = new LensLineNumberMargin { Margin = numbers.Margin };
            lensAware.SetValue(TextBlock.ForegroundProperty, numbers.GetValue(TextBlock.ForegroundProperty));
            margins[margins.IndexOf(numbers)] = lensAware;
        }
        view.TextArea.AddHandler(InputElement.KeyDownEvent, OnLensKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>
    /// Up and Down go over a code lens's row, as in Visual Studio: from a member's code to the line above, and from the
    /// line above to the code. The editor's own would stop on the row, and put the caret back where it was.
    /// </summary>
    void OnLensKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Up or Key.Down) || (e.KeyModifiers & ~KeyModifiers.Shift) != 0 || sender is not AvaloniaEdit.Editing.TextArea area
            || !_showCodeLens || _lenses.Count == 0 || HasExtraCarets)
            return;
        var view = area.TextView;
        var caret = area.Caret;
        var line = view.GetOrConstructVisualLine(Document.GetLineByNumber(caret.Line));
        var row = line.GetTextLine(caret.VisualColumn, caret.Position.IsAtEndOfLine);
        var index = line.TextLines.IndexOf(row);
        VisualLine target;
        TextLine targetRow;
        if (e.Key == Key.Up && HasLensRow(line) && index == 1)
        {
            e.Handled = true;
            if (line.FirstDocumentLine.PreviousLine is not { } above)
                return;
            target = view.GetOrConstructVisualLine(above);
            targetRow = target.TextLines[^1];
        }
        else if (e.Key == Key.Down && index == line.TextLines.Count - 1 && line.LastDocumentLine.NextLine is { } next
            && view.GetOrConstructVisualLine(next) is { } below && HasLensRow(below))
        {
            e.Handled = true;
            target = below;
            targetRow = below.TextLines[1];
        }
        else
            return;
        var x = line.GetTextLineVisualXPosition(row, caret.VisualColumn);
        var column = target.GetVisualColumn(new Point(x, target.GetTextLineVisualYPosition(targetRow, VisualYPosition.LineMiddle)), area.Selection.EnableVirtualSpace);
        var position = new TextViewPosition(Document.GetLocation(target.FirstDocumentLine.Offset + target.GetRelativeOffset(column)), column);
        var from = caret.Position;
        caret.Position = position;
        if ((e.KeyModifiers & KeyModifiers.Shift) != 0)
            area.Selection = area.Selection.StartSelectionOrSetEndpoint(from, position);
        else
            area.ClearSelection();
        caret.BringCaretToView();
    }

    void ScheduleHints(int delay)
    {
        if (_lsp is null)
            return;
        _hintsTimer ??= new DispatcherTimer(DispatcherPriority.Background);
        _hintsTimer.Stop();
        _hintsTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, delay));
        _hintsTimer.Tick -= OnHintsTick;
        _hintsTimer.Tick += OnHintsTick;
        _hintsTimer.Start();
    }

    void OnHintsTick(object? sender, EventArgs e)
    {
        _hintsTimer!.Stop();
        _ = ReadHintsAsync();
    }

    async Task ReadHintsAsync()
    {
        if (_lsp is not { } lsp)
            return;
        _hintsCts?.Cancel();
        var cts = _hintsCts = new CancellationTokenSource();
        FlushChanges();
        try
        {
            var hints = _showInlayHints && lsp.ProvidesInlayHints
                ? await lsp.InlayHintsAsync(FilePath, new LspRange(new LspPosition(0, 0), new LspPosition(Document.LineCount, 0)), cts.Token)
                : [];
            var lenses = _showCodeLens && lsp.ProvidesCodeLens ? await lsp.CodeLensesAsync(FilePath, cts.Token) : [];
            if (cts.IsCancellationRequested)
                return;
            _hints = [.. hints.Select(h => new PlacedHint(Anchor(TextPositions.ToOffset(Document, h.Position)), h))];
            _lenses = [.. lenses.Where(l => l.Title is { Length: > 0 }).Select(l => new PlacedLens(Anchor(TextPositions.ToOffset(Document, l.Range.Start)), l))];
            RedrawHints();
        }
        catch (Exception ex) when (ex is LspException or IOException or OperationCanceledException)
        {
        }
    }

    TextAnchor Anchor(int offset)
    {
        var anchor = Document.CreateAnchor(Math.Clamp(offset, 0, Document.TextLength));
        anchor.MovementType = AnchorMovementType.BeforeInsertion;
        return anchor;
    }

    void RedrawHints()
    {
        foreach (var view in Views)
            view.TextArea.TextView.Redraw();
    }

    static readonly IBrush HintForeground = new SolidColorBrush(Color.Parse("#9D9D9D"));
    static readonly IBrush HintBackground = new SolidColorBrush(Color.Parse("#2E2E33"));
    static readonly IBrush LensForeground = new SolidColorBrush(Color.Parse("#8A8A8A"));

    /// <summary>
    /// Text in a line that is no text of the document's (it takes no offset, the caret goes around it), in a smaller font
    /// than the code's: an inlay hint, a code lens. Clicked, it does what it is for.
    /// </summary>
    sealed class HintElement(string text, IBrush foreground, IBrush? background, Action? click) : VisualLineElement(text.Length, 0)
    {
        public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context)
        {
            var properties = TextRunProperties;
            var run = new GenericTextRunProperties(properties.Typeface, properties.FontRenderingEmSize * 0.85, null, foreground, background);
            return new TextCharacters(text[(startVisualColumn - VisualColumn)..], run);
        }

        protected override void OnQueryCursor(PointerEventArgs e)
        {
            if (click is not null)
            {
                e.Handled = true;
                if (e.Source is InputElement element)
                    element.Cursor = new Cursor(StandardCursorType.Hand);
            }
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            if (click is null || !e.GetCurrentPoint(null).Properties.IsLeftButtonPressed)
                return;
            e.Handled = true;
            click();
        }
    }

    /// <summary>The hints at an offset of a line, as one element: no text of the document's, only room in the line.</summary>
    sealed class InlayHintGenerator(EditorHost host) : VisualLineElementGenerator
    {
        public override int GetFirstInterestedOffset(int startOffset)
        {
            if (!host._showInlayHints || host._hints.Count == 0)
                return -1;
            var end = CurrentContext.VisualLine.LastDocumentLine.EndOffset;
            var next = -1;
            foreach (var hint in host._hints)
                if (!hint.Anchor.IsDeleted && hint.Anchor.Offset >= startOffset && hint.Anchor.Offset <= end && (next < 0 || hint.Anchor.Offset < next))
                    next = hint.Anchor.Offset;
            return next;
        }

        public override VisualLineElement? ConstructElement(int offset)
        {
            var at = host._hints.Where(h => !h.Anchor.IsDeleted && h.Anchor.Offset == offset).ToList();
            if (at.Count == 0)
                return null;
            var label = string.Join(" ", at.Select(h => h.Hint.Label.Trim()));
            // Text of its own, smaller than the code's (so the line keeps its height), on a background, its space around.
            return new HintElement((at[0].Hint.PaddingLeft ? " " : "") + " " + label + " " + (at[^1].Hint.PaddingRight ? " " : ""), HintForeground, HintBackground, null);
        }
    }

    /// <summary>
    /// A member's code lens as a row of its own above its line, as Visual Studio has it: the line's indentation (the code's
    /// size, so the title starts over the member's first letter), the title in a smaller font, and the row's end. No text of
    /// the document's: the line's first character is where the code starts below it.
    /// </summary>
    sealed class LensRowElement(string indentation, string title, Action click) : VisualLineElement(indentation.Length + title.Length + 1, 0)
    {
        /// <summary>
        /// No caret stops on the row: the line's first one is where its code starts (going right into the line), and
        /// going left from there is the line above's end, not the row's start.
        /// </summary>
        public override int GetNextCaretPosition(int visualColumn, AvaloniaEdit.Document.LogicalDirection direction, CaretPositioningMode mode) =>
            direction == AvaloniaEdit.Document.LogicalDirection.Forward && visualColumn < VisualColumn + VisualLength ? VisualColumn + VisualLength : -1;

        public override bool HandlesLineBorders => true;

        public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context)
        {
            var at = startVisualColumn - VisualColumn;
            if (at < indentation.Length)
                return new TextCharacters(indentation[at..], TextRunProperties);
            if (at < indentation.Length + title.Length)
            {
                var properties = TextRunProperties;
                var small = new GenericTextRunProperties(properties.Typeface, properties.FontRenderingEmSize * 0.8, null, LensForeground, null);
                return new TextCharacters(title[(at - indentation.Length)..], small);
            }
            return new TextEndOfLine(1);
        }

        protected override void OnQueryCursor(PointerEventArgs e)
        {
            e.Handled = true;
            if (e.Source is InputElement element)
                element.Cursor = new Cursor(StandardCursorType.Hand);
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed)
                return;
            e.Handled = true;
            click();
        }
    }

    /// <summary>Whether <paramref name="visual"/> begins with a code lens's row (the margins mark its code's row, the one below).</summary>
    internal static bool HasLensRow(VisualLine visual) => visual.TextLines.Count > 1 && visual.Elements.Count > 0 && visual.Elements[0] is LensRowElement;

    /// <summary>The height of <paramref name="visual"/>'s code lens row; 0 for none.</summary>
    internal static double LensRowHeight(VisualLine visual) => HasLensRow(visual) ? visual.TextLines[0].Height : 0;

    /// <summary>The top of <paramref name="visual"/>'s code (below its code lens's row, if it has one), in the text view's document coordinates.</summary>
    internal static double CodeTop(VisualLine visual) => visual.VisualTop + LensRowHeight(visual);

    /// <summary>The height of <paramref name="visual"/>'s code, its rows all (a wrapped line's too), without its code lens's row.</summary>
    internal static double CodeHeight(VisualLine visual) => visual.Height - LensRowHeight(visual);

    /// <summary>The height of <paramref name="visual"/>'s first row of code.</summary>
    internal static double FirstRowHeight(VisualLine visual, double fallback) =>
        visual.TextLines.Count > (HasLensRow(visual) ? 1 : 0) ? visual.TextLines[HasLensRow(visual) ? 1 : 0].Height : fallback;

    /// <summary>
    /// The line numbers, each beside its line's code: a line with a code lens has its number on the row below the lens's,
    /// as Visual Studio shows it (the editor's own margin puts it on a line's first row).
    /// </summary>
    sealed class LensLineNumberMargin : AvaloniaEdit.Editing.LineNumberMargin
    {
        public override void Render(DrawingContext context)
        {
            if (TextView is not { VisualLinesValid: true } view)
                return;
            var foreground = GetValue(TextBlock.ForegroundProperty);
            foreach (var line in view.VisualLines)
            {
                var row = line.TextLines[HasLensRow(line) ? 1 : 0];
                var number = new FormattedText(line.FirstDocumentLine.LineNumber.ToString(System.Globalization.CultureInfo.CurrentCulture),
                    System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface, EmSize, foreground);
                var y = line.GetTextLineVisualYPosition(row, VisualYPosition.TextTop);
                context.DrawText(number, new Point(Bounds.Width - number.Width, y - view.VerticalOffset));
            }
        }
    }

    /// <summary>A member's code lens above its line: its title, which finds the references when clicked.</summary>
    sealed class CodeLensGenerator(EditorHost host) : VisualLineElementGenerator
    {
        public override int GetFirstInterestedOffset(int startOffset)
        {
            if (!host._showCodeLens || host._lenses.Count == 0)
                return -1;
            var line = CurrentContext.VisualLine.FirstDocumentLine;
            return host._lenses.Any(l => !l.Anchor.IsDeleted && l.Anchor.Line == line.LineNumber) && startOffset <= line.Offset ? line.Offset : -1;
        }

        public override VisualLineElement? ConstructElement(int offset)
        {
            var line = CurrentContext.Document.GetLineByOffset(offset);
            var lenses = host._lenses.Where(l => !l.Anchor.IsDeleted && l.Anchor.Line == line.LineNumber).ToList();
            if (lenses.Count == 0 || offset != line.Offset)
                return null;
            // The member's own indentation, its tabs as the editor shows them.
            var text = CurrentContext.Document.GetText(line);
            var indent = text[..(text.Length - text.TrimStart(' ', '\t').Length)].Replace("\t", new string(' ', CurrentContext.TextView.Options.IndentationSize));
            // Find All References at the member's name (where the lens's range starts).
            var at = TextPositions.ToOffset(host.Document, lenses[0].Lens.Range.Start);
            return new LensRowElement(indent, string.Join("  |  ", lenses.Select(l => l.Lens.Title)), () => _ = host.FindReferencesAsync(at));
        }
    }
}
