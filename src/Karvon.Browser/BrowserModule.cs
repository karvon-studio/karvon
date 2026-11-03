using Bengine.Introspection.Servers;
using Karvon.Browser.ViewModels;
using Karvon.Browser.Views;
using Karvon.Claude;
using Karvon.Modules;

namespace Karvon.Browser;

/// <summary>
/// Browser: Karvon's own Chrome (the installed one, with a profile of Karvon's that keeps its cookies, sign-ins and the
/// passwords Chrome's manager saves), in its own window, which Claude Code drives with its tools (<see cref="BrowserClaudeTools"/>);
/// its tool window starts and stops it, shows its tabs, and keeps the sign-ins Karvon fills in for Claude.
/// </summary>
public sealed class BrowserModule : KarvonModule
{
    public const string ToolWindowId = "Browser";

    ManagedBrowser? _browser;
    BrowserLogins? _logins;
    BrowserViewModel? _view;

    /// <summary>How the browser is made (the tests' headless one, in a folder of their own).</summary>
    internal Func<ManagedBrowser>? CreateBrowser { get; set; }

    internal ICredentialStore? Keyring { get; set; } = Karvon.Services.Keyring.System;

    internal string LoginsFile { get; set; } = Path.Combine(Karvon.Services.Settings.ConfigDirectory, "browser-logins.json");

    /// <summary>Karvon's profile of Chrome.</summary>
    public static string ProfileFolder => Path.Combine(ModuleComponent.DataRoot, "browser", "profile");

    public ManagedBrowser Browser => _browser ??= CreateBrowser?.Invoke() ?? new ManagedBrowser(ProfileFolder, ChromeLocator.Find);

    public BrowserLogins Logins => _logins ??= new BrowserLogins(LoginsFile, Keyring);

    public override string Id => "browser";

    public override string Name => "Browser";

    public override string Icon => "chrome";

    public override string Description =>
        "Karvon's own Chrome, in its own window, which Claude Code drives with its tools: pages opened, read as an outline, clicked, "
        + "typed into, their console and network, pictures of them; a sign-in Karvon keeps filled in without Claude seeing the "
        + "password. Its profile keeps its cookies and the passwords Chrome saves.";

    public override IReadOnlyList<ToolWindowInfo> ToolWindows =>
        [new(ToolWindowId, "Browser", "_Browser", "Globe", DockSide.Bottom, () => new BrowserView { DataContext = EnsureView() })];

    public override Avalonia.Controls.Templates.IDataTemplate OverlayTemplate => Template<BrowserViewModel>(_ => new BrowserOverlay());

    public BrowserViewModel EnsureView()
    {
        if (_view is null)
        {
            Overlay = _view = new BrowserViewModel(Browser, Logins, ChromeLocator.Find);
            _ = _view.FindRunningAsync();
        }
        return _view;
    }

    public override IEnumerable<IClaudeTool> ClaudeTools() => BrowserClaudeTools.Create(async ct =>
    {
        // The Chrome Karvon started before (it stays open when Karvon closes) is found again without starting another.
        if (!Browser.IsRunning)
            await Browser.FindRunningAsync(ct);
        return Browser;
    }, Logins);

    /// <summary>Karvon lets go of Chrome, which stays open.</summary>
    public override void Dispose()
    {
        _view?.Dispose();
        if (_browser is not null)
            _ = _browser.DisposeAsync().AsTask();
        base.Dispose();
    }
}
