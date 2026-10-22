using Avalonia.Data.Converters;

namespace VSL.Servers.ViewModels;

public static class ServerConverters
{
    public static readonly IValueConverter Size = new FuncValueConverter<long, string>(bytes => RemoteCommands.Size(bytes));
}
