using System.Globalization;
using Avalonia.Data.Converters;
using Karvon.Claude;

namespace Karvon.Views;

/// <summary>Whether two accounts are the same folder (the ticked row of the accounts menu).</summary>
public sealed class SameAccountConverter : IMultiValueConverter
{
    public static SameAccountConverter Instance { get; } = new();

    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        values is [ClaudeAccount a, ClaudeAccount b] && ClaudeAccount.SamePath(a.ConfigDirectory, b.ConfigDirectory);
}
