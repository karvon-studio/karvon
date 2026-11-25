using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Karvon.Postman.ViewModels;

/// <summary>How the Postman views show what they show.</summary>
public static class PostmanConverters
{
    public static readonly IValueConverter Bold = new FuncValueConverter<bool, FontWeight>(bold => bold ? FontWeight.SemiBold : FontWeight.Normal);

    /// <summary>How many there are, in a tab's title; nothing for none.</summary>
    public static readonly IValueConverter Count = new FuncValueConverter<int, string>(count => count == 0 ? "" : $"({count})");

    /// <summary>A test's mark: passed, failed, skipped.</summary>
    public static readonly IValueConverter TestIcon = new FuncValueConverter<PostmanTest?, string>(test => test is null ? "" : test.IsSkipped ? "Pending" : test.Passed ? "Ok" : "Error");

    /// <summary>A result's mark in a run.</summary>
    public static readonly IValueConverter ResultIcon = new FuncValueConverter<PostmanResult?, string>(result => result is null ? "" : result.IsSkipped ? "Pending" : result.Succeeded ? "Ok" : "Error");
}

/// <summary>How a row of a form shows whether it is a file.</summary>
public static class PostmanRowConverters
{
    /// <summary>Text or File, as the choice's place, both ways.</summary>
    public static readonly IValueConverter FileIndex = new FileIndexConverter();

    public static readonly IValueConverter ValueHint = new FuncValueConverter<bool, string>(file => file ? "A file's path" : "Value");

    sealed class FileIndexConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => value is true ? 1 : 0;

        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => value is 1;
    }
}
