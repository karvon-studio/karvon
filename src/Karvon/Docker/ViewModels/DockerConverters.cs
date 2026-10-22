using System.Globalization;
using Avalonia.Data.Converters;

namespace Karvon.Docker.ViewModels;

public static class DockerConverters
{
    /// <summary>Bytes as Docker Desktop shows them ("345.2 MB").</summary>
    public static readonly IValueConverter Size = new FuncValueConverter<long, string>(bytes => DockerFormat.Size(bytes));
}
