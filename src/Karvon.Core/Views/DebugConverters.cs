using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Karvon.Views;

/// <summary>How the debugger's windows color what they show.</summary>
public static class DebugConverters
{
    /// <summary>An error's text red (the Immediate window's answer that is one), the rest as text is.</summary>
    public static readonly IValueConverter ErrorBrush = new FuncValueConverter<bool, IBrush>(error =>
        new SolidColorBrush(Color.Parse(error ? "#F48771" : "#DCDCDC")));
}
