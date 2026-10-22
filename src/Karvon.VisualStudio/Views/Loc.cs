using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Data;
using System.Windows.Markup;
using Karvon.Claude;

namespace Karvon.VisualStudio.Views;

/// <summary>The panel's language, shared with Karvon (see <see cref="ClaudeLanguage"/>).</summary>
public static class Loc
{
    public static ClaudeLanguage Instance => ClaudeLanguage.Instance;
}

/// <summary><c>{v:L 'English text'}</c>: the text in the panel's language, updating when the language changes.</summary>
public sealed class LExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;

    // Bound to Language with the key as the parameter (an indexer path would split keys at their commas).
    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding(nameof(ClaudeLanguage.Language)) { Source = Loc.Instance, Mode = BindingMode.OneWay, Converter = KeyConverter.Instance, ConverterParameter = Key }
            .ProvideValue(serviceProvider);
}

sealed class KeyConverter : IValueConverter
{
    public static readonly KeyConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Loc.Instance.Translate(parameter as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary><c>{v:Tr Path}</c>: a bound text (from the view model) in the panel's language.</summary>
public sealed class TrExtension(string path) : MarkupExtension
{
    public string Path { get; set; } = path;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new MultiBinding { Converter = TranslateConverter.Instance, Mode = BindingMode.OneWay };
        binding.Bindings.Add(new Binding(Path) { Mode = BindingMode.OneWay });
        binding.Bindings.Add(new Binding(nameof(ClaudeLanguage.Language)) { Source = Loc.Instance });
        return binding.ProvideValue(serviceProvider);
    }
}

sealed class TranslateConverter : IMultiValueConverter
{
    public static readonly TranslateConverter Instance = new();

    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture) =>
        Loc.Instance.Translate(values.Length > 0 ? values[0]?.ToString() : null);

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
