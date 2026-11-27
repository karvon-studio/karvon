using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Karvon.Jira.ViewModels;

namespace Karvon.Jira.Views;

/// <summary>
/// An issue's assignee on its card, as Jira draws them: their picture in a circle; their initials on a colour of their
/// own when it cannot be read; a grey figure when nobody is assigned. Its tooltip says who.
/// </summary>
public sealed class JiraAvatar : Control
{
    public static readonly StyledProperty<string?> PersonProperty = AvaloniaProperty.Register<JiraAvatar, string?>(nameof(Person));

    public static readonly StyledProperty<string?> UrlProperty = AvaloniaProperty.Register<JiraAvatar, string?>(nameof(Url));

    public static readonly StyledProperty<JiraAvatars?> AvatarsProperty = AvaloniaProperty.Register<JiraAvatar, JiraAvatars?>(nameof(Avatars));

    /// <summary>Atlassian's colours for initials.</summary>
    static readonly Color[] Colors = [.. new[] { "#0055CC", "#1F845A", "#C9372C", "#6E5DC6", "#B65C02", "#227D9B", "#943D73", "#5B7F24" }.Select(Color.Parse)];

    static readonly IBrush Nobody = new SolidColorBrush(Color.Parse("#454549"));

    static readonly IBrush Figure = new SolidColorBrush(Color.Parse("#A1A1A6"));

    Bitmap? _picture;

    static JiraAvatar() => AffectsRender<JiraAvatar>(PersonProperty);

    public JiraAvatar() => ToolTip.SetTip(this, "Unassigned");

    public string? Person
    {
        get => GetValue(PersonProperty);
        set => SetValue(PersonProperty, value);
    }

    public string? Url
    {
        get => GetValue(UrlProperty);
        set => SetValue(UrlProperty, value);
    }

    public JiraAvatars? Avatars
    {
        get => GetValue(AvatarsProperty);
        set => SetValue(AvatarsProperty, value);
    }

    /// <summary>Its picture is drawn (not initials, not the figure of nobody).</summary>
    public bool ShowsPicture => _picture is not null;

    /// <summary>What it shows without a picture: the first letters of the first two words ("Dev Eloper": DE).</summary>
    public static string Initials(string name) =>
        string.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PersonProperty)
            ToolTip.SetTip(this, Person is { Length: > 0 } person ? $"Assignee: {person}" : "Unassigned");
        else if (change.Property == UrlProperty || change.Property == AvatarsProperty)
            _ = LoadAsync();
    }

    async Task LoadAsync()
    {
        _picture = null;
        InvalidateVisual();
        if (Url is not { } url || Avatars is not { } avatars)
            return;
        var picture = await avatars.GetAsync(url);
        if (Url == url)
        {
            _picture = picture;
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0)
            return;
        var circle = new Rect((Bounds.Width - size) / 2, (Bounds.Height - size) / 2, size, size);
        var center = circle.Center;
        if (Person is not { Length: > 0 } person)
        {
            // Nobody: a head and shoulders in a grey circle.
            context.DrawEllipse(Nobody, null, circle);
            using (context.PushGeometryClip(new EllipseGeometry(circle)))
            {
                context.DrawEllipse(Figure, null, new Point(center.X, circle.Y + size * 0.4), size * 0.18, size * 0.18);
                context.DrawEllipse(Figure, null, new Point(center.X, circle.Bottom + size * 0.08), size * 0.34, size * 0.3);
            }
            return;
        }
        if (_picture is { } picture)
        {
            using (context.PushGeometryClip(new EllipseGeometry(circle)))
                context.DrawImage(picture, new Rect(picture.Size), circle);
            return;
        }
        // Each name its own colour, the same every time (string.GetHashCode is not).
        var hue = Colors[person.Aggregate(0, (h, c) => (h * 31 + c) & 0x7FFFFFFF) % Colors.Length];
        context.DrawEllipse(new SolidColorBrush(hue), null, circle);
        var text = new FormattedText(Initials(person), System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, weight: FontWeight.SemiBold), size * 0.4, Brushes.White);
        context.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2));
    }
}

/// <summary>An issue's type as Jira's icon of it: a bug's red dot, a task's blue tick, a story's green bookmark, an epic's purple bolt.</summary>
public sealed class JiraTypeIcon : Control
{
    public static readonly StyledProperty<string?> TypeProperty = AvaloniaProperty.Register<JiraTypeIcon, string?>(nameof(Type));

    public static readonly StyledProperty<bool> SubtaskProperty = AvaloniaProperty.Register<JiraTypeIcon, bool>(nameof(Subtask));

    public enum Kind
    {
        Task,
        Bug,
        Story,
        Epic,
        Subtask,
    }

    static JiraTypeIcon() => AffectsRender<JiraTypeIcon>(TypeProperty, SubtaskProperty);

    public string? Type
    {
        get => GetValue(TypeProperty);
        set => SetValue(TypeProperty, value);
    }

    public bool Subtask
    {
        get => GetValue(SubtaskProperty);
        set => SetValue(SubtaskProperty, value);
    }

