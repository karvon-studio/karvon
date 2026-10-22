using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;
using Karvon.Claude;

namespace Karvon.Views;

/// <summary><c>{v:L 'English text'}</c>: a text of the Claude Code panel in its language (English or Uzbek), as in the VS extension.</summary>
public sealed class LExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;

    // Bound to Language with the key as the parameter, so it changes when the language does.
    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new ReflectionBinding(nameof(ClaudeLanguage.Language))
        {
            Source = ClaudeLanguage.Instance,
            Mode = BindingMode.OneWay,
            Converter = KeyConverter.Instance,
            ConverterParameter = Key,
        };

    sealed class KeyConverter : IValueConverter
    {
        public static readonly KeyConverter Instance = new();

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => ClaudeLanguage.Instance.Translate(parameter as string);

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}

/// <summary><c>{v:Tr Path}</c>: a text from the view model ("Working... (3s)", a mode's name) in the panel's language.</summary>
public sealed class TrExtension(string path) : MarkupExtension
{
    public string Path { get; set; } = path;

    public override object ProvideValue(IServiceProvider serviceProvider) => new MultiBinding
    {
        Converter = TranslateConverter.Instance,
        Mode = BindingMode.OneWay,
        Bindings =
        {
            new ReflectionBinding(Path) { Mode = BindingMode.OneWay },
            new ReflectionBinding(nameof(ClaudeLanguage.Language)) { Source = ClaudeLanguage.Instance },
        },
    };

    sealed class TranslateConverter : IMultiValueConverter
    {
        public static readonly TranslateConverter Instance = new();

        public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
            ClaudeLanguage.Instance.Translate(values.Count > 0 ? values[0]?.ToString() : null);
    }
}
