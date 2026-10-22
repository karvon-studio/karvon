using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Karvon.Database.Views;

/// <summary>
/// Shows a Database Explorer document (its Bengine Data Studio view, from the application's data templates). The
/// explorer's window-wide shortcuts (Find Action, Go to Object, Query History...) work in it, as in Studio.
/// </summary>
public sealed class DatabaseDocumentHost : ContentControl, Modules.IKeymapOwner
{
    protected override Type StyleKeyOverride => typeof(ContentControl);

    public DatabaseDocumentHost() =>
        AddHandler(KeyDownEvent, (_, e) => DatabaseExplorerView.Current?.HandleDocumentKey(e), RoutingStrategies.Tunnel);
}
