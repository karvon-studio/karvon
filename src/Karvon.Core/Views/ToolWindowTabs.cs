using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Karvon.Modules;
using Karvon.ViewModels;

namespace Karvon.Views;

/// <summary>
/// The side tool groups' tabs as VS Code's activity bar: an icon per tool window (its name in the tooltip), a badge
/// with a count (Git Changes' changes), the tool window in front marked at the outer edge.
/// </summary>
public static class ToolWindowTabs
{
    /// <summary>A count on the tab's icon; 0 shows none.</summary>
    public static readonly AttachedProperty<int> BadgeProperty = AvaloniaProperty.RegisterAttached<TabItem, int>("Badge", typeof(ToolWindowTabs));

    public static int GetBadge(TabItem item) => item.GetValue(BadgeProperty);

    public static void SetBadge(TabItem item, int value) => item.SetValue(BadgeProperty, value);

    /// <summary>The modules' tool windows' icons, by their ids.</summary>
    static readonly Dictionary<string, string> ModuleIcons = [];

    /// <summary>The modules' tool windows' pictures as apps (the OS view's), by their ids.</summary>
    static readonly Dictionary<string, string> AppIcons = [];

    public static void Register(ToolWindowInfo tool)
    {
        ModuleIcons[tool.Id] = tool.Icon;
        if (tool.AppIcon is { } app)
            AppIcons[tool.Id] = app;
    }

    /// <summary>
    /// A tool window's picture as an app: its module's, else Karvon's own in Assets/Apps (by its id, .png or .svg); null
    /// for its icon on a tile.
    /// </summary>
    public static string? AppIconOf(string id)
    {
        if (AppIcons.TryGetValue(id, out var given))
            return given;
        foreach (var extension in new[] { ".png", ".svg" })
        {
            var uri = new Uri($"avares://Karvon.Core/Assets/Apps/{id}{extension}");
            if (Avalonia.Platform.AssetLoader.Exists(uri))
                return uri.ToString();
        }
        return null;
    }

    /// <summary>The icon of a tool window, by its id (the tab's Tag).</summary>
    public static string IconOf(string? id) => id is not null && ModuleIcons.TryGetValue(id, out var icon) ? icon : id switch
    {
        ToolWindows.SolutionExplorer => "ToolExplorer",
        ToolWindows.TestExplorer => "ToolTests",
        ToolWindows.GitChanges => "ToolGit",
        ToolWindows.ErrorList => "ToolErrors",
        ToolWindows.Output => "ToolOutput",
        ToolWindows.FindResults => "ToolFind",
        ToolWindows.Locals => "ToolLocals",
        ToolWindows.Watch => "Eye",
        ToolWindows.CallStack => "ToolCallStack",
        ToolWindows.Breakpoints => "Breakpoint",
        ToolWindows.Immediate => "ToolImmediate",
        ToolWindows.ExceptionSettings => "Warning",
        ToolWindows.CoverageResults => "ToolCoverage",
        ToolWindows.DiagnosticTools => "Gauge",
        ToolWindows.Terminal => "ToolTerminal",
        ToolWindows.Git => "ToolGit",
        ToolWindows.Testing => "ToolTests",
        _ => "ToolExplorer",
    };

    public static readonly IValueConverter Icon = new FuncValueConverter<object?, string>(tag => IconOf(tag as string));

    static readonly Dictionary<string, Avalonia.Media.IImage?> Icons2D = [];

    /// <summary>A tool window's 2D picture (Assets/Icons2D, by its id) for its activity bar icon; null for its drawn icon.</summary>
    public static Avalonia.Media.IImage? Icon2DOf(string? id)
    {
        if (id is null)
            return null;
        lock (Icons2D)
        {
            if (Icons2D.TryGetValue(id, out var known))
                return known;
            var uri = new Uri($"avares://Karvon.Core/Assets/Icons2D/{id}.png");
            Avalonia.Media.IImage? picture = null;
            if (Avalonia.Platform.AssetLoader.Exists(uri))
            {
                using var stream = Avalonia.Platform.AssetLoader.Open(uri);
                picture = new Avalonia.Media.Imaging.Bitmap(stream);
            }
            return Icons2D[id] = picture;
        }
    }

    public static readonly IValueConverter Icon2D = new FuncValueConverter<object?, Avalonia.Media.IImage?>(tag => Icon2DOf(tag as string));

    public static readonly IValueConverter HasIcon2D = new FuncValueConverter<object?, bool>(tag => Icon2DOf(tag as string) is not null);

    public static readonly IValueConverter HasNoIcon2D = new FuncValueConverter<object?, bool>(tag => Icon2DOf(tag as string) is null);

    /// <summary>"7", "99+"; empty for none.</summary>
    public static readonly IValueConverter BadgeText = new FuncValueConverter<int, string>(count =>
        count <= 0 ? "" : count > 99 ? "99+" : count.ToString(CultureInfo.InvariantCulture));

    public static readonly IValueConverter HasBadge = new FuncValueConverter<int, bool>(count => count > 0);
}