    /// <summary>A type by its name, as a site names it (English, Russian, Uzbek); a task when it is none of the others.</summary>
    public static Kind KindOf(string? type, bool subtask)
    {
        var name = (type ?? "").ToLowerInvariant();
        if (subtask || name.Contains("sub") || name.Contains("подзадач"))
            return Kind.Subtask;
        if (name.Contains("bug") || name.Contains("ошибк") || name.Contains("баг") || name.Contains("xato"))
            return Kind.Bug;
        if (name.Contains("story") || name.Contains("истори"))
            return Kind.Story;
        if (name.Contains("epic") || name.Contains("эпик"))
            return Kind.Epic;
        return Kind.Task;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TypeProperty)
            ToolTip.SetTip(this, Type);
    }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0)
            return;
        var kind = KindOf(Type, Subtask);
        var color = kind switch
        {
            Kind.Bug => "#E5493A",
            Kind.Story => "#63BA3C",
            Kind.Epic => "#904EE2",
            _ => "#4BADE8",
        };
        // Drawn on a 16-unit square.
        var s = size / 16;
        using (context.PushTransform(Matrix.CreateScale(s, s) * Matrix.CreateTranslation((Bounds.Width - size) / 2, (Bounds.Height - size) / 2)))
        {
            context.DrawRectangle(new SolidColorBrush(Color.Parse(color)), null, new Rect(0, 0, 16, 16), 3, 3);
            var white = Brushes.White;
            var pen = new Pen(white, 1.8, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            switch (kind)
            {
                case Kind.Bug:
                    context.DrawEllipse(white, null, new Point(8, 8), 3.6, 3.6);
                    break;
                case Kind.Story:
                    context.DrawGeometry(white, null, StreamGeometry.Parse("M5,3.5 H11 V12.5 L8,10 L5,12.5 Z"));
                    break;
                case Kind.Epic:
                    context.DrawGeometry(white, null, StreamGeometry.Parse("M9.5,2.5 L4.5,9 H7.8 L6.5,13.5 L11.5,7 H8.2 Z"));
                    break;
                case Kind.Subtask:
                    var thin = new Pen(white, 1.3);
                    context.DrawRectangle(null, thin, new Rect(3.5, 3.5, 6, 6));
                    context.DrawRectangle(white, null, new Rect(7, 7, 5.5, 5.5));
                    break;
                default:
                    context.DrawGeometry(null, pen, StreamGeometry.Parse("M4.5,8.3 L7,10.8 L11.5,5.5"));
                    break;
            }
        }
    }
}

/// <summary>An issue's priority as Jira's arrows: red up for the highest, orange bars for medium, blue down for the lowest.</summary>
public sealed class JiraPriorityIcon : Control
{
    public static readonly StyledProperty<string?> PriorityProperty = AvaloniaProperty.Register<JiraPriorityIcon, string?>(nameof(Priority));

    public enum Level
    {
        None,
        Highest,
        High,
        Medium,
        Low,
        Lowest,
    }

    static JiraPriorityIcon() => AffectsRender<JiraPriorityIcon>(PriorityProperty);

    public string? Priority
    {
        get => GetValue(PriorityProperty);
        set => SetValue(PriorityProperty, value);
    }

    /// <summary>A priority by its name, as a site names it (Jira's own, Server's Blocker to Trivial, Russian); none when unknown.</summary>
    public static Level LevelOf(string? priority)
    {
        var name = (priority ?? "").ToLowerInvariant();
        if (name.Length == 0)
            return Level.None;
        if (name.Contains("highest") || name.Contains("blocker") || name.Contains("critical") || name.Contains("высочайш") || name.Contains("блок") || name.Contains("критич"))
            return Level.Highest;
        if (name.Contains("lowest") || name.Contains("trivial") || name.Contains("незначит"))
            return Level.Lowest;
        if (name.Contains("high") || name.Contains("major") || name.Contains("высок"))
            return Level.High;
        if (name.Contains("low") || name.Contains("minor") || name.Contains("низк"))
            return Level.Low;
        if (name.Contains("medium") || name.Contains("normal") || name.Contains("средн"))
            return Level.Medium;
        return Level.None;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PriorityProperty)
        {
            ToolTip.SetTip(this, Priority is { } priority ? $"Priority: {priority}" : null);
            IsVisible = LevelOf(Priority) != Level.None;
        }
    }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        var level = LevelOf(Priority);
        if (size <= 0 || level == Level.None)
            return;
        var color = level switch
        {
            Level.Highest => "#FF5630",
            Level.High => "#FF7452",
            Level.Medium => "#FFAB00",
            Level.Low => "#2684FF",
            _ => "#0065FF",
        };
        var pen = new Pen(new SolidColorBrush(Color.Parse(color)), 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        var s = size / 16;
        using (context.PushTransform(Matrix.CreateScale(s, s) * Matrix.CreateTranslation((Bounds.Width - size) / 2, (Bounds.Height - size) / 2)))
        {
            void Chevron(double y, bool up) =>
                context.DrawGeometry(null, pen, StreamGeometry.Parse(up ? $"M3.5,{y + 4} L8,{y} L12.5,{y + 4}" : $"M3.5,{y} L8,{y + 4} L12.5,{y}"));
            switch (level)
            {
                case Level.Highest:
                    Chevron(3, up: true);
                    Chevron(8, up: true);
                    break;
                case Level.High:
                    Chevron(5.5, up: true);
                    break;
                case Level.Medium:
                    context.DrawLine(pen, new Point(3.5, 6), new Point(12.5, 6));
                    context.DrawLine(pen, new Point(3.5, 10), new Point(12.5, 10));
                    break;
                case Level.Low:
                    Chevron(6, up: false);
                    break;
                default:
                    Chevron(3.5, up: false);
                    Chevron(8.5, up: false);
                    break;
            }
        }
    }
}
