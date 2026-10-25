using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;
using Karvon.Editor;

namespace Karvon.Git;

/// <summary>
/// The merge editor (see <see cref="MergeEditorViewModel"/>): the incoming and current sides read-only, their conflicts
/// shaded; the result below, its conflicts red until resolved, green after. A conflict picked in the list comes into view.
/// </summary>
public partial class MergeEditorView : UserControl
{
    static readonly IBrush IncomingShade = new SolidColorBrush(Color.FromArgb(0x38, 0x4D, 0xAA, 0xFC));
    static readonly IBrush CurrentShade = new SolidColorBrush(Color.FromArgb(0x38, 0x57, 0xA6, 0x4A));
    static readonly IBrush Unresolved = new SolidColorBrush(Color.FromArgb(0x40, 0xF1, 0x4C, 0x4C));
    static readonly IBrush Resolved = new SolidColorBrush(Color.FromArgb(0x30, 0x57, 0xA6, 0x4A));

    MergeEditorViewModel? _vm;

    public MergeEditorView()
    {
        InitializeComponent();
        IncomingEditor.IsReadOnly = true;
        CurrentEditor.IsReadOnly = true;
        IncomingEditor.TextArea.TextView.BackgroundRenderers.Add(new LinesRenderer(() => _vm?.IncomingRanges ?? [], IncomingShade));
        CurrentEditor.TextArea.TextView.BackgroundRenderers.Add(new LinesRenderer(() => _vm?.CurrentRanges ?? [], CurrentShade));
        ResultEditor.TextArea.TextView.BackgroundRenderers.Add(new PlacesRenderer(() => _vm));
        ConflictList.SelectionChanged += (_, _) =>
        {
            if (ConflictList.SelectedItem is MergeConflictRow row && _vm is not null)
                Reveal(row);
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.Loaded -= ShowSides;
            _vm.RevealRequested -= RevealResult;
        }
        _vm = DataContext as MergeEditorViewModel;
        if (_vm is null)
            return;
        _vm.Loaded += ShowSides;
        _vm.RevealRequested += RevealResult;
        ResultEditor.Document = _vm.Result;
        foreach (var editor in new[] { IncomingEditor, CurrentEditor, ResultEditor })
            TextMateSupport.Install(editor, _vm.FilePath);
        ShowSides();
    }

    void ShowSides()
    {
        if (_vm is null)
            return;
        IncomingEditor.Text = _vm.Incoming;
        CurrentEditor.Text = _vm.Current;
        ResultEditor.TextArea.TextView.Redraw();
    }

    void Reveal(MergeConflictRow row)
    {
        if (_vm is null)
            return;
        if (row.Index < _vm.IncomingRanges.Count)
            IncomingEditor.ScrollTo(_vm.IncomingRanges[row.Index].Line, 0);
        if (row.Index < _vm.CurrentRanges.Count)
            CurrentEditor.ScrollTo(_vm.CurrentRanges[row.Index].Line, 0);
        RevealResult(row.Line);
    }

    void RevealResult(int line)
    {
        ResultEditor.TextArea.TextView.Redraw();
        if (line < 1 || line > ResultEditor.Document.LineCount)
            return;
        ResultEditor.ScrollTo(line, 0);
        ResultEditor.TextArea.Caret.Line = line;
    }

    /// <summary>Whole lines shaded: a side's conflicts.</summary>
    sealed class LinesRenderer(Func<IReadOnlyList<(int Line, int Count)>> ranges, IBrush brush) : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView view, DrawingContext dc)
        {
            if (!view.VisualLinesValid)
                return;
            var all = ranges();
            foreach (var visual in view.VisualLines)
            {
                var line = visual.FirstDocumentLine.LineNumber;
                if (all.Any(r => line >= r.Line && line < r.Line + r.Count))
                    dc.FillRectangle(brush, new Rect(0, visual.VisualTop - view.VerticalOffset, view.Bounds.Width, visual.Height));
            }
        }
    }

    /// <summary>The result's conflicts: red while their markers are there, green once resolved.</summary>
    sealed class PlacesRenderer(Func<MergeEditorViewModel?> vm) : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView view, DrawingContext dc)
        {
            if (vm() is not { } editor || !view.VisualLinesValid || view.Document is not { } document)
                return;
            foreach (var place in editor.Places)
            {
                var row = editor.Conflicts[place.Index];
                if (place.Length == 0)
                    continue;
                var first = document.GetLineByOffset(place.StartOffset).LineNumber;
                var last = document.GetLineByOffset(Math.Max(place.StartOffset, place.EndOffset - 1)).LineNumber;
                foreach (var visual in view.VisualLines)
                {
                    var line = visual.FirstDocumentLine.LineNumber;
                    if (line >= first && line <= last)
                        dc.FillRectangle(row.IsResolved ? Resolved : Unresolved, new Rect(0, visual.VisualTop - view.VerticalOffset, view.Bounds.Width, visual.Height));
                }
            }
        }
    }
}
