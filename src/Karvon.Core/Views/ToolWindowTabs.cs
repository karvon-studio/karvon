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

    public static void Register(ToolWindowInfo tool) => ModuleIcons[tool.Id] = tool.Icon;

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
        ToolWindows.Terminal => "ToolTerminal",
        _ => "ToolExplorer",
    };

    public static readonly IValueConverter Icon = new FuncValueConverter<object?, string>(tag => IconOf(tag as string));

    /// <summary>"7", "99+"; empty for none.</summary>
    public static readonly IValueConverter BadgeText = new FuncValueConverter<int, string>(count =>
        count <= 0 ? "" : count > 99 ? "99+" : count.ToString(CultureInfo.InvariantCulture));

    public static readonly IValueConverter HasBadge = new FuncValueConverter<int, bool>(count => count > 0);
}
