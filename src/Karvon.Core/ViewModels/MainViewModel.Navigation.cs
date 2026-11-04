using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Karvon.Lsp;

namespace Karvon.ViewModels;

/// <summary>
/// View > Navigate Backward and Forward (Ctrl+-, Ctrl+Shift+-, the mouse's back and forward buttons), as Visual Studio
/// goes: to where the caret was before a jump, another document or a go-to, through the files too.
/// </summary>
public partial class MainViewModel
{
    /// <summary>Where the caret was (1-based).</summary>
    public sealed record NavigationPoint(string FilePath, int Line, int Column);

    /// <summary>How far the caret moves in a document, at once, for the place it left to be one to go back to.</summary>
    const int NavigationJumpLines = 10;

    const int NavigationLimit = 100;

    readonly List<NavigationPoint> _backward = [];
    readonly List<NavigationPoint> _forward = [];
    NavigationPoint? _here;
    bool _navigating;

    internal IReadOnlyList<NavigationPoint> BackwardPoints => _backward;

    static NavigationPoint PointOf(DocumentViewModel doc) => new(doc.FilePath, doc.Host.CaretPosition.Line, doc.Host.CaretPosition.Column);

    /// <summary>The caret moved, or another document is active: a jump leaves a point behind.</summary>
    void TrackNavigation(DocumentViewModel doc)
    {
        if (_navigating || ActiveDocument != doc)
            return;
        var now = PointOf(doc);
        if (_here is { } here && (here.FilePath != now.FilePath || Math.Abs(here.Line - now.Line) >= NavigationJumpLines))
            Push(_backward, here, clearForward: true);
        _here = now;
    }

    void Push(List<NavigationPoint> points, NavigationPoint point, bool clearForward)
    {
        if (points.Count == 0 || points[^1] != point)
            points.Add(point);
        if (points.Count > NavigationLimit)
            points.RemoveAt(0);
        if (clearForward)
            _forward.Clear();
    }

    /// <summary>Before a go-to (definition, a result, an error): where the caret is now is a point to come back to.</summary>
    void RememberNavigation()
    {
        if (_navigating)
            return;
        if (ActiveEditor is { } doc)
            _here = PointOf(doc);
        if (_here is { } here)
            Push(_backward, here, clearForward: true);
    }

    [RelayCommand]
    internal void NavigateBackward() => Navigate(_backward, _forward);

    [RelayCommand]
    internal void NavigateForward() => Navigate(_forward, _backward);

    void Navigate(List<NavigationPoint> from, List<NavigationPoint> to)
    {
        if (_navigating)
            return;
        if (ActiveEditor is { } active)
            _here = PointOf(active);
        while (from.Count > 0)
        {
            var point = from[^1];
            from.RemoveAt(from.Count - 1);
            if (point == _here || !File.Exists(point.FilePath) && Editors.All(d => d.FilePath != point.FilePath))
                continue;
            if (_here is { } here)
                Push(to, here, clearForward: false);
            var at = new LspPosition(point.Line - 1, point.Column - 1);
            Show(point.FilePath, new LspRange(at, at));
            return;
        }
    }

    /// <summary>The document at the range, the caret there; the history left as it is.</summary>
    void Show(string path, LspRange range)
    {
        _navigating = true;
        if (OpenDocument(path) is not { } doc)
        {
            _navigating = false;
            return;
        }
        Dispatcher.UIThread.Post(() =>
        {
            var lines = doc.Host.Document.LineCount;
            var line = Math.Clamp(range.Start.Line, 0, lines - 1);
            var length = doc.Host.Document.GetLineByNumber(line + 1).Length;
            var start = new LspPosition(line, Math.Clamp(range.Start.Character, 0, length));
            doc.Host.NavigateTo(range.Start == range.End ? new LspRange(start, start) : range);
            _navigating = false;
            _here = PointOf(doc);
        }, DispatcherPriority.Loaded);
    }
}
