using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Karvon.Claude.Remote;

namespace Karvon.Views;

/// <summary>
/// Telegram's remote control set up in three steps: the bot's token (checked with Telegram), a group with topics where the
/// bot is an administrator, and a code sent there, which pairs the group and the person who sent it. It shows how far it
/// got as it goes, and what is wrong in words.
/// </summary>
public sealed class TelegramSetupWindow : Window
{
    readonly TelegramRemote _remote;
    readonly TextBox _token;
    readonly Button _check;
    readonly TextBlock _tokenState;
    readonly TextBlock _groupText;
    readonly StackPanel _groupStep;
    readonly StackPanel _pairStep;
    readonly TextBox _code;
    readonly Button _copy;
    readonly Button _again;
    readonly TextBlock _pairState;
    readonly Border _session;
    readonly VsIcon _eye;

    static readonly IBrush Good = new SolidColorBrush(Color.Parse("#89D185"));
    static readonly IBrush Bad = new SolidColorBrush(Color.Parse("#F48771"));

    public TelegramSetupWindow(TelegramRemote remote)
    {
        _remote = remote;
        Title = "Telegram Remote Control";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("VsEnvironmentBrush", "#2D2D30");
        Foreground = Brush("VsTextBrush", "#F1F1F1");
        var muted = Brush("VsGrayTextBrush", "#9D9D9D");

        _token = new TextBox { Name = "Token", PasswordChar = '•', Watermark = "123456789:AA…" };
        _eye = new VsIcon { Kind = "Eye", Width = 14, Height = 14 };
        var show = new Button { Name = "ShowToken", Content = _eye, Padding = new Thickness(8, 0), VerticalAlignment = VerticalAlignment.Stretch };
        ToolTip.SetTip(show, "Show the token");
        show.Click += (_, _) =>
        {
            var shown = _token.PasswordChar != '\0';
            _token.PasswordChar = shown ? '\0' : '•';
            _eye.Kind = shown ? "EyeOff" : "Eye";
        };
        _check = new Button { Name = "Check", Content = "Check", MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        _check.Click += async (_, _) => await CheckAsync();
        _token.KeyDown += async (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                e.Handled = true;
                await CheckAsync();
            }
        };
        _tokenState = new TextBlock { Name = "TokenState", FontSize = 12, TextWrapping = TextWrapping.Wrap };

        _groupText = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = muted };
        _groupStep = new StackPanel { Spacing = 6, Children = { Heading("2. A group with topics"), _groupText } };

