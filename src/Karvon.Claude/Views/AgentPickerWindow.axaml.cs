using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Karvon.ViewModels;

namespace Karvon.Views;

/// <summary>Choose Agent and Model (<see cref="AgentPickerViewModel"/>): closes when the agent is chosen or the user cancels.</summary>
public partial class AgentPickerWindow : Window
{
    /// <summary>An agent's status: green when it is there.</summary>
    public static readonly IValueConverter StatusBrush =
        new FuncValueConverter<bool, IBrush>(ready => ready ? new SolidColorBrush(Color.Parse("#89D185")) : new SolidColorBrush(Color.Parse("#A0A0A0")));

    public AgentPickerWindow()
    {
        InitializeComponent();
        Opened += (_, _) => this.FindControl<TextBox>("SearchBox")?.Focus();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is AgentPickerViewModel picker)
            picker.CloseRequested += Close;
    }

    void OnModelDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is AgentPickerViewModel picker && picker.UseCommand.CanExecute(null))
            picker.UseCommand.Execute(null);
    }

    void OnLookAgain(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AgentPickerViewModel { SelectedProvider: { } provider } picker)
        {
            provider.Refresh();
            picker.UseCommand.NotifyCanExecuteChanged();
            picker.SelectedProvider = null;
            picker.SelectedProvider = provider;
        }
    }
}
