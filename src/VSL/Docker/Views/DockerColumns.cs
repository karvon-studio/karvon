using System.Globalization;
using Avalonia;
using Avalonia.Controls;

namespace VSL.Docker.Views;

/// <summary>
/// A table row's columns (the header's and each row's, laid out alike): fixed widths, one column that takes the rest,
/// and columns that go when the row is too narrow for them, in the order their numbers give, as Docker Desktop's
/// tables drop Container ID and Last started in a narrow window. <see cref="Layout"/> is "40,*180,124?3,200,..."
/// (a width; "*180" fills, at least 180 wide; "?3" goes third).
/// </summary>
public sealed class DockerColumns : Panel
{
    public static readonly StyledProperty<string> LayoutProperty = AvaloniaProperty.Register<DockerColumns, string>(nameof(Layout), "");

    public static readonly AttachedProperty<int> ColumnProperty = AvaloniaProperty.RegisterAttached<DockerColumns, Control, int>("Column");

    static DockerColumns()
    {
        AffectsMeasure<DockerColumns>(LayoutProperty);
        AffectsParentMeasure<DockerColumns>(ColumnProperty);
        ClipToBoundsProperty.OverrideDefaultValue<DockerColumns>(true);
    }

    public string Layout
    {
        get => GetValue(LayoutProperty);
        set => SetValue(LayoutProperty, value);
    }

    public static int GetColumn(Control control) => control.GetValue(ColumnProperty);

    public static void SetColumn(Control control, int value) => control.SetValue(ColumnProperty, value);

    readonly record struct Column(double Width, bool Fills, int DropOrder);

    string? _parsedLayout;
    Column[] _columns = [];
    double[] _widths = [];

    Column[] Columns()
    {
        if (_parsedLayout == Layout)
            return _columns;
        _parsedLayout = Layout;
        _columns = Layout.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(spec =>
        {
            var drop = spec.IndexOf('?') is >= 0 and var q ? int.Parse(spec[(q + 1)..], CultureInfo.InvariantCulture) : 0;
            var width = drop > 0 ? spec[..spec.IndexOf('?')] : spec;
            var fills = width.StartsWith('*');
            return new Column(double.Parse(fills ? width.Length > 1 ? width[1..] : "0" : width, CultureInfo.InvariantCulture), fills, drop);
        }).ToArray();
        return _columns;
    }

    /// <summary>Each column's width for a row this wide: optional ones dropped (0) until the rest fits.</summary>
    double[] Widths(double available)
    {
        var columns = Columns();
        var shown = columns.Select(_ => true).ToArray();
        double Needed() => columns.Where((_, i) => shown[i]).Sum(c => c.Width);
        foreach (var (column, index) in columns.Select((c, i) => (c, i)).Where(x => x.c.DropOrder > 0).OrderBy(x => x.c.DropOrder))
        {
            if (Needed() <= available || double.IsInfinity(available))
                break;
            shown[index] = false;
        }
        var fixedWidth = columns.Where((c, i) => shown[i] && !c.Fills).Sum(c => c.Width);
        var fill = columns.Where((c, i) => shown[i] && c.Fills).ToList();
        var rest = double.IsInfinity(available) ? 0 : Math.Max(available - fixedWidth, 0);
        return columns.Select((c, i) => !shown[i] ? 0 : c.Fills ? Math.Max(rest / Math.Max(fill.Count, 1), c.Width) : c.Width).ToArray();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _widths = Widths(availableSize.Width);
        var height = 0.0;
        foreach (var child in Children)
        {
            var column = Math.Clamp(GetColumn(child), 0, Math.Max(_widths.Length - 1, 0));
            var width = _widths.Length == 0 ? 0 : _widths[column];
            child.Measure(new Size(width, availableSize.Height));
            if (width > 0)
                height = Math.Max(height, child.DesiredSize.Height);
        }
        return new Size(double.IsInfinity(availableSize.Width) ? _widths.Sum() : availableSize.Width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _widths = Widths(finalSize.Width);
        var lefts = new double[_widths.Length];
        for (var i = 1; i < _widths.Length; i++)
            lefts[i] = lefts[i - 1] + _widths[i - 1];
        foreach (var child in Children)
        {
            var column = Math.Clamp(GetColumn(child), 0, Math.Max(_widths.Length - 1, 0));
            var width = _widths.Length == 0 ? 0 : _widths[column];
            // A dropped column's cells go out of sight (the panel clips).
            child.Arrange(width > 0 ? new Rect(lefts[column], 0, width, finalSize.Height) : new Rect(-10_000, 0, 0, finalSize.Height));
        }
        return finalSize;
    }
}
