using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;

namespace Karvon.Views;

/// <summary>
/// Icons in a panel put in another order by dragging them, as Ubuntu's dock and apps grid: the one dragged follows the
/// pointer (along the dock, anywhere in a grid), the others make room where it would go, and it is put there when let go.
/// The icons are the panel's own children, each the same size.
/// </summary>
public sealed class DragReorder(Panel panel, Orientation? axis, Func<bool> enabled, Action<int, int> moved)
{
    /// <summary>How far the pointer goes before a press is a drag, not a click.</summary>
    const double Threshold = 6;

    readonly List<Control> _items = [];
    Control? _pressed;
    Point _start;
    Rect[] _slots = [];
    int _from;
    int _to;
    bool _dragging;

    /// <summary>The press now let go was a drag: the icon's click is not one.</summary>
    public bool Dragged { get; private set; }

    /// <summary>The next icon, after those added before it.</summary>
    public void Add(Control item)
    {
        _items.Add(item);
        // Seen when the icon has handled them too (a button does): a press, a move, a release.
        item.AddHandler(InputElement.PointerPressedEvent, (_, e) => Pressed(item, e), RoutingStrategies.Bubble, handledEventsToo: true);
        item.AddHandler(InputElement.PointerMovedEvent, (_, e) => Moved(item, e), RoutingStrategies.Bubble, handledEventsToo: true);
        item.AddHandler(InputElement.PointerReleasedEvent, (_, e) => Released(item, e), RoutingStrategies.Bubble, handledEventsToo: true);
        item.PointerCaptureLost += (_, _) =>
        {
            if (_pressed == item)
                Cancel();
        };
    }

    void Pressed(Control item, PointerPressedEventArgs e)
    {
        Dragged = false;
        if (!e.GetCurrentPoint(item).Properties.IsLeftButtonPressed || !enabled())
            return;
        _pressed = item;
        _start = e.GetPosition(panel);
    }

    void Moved(Control item, PointerEventArgs e)
    {
        if (_pressed != item)
            return;
        var position = e.GetPosition(panel);
        var delta = new Vector(position.X - _start.X, position.Y - _start.Y);
        if (axis == Orientation.Vertical)
            delta = new Vector(0, delta.Y);
        else if (axis == Orientation.Horizontal)
            delta = new Vector(delta.X, 0);
        if (!_dragging)
        {
            if (Math.Max(Math.Abs(delta.X), Math.Abs(delta.Y)) < Threshold)
                return;
            Begin(item);
        }
        // Where it would go: the place nearest to it.
        var center = _slots[_from].Center + delta;
        _to = Enumerable.Range(0, _slots.Length).MinBy(i => Distance(_slots[i].Center, center));
        item.RenderTransform = Translate(delta);
        for (var i = 0; i < _items.Count; i++)
        {
            if (i == _from)
                continue;
            // The others' places once it is there.
            var at = i > _from && i <= _to ? i - 1 : i < _from && i >= _to ? i + 1 : i;
            _items[i].RenderTransform = Translate(new Vector(_slots[at].X - _slots[i].X, _slots[at].Y - _slots[i].Y));
        }
    }

    void Begin(Control item)
    {
        _dragging = true;
        Dragged = true;
        _slots = [.. _items.Select(i => i.Bounds)];
        _from = _to = _items.IndexOf(item);
        foreach (var other in _items)
            other.Transitions = other == item
                ? null
                : [new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(160), Easing = new CubicEaseOut() }];
        item.ZIndex = 1;
        item.Opacity = 0.9;
    }

    void Released(Control item, PointerReleasedEventArgs e)
    {
        if (_pressed != item)
            return;
        var (dragging, from, to) = (_dragging, _from, _to);
        _pressed = null;
        _dragging = false;
        if (!dragging)
            return;
        // The icon's click (handled before this) was not one; a key's next is.
        Dragged = false;
        if (from != to)
            moved(from, to);
        else
            Reset();
    }

    void Cancel()
    {
        var dragging = _dragging;
        _pressed = null;
        _dragging = false;
        Dragged = false;
        if (dragging)
            Reset();
    }

    void Reset()
    {
        foreach (var item in _items)
        {
            item.RenderTransform = null;
            item.ZIndex = 0;
            item.Opacity = 1;
        }
    }

    static ITransform Translate(Vector by)
    {
        var builder = TransformOperations.CreateBuilder(1);
        builder.AppendTranslate(by.X, by.Y);
        return builder.Build();
    }

    static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
