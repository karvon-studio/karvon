using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using Karvon.Editor;
using Karvon.Http.ViewModels;

namespace Karvon.Http.Views;

/// <summary>A .http file's response pane (see <see cref="HttpPaneViewModel"/>), beside its editor, with ▶ in the editor's margin at each request.</summary>
public sealed class HttpPane : EditorPane
{
    readonly HttpPaneView _view;
    readonly Action? _disposed;

    public HttpPane(EditorHost host, Func<string?> root, string? environment, Action? disposed = null)
    {
        _disposed = disposed;
        Model = new HttpPaneViewModel(host, root, environment);
        _view = new HttpPaneView { DataContext = Model };
        host.AddMargin(() => new HttpRequestMargin(Model));
    }

    public HttpPaneViewModel Model { get; }

    public override Control View => _view;

    public override void Dispose() => _disposed?.Invoke();
}

/// <summary>A green ▶ at each request's line: a click sends it.</summary>
public sealed class HttpRequestMargin : AbstractMargin
{
    static readonly IBrush Send = new SolidColorBrush(Color.Parse("#89D185"));
    readonly HttpPaneViewModel _model;

    public HttpRequestMargin(HttpPaneViewModel model)
    {
        _model = model;
        model.RequestsChanged += InvalidateVisual;
        Cursor = new Cursor(StandardCursorType.Hand);
        ToolTip.SetTip(this, "Send Request");
    }

    protected override Size MeasureOverride(Size availableSize) => new(16, 0);

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
        if (TextView is not { VisualLinesValid: true } view)
            return;
        var lines = _model.Requests.Select(r => r.Line).ToHashSet();
        foreach (var visual in view.VisualLines)
        {
            if (!lines.Contains(visual.FirstDocumentLine.LineNumber))
                continue;
            var top = visual.VisualTop - view.VerticalOffset + (visual.Height - 10) / 2;
            var triangle = new StreamGeometry();
            using (var ctx = triangle.Open())
            {
                ctx.BeginFigure(new Point(4, top), true);
                ctx.LineTo(new Point(13, top + 5));
                ctx.LineTo(new Point(4, top + 10));
                ctx.EndFigure(true);
            }
            dc.DrawGeometry(Send, null, triangle);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (TextView is not { VisualLinesValid: true } view || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        var line = view.GetDocumentLineByVisualTop(e.GetPosition(this).Y + view.VerticalOffset);
        if (line is null || _model.Requests.All(r => r.Line != line.LineNumber))
            return;
        e.Handled = true;
        _ = _model.SendAtAsync(line.LineNumber);
    }
}
