using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace Karvon.Views;

/// <summary>Converters the window's own templates use.</summary>
public static class VsConverters
{
    /// <summary>An icon dimmed while what it stands for is off.</summary>
    public static readonly IValueConverter DimmedOpacity = new FuncValueConverter<bool, double>(dimmed => dimmed ? 0.45 : 1.0);

    /// <summary>A menu item that is a switch shows its tick.</summary>
    public static readonly IValueConverter CheckToggle = new FuncValueConverter<bool, MenuItemToggleType>(checkable => checkable ? MenuItemToggleType.CheckBox : MenuItemToggleType.None);
}
