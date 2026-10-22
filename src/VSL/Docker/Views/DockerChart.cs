using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace VSL.Docker.Views;

/// <summary>
/// The Stats tab's line chart: the last minute of samples, scaled to the largest (or <see cref="Maximum"/>), with a
/// soft fill under the line as Docker Desktop draws them. <see cref="Version"/> changes with each sample.
/// </summary>
public sealed class DockerChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty = AvaloniaProperty.Register<DockerChart, IReadOnlyList<double>?>(nameof(Values));

    public static readonly StyledProperty<int> VersionProperty = AvaloniaProperty.Register<DockerChart, int>(nameof(Version));

    public static readonly StyledProperty<double> MaximumProperty = AvaloniaProperty.Register<DockerChart, double>(nameof(Maximum));

    public static readonly StyledProperty<Color> ColorProperty = AvaloniaProperty.Register<DockerChart, Color>(nameof(Color), Color.Parse("#4E8DF5"));

    /// <summary>The points a full chart shows (one a second for a minute).</summary>
    public const int Points = 60;

    static DockerChart() => AffectsRender<DockerChart>(ValuesProperty, VersionProperty, MaximumProperty, ColorProperty);

    public IReadOnlyList<double>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public int Version
    {
        get => GetValue(VersionProperty);
        set => SetValue(VersionProperty, value);
    }

    /// <summary>The top of the scale (100 for a percentage); 0 scales to the largest sample.</summary>
    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public Color Color
    {
        get => GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        var grid = new Pen(new SolidColorBrush(Color.Parse("#333337")), 1);
        for (var i = 1; i < 4; i++)
        {
            var y = Math.Round(bounds.Height * i / 4) + 0.5;
            context.DrawLine(grid, new Point(0, y), new Point(bounds.Width, y));
        }
        if (Values is not { Count: > 0 } values || bounds.Width < 2 || bounds.Height < 2)
            return;
        var top = Maximum > 0 ? Maximum : Math.Max(values.Max() * 1.4, 1e-9);
        var step = bounds.Width / (Points - 1);
        var start = bounds.Width - step * (values.Count - 1);
        Point At(int i) => new(start + step * i, bounds.Height - Math.Clamp(values[i] / top, 0, 1) * (bounds.Height - 2) - 1);
        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var g = line.Open())
        {
            g.BeginFigure(At(0), false);
            for (var i = 1; i < values.Count; i++)
                g.LineTo(At(i));
            g.EndFigure(false);
        }
        using (var g = area.Open())
        {
            g.BeginFigure(new Point(At(0).X, bounds.Height), true);
            for (var i = 0; i < values.Count; i++)
                g.LineTo(At(i));
            g.LineTo(new Point(At(values.Count - 1).X, bounds.Height));
            g.EndFigure(true);
        }
        context.DrawGeometry(new SolidColorBrush(Color, 0.16), null, area);
        context.DrawGeometry(null, new Pen(new SolidColorBrush(Color), 1.6, lineJoin: PenLineJoin.Round), line);
    }
}
