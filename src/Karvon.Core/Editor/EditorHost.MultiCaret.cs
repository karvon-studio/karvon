using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace Karvon.Editor;

/// <summary>
/// Visual Studio's multiple carets: Ctrl+Alt+click adds one, Shift+Alt+. adds a selection at the next match of the
/// selection (the word at the caret first), Shift+Alt+; at every match; then what is typed, Backspace, Delete, Enter and
/// Left, Right, Home and End act at each. Escape (or a click) leaves the main one only.
/// </summary>
public sealed partial class EditorHost
{
    /// <summary>A caret more than the editor's own: where it is, and where its selection starts (none when they are the same).</summary>
    sealed record ExtraCaret(TextAnchor Caret, TextAnchor Start);

    readonly List<ExtraCaret> _carets = [];

    /// <summary>Every caret's offset and its selection's start: the editor's own first.</summary>
    public IReadOnlyList<(int Caret, int Start)> Carets =>
        [(Editor.CaretOffset, Editor.SelectionLength > 0 ? (Editor.SelectionStart == Editor.CaretOffset ? Editor.SelectionStart + Editor.SelectionLength : Editor.SelectionStart) : Editor.CaretOffset),
            .. _carets.Select(c => (c.Caret.Offset, c.Start.Offset))];

    public bool HasExtraCarets => _carets.Count > 0;

    void InstallMultiCaret(TextEditor view) => view.TextArea.TextView.BackgroundRenderers.Add(new ExtraCaretRenderer(this));

    TextAnchor CaretAnchor(int offset)
    {
        var anchor = Document.CreateAnchor(Math.Clamp(offset, 0, Document.TextLength));
        anchor.MovementType = AnchorMovementType.AfterInsertion;
        anchor.SurviveDeletion = true;
        return anchor;
    }

    /// <summary>A caret more at an offset (a selection from start to it).</summary>
    public void AddCaret(int offset, int? start = null)
    {
        if (Carets.Any(c => c.Caret == offset))
            return;
        _carets.Add(new ExtraCaret(CaretAnchor(offset), CaretAnchor(start ?? offset)));
        RedrawCarets();
        StatusMessage?.Invoke($"{_carets.Count + 1} carets.");
    }

    public void ClearExtraCarets()
    {
        if (_carets.Count == 0)
            return;
        _carets.Clear();
        RedrawCarets();
    }

    void RedrawCarets()
    {
        foreach (var view in Views)
            view.TextArea.TextView.InvalidateLayer(KnownLayer.Caret);
    }

    /// <summary>The selection to look for: the editor's, else the word at the caret (selected first, as VS does).</summary>
    string? MatchText()
    {
        if (Editor.SelectionLength > 0)
            return Editor.SelectedText;
        var offset = Editor.CaretOffset;
        int start = offset, end = offset;
        while (start > 0 && IsIdentifierChar(Document.GetCharAt(start - 1)))
            start--;
        while (end < Document.TextLength && IsIdentifierChar(Document.GetCharAt(end)))
            end++;
        if (end == start)
            return null;
        Editor.Select(start, end - start);
        return Document.GetText(start, end - start);
    }

    /// <summary>Shift+Alt+.: a selection at the next match after the last caret (from the top again after the end).</summary>
    public void AddNextMatchCaret()
    {
        if (MatchText() is not { Length: > 0 } text)
            return;
        var text_ = Document.Text;
        var after = Carets.Max(c => Math.Max(c.Caret, c.Start));
        var at = text_.IndexOf(text, after, StringComparison.Ordinal);
        if (at < 0)
            at = text_.IndexOf(text, StringComparison.Ordinal);
        if (at < 0 || Carets.Any(c => Math.Min(c.Caret, c.Start) == at))
            return;
        AddCaret(at + text.Length, at);
    }

    /// <summary>Shift+Alt+;: a selection at every match of the selection.</summary>
    public void AddAllMatchCarets()
    {
        if (MatchText() is not { Length: > 0 } text)
            return;
        var all = Document.Text;
        for (var at = all.IndexOf(text, StringComparison.Ordinal); at >= 0; at = all.IndexOf(text, at + text.Length, StringComparison.Ordinal))
            if (!Carets.Any(c => Math.Min(c.Caret, c.Start) == at))
                AddCaret(at + text.Length, at);
    }

    /// <summary>
    /// Text put in at every caret (each selection replaced), from the last to the first, as one undo step; the editor's own
    /// caret after its text, the others after theirs (their anchors move with what is put in before them).
    /// </summary>
    void InsertAtCarets(string text) => EditAtCarets((start, end) => (start, end, text));

    /// <summary>Backspace (back) or Delete at every caret: a selection deleted, else the character before or after it.</summary>
    void DeleteAtCarets(bool back) => EditAtCarets((start, end) =>
        start != end ? (start, end, "") : back ? (Math.Max(0, start - 1), start, "") : (start, Math.Min(Document.TextLength, start + 1), ""));

