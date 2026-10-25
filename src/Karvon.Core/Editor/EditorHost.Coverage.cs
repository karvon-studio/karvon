using Avalonia;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using Karvon.Testing;

namespace Karvon.Editor;

/// <summary>Code coverage beside the text, as Visual Studio colors it: a line covered green, not covered red, partly covered amber.</summary>
public sealed partial class EditorHost
{
    FileCoverage? _coverage;

    public FileCoverage? Coverage => _coverage;

    /// <summary>The file's coverage shown (null: none), in each pane.</summary>
    public void SetCoverage(FileCoverage? coverage)
    {
        _coverage = coverage;
        foreach (var view in Views)
            AddCoverageMargin(view);
        foreach (var margin in Views.SelectMany(v => v.TextArea.LeftMargins.OfType<CoverageMargin>()))
            margin.InvalidateVisual();
    }

    void AddCoverageMargin(TextEditor view)
    {
        if (_coverage is null || view.TextArea.LeftMargins.OfType<CoverageMargin>().Any())
            return;
        // Beside the change bars, between the line numbers and the outlining.
        var margins = view.TextArea.LeftMargins;
        var git = margins.OfType<GitChangeMargin>().FirstOrDefault();
        margins.Insert(git is null ? margins.Count : margins.IndexOf(git) + 1, new CoverageMargin(() => _coverage));
    }

    sealed class CoverageMargin(Func<FileCoverage?> coverage) : AbstractMargin
    {
        static readonly IBrush Covered = new SolidColorBrush(Color.Parse("#57A64A"));
        static readonly IBrush NotCovered = new SolidColorBrush(Color.Parse("#E51400"));
        static readonly IBrush Partial = new SolidColorBrush(Color.Parse("#E2A355"));

        protected override Size MeasureOverride(Size availableSize) => new(5, 0);

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

        public override void Render(DrawingContext dc)
        {
            if (TextView is not { VisualLinesValid: true } view || coverage() is not { } file)
                return;
            foreach (var visual in view.VisualLines)
            {
                if (!file.Lines.TryGetValue(visual.FirstDocumentLine.LineNumber, out var line))
                    continue;
                dc.FillRectangle(!line.IsCovered ? NotCovered : line.Partial ? Partial : Covered,
                    new Rect(1, visual.VisualTop - view.VerticalOffset, 4, visual.Height));
            }
        }
    }
}
