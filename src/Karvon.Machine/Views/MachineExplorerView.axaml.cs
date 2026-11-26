using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Karvon.Machine.ViewModels;

namespace Karvon.Machine.Views;

/// <summary>This Computer's explorer (see <see cref="MachineExplorerViewModel"/>): a section chosen opens its page.</summary>
public partial class MachineExplorerView : UserControl
{
    public MachineExplorerView()
    {
        InitializeComponent();
        // A click on a section opens its page, also when it was chosen already (its page closed since).
        Sections.AddHandler(TappedEvent, OnTapped, RoutingStrategies.Bubble, handledEventsToo: true);
        Sections.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Sections.SelectedItem is MachineSection section)
                section.Open();
        };
        AttachedToVisualTree += (_, _) => _ = (DataContext as MachineExplorerViewModel)?.RefreshAsync();
    }

    void OnTapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Control)?.DataContext is MachineSection section)
            section.Open();
    }
}
