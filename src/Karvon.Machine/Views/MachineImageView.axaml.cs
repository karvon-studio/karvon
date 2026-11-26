using Avalonia.Controls;
using Avalonia.Input;
using Karvon.Machine.ViewModels;

namespace Karvon.Machine.Views;

/// <summary>A picture (see <see cref="ImagePage"/>): ← and → step through the folder's pictures.</summary>
public partial class MachineImageView : UserControl
{
    public MachineImageView()
    {
        InitializeComponent();
        KeyDown += (_, e) =>
        {
            if (DataContext is not ImagePage page)
                return;
            if (e.Key == Key.Right)
                page.NextCommand.Execute(null);
            else if (e.Key == Key.Left)
                page.PreviousCommand.Execute(null);
        };
        AttachedToVisualTree += (_, _) => Focus();
    }
}
