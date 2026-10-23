using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace Karvon.Views;

/// <summary>Karvon Studio Installer (see <see cref="ViewModels.InstallerViewModel"/>).</summary>
public partial class InstallerWindow : Window
{
    /// <summary>Modify's second button: Close, or Back once a change needs Karvon Studio to start again (Restart is on the product).</summary>
    public static readonly IValueConverter CloseText = new FuncValueConverter<bool, string>(restart => restart ? "Back" : "Close");

    public InstallerWindow() => InitializeComponent();
}
