using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Karvon.Machine.Views;

/// <summary>
/// A folder measured, as rings around it (as the Disk Usage Analyzer draws it): its folders in the first ring, each as
/// much of it as its share of the folder's size, their folders in the next one within theirs. The one pointed at is told
/// in the middle; one clicked is gone into, the middle clicked goes up.
/// </summary>
public sealed class UsageRings : Control, Avalonia.Rendering.ICustomHitTest
{
    public static readonly StyledProperty<FolderSize?> RootProperty = AvaloniaProperty.Register<UsageRings, FolderSize?>(nameof(Root));

    /// <summary>Changed as the folder's folders are measured: it is drawn again.</summary>
    public static readonly StyledProperty<int> RevisionProperty = AvaloniaProperty.Register<UsageRings, int>(nameof(Revision));

    /// <summary>The rings drawn around the middle.</summary>
    public const int Rings = 4;

    /// <summary>A folder smaller than this (degrees) is not drawn: too thin to be seen or pointed at.</summary>
    const double Thinnest = 0.6;

    static readonly Color[] Colors = [.. new[] { "#4DAAFC", "#B180D7", "#4EC9B0", "#DCDCAA", "#CE9178", "#6A9955", "#C586C0", "#F14C4C" }.Select(Color.Parse)];

    static readonly IBrush Middle = new SolidColorBrush(Color.Parse("#252526"));

    static readonly IPen Gap = new Pen(new SolidColorBrush(Color.Parse("#1E1E1E")), 1);

    /// <summary>What was drawn last: each folder's ring and angles (where the pointer is found).</summary>
    readonly List<(FolderSize Folder, int Ring, double Start, double Sweep, Color Color)> _drawn = [];

    FolderSize? _over;

    static UsageRings() => AffectsRender<UsageRings>(RootProperty, RevisionProperty);

    public UsageRings()
    {
        Cursor = new Cursor(StandardCursorType.Hand);
        ClipToBounds = true;
    }

    public FolderSize? Root
    {
        get => GetValue(RootProperty);
        set => SetValue(RootProperty, value);
    }

    public int Revision
    {
        get => GetValue(RevisionProperty);
        set => SetValue(RevisionProperty, value);
    }

    /// <summary>The pointer is followed over all of it (between the rings too: nothing is pointed at there).</summary>
    public bool HitTest(Point point) => true;

    /// <summary>A ring's folder clicked (or the middle: the root).</summary>
    public event Action<FolderSize>? Chosen;

    /// <summary>The folder pointed at, when one is.</summary>
    public FolderSize? Over => _over;

    Point Center => new(Bounds.Width / 2, Bounds.Height / 2);

    /// <summary>The middle's radius and each ring's width.</summary>
    (double Inner, double Width) Radii()
    {
        var radius = Math.Min(Bounds.Width, Bounds.Height) / 2 - 2;
        var width = radius / (Rings + 1.6);
        return (width * 1.6, width);
    }

    /// <summary>The folder drawn at a point: the root in the middle, a ring's folder, none elsewhere.</summary>
    public FolderSize? FolderAt(Point point)
    {
        if (Root is not { } root)
            return null;
        var (inner, width) = Radii();
        var (dx, dy) = (point.X - Center.X, point.Y - Center.Y);
        var distance = Math.Sqrt(dx * dx + dy * dy);
        if (distance <= inner)
            return root;
        var ring = (int)((distance - inner) / width);
        LayOut();
        // Clockwise from the top, as it is drawn.
        var angle = (Math.Atan2(dx, -dy) * 180 / Math.PI + 360) % 360;
        return _drawn.FirstOrDefault(d => d.Ring == ring && angle >= d.Start && angle < d.Start + d.Sweep).Folder;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var over = FolderAt(e.GetPosition(this));
        if (over != _over)
        {
            _over = over;
            InvalidateVisual();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _over = null;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.InitialPressMouseButton == MouseButton.Left && FolderAt(e.GetPosition(this)) is { } folder)
        {
            _over = null;
            Chosen?.Invoke(folder);
        }
    }

    /// <summary>Each folder's ring and angles, as they are drawn.</summary>
    void LayOut()
    {
        _drawn.Clear();
        if (Root is { } root)
            Lay(root, 0, 0, 360, null);
    }

    public override void Render(DrawingContext context)
    {
        LayOut();
        if (Root is not { } root)
            return;
        var (inner, width) = Radii();
        var center = Center;
        foreach (var (folder, ring, start, sweep, color) in _drawn)
        {
            var shade = folder == _over ? color : Color.FromArgb((byte)(255 * (1 - ring * 0.16)), color.R, color.G, color.B);
            context.DrawGeometry(new SolidColorBrush(shade), Gap, Segment(center, inner + ring * width, inner + (ring + 1) * width, start, sweep));
        }
        context.DrawEllipse(Middle, null, center, inner - 1, inner - 1);

        // In the middle: the folder pointed at (its size, its share of the root), else the root's.
        var shown = _over ?? root;
        var name = new FormattedText(shown.Name, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, weight: FontWeight.SemiBold), 13, Brushes.White)
        { MaxTextWidth = inner * 1.7, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
        var share = shown == root || root.Size <= 0 ? "" : $" · {100.0 * shown.Size / root.Size:0.#}%";
        var size = new FormattedText(ResourceSampler.Bytes(shown.Size) + share, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default), 12, new SolidColorBrush(Color.Parse("#9D9D9D")));
        context.DrawText(name, new Point(center.X - name.Width / 2, center.Y - name.Height));
        context.DrawText(size, new Point(center.X - size.Width / 2, center.Y + 2));
    }

    /// <summary>A folder's folders within its angles in the next ring, and theirs in the one after (to the last ring).</summary>
    void Lay(FolderSize folder, int ring, double start, double sweep, Color? color)
    {
        if (ring >= Rings || folder.Size <= 0)
            return;
        var at = start;
        var index = 0;
        foreach (var child in folder.Folders)
        {
            var part = sweep * child.Size / folder.Size;
            if (part < Thinnest)
                break;
            var hue = color ?? Colors[index++ % Colors.Length];
            _drawn.Add((child, ring, at, part, hue));
            Lay(child, ring + 1, at, part, hue);
            at += part;
        }
    }

    /// <summary>A ring's piece: between two radii, from an angle (clockwise from the top) for a sweep.</summary>
    static StreamGeometry Segment(Point center, double inner, double outer, double start, double sweep)
    {
        sweep = Math.Min(sweep, 359.99);
        Point At(double radius, double angle)
        {
            var radians = angle * Math.PI / 180;
            return new Point(center.X + radius * Math.Sin(radians), center.Y - radius * Math.Cos(radians));
        }
        var large = sweep > 180;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(At(outer, start), true);
            g.ArcTo(At(outer, start + sweep), new Size(outer, outer), 0, large, SweepDirection.Clockwise);
            g.LineTo(At(inner, start + sweep));
            g.ArcTo(At(inner, start), new Size(inner, inner), 0, large, SweepDirection.CounterClockwise);
            g.EndFigure(true);
        }
        return geometry;
    }
}
