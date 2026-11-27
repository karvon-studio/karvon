using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Karvon.Views;

/// <summary>
/// A tool window as an app, as the OS view's dock and apps grid show it: its picture (the module's, or Karvon's own in
/// Assets/Apps) when it has one, else its icon in white on a rounded tile of its own colour.
/// </summary>
public sealed class AppIcon : Border
{
    public static readonly StyledProperty<string?> AppIdProperty = AvaloniaProperty.Register<AppIcon, string?>(nameof(AppId));

    /// <summary>The tiles' colours, from their top to their bottom.</summary>
    static readonly (string Top, string Bottom)[] Tiles =
    [
        ("#4F8DF7", "#2459D6"), ("#22C08A", "#0E8A5F"), ("#F6B23C", "#D9801A"), ("#F0645A", "#C8352D"), ("#9A6CF4", "#6A3CD3"),
        ("#EE5FA6", "#C02D78"), ("#26C2B5", "#12857B"), ("#7C8BA1", "#4A576B"), ("#F98B3D", "#D45A12"), ("#2EC0E0", "#1484A6"),
        ("#3A3A42", "#1F1F25"),
    ];

    /// <summary>Those whose colour says what they are (Claude's orange, Git's red), the rest by their id.</summary>
    static readonly Dictionary<string, int> Known = new(StringComparer.Ordinal)
    {
        // Those whose icon has its own colour (Claude's orange, a breakpoint's red) on a tile it stands out on.
        ["SolutionExplorer"] = 4, ["Claude"] = 10, ["Breakpoints"] = 10, ["ExceptionSettings"] = 10, ["CodeMap"] = 10, ["GitChanges"] = 3, ["Git"] = 3, ["DatabaseExplorer"] = 0, ["DockerExplorer"] = 9,
        ["Terminal"] = 7, ["TestExplorer"] = 1, ["Testing"] = 1, ["Kubernetes"] = 0, ["Jira"] = 0, ["Postman"] = 8, ["Machine"] = 6,
        ["Servers"] = 7, ["Browser"] = 9, ["Agents"] = 5, ["Skills"] = 2, ["ErrorList"] = 3, ["Output"] = 7,
    };

    static AppIcon() => AppIdProperty.Changed.AddClassHandler<AppIcon>((icon, _) => icon.Build());

    public AppIcon()
    {
        Width = Height = 48;
        ClipToBounds = false;
    }

    public string? AppId
    {
        get => GetValue(AppIdProperty);
        set => SetValue(AppIdProperty, value);
    }

    /// <summary>Its picture's own (not a tile with its icon).</summary>
    public bool HasPicture { get; private set; }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        var size = Math.Min(e.NewSize.Width, e.NewSize.Height);
        if (!HasPicture)
            CornerRadius = new CornerRadius(size * 0.24);
        if (Child is VsIcon glyph)
            glyph.Width = glyph.Height = size * 0.56;
    }

    void Build()
    {
        HasPicture = false;
        if (AppId is not { } id)
        {
            Child = null;
            Background = null;
            return;
        }
        if (ToolWindowTabs.AppIconOf(id) is { } uri && Picture(new Uri(uri)) is { } picture)
        {
            HasPicture = true;
            Background = null;
            BoxShadow = default;
            Child = new Image { Source = picture, Stretch = Stretch.Uniform };
            return;
        }
        var (top, bottom) = Tiles[Known.TryGetValue(id, out var known) ? known : Math.Abs(id.Aggregate(17, (h, c) => h * 31 + c)) % (Tiles.Length - 1)];
        Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.Parse(top), 0), new GradientStop(Color.Parse(bottom), 1) },
        };
        BoxShadow = BoxShadows.Parse("0 2 6 0 #55000000");
        var size = Math.Min(Width, Height);
        Child = new VsIcon
        {
            Kind = ToolWindowTabs.IconOf(id),
            Foreground = Brushes.White,
            Width = size * 0.56,
            Height = size * 0.56,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
    }

    /// <summary>A PNG, or an SVG as Karvon draws them; null when it cannot be read.</summary>
    static IImage? Picture(Uri uri)
    {
        try
        {
            using var stream = AssetLoader.Open(uri);
            return uri.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ? SvgDrawing.Load(stream) : new Bitmap(stream);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }
}
