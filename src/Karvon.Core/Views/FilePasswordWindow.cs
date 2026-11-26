using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Karvon.Views;

/// <summary>
/// The password of a password-protected file (an Excel workbook, a Word document) that Karvon's document engines open: shown
/// while its eye is on, why the last one did not open it said above it. It goes to the engine in Karvon's process alone,
/// never to Claude or another agent, which only learn the file opened (or did not). Its result is the password, or null
/// when cancelled.
/// </summary>
public sealed class FilePasswordWindow : Window
{
    readonly TextBox _password;
    readonly Button _open;
    readonly VsIcon _eye;

    public FilePasswordWindow(string path, string? problem)
    {
        Title = "Password";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("VsEnvironmentBrush", "#2D2D30");
        Foreground = Brush("VsTextBrush", "#F1F1F1");
        var muted = Brush("VsGrayTextBrush", "#9D9D9D");

        _password = new TextBox { Name = "Password", PasswordChar = '•', PlaceholderText = "Password" };
        _eye = new VsIcon { Kind = "Eye", Width = 14, Height = 14 };
        var show = new Button { Name = "Show", Content = _eye, Padding = new Thickness(8, 0), VerticalAlignment = VerticalAlignment.Stretch };
        ToolTip.SetTip(show, "Show the password");
        show.Click += (_, _) =>
        {
            var shown = _password.PasswordChar != '\0';
            _password.PasswordChar = shown ? '\0' : '•';
            _eye.Kind = shown ? "EyeOff" : "Eye";
        };
        _open = new Button { Name = "Open", Content = "Open", IsDefault = true, IsEnabled = false, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Name = "Cancel", Content = "Cancel", IsCancel = true, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        _open.Click += (_, _) =>
        {
            if (_password.Text is { Length: > 0 } password)
                Close(password);
        };
        cancel.Click += (_, _) => Close(null);
        _password.TextChanged += (_, _) => _open.IsEnabled = _password.Text is { Length: > 0 };

        var name = System.IO.Path.GetFileName(path);
        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = $"{name} is password-protected", FontSize = 17, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = System.IO.Path.GetDirectoryName(path) ?? "", Foreground = muted, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap, Foreground = muted,
                    Text = "Its password opens it in Karvon's document engine. Claude and the other agents never see it: they are told only whether it opened.",
                },
                new TextBlock
                {
                    Name = "Problem", Text = problem ?? "", IsVisible = problem is not null, TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.Parse("#F48771")),
                },
                new DockPanel { Children = { Docked(show, Dock.Right), _password } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, _open } },
            },
        };
        Opened += (_, _) => Dispatcher.UIThread.Post(() => _password.Focus());
    }

    static Control Docked(Control control, Dock dock)
    {
        DockPanel.SetDock(control, dock);
        return control;
    }

    static IBrush Brush(string resource, string fallback) =>
        Application.Current?.TryFindResource(resource, out var found) == true && found is IBrush brush ? brush : new SolidColorBrush(Color.Parse(fallback));
}
