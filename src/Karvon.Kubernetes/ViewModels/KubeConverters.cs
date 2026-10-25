using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Karvon.Kubernetes.ViewModels;

/// <summary>How the Kubernetes pages show a kind, a problem, a condition and an event.</summary>
public static class KubeConverters
{
    public static readonly IValueConverter KindIcon = new FuncValueConverter<KubeKind, string>(kind => kind.Icon());

    public static readonly IValueConverter ProblemBrush = new FuncValueConverter<bool, IBrush>(problem =>
        new SolidColorBrush(Color.Parse(problem ? "#F14C4C" : "#9D9D9D")));

    public static readonly IValueConverter TrueIcon = new FuncValueConverter<bool, string>(yes => yes ? "Ok" : "Pending");

    public static readonly IValueConverter WarningIcon = new FuncValueConverter<bool, string>(warning => warning ? "Warning" : "Info");
}
