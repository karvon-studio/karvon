using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Karvon.Git;

/// <summary>A row's part of the history's graph: its lines down and the commit's dot, in its lanes' colors.</summary>
public sealed class GitGraphCell : Control
{
    public const double LaneWidth = 14;

    static readonly Color[] Palette =
    [
        Color.Parse("#3794FF"), Color.Parse("#E8AB53"), Color.Parse("#3FB950"), Color.Parse("#D670D6"),
        Color.Parse("#F14C4C"), Color.Parse("#4EC9B0"), Color.Parse("#CE9178"), Color.Parse("#B5CEA8"),
    ];

    public static readonly StyledProperty<GitGraphRow?> RowProperty = AvaloniaProperty.Register<GitGraphCell, GitGraphRow?>(nameof(Row));

    public static readonly StyledProperty<bool> IsMergeProperty = AvaloniaProperty.Register<GitGraphCell, bool>(nameof(IsMerge));

    public static readonly StyledProperty<bool> IsHeadProperty = AvaloniaProperty.Register<GitGraphCell, bool>(nameof(IsHead));

    static GitGraphCell() => AffectsRender<GitGraphCell>(RowProperty, IsMergeProperty, IsHeadProperty);

    public GitGraphRow? Row
    {
        get => GetValue(RowProperty);
        set => SetValue(RowProperty, value);
    }

    public bool IsMerge
    {
        get => GetValue(IsMergeProperty);
        set => SetValue(IsMergeProperty, value);
    }

    /// <summary>The checked-out commit: its dot is ringed.</summary>
    public bool IsHead
    {
        get => GetValue(IsHeadProperty);
        set => SetValue(IsHeadProperty, value);
    }

    static double X(int lane) => LaneWidth / 2 + lane * LaneWidth + 4;

    static IBrush Brush(int color) => new SolidColorBrush(Palette[color % Palette.Length]);

    public override void Render(DrawingContext context)
    {
        if (Row is not { } row)
            return;
        var h = Bounds.Height;
        var middle = h / 2;
        foreach (var line in row.Top)
            Line(context, line, 0, middle);
        foreach (var line in row.Bottom)
            Line(context, line, middle, h);
        var center = new Point(X(row.Lane), middle);
        var brush = Brush(row.Color);
        if (IsHead)
            context.DrawEllipse(null, new Pen(brush, 1.5), center, 6.5, 6.5);
        if (IsMerge)
            context.DrawEllipse(new SolidColorBrush(Color.Parse("#1E1E1E")), new Pen(brush, 2), center, 3.5, 3.5);
        else
            context.DrawEllipse(brush, null, center, 4, 4);
    }

    static void Line(DrawingContext context, GitGraphLine line, double y1, double y2)
    {
        var pen = new Pen(Brush(line.Color), 1.6);
        Point from = new(X(line.From), y1), to = new(X(line.To), y2);
        if (line.From == line.To)
        {
            context.DrawLine(pen, from, to);
            return;
        }
        // A lane joining or leaving another: a curve, as Git Graph draws it.
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(from, false);
            g.CubicBezierTo(new Point(from.X, (y1 + y2) / 2), new Point(to.X, (y1 + y2) / 2), to);
            g.EndFigure(false);
        }
        context.DrawGeometry(null, pen, geometry);
    }
}
