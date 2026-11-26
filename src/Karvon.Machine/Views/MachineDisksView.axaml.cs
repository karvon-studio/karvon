using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Karvon.Machine.ViewModels;

namespace Karvon.Machine.Views;

/// <summary>The Disks page (see <see cref="DisksPage"/>): a row double-clicked (or Enter) is opened, a ring's folder clicked gone into.</summary>
public partial class MachineDisksView : UserControl
{
    public MachineDisksView()
    {
        InitializeComponent();
        Rings.Chosen += folder => (DataContext as DisksPage)?.Choose(folder);
        Table.AddHandler(DoubleTappedEvent, (_, e) => Open(e.Source as Control), RoutingStrategies.Bubble, handledEventsToo: true);
        Table.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
                Open(Table.ContainerFromItem(Table.SelectedItem ?? new object()) as Control);
            else if (e.Key == Key.Back && DataContext is DisksPage page)
                page.UpCommand.Execute(null);
        };
    }

    void Open(Control? control)
    {
        if (control?.DataContext is FileRow row && DataContext is DisksPage page)
            page.Open(row);
    }
}
