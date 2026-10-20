using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace VSL.Views;

/// <summary>
/// Shows a Database Explorer document (its Bengine Data Studio view, from the application's data templates). The
/// explorer's window-wide shortcuts (Find Action, Go to Object, Query History...) work in it, as in Studio.
/// </summary>
public sealed class DatabaseDocumentHost : ContentControl
{
    protected override Type StyleKeyOverride => typeof(ContentControl);

    public DatabaseDocumentHost() =>
        AddHandler(KeyDownEvent, (_, e) => Database.Views.DatabaseExplorerView.Current?.HandleDocumentKey(e), RoutingStrategies.Tunnel);
}
