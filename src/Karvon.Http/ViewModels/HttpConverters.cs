using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Karvon.Http.ViewModels;

public static class HttpConverters
{
    /// <summary>A method's color, as API tools show them: GET blue, POST green, PUT and PATCH amber, DELETE red.</summary>
    public static readonly IValueConverter MethodBrush = new FuncValueConverter<string, IBrush>(method => new SolidColorBrush(Color.Parse(method switch
    {
        "GET" => "#4DAAFC",
        "POST" => "#73C991",
        "PUT" or "PATCH" => "#E2C08D",
        "DELETE" => "#F14C4C",
        _ => "#C5C5C5",
    })));
}
