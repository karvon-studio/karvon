using System.Globalization;
using Avalonia;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Folding;
using AvaloniaEdit.Rendering;

namespace Karvon.Editor;

/// <summary>
/// Sticky scroll, as Visual Studio has it: the lines that open the scopes the top of the view is in (its namespace, class,
/// method) stay at the top while their insides scroll by; one clicked scrolls to it.
/// </summary>
public sealed partial class EditorHost
{
    const int MaxStickyLines = 5;
    bool _showStickyScroll = true;

    public bool ShowStickyScroll
    {
        get => _showStickyScroll;
        set
        {
            _showStickyScroll = value;
            foreach (var view in Views)
                view.TextArea.TextView.InvalidateLayer(KnownLayer.Caret);
        }
    }

    void InstallStickyScroll(TextEditor view)
    {
        view.TextArea.TextView.BackgroundRenderers.Add(new StickyScrollRenderer(this, view));
        view.TextArea.TextView.ScrollOffsetChanged += (_, _) => view.TextArea.TextView.InvalidateLayer(KnownLayer.Caret);
        view.TextArea.AddHandler(InputElement.PointerPressedEvent, (_, e) => OnStickyPressed(view, e), RoutingStrategies.Tunnel);
    }

    /// <summary>The lines that stay at the top of a pane now: the starts of the scopes its first line is in, outermost first.</summary>
    public IReadOnlyList<int> StickyLines(TextEditor view)
    {
        if (!_showStickyScroll || (view == _second ? _secondFolding : _folding) is not { } folding)
            return [];
        var text = view.TextArea.TextView;
        if (!text.VisualLinesValid && text.VisualLines.Count == 0)
            return [];
        // The scopes the line under the sticky lines is in; each sticky line hides one more, so look as deep as they go.
        var lines = new List<int>();
        for (var depth = 0; depth <= MaxStickyLines; depth++)
        {
            var top = text.GetDocumentLineByVisualTop(text.VerticalOffset + depth * text.DefaultLineHeight)?.LineNumber ?? 1;
            lines = [.. Scopes(folding, top)];
            if (lines.Count <= depth)
                break;
        }
        return lines.Count > MaxStickyLines ? lines[^MaxStickyLines..] : lines;
    }

    IEnumerable<int> Scopes(FoldingManager folding, int line) =>
        folding.AllFoldings
            .Select(f => (Start: Document.GetLineByOffset(f.StartOffset).LineNumber, End: Document.GetLineByOffset(Math.Min(f.EndOffset, Document.TextLength)).LineNumber))
            .Where(s => s.Start < line && s.End > line && s.End - s.Start > 1)
            .Select(s => s.Start).Distinct().Order();

    void OnStickyPressed(TextEditor view, PointerPressedEventArgs e)
    {
        var lines = StickyLines(view);
        if (lines.Count == 0)
            return;
        var text = view.TextArea.TextView;
        var y = e.GetPosition(text).Y;
        var row = (int)(y / text.DefaultLineHeight);
        if (y < 0 || row >= lines.Count)
            return;
        e.Handled = true;
        var line = lines[row];
        view.ScrollToVerticalOffset(text.GetVisualTopByDocumentLine(line));
        view.TextArea.Caret.Line = line;
        view.TextArea.Caret.Column = 1;
    }

    /// <summary>The sticky lines over the text, on a background of their own.</summary>
    sealed class StickyScrollRenderer(EditorHost host, TextEditor view) : IBackgroundRenderer
    {
        static readonly IBrush Background = new SolidColorBrush(Color.Parse("#252526"));
        static readonly IBrush Text = new SolidColorBrush(Color.Parse("#C8C8C8"));
        static readonly IPen Shadow = new Pen(new SolidColorBrush(Color.Parse("#000000"), 0.5), 2);

        public KnownLayer Layer => KnownLayer.Caret;

        public void Draw(TextView textView, DrawingContext dc)
        {
            var lines = host.StickyLines(view);
            if (lines.Count == 0)
                return;
            var height = textView.DefaultLineHeight;
            dc.FillRectangle(Background, new Rect(0, 0, textView.Bounds.Width, lines.Count * height));
            dc.DrawLine(Shadow, new Point(0, lines.Count * height + 1), new Point(textView.Bounds.Width, lines.Count * height + 1));
            var typeface = new Typeface(textView.GetValue(TextElement.FontFamilyProperty));
            var size = textView.GetValue(TextElement.FontSizeProperty);
            for (var i = 0; i < lines.Count; i++)
            {
                var line = host.Document.GetLineByNumber(lines[i]);
                var formatted = new FormattedText(host.Document.GetText(line).TrimEnd(), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, Text);
                dc.DrawText(formatted, new Point(-textView.HorizontalOffset, i * height + (height - formatted.Height) / 2));
            }
        }
    }
}
