using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Karvon.Services;

namespace Karvon.Views;

/// <summary>
/// A passphrase for Karvon's vault, set or changed: typed twice, shown while its eye is on, checked as it is typed (Set is
/// enabled once it is long enough and the two are the same), and what forgetting it costs said before it is set. Its
/// result is the passphrase, or null when cancelled.
/// </summary>
public sealed class VaultPassphraseWindow : Window
{
    readonly TextBox _first;
    readonly TextBox _again;
    readonly TextBlock _length;
    readonly TextBlock _same;
    readonly Button _set;
    readonly VsIcon _eye;

    static readonly IBrush Good = new SolidColorBrush(Color.Parse("#89D185"));
    static readonly IBrush Bad = new SolidColorBrush(Color.Parse("#F48771"));

    public VaultPassphraseWindow(bool change)
    {
        Title = change ? "Change the Vault's Passphrase" : "Vault Passphrase";
        Width = 500;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("VsEnvironmentBrush", "#2D2D30");
        Foreground = Brush("VsTextBrush", "#F1F1F1");
        var muted = Brush("VsGrayTextBrush", "#9D9D9D");

        _first = new TextBox { Name = "Passphrase", PasswordChar = '•', Watermark = "Passphrase" };
        _again = new TextBox { Name = "Again", PasswordChar = '•', Watermark = "The passphrase again" };
        _eye = new VsIcon { Kind = "Eye", Width = 14, Height = 14 };
        var show = new Button { Name = "Show", Content = _eye, Padding = new Thickness(8, 0), VerticalAlignment = VerticalAlignment.Stretch };
        ToolTip.SetTip(show, "Show the passphrase");
        show.Click += (_, _) => Show(_first.PasswordChar != '\0');
        _length = new TextBlock { Name = "Length", FontSize = 12 };
        _same = new TextBlock { Name = "Same", FontSize = 12 };
        _set = new Button { Name = "Set", Content = change ? "Change Passphrase" : "Set Passphrase", IsDefault = true, IsEnabled = false, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Name = "Cancel", Content = "Cancel", IsCancel = true, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        _set.Click += (_, _) =>
        {
            if (Problem(_first.Text ?? "", _again.Text ?? "") is null)
                Close(_first.Text);
        };
        cancel.Click += (_, _) => Close(null);
        _first.TextChanged += (_, _) => Check();
        _again.TextChanged += (_, _) => Check();
        // Enter in the first goes on to the second.
        _first.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                _again.Focus();
                e.Handled = true;
            }
        };

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = change ? "Change the vault's passphrase" : "A passphrase for Karvon's vault", FontSize = 17, FontWeight = FontWeight.SemiBold },
                new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap, Foreground = muted,
                    Text = "Karvon Studio asks for it each time it starts. The vault's key is then kept by it alone, not in the system keyring, "
                        + "where any program you run can ask for it; and the values of Postman's environments are kept from one session to the next.",
                },
                new DockPanel { Children = { Docked(show, Dock.Right), _first } },
                _again,
                new StackPanel { Spacing = 2, Children = { _length, _same } },
                new Border
                {
                    Name = "Forgotten",
                    Background = new SolidColorBrush(Color.Parse("#3A2A1E")),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(12, 8),
                    Child = new TextBlock
                    {
                        TextWrapping = TextWrapping.Wrap,
                        Text = "Forgotten, it cannot be recovered or reset, by Karvon or anyone. You could only forget the vault as Karvon starts: "
                            + "you sign in again to what it kept (Jira, the data sources, the servers, GitLab), and the values of Postman's "
                            + "environments are lost. Keep it in a password manager.",
                    },
                },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, _set } },
            },
        };
        Check();
        Opened += (_, _) => Dispatcher.UIThread.Post(() => _first.Focus());
    }

    /// <summary>What keeps <paramref name="first"/> from being the passphrase, typed again as <paramref name="again"/>; null when nothing does.</summary>
    public static string? Problem(string first, string again) =>
        first.Length < SecretVault.MinPassphrase ? $"{SecretVault.MinPassphrase} characters at least: {first.Length} so far"
        : again != first ? "The two are not the same"
        : null;

    void Check()
    {
        var first = _first.Text ?? "";
        var again = _again.Text ?? "";
        var longEnough = first.Length >= SecretVault.MinPassphrase;
        _length.Text = longEnough ? $"✓ {SecretVault.MinPassphrase} characters at least" : $"{SecretVault.MinPassphrase} characters at least: {first.Length} so far";
        _length.Foreground = longEnough ? Good : first.Length > 0 ? Bad : Brush("VsGrayTextBrush", "#9D9D9D");
        var same = again.Length > 0 && again == first;
        _same.Text = again.Length == 0 ? "Typed again below, the same" : same ? "✓ The two are the same" : "The two are not the same";
        _same.Foreground = same ? Good : again.Length > 0 ? Bad : Brush("VsGrayTextBrush", "#9D9D9D");
        _set.IsEnabled = Problem(first, again) is null;
    }

    /// <summary>Both shown as typed, or hidden.</summary>
    void Show(bool shown)
    {
        _first.PasswordChar = _again.PasswordChar = shown ? '\0' : '•';
        _eye.Kind = shown ? "EyeOff" : "Eye";
    }

    static Control Docked(Control control, Dock dock)
    {
        DockPanel.SetDock(control, dock);
        return control;
    }

    static IBrush Brush(string resource, string fallback) =>
        Application.Current?.TryFindResource(resource, out var found) == true && found is IBrush brush ? brush : new SolidColorBrush(Color.Parse(fallback));
}
