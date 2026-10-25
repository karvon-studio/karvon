using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit;

namespace Karvon.Editor;

/// <summary>
/// The minimap, as Visual Studio's scroll bar map mode: the whole file in small beside the text, where the view is in it
/// marked; a click or a drag scrolls there. In place of the vertical scroll bar.
/// </summary>
public sealed partial class EditorHost
{
    const double MinimapWidth = 90;
    readonly Dictionary<TextEditor, Minimap> _minimaps = [];
    bool _showMinimap;

    public bool ShowMinimap
    {
        get => _showMinimap;
        set
        {
            if (_showMinimap == value)
                return;
            _showMinimap = value;
            foreach (var view in Views)
                PlaceMinimap(view);
        }
    }

    /// <summary>A pane's minimap shown or not (beside it in its row of the panes' grid), its scroll bar given back when not.</summary>
    void PlaceMinimap(TextEditor view)
    {
        if (_showMinimap)
        {
            if (!_minimaps.TryGetValue(view, out var map))
            {
                map = _minimaps[view] = new Minimap(this, view);
                _viewGrid.Children.Add(map);
            }
            Grid.SetRow(map, Grid.GetRow(view));
            view.Margin = new Thickness(0, 0, MinimapWidth, 0);
            view.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        }
        else if (_minimaps.Remove(view, out var map))
        {
            _viewGrid.Children.Remove(map);
            view.Margin = default;
            view.VerticalScrollBarVisibility = ScrollBarVisibility.Visible;
        }
    }

    /// <summary>The panes moved (a split made or undone): their minimaps go with them.</summary>
    void PlaceMinimaps()
    {
        foreach (var gone in _minimaps.Keys.Where(v => !Views.Contains(v)).ToList())
        {
            _viewGrid.Children.Remove(_minimaps[gone]);
            _minimaps.Remove(gone);
        }
        foreach (var view in Views)
            PlaceMinimap(view);
    }

    sealed class Minimap : Control
    {
        static readonly IBrush Background = new SolidColorBrush(Color.Parse("#1E1E1E"));
        static readonly IBrush Ink = new SolidColorBrush(Color.Parse("#5A5A5A"));
        static readonly IBrush Viewport = new SolidColorBrush(Color.Parse("#FFFFFF"), 0.08);
        static readonly IPen Edge = new Pen(new SolidColorBrush(Color.Parse("#3F3F46")), 1);
        readonly EditorHost _host;
        readonly TextEditor _view;
        bool _dragging;

        public Minimap(EditorHost host, TextEditor view)
        {
            _host = host;
            _view = view;
            Width = MinimapWidth;
            HorizontalAlignment = HorizontalAlignment.Right;
            ClipToBounds = true;
            Cursor = new Cursor(StandardCursorType.Arrow);
            view.TextArea.TextView.ScrollOffsetChanged += (_, _) => InvalidateVisual();
            host.Document.Changed += (_, _) => InvalidateVisual();
        }

        /// <summary>How tall a line is in the map: two pixels, less when the file is taller than the map.</summary>
        double Scale => Math.Min(2, Bounds.Height / Math.Max(1, _host.Document.LineCount));

        public override void Render(DrawingContext dc)
        {
            dc.FillRectangle(Background, new Rect(Bounds.Size));
            dc.DrawLine(Edge, new Point(0.5, 0), new Point(0.5, Bounds.Height));
            var document = _host.Document;
            var scale = Scale;
            // One line in the map per pixel row at most: a tall file's lines are sampled.
            var step = Math.Max(1, (int)Math.Ceiling(1 / scale));
            for (var n = 1; n <= document.LineCount; n += step)
            {
                var line = document.GetLineByNumber(n);
                var text = document.GetText(line);
                var y = (n - 1) * scale;
                var x = 4.0;
                var run = 0;
                for (var i = 0; i <= text.Length && x < Bounds.Width; i++)
                {
                    var c = i < text.Length ? text[i] : ' ';
                    if (!char.IsWhiteSpace(c))
                    {
                        run++;
                        continue;
                    }
                    if (run > 0)
                        dc.FillRectangle(Ink, new Rect(x, y, run * 0.8, Math.Max(1, scale - 0.5)));
                    x += (run + (c == '\t' ? 4 : 1)) * 0.8;
                    run = 0;
                }
            }
            var view = _view.TextArea.TextView;
            if (view.DocumentHeight > 0)
            {
                var top = view.VerticalOffset / view.DocumentHeight * document.LineCount * scale;
                var height = Math.Max(8, view.Bounds.Height / view.DocumentHeight * document.LineCount * scale);
                dc.FillRectangle(Viewport, new Rect(0, top, Bounds.Width, height));
            }
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            _dragging = true;
            e.Pointer.Capture(this);
            ScrollTo(e.GetPosition(this).Y);
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (_dragging)
                ScrollTo(e.GetPosition(this).Y);
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            _dragging = false;
            e.Pointer.Capture(null);
        }

        /// <summary>The view scrolled so that the map's point is in its middle.</summary>
        void ScrollTo(double y)
        {
            var view = _view.TextArea.TextView;
            var line = Math.Clamp((int)(y / Scale) + 1, 1, _host.Document.LineCount);
            _view.ScrollToVerticalOffset(Math.Max(0, view.GetVisualTopByDocumentLine(line) - view.Bounds.Height / 2));
        }
    }
}