        _code = new TextBox { Name = "Code", IsReadOnly = true, FontFamily = new FontFamily("monospace"), FontSize = 15 };
        _copy = new Button { Name = "Copy", Content = "Copy", MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        _copy.Click += async (_, _) =>
        {
            if (Clipboard is { } clipboard && _code.Text is { Length: > 0 } text)
            {
                await clipboard.SetTextAsync(text);
                _copy.Content = "Copied";
            }
        };
        _again = new Button { Name = "PairAgain", Content = "Pair with another group", IsVisible = false };
        _again.Click += (_, _) =>
        {
            _remote.BeginPairing();
            Refresh();
        };
        _pairState = new TextBlock { Name = "PairState", FontSize = 12, TextWrapping = TextWrapping.Wrap };
        _pairStep = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                Heading("3. Pair it"),
                new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = muted, Text = "Send this in the group. Whoever sends it there is the one Karvon hears; no one else, and nowhere else." },
                new DockPanel { Children = { Docked(_copy, Dock.Right), _code } },
                _pairState,
                _again,
            },
        };
        _session = new Border
        {
            Name = "ForSession",
            Background = new SolidColorBrush(Color.Parse("#3A2A1E")),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 8),
            Child = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = "The vault has no passphrase: the token and the pairing are kept until Karvon closes. Set one (Tools > Vault > Set Passphrase) to keep them.",
            },
        };
        var close = new Button { Name = "Close", Content = "Close", IsCancel = true, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 14,
            Children =
            {
                new StackPanel
                {
                    Spacing = 6,
                    Children =
                    {
                        new TextBlock { Text = "Remote control over Telegram", FontSize = 17, FontWeight = FontWeight.SemiBold },
                        new TextBlock
                        {
                            TextWrapping = TextWrapping.Wrap, Foreground = muted,
                            Text = "Your conversations in a Telegram group of yours: each in a topic of its own, what it does as it goes, its questions "
                                + "with Allow and Deny, its answer when it ends. A message in its topic is a prompt for it; one in General begins a new "
                                + "conversation. The token is kept in the vault's sealed part, which no agent reaches.",
                        },
                    },
                },
                new StackPanel
                {
                    Spacing = 6,
                    Children =
                    {
                        Heading("1. A bot"),
                        new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = muted, Text = "In Telegram, send /newbot to @BotFather and paste the token it gives." },
                        new DockPanel { Children = { Docked(_check, Dock.Right), Docked(show, Dock.Right), _token } },
                        _tokenState,
                    },
                },
                _groupStep,
                _pairStep,
                _session,
                close,
            },
        };
        _remote.Changed += OnChanged;
        Closed += (_, _) => _remote.Changed -= OnChanged;
        Refresh();
        Opened += (_, _) => Dispatcher.UIThread.Post(() => (_remote.Token is null ? _token : (Control)close).Focus());
    }

    void OnChanged() => Dispatcher.UIThread.Post(Refresh);

    async Task CheckAsync()
    {
        if ((_token.Text ?? "").Trim() is not { Length: > 0 } token)
            return;
        _check.IsEnabled = false;
        _tokenState.Text = "Asking Telegram…";
        _tokenState.Foreground = Brush("VsGrayTextBrush", "#9D9D9D");
        var problem = await _remote.SetTokenAsync(token);
        _check.IsEnabled = true;
        if (problem is not null)
        {
            _tokenState.Text = problem;
            _tokenState.Foreground = Bad;
            return;
        }
        _token.Text = "";
        _remote.BeginPairing();
        Refresh();
    }

    /// <summary>Each step as far as it got.</summary>
    void Refresh()
    {
        var hasToken = _remote.Token is not null;
        var pairing = _remote.Pairing;
        if (hasToken && _tokenState.Foreground != Bad)
        {
            _tokenState.Text = "✓ The bot is set up" + (_remote.BotName is { } name ? $": {name}" : "") + ". Paste another token to change it.";
            _tokenState.Foreground = Good;
        }
        _groupStep.IsEnabled = _pairStep.IsEnabled = hasToken;
        _groupStep.Opacity = _pairStep.Opacity = hasToken ? 1 : 0.5;
        var bot = _remote.BotName ?? "your bot";
        _groupText.Text = $"Create a group (or use one of yours), turn Topics on in its settings, and add {bot} to it as an administrator who may manage topics.";
        if (pairing is not null)
        {
            _code.Text = "";
            _code.IsVisible = _copy.IsVisible = false;
            _pairState.Text = $"✓ Paired with “{pairing.ChatTitle}”: Karvon hears {pairing.UserName} alone there.";
            _pairState.Foreground = Good;
            _again.IsVisible = true;
        }
        else
        {
            var code = hasToken ? _remote.PairingCode ?? _remote.BeginPairing() : null;
            _code.Text = code is null ? "" : "/pair " + code;
            _code.IsVisible = _copy.IsVisible = true;
            _pairState.Text = !hasToken ? "" : _remote.State.StartsWith("Waiting", StringComparison.Ordinal) ? "Waiting for it to be sent… (the code is good for 15 minutes)" : _remote.State;
            _pairState.Foreground = Brush("VsGrayTextBrush", "#9D9D9D");
            _again.IsVisible = false;
        }
        _session.IsVisible = hasToken && _remote.IsForSession;
    }

    static TextBlock Heading(string text) => new() { Text = text, FontWeight = FontWeight.SemiBold };

    static Control Docked(Control control, Dock dock)
    {
        DockPanel.SetDock(control, dock);
        if (dock == Dock.Right)
            control.Margin = new Thickness(6, 0, 0, 0);
        return control;
    }

    static IBrush Brush(string resource, string fallback) =>
        Application.Current?.TryFindResource(resource, out var found) == true && found is IBrush brush ? brush : new SolidColorBrush(Color.Parse(fallback));
}
