using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace Karvon.Editor;

/// <summary>
/// Inline completions, as Visual Studio's IntelliCode and Copilot show them: a moment after typing stops, a module's
/// suggestion (an AI's) in gray after the caret, its further lines below it, pushing the code down. Tab puts it in,
/// Ctrl+Right its next word, Escape (or moving away) dismisses it, Alt+\ asks for one at once; typing what it says keeps
/// the rest of it.
/// </summary>
public sealed partial class EditorHost
{
    /// <summary>A suggestion shown at its place (an anchor after what is typed there), its text as the editor shows it.</summary>
    sealed record Suggestion(TextAnchor Anchor, string Text);

    /// <summary>The text before the caret a request is given, and after it.</summary>
    const int SuggestionPrefixLength = 8000, SuggestionSuffixLength = 2000;

    static readonly IBrush SuggestionForeground = new SolidColorBrush(Color.Parse("#7F7F7F"));

    Suggestion? _suggestion;

    /// <summary>The line the suggestion shows in (the last one it showed in, once it is gone): the only line drawn again for it.</summary>
    DocumentLine? _suggestionLine;

    DispatcherTimer? _suggestionTimer;
    CancellationTokenSource? _suggestionCts;
    /// <summary>The document's changes so far: a suggestion that comes after one is for text no longer there.</summary>
    int _suggestionEdits;

    /// <summary>Where suggestions come from (the first module's that gives them), or null for none.</summary>
    public Func<InlineCompletionRequest, CancellationToken, Task<string?>>? InlineCompletions { get; set; }

    /// <summary>How long typing stops before a suggestion is asked for.</summary>
    public static TimeSpan InlineCompletionDelay { get; set; } = TimeSpan.FromMilliseconds(350);

    /// <summary>The suggestion shown (its lines joined by \n), or null.</summary>
    public string? InlineSuggestion => _suggestion?.Text;

    void InitializeInlineCompletion() => Document.Changing += OnSuggestionDocumentChanging;

    void InstallInlineCompletion(TextEditor view)
    {
        // Before the other generators: at a line's end the suggestion is at the caret, a lens after it.
        view.TextArea.TextView.ElementGenerators.Insert(0, new SuggestionGenerator(this));
        // A suggestion's further lines start at the line's start: they bring their own indentation.
        view.Options.InheritWordWrapIndentation = false;
        // The caret moved away (a click, a key): the suggestion goes, and one on its way is no longer wanted. Typing moves
        // it too, and asks again once the text is in.
        view.TextArea.Caret.PositionChanged += (_, _) =>
        {
            if (view != _active)
                return;
            if (_suggestion is not { } shown)
            {
                _suggestionTimer?.Stop();
                _suggestionCts?.Cancel();
            }
            else if (Editor.CaretOffset != shown.Anchor.Offset)
                DismissInlineSuggestion();
        };
        view.TextArea.LostFocus += (_, _) => _suggestionTimer?.Stop();
    }

    /// <summary>Typing stopped (a character, a new line): a suggestion is asked for a moment after, when one fits here.</summary>
    void ScheduleInlineCompletion()
    {
        if (InlineCompletions is null || _suggestion is not null)
            return;
        _suggestionCts?.Cancel();
        _suggestionTimer ??= new DispatcherTimer(DispatcherPriority.Background);
        _suggestionTimer.Stop();
        _suggestionTimer.Interval = InlineCompletionDelay;
        _suggestionTimer.Tick -= OnSuggestionTick;
        _suggestionTimer.Tick += OnSuggestionTick;
        _suggestionTimer.Start();
    }

    void OnSuggestionTick(object? sender, EventArgs e)
    {
        _suggestionTimer!.Stop();
        _ = RequestInlineCompletionAsync();
    }

