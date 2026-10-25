using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Karvon.Views;

/// <summary>A graph of the last minute's samples (Diagnostic Tools' CPU and memory): a line over its filled area, newest on the right.</summary>
public sealed class UsageGraph : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty = AvaloniaProperty.Register<UsageGraph, IReadOnlyList<double>?>(nameof(Values));

    /// <summary>The top of the scale (CPU's 100); 0: the largest value's, rounded up.</summary>
    public static readonly StyledProperty<double> MaximumProperty = AvaloniaProperty.Register<UsageGraph, double>(nameof(Maximum));

    public static readonly StyledProperty<Color> ColorProperty = AvaloniaProperty.Register<UsageGraph, Color>(nameof(Color), Color.Parse("#3794FF"));

    static UsageGraph() => AffectsRender<UsageGraph>(ValuesProperty, MaximumProperty, ColorProperty);

    public IReadOnlyList<double>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

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

    /// <summary>How many samples the width holds.</summary>
    public const int Points = 60;

    static readonly IPen Grid = new Pen(new SolidColorBrush(Color.Parse("#3F3F46")), 1);

    public override void Render(DrawingContext dc)
    {
        var (width, height) = (Bounds.Width, Bounds.Height);
        dc.FillRectangle(new SolidColorBrush(Color.Parse("#1E1E1E")), new Rect(Bounds.Size));
        for (var i = 1; i < 4; i++)
            dc.DrawLine(Grid, new Point(0, Math.Round(height * i / 4) + 0.5), new Point(width, Math.Round(height * i / 4) + 0.5));
        if (Values is not { Count: > 0 } values || width <= 0 || height <= 0)
            return;
        var top = Maximum > 0 ? Maximum : Scale(values.Max());
        var step = width / (Points - 1);
        var x0 = width - (values.Count - 1) * step;
        Point At(int i) => new(x0 + i * step, height - Math.Clamp(values[i] / top, 0, 1) * (height - 1));
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
            g.BeginFigure(new Point(x0, height), true);
            for (var i = 0; i < values.Count; i++)
                g.LineTo(At(i));
            g.LineTo(new Point(width, height));
            g.EndFigure(true);
        }
        dc.DrawGeometry(new SolidColorBrush(Color, 0.25), null, area);
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color), 1.5), line);
    }

    /// <summary>A round top above the value: 1, 2 or 5 times a power of ten.</summary>
    internal static double Scale(double value)
    {
        if (value <= 0)
            return 1;
        var power = Math.Pow(10, Math.Floor(Math.Log10(value)));
        foreach (var m in new[] { 1, 2, 5, 10 })
            if (value <= m * power)
                return m * power;
        return 10 * power;
    }
}
