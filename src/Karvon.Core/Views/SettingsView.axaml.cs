using Avalonia.Controls;
using Avalonia.Input;
using Karvon.ViewModels;

namespace Karvon.Views;

/// <summary>Settings (see <see cref="SettingsDocument"/>): Ctrl+F goes to the search, Esc there ends it; Enter keeps a text.</summary>
public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => SearchBox.Focus();
        Pages.SelectionChanged += (_, _) => Scroller.Offset = default;
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.F && PlatformKeys.HasCommand(e.KeyModifiers))
            {
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && SearchBox.IsFocused && DataContext is SettingsDocument { Search.Length: > 0 } settings)
            {
                settings.Search = "";
                e.Handled = true;
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    /// <summary>Enter in a text of one line keeps it, as leaving it does.</summary>
    void OnTextKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox { AcceptsReturn: false } box)
        {
            Avalonia.Data.BindingOperations.GetBindingExpressionBase(box, TextBox.TextProperty)?.UpdateSource();
            e.Handled = true;
        }
    }
}
