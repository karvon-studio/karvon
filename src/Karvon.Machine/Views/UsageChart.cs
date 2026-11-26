using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Karvon.Machine.Views;

/// <summary>The last looks' shares (0 to 100) as a line over its filled area, the newest at the right.</summary>
public sealed class UsageChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty = AvaloniaProperty.Register<UsageChart, IReadOnlyList<double>?>(nameof(Values));

    public static readonly StyledProperty<IBrush?> StrokeProperty = AvaloniaProperty.Register<UsageChart, IBrush?>(nameof(Stroke), new SolidColorBrush(Color.Parse("#4DAAFC")));

    /// <summary>How many looks fill its width.</summary>
    public int Length { get; set; } = 120;

    static UsageChart() => AffectsRender<UsageChart>(ValuesProperty, StrokeProperty);

    public IReadOnlyList<double>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != ValuesProperty)
            return;
        if (change.OldValue is INotifyCollectionChanged old)
            old.CollectionChanged -= OnChanged;
        if (change.NewValue is INotifyCollectionChanged now)
            now.CollectionChanged += OnChanged;
    }

    void OnChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        var (width, height) = (Bounds.Width, Bounds.Height);
        var grid = new Pen(new SolidColorBrush(Color.Parse("#2D2D30")), 1);
        for (var i = 1; i < 4; i++)
            context.DrawLine(grid, new Point(0, height * i / 4), new Point(width, height * i / 4));
        if (Values is not { Count: > 1 } values || Stroke is not ISolidColorBrush stroke)
            return;
        var step = width / Math.Max(1, Length - 1);
        var start = width - step * (values.Count - 1);
        var points = values.Select((v, i) => new Point(start + i * step, height - Math.Clamp(v, 0, 100) / 100 * height)).ToList();
        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            g.BeginFigure(points[0], false);
            foreach (var p in points.Skip(1))
                g.LineTo(p);
        }
        var area = new StreamGeometry();
        using (var g = area.Open())
        {
            g.BeginFigure(new Point(points[0].X, height), true);
            foreach (var p in points)
                g.LineTo(p);
            g.LineTo(new Point(points[^1].X, height));
        }
        context.DrawGeometry(new SolidColorBrush(stroke.Color, 0.18), null, area);
        context.DrawGeometry(null, new Pen(stroke, 1.5), line);
    }
}
