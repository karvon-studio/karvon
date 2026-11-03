using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Karvon.Ui;

namespace Karvon.Browser.ViewModels;

/// <summary>
/// The Browser tool window: Karvon's Chrome started and stopped, an address opened in it, its tabs (a click brings one to
/// the front), and the sign-ins Karvon keeps for Claude's browser_login.
/// </summary>
public sealed partial class BrowserViewModel : ObservableObject, IDisposable
{
    readonly ManagedBrowser _browser;
    readonly BrowserLogins _logins;
    readonly Func<string?> _chrome;

    public BrowserViewModel(ManagedBrowser browser, BrowserLogins logins, Func<string?> chrome)
    {
        _browser = browser;
        _logins = logins;
        _chrome = chrome;
        _browser.Changed += OnChanged;
        _logins.Changed += OnChanged;
        Refresh();
    }

    public ObservableCollection<BrowserTab> Tabs { get; } = [];

    public ObservableCollection<BrowserLogin> Logins { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial bool IsRunning { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial string? Error { get; private set; }

    [ObservableProperty]
    public partial string Address { get; set; } = "";

    [ObservableProperty]
    public partial bool IsStarting { get; private set; }

    public bool HasLogins => Logins.Count > 0;

    public bool CanKeepLogins => _logins.CanKeep;

    public string ProfileText => $"Karvon's profile: {_browser.Profile}";

    public string StatusText => Error ?? (IsRunning ? $"Running · {Tabs.Count} tab(s)"
        : _chrome() is { } chrome ? $"Not running · {Path.GetFileName(chrome)}" : "No Chrome is installed (nor Chromium, nor Edge)");

    void OnChanged() => Dispatcher.UIThread.Post(Refresh);

    void Refresh()
    {
        IsRunning = _browser.IsRunning;
        Tabs.Clear();
        foreach (var tab in _browser.Tabs)
            Tabs.Add(tab);
        Logins.Clear();
        foreach (var login in _logins.All.OrderBy(l => l.Host).ThenBy(l => l.Username))
            Logins.Add(login);
        OnPropertyChanged(nameof(HasLogins));
        OnPropertyChanged(nameof(StatusText));
    }

    /// <summary>The Chrome Karvon started before, found again (none started).</summary>
    public async Task FindRunningAsync()
    {
        try
        {
            await _browser.FindRunningAsync(default);
        }
        catch (InvalidOperationException)
        {
        }
        Refresh();
    }

    async Task RunAsync(Func<Task> action)
    {
        Error = null;
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            Error = ex.Message;
        }
        Refresh();
    }

    [RelayCommand]
    async Task StartAsync()
    {
        IsStarting = true;
        try
        {
            await RunAsync(() => _browser.StartAsync(default));
        }
        finally
        {
            IsStarting = false;
        }
    }

    [RelayCommand]
    Task StopAsync() => RunAsync(_browser.StopAsync);

    /// <summary>The address in the current tab (Chrome started first when it is not running).</summary>
    [RelayCommand]
    Task GoAsync() => RunAsync(async () =>
    {
        var url = Address.Trim();
        if (url.Length == 0)
            return;
        if (!url.Contains("://", StringComparison.Ordinal))
            url = (url.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) ? "http://" : "https://") + url;
        await _browser.StartAsync(default);
        await _browser.NavigateAsync(url, default);
    });

    [RelayCommand]
    Task ShowTabAsync(BrowserTab tab) => RunAsync(() => _browser.SelectTabAsync(tab.Id, default));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDialog))]
    public partial OverlayDialog? Dialog { get; set; }

    public bool HasDialog => Dialog is not null;

    async Task<T?> ShowAsync<T>(T dialog) where T : OverlayDialog
    {
        while (Dialog is { } open)
            await open.Result;
        Dialog = dialog;
        try
        {
            return await dialog.Result ? dialog : null;
        }
        finally
        {
            Dialog = null;
        }
    }

    /// <summary>A sign-in kept: the current tab's site offered.</summary>
    [RelayCommand]
    async Task AddLoginAsync()
    {
        var site = _browser.Current is { } tab && ManagedBrowser.OriginOf(tab.Url) is { } origin ? origin : "";
        if (await ShowAsync(new AddLoginDialog(site)) is not { } form)
            return;
        await RunAsync(() =>
        {
            _logins.Save(form.Site, form.Username, form.Password);
            return Task.CompletedTask;
        });
    }

    [RelayCommand]
    void RemoveLogin(BrowserLogin login)
    {
        _logins.Remove(login);
        Refresh();
    }

    public void Dispose()
    {
        _browser.Changed -= OnChanged;
        _logins.Changed -= OnChanged;
    }
}

/// <summary>Add Sign-in: a site, the name and the password Karvon fills in for Claude (the password into the vault).</summary>
public sealed partial class AddLoginDialog(string site) : OverlayDialog("Add Sign-in", "Save")
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problem), nameof(CanConfirm))]
    public partial string Site { get; set; } = site;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problem), nameof(CanConfirm))]
    public partial string Username { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problem), nameof(CanConfirm))]
    public partial string Password { get; set; } = "";

    public string? Problem =>
        Site.Trim().Length > 0 && BrowserLogins.OriginOf(Site) is null ? "The site: its address (https://example.com)."
        : null;

    public override bool CanConfirm => Problem is null && BrowserLogins.OriginOf(Site) is not null && Username.Trim().Length > 0 && Password.Length > 0;
}