    void EditAtCarets(Func<int, int, (int Start, int End, string Text)> edit)
    {
        // From the last to the first: an edit never moves the places of those not made yet. Each caret is put after its
        // own text; the edits before it, made after, move it on (the anchors and the editor's caret follow the text).
        var all = Carets.Select((c, i) => (Index: i, Start: Math.Min(c.Caret, c.Start), End: Math.Max(c.Caret, c.Start))).OrderByDescending(c => c.Start).ToList();
        Document.BeginUpdate();
        try
        {
            foreach (var c in all)
            {
                var (start, end, text) = edit(c.Start, c.End);
                Document.Replace(start, end - start, text);
                var after = start + text.Length;
                if (c.Index == 0)
                    Editor.Select(after, 0);
                else
                    _carets[c.Index - 1] = new ExtraCaret(CaretAnchor(after), CaretAnchor(after));
            }
        }
        finally
        {
            Document.EndUpdate();
        }
        MergeCarets();
        RedrawCarets();
    }

    /// <summary>Left, Right, Home or End at every caret (each selection given up).</summary>
    void MoveCarets(Key key)
    {
        int Move(int offset) => key switch
        {
            Key.Left => Math.Max(0, offset - 1),
            Key.Right => Math.Min(Document.TextLength, offset + 1),
            Key.Home => Document.GetLineByOffset(offset).Offset,
            _ => Document.GetLineByOffset(offset).EndOffset,
        };
        for (var i = 0; i < _carets.Count; i++)
        {
            var to = Move(_carets[i].Caret.Offset);
            _carets[i] = new ExtraCaret(CaretAnchor(to), CaretAnchor(to));
        }
        Editor.Select(Move(Editor.CaretOffset), 0);
        MergeCarets();
        RedrawCarets();
    }

    /// <summary>Carets that came to the same place are one (as Visual Studio merges them).</summary>
    void MergeCarets()
    {
        var seen = new HashSet<int> { Editor.CaretOffset };
        _carets.RemoveAll(c => !seen.Add(c.Caret.Offset));
    }

    /// <summary>The keys several carets take; true when handled.</summary>
    bool OnMultiCaretKey(KeyEventArgs e)
    {
        var shiftAlt = e.KeyModifiers == (KeyModifiers.Shift | KeyModifiers.Alt);
        if (shiftAlt && e.Key == Key.OemPeriod)
        {
            AddNextMatchCaret();
            return true;
        }
        if (shiftAlt && e.Key == Key.OemSemicolon)
        {
            AddAllMatchCarets();
            return true;
        }
        if (_carets.Count == 0)
            return false;
        switch (e.Key)
        {
            case Key.Escape:
                ClearExtraCarets();
                return true;
            case Key.Back when e.KeyModifiers == KeyModifiers.None:
                DeleteAtCarets(back: true);
                return true;
            case Key.Delete when e.KeyModifiers == KeyModifiers.None:
                DeleteAtCarets(back: false);
                return true;
            case Key.Enter when e.KeyModifiers == KeyModifiers.None:
                InsertAtCarets(Document.Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n");
                return true;
            case Key.Left or Key.Right or Key.Home or Key.End when e.KeyModifiers == KeyModifiers.None:
                MoveCarets(e.Key);
                return true;
            case Key.Up or Key.Down or Key.PageUp or Key.PageDown:
                ClearExtraCarets();
                return false;
        }
        return false;
    }

    /// <summary>Ctrl+Alt+click adds a caret; a click without leaves the main one only.</summary>
    void OnMultiCaretPointer(PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(Editor).Properties.IsLeftButtonPressed)
            return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt) && Karvon.Views.PlatformKeys.HasCommand(e.KeyModifiers))
        {
            if (Editor.GetPositionFromPoint(e.GetPosition(Editor)) is { } position)
            {
                AddCaret(Document.GetOffset(position.Location));
                e.Handled = true;
            }
            return;
        }
        ClearExtraCarets();
    }

    /// <summary>The other carets, drawn as the editor's own is, their selections as its selection.</summary>
    sealed class ExtraCaretRenderer(EditorHost host) : IBackgroundRenderer
    {
        static readonly IBrush CaretBrush = new SolidColorBrush(Color.Parse("#DCDCDC"));
        static readonly IBrush SelectionBrush = new SolidColorBrush(Color.Parse("#264F78"));

        public KnownLayer Layer => KnownLayer.Caret;

        public void Draw(TextView view, DrawingContext dc)
        {
            if (host._carets.Count == 0 || !view.VisualLinesValid)
                return;
            foreach (var c in host._carets)
            {
                var (start, end) = (Math.Min(c.Caret.Offset, c.Start.Offset), Math.Max(c.Caret.Offset, c.Start.Offset));
                if (end > start)
                    foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(view, new TextSegment { StartOffset = start, EndOffset = end }))
                        dc.FillRectangle(SelectionBrush, rect);
                var location = host.Document.GetLocation(c.Caret.Offset);
                var position = view.GetVisualPosition(new TextViewPosition(location), VisualYPosition.LineTop) - view.ScrollOffset;
                var bottom = view.GetVisualPosition(new TextViewPosition(location), VisualYPosition.LineBottom) - view.ScrollOffset;
                dc.FillRectangle(CaretBrush, new Rect(position.X, position.Y, 1.5, bottom.Y - position.Y));
            }
        }
    }
}
