using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;

namespace VSL.Views;

/// <summary>
/// A tool window (Solution Explorer, Output, Error List...) floated out of the main window: its tab in a window of its
/// own, as in Visual Studio. Closing the window docks the tool window back where it came from.
/// </summary>
public sealed class FloatingToolWindow : Window
{
    readonly MainWindow _shell;
    bool _closing;

    public FloatingToolWindow(MainWindow shell)
    {
        _shell = shell;
        DataContext = shell.DataContext;
        Icon = shell.Icon;
        Width = 420;
        Height = 560;
        Background = shell.FindResource("VsToolWindowBrush") as Avalonia.Media.IBrush;
        Foreground = shell.FindResource("VsTextBrush") as Avalonia.Media.IBrush;
        Tabs = new TabControl
        {
            TabStripPlacement = Dock.Bottom,
            Padding = new Thickness(0),
            ItemContainerTheme = shell.FindResource("VsToolTab") as ControlTheme,
        };
        Tabs.SelectionChanged += (_, _) => UpdateTitle();
        Content = Tabs;
        AddHandler(KeyDownEvent, (_, e) => shell.HandleKeyFromFloatingWindow(e), RoutingStrategies.Tunnel);
    }

    public TabControl Tabs { get; }

    internal void UpdateTitle() => Title = Tabs.SelectedItem is TabItem { Header: string header } ? header : "VSL";

    /// <summary>Its last tool window went back into the main window: the window goes without docking anything.</summary>
    internal void CloseEmpty()
    {
        _closing = true;
        Close();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        // With the main window it just closes (and floats again next time): cancelling would keep the main window open.
        if (_closing || e.CloseReason is WindowCloseReason.OwnerWindowClosing or WindowCloseReason.ApplicationShutdown)
            return;
        // Closing a floating tool window puts it back in the main window rather than losing it.
        e.Cancel = true;
        _shell.DockFloatingTools(this);
    }
}
