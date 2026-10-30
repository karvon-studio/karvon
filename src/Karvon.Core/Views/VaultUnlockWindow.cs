using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Karvon.Services;

namespace Karvon.Views;

/// <summary>
/// What Karvon Studio shows first when its vault has a passphrase: the passphrase, before any module reads a secret.
/// Not now goes on with the vault locked (each sign-in it keeps is asked for this time); a passphrase forgotten takes what
/// the vault kept with it, once the user says so.
/// </summary>
public sealed class VaultUnlockWindow : Window
{
    readonly SecretVault _vault;
    readonly TextBox _passphrase;
    readonly TextBlock _error;
    readonly Button _unlock;
    readonly StackPanel _ask;
    readonly StackPanel _forget;
    bool _done;

    /// <summary>Unlocked, or left locked: Karvon goes on.</summary>
    public event Action? Finished;

    public VaultUnlockWindow(SecretVault vault)
    {
        _vault = vault;
        Title = "Karvon Studio";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brush("VsEnvironmentBrush", "#2D2D30");
        Foreground = Brush("VsTextBrush", "#F1F1F1");

        _passphrase = new TextBox { Name = "Passphrase", PasswordChar = '•', Watermark = "Passphrase" };
        _error = new TextBlock { Name = "Error", Foreground = new SolidColorBrush(Color.Parse("#F48771")), TextWrapping = TextWrapping.Wrap, IsVisible = false };
        _unlock = new Button { Name = "Unlock", Content = "Unlock", IsDefault = true, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        var later = new Button { Name = "Later", Content = "Not now", MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        var forgot = new Button { Name = "Forgot", Content = "Forgot it…", Background = Brushes.Transparent, Foreground = new SolidColorBrush(Color.Parse("#4DAAFC")), Padding = new Thickness(0) };
        _unlock.Click += async (_, _) => await UnlockAsync();
        later.Click += (_, _) => Finish();
        forgot.Click += (_, _) => ShowForget(true);

        _ask = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "Unlock Karvon's vault", FontSize = 17, FontWeight = FontWeight.SemiBold },
                new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap, Foreground = Brush("VsGrayTextBrush", "#9D9D9D"),
                    Text = "Its passphrase opens the sign-ins Karvon keeps for you (Jira, the data sources, the servers, the tunnels, GitLab). "
                        + "Not now: Karvon starts with the vault locked, and asks for each of them this time.",
                },
                _passphrase,
                _error,
                new DockPanel
                {
                    LastChildFill = false,
                    Children = { forgot, Right(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { later, _unlock } }) },
                },
            },
        };
        var forgetButton = new Button { Name = "ForgetVault", Content = "Forget the vault", MinWidth = 90, Background = new SolidColorBrush(Color.Parse("#A1260D")) };
        var back = new Button { Name = "Back", Content = "Back", MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        forgetButton.Click += (_, _) => ForgetVault();
        back.Click += (_, _) => ShowForget(false);
        _forget = new StackPanel
        {
            Spacing = 10,
            IsVisible = false,
            Children =
            {
                new TextBlock { Text = "Forget the vault?", FontSize = 17, FontWeight = FontWeight.SemiBold },
                new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = "Without its passphrase nothing in it can be opened: what it kept goes, and you sign in to each service again. "
                        + "Its new key is kept in the system keyring, as before a passphrase was set.",
                },
                Right(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { back, forgetButton } }),
            },
        };
        Content = new StackPanel { Margin = new Thickness(20), Children = { _ask, _forget } };
        Opened += (_, _) => Dispatcher.UIThread.Post(() => _passphrase.Focus());
        // Closed without a word: as Not now.
        Closing += (_, _) => Finish();
    }

    /// <summary>Whether the vault was unlocked (else Karvon goes on with it locked).</summary>
    public bool Unlocked => !_vault.IsLocked;

    async Task UnlockAsync()
    {
        var passphrase = _passphrase.Text ?? "";
        if (passphrase.Length == 0)
            return;
        _unlock.IsEnabled = false;
        // The key drawn from the passphrase takes a moment (that is what makes guessing it slow).
        var opened = await Task.Run(() => _vault.Unlock(passphrase));
        _unlock.IsEnabled = true;
        if (opened)
        {
            Finish();
            return;
        }
        _error.Text = "That is not the vault's passphrase.";
        _error.IsVisible = true;
        _passphrase.SelectAll();
        _passphrase.Focus();
    }

    void ShowForget(bool forget)
    {
        _ask.IsVisible = !forget;
        _forget.IsVisible = forget;
    }

    void ForgetVault()
    {
        if (!_vault.Forget())
        {
            ShowForget(false);
            _error.Text = "The system keyring could not keep the vault's new key: the vault stays locked.";
            _error.IsVisible = true;
            return;
        }
        Finish();
    }

    void Finish()
    {
        if (_done)
            return;
        _done = true;
        Finished?.Invoke();
    }

    static Control Right(Control control)
    {
        DockPanel.SetDock(control, Dock.Right);
        return control;
    }

    IBrush Brush(string resource, string fallback) =>
        Application.Current?.TryFindResource(resource, out var found) == true && found is IBrush brush ? brush : new SolidColorBrush(Color.Parse(fallback));
}
