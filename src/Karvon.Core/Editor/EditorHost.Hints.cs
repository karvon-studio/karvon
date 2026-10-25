using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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
/// argument, a type after a var) in the line, and CodeLens (a member's "3 references") after its line, which finds them.
/// Both follow edits (their places are anchors) and are read again a moment after.
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

    /// <summary>CodeLens after the members' lines (View > CodeLens).</summary>
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

    /// <summary>A member's code lens after its line: its title, which finds the references when clicked.</summary>
    sealed class CodeLensGenerator(EditorHost host) : VisualLineElementGenerator
    {
        public override int GetFirstInterestedOffset(int startOffset)
        {
            if (!host._showCodeLens || host._lenses.Count == 0)
                return -1;
            var line = CurrentContext.VisualLine.LastDocumentLine;
            return host._lenses.Any(l => !l.Anchor.IsDeleted && l.Anchor.Line == line.LineNumber) && startOffset <= line.EndOffset ? line.EndOffset : -1;
        }

        public override VisualLineElement? ConstructElement(int offset)
        {
            var line = CurrentContext.Document.GetLineByOffset(offset);
            var lenses = host._lenses.Where(l => !l.Anchor.IsDeleted && l.Anchor.Line == line.LineNumber).ToList();
            if (lenses.Count == 0 || offset != line.EndOffset)
                return null;
            // Find All References at the member's name (where the lens's range starts).
            var at = TextPositions.ToOffset(host.Document, lenses[0].Lens.Range.Start);
            return new HintElement("     " + string.Join("  |  ", lenses.Select(l => l.Lens.Title)), LensForeground, null, () => _ = host.FindReferencesAsync(at));
        }
    }
}