    /// <summary>
    /// A suggestion asked for at the caret now (Alt+\, and a moment after typing stops), shown when it comes while the
    /// caret and the text are as they were: whether one is shown.
    /// </summary>
    public async Task<bool> RequestInlineCompletionAsync()
    {
        _suggestionTimer?.Stop();
        _suggestionCts?.Cancel();
        if (InlineCompletions is not { } provider || Editor.IsReadOnly || _carets.Count > 0 || !Editor.TextArea.Selection.IsEmpty || _completionWindow is not null)
            return false;
        var caret = Editor.CaretOffset;
        var line = Document.GetLineByOffset(caret);
        var lineBefore = Document.GetText(line.Offset, caret - line.Offset);
        var lineAfter = Document.GetText(caret, line.EndOffset - caret);
        if (!InlineSuggestions.FitsBefore(lineAfter))
            return false;
        var start = Math.Max(0, caret - SuggestionPrefixLength);
        var end = Math.Min(Document.TextLength, caret + SuggestionSuffixLength);
        // Not half a character (a surrogate pair's), which no JSON can carry.
        if (start > 0 && char.IsLowSurrogate(Document.GetCharAt(start)))
            start++;
        if (end < Document.TextLength && end > caret && char.IsHighSurrogate(Document.GetCharAt(end - 1)))
            end--;
        var request = new InlineCompletionRequest(FilePath, Document.GetText(start, caret - start), Document.GetText(caret, end - caret));
        var edits = _suggestionEdits;
        var cts = _suggestionCts = new CancellationTokenSource();
        string? text;
        try
        {
            text = await provider(request, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        if (cts.IsCancellationRequested || edits != _suggestionEdits || Editor.CaretOffset != caret || _completionWindow is not null || _carets.Count > 0
            || text is null || InlineSuggestions.Fit(text, lineBefore, lineAfter) is not { } fitted)
            return false;
        ShowInlineSuggestion(caret, fitted);
        return true;
    }

    /// <summary>A suggestion in gray at an offset (the caret's), until it is taken or dismissed.</summary>
    void ShowInlineSuggestion(int offset, string text)
    {
        var anchor = Document.CreateAnchor(offset);
        // What is typed at its place goes before it.
        anchor.MovementType = AnchorMovementType.AfterInsertion;
        _suggestion = new Suggestion(anchor, text);
        _suggestionLine = Document.GetLineByOffset(offset);
        RedrawSuggestion();
    }

    public void DismissInlineSuggestion() => DismissInlineSuggestion(now: false);

    /// <summary>
    /// The suggestion gone; <paramref name="now"/> lays the lines out again at once, so a key that moves the caret next
    /// moves it in them as they are without it.
    /// </summary>
    void DismissInlineSuggestion(bool now)
    {
        _suggestionTimer?.Stop();
        _suggestionCts?.Cancel();
        if (_suggestion is null)
            return;
        _suggestion = null;
        RedrawSuggestion();
        if (now)
            foreach (var view in Views)
                view.TextArea.TextView.EnsureVisualLines();
    }

    /// <summary>Tab: the whole suggestion put in, the caret after it.</summary>
    public bool AcceptInlineSuggestion() => _suggestion is { } shown && Accept(shown.Text);

    /// <summary>Ctrl+Right: the suggestion's next word put in (the blank space before it too), the rest still shown.</summary>
    public bool AcceptInlineSuggestionWord()
    {
        if (_suggestion is not { } shown)
            return false;
        var text = shown.Text;
        var i = 0;
        while (i < text.Length && char.IsWhiteSpace(text[i]))
            i++;
        if (i < text.Length && IsIdentifierChar(text[i]))
            while (i < text.Length && IsIdentifierChar(text[i]))
                i++;
        else if (i < text.Length)
            i++;
        return Accept(text[..i]);
    }

    /// <summary>
    /// The start of the suggestion put in at its place as typing would put it, one undo step: the rest of it stays shown
    /// (<see cref="OnSuggestionDocumentChanging"/>), and none is left when it was all of it.
    /// </summary>
    bool Accept(string part)
    {
        if (_suggestion is not { } shown || part.Length == 0)
            return false;
        var offset = shown.Anchor.Offset;
        Document.Insert(offset, part.Replace("\n", TextUtilities.GetNewLineFromDocument(Document, Document.GetLineByOffset(offset).LineNumber)));
        Editor.CaretOffset = shown.Anchor.IsDeleted ? offset + part.Length : shown.Anchor.Offset;
        Editor.TextArea.Caret.BringCaretToView();
        return true;
    }

    /// <summary>
    /// What is typed at the suggestion's place that it says (its line breaks as the document has them) leaves the rest
    /// of it shown; any other change dismisses it.
    /// </summary>
    void OnSuggestionDocumentChanging(object? sender, DocumentChangeEventArgs e)
    {
        _suggestionEdits++;
        if (_suggestion is not { } shown)
            return;
        var typed = e.InsertedText.Text.Replace("\r\n", "\n");
        if (e.RemovalLength == 0 && e.Offset == shown.Anchor.Offset && typed.Length > 0 && typed.Length < shown.Text.Length
            && shown.Text.StartsWith(typed, StringComparison.Ordinal))
            _suggestion = shown with { Text = shown.Text[typed.Length..] };
        else
            _suggestion = null;
        Dispatcher.UIThread.Post(RedrawSuggestion, DispatcherPriority.Render);
    }

    /// <summary>The suggestion's keys: whether the key was one (and so handled).</summary>
    bool OnSuggestionKey(KeyEventArgs e)
    {
        var alt = e.KeyModifiers == KeyModifiers.Alt;
        if (alt && e.Key is Key.OemPipe or Key.OemBackslash)
        {
            DismissInlineSuggestion();
            _ = RequestInlineCompletionAsync();
            return true;
        }
        if (_suggestion is null)
            return false;
        switch (e.Key)
        {
            case Key.Tab when e.KeyModifiers == KeyModifiers.None && _completionWindow is null:
                return AcceptInlineSuggestion();
            case Key.Right when Karvon.Views.PlatformKeys.HasCommand(e.KeyModifiers) && !e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                return AcceptInlineSuggestionWord();
            case Key.Escape:
                DismissInlineSuggestion();
                return true;
            case Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown:
                DismissInlineSuggestion(now: true);
                return false;
        }
        return false;
    }

    void RedrawSuggestion()
    {
        foreach (var view in Views)
            if (_suggestionLine is { IsDeleted: false } line)
                view.TextArea.TextView.Redraw(line);
            else
                view.TextArea.TextView.Redraw();
    }

    /// <summary>
    /// The suggestion's text in the line, in gray: no text of the document's (the caret stays before it). Its line breaks
    /// break the line there, so its further lines show under it, the code after them moved down as Visual Studio shows them.
    /// </summary>
    sealed class SuggestionElement(string text) : VisualLineElement(text.Length, 0)
    {
        public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context)
        {
            var properties = TextRunProperties;
            var run = new GenericTextRunProperties(properties.Typeface, properties.FontRenderingEmSize, null, SuggestionForeground);
            return new TextCharacters(text[(startVisualColumn - VisualColumn)..], run);
        }

        /// <summary>Its place's offset is before it, wherever the caret comes from.</summary>
        public override int GetVisualColumn(int relativeTextOffset) => VisualColumn;

        /// <summary>No caret stops in it or after it.</summary>
        public override int GetNextCaretPosition(int visualColumn, AvaloniaEdit.Document.LogicalDirection direction, CaretPositioningMode mode) =>
            direction == AvaloniaEdit.Document.LogicalDirection.Backward && visualColumn > VisualColumn ? VisualColumn : -1;
    }

    sealed class SuggestionGenerator(EditorHost host) : VisualLineElementGenerator
    {
        public override int GetFirstInterestedOffset(int startOffset)
        {
            if (host._suggestion is not { Anchor.IsDeleted: false } shown)
                return -1;
            var offset = shown.Anchor.Offset;
            return offset >= startOffset && offset <= CurrentContext.VisualLine.LastDocumentLine.EndOffset ? offset : -1;
        }

        public override VisualLineElement? ConstructElement(int offset) =>
            host._suggestion is { } shown && shown.Anchor.Offset == offset ? new SuggestionElement(shown.Text) : null;
    }
}
