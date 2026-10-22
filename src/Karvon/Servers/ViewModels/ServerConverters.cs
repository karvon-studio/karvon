using Avalonia.Data.Converters;

namespace Karvon.Servers.ViewModels;

public static class ServerConverters
{
    public static readonly IValueConverter Size = new FuncValueConverter<long, string>(bytes => RemoteCommands.Size(bytes));
}
