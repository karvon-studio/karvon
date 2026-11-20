using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Karvon.Bek.ViewModels;

/// <summary>What the preview needs of Karvon.</summary>
public interface IPreviewHost
{
    /// <summary>A line bek-preview said, for the module's pane of the Output window.</summary>
    void Said(string line);

    void OpenUrl(string url);

    void Status(string text);

    /// <summary>An app's component the user picks; null when they cancel.</summary>
    Task<string?> PickComponentAsync();
}

/// <summary>
/// The Bek Preview tool window: an app of the phone's run by bek-preview, its screen live (taps, scrolling, typing and Back
/// go to it), its log, and how it is shown (theme, language, size). Its elements, styles and store are in bek-preview's own
/// page, opened in the browser.
/// </summary>
public sealed partial class PreviewViewModel(IPreviewHost host) : ObservableObject, IAsyncDisposable
{
    /// <summary>The log lines the window keeps.</summary>
    const int LogKept = 500;

    public static IReadOnlyList<string> Locales { get; } = ["en-US", "uz-UZ", "ru-RU"];

    public static IReadOnlyList<string> Sizes { get; } = ["536x984", "412x868", "360x740"];

    /// <summary>One action at a time, the user's and Claude's, in the order they were made: typing stays in order.</summary>
    readonly SemaphoreSlim _acting = new(1, 1);

    /// <summary>One app opened, or stopped, at a time: one asked for while another starts takes its place.</summary>
    readonly SemaphoreSlim _opening = new(1, 1);

    PreviewProcess? _preview;
    CancellationTokenSource? _following;

    /// <summary>The start of the app on its way, which a newer one, or a stop, cancels.</summary>
    CancellationTokenSource? _starting;

    /// <summary>The settings being shown from bek-preview's state, not chosen by the user.</summary>
    bool _showing;

    /// <summary>Scrolling gathered while the last of it is on its way.</summary>
    (double X, double Y, double Dx, double Dy) _scrolling;
    bool _scrollSending;

    public IPreviewHost Host => host;

    /// <summary>bek-preview's program: on the PATH or in cargo's bin folder (the tests give their own).</summary>
    internal Func<string?> FindProgram { get; init; } = () => PreviewProcess.Find();

    /// <summary>The API of the app shown; null when none is.</summary>
    public PreviewClient? Client => _preview?.Client;

    /// <summary>bek-preview's last state; null when no app is shown.</summary>
    public PreviewState? State { get; private set; }

    public ObservableCollection<string> Log { get; } = [];

    [ObservableProperty]
    public partial Bitmap? Screen { get; private set; }

    [ObservableProperty]
    public partial string Title { get; private set; } = "Bek Preview";

    [ObservableProperty]
    public partial string Timing { get; private set; } = "";

    /// <summary>Why the app is not running, over its last screen.</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReloadCommand), nameof(ResetCommand), nameof(BackCommand), nameof(StopCommand), nameof(OpenInBrowserCommand))]
    public partial bool HasApp { get; private set; }

    [ObservableProperty]
    public partial string Message { get; private set; } =
        "No app is shown. Pick its component (a .wasm) with +, or right-click it or its folder in Solution Explorer > Preview on the Phone.";

    [ObservableProperty]
    public partial bool Dark { get; set; }

    [ObservableProperty]
    public partial string Locale { get; set; } = "en-US";

    [ObservableProperty]
    public partial string Size { get; set; } = "536x984";

    /// <summary>Shows <paramref name="component"/> (its manifest beside it, or <paramref name="manifest"/>), in place of the app shown.</summary>
    /// <exception cref="InvalidOperationException">
    /// bek-preview is not installed, ended at once or did not answer; or another app was asked for, or the preview stopped, before this one showed.
    /// </exception>
    public async Task OpenAsync(string component, string? manifest, CancellationToken cancellationToken = default)
    {
        var program = FindProgram() ?? throw new InvalidOperationException($"bek-preview is not installed: {PreviewProcess.Install}");
        // An app still on its way gives way to this one: its bek-preview ends before this one starts.
        _starting?.Cancel();
        await _opening.WaitAsync(cancellationToken);
        using var starting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _starting = starting;
        try
        {
            await StopShownAsync();
            Message = $"Starting {Path.GetFileName(component)}…";
            var preview = await PreviewProcess.StartAsync(program, component, manifest, host.Said, starting.Token);
            _preview = preview;
            HasApp = true;
            _following = new CancellationTokenSource();
            _ = FollowAsync(preview, _following.Token);
        }
        catch (OperationCanceledException) when (starting.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException($"{Path.GetFileName(component)} was not shown: another app was opened in its place, or the preview was stopped.");
        }
        catch (OperationCanceledException)
        {
            Message = "The preview was stopped.";
            throw;
        }
        catch (InvalidOperationException ex)
        {
            Message = ex.Message;
            throw;
        }
        finally
        {
            if (_starting == starting)
                _starting = null;
            _opening.Release();
        }
    }

    [RelayCommand]
    async Task Open()
    {
        if (await host.PickComponentAsync() is not { } component)
            return;
        try
        {
            await OpenAsync(component, null);
        }
        catch (InvalidOperationException ex)
        {
            host.Status(ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(HasApp))]
    Task Reload() => ActAsync(new PreviewAction.Reload());

    [RelayCommand(CanExecute = nameof(HasApp))]
    Task Reset() => ActAsync(new PreviewAction.Reset());

    [RelayCommand(CanExecute = nameof(HasApp))]
    Task Back() => ActAsync(new PreviewAction.Back());

    [RelayCommand(CanExecute = nameof(HasApp))]
    void OpenInBrowser()
    {
        if (Client is { } client)
            host.OpenUrl(client.Address.ToString());
    }

    /// <summary>The app shown stops, and one on its way: their bek-preview has ended when this returns.</summary>
    [RelayCommand(CanExecute = nameof(HasApp))]
    public async Task StopAsync()
    {
        _starting?.Cancel();
        await _opening.WaitAsync();
        try
        {
            await StopShownAsync();
            Message = "The preview was stopped.";
        }
        finally
        {
            _opening.Release();
        }
    }

    async Task StopShownAsync()
    {
        _following?.Cancel();
        _following = null;
        if (_preview is { } preview)
        {
            _preview = null;
            await preview.DisposeAsync();
        }
        State = null;
        HasApp = false;
        ShowScreen(null);
        Error = null;
        Title = "Bek Preview";
        Timing = "";
    }

    /// <summary>The screen shown, the one before it let go of: its pixels are native memory, a frame's worth each.</summary>
    void ShowScreen(Bitmap? screen)
    {
        var before = Screen;
        Screen = screen;
        before?.Dispose();
    }

    partial void OnDarkChanged(bool value) => Change(new PreviewAction.Change(Dark: value));

    partial void OnLocaleChanged(string value) => Change(new PreviewAction.Change(Locale: value));

    partial void OnSizeChanged(string value) => Change(new PreviewAction.Change(Size: value));

    void Change(PreviewAction.Change change)
    {
        if (!_showing && HasApp)
            _ = ActAsync(change);
    }

    public Task TapAsync(double x, double y) => ActAsync(new PreviewAction.Tap(x, y));

    public Task TypeAsync(string text) => ActAsync(new PreviewAction.TypeText(text));

    public Task PressAsync(PreviewKey key) => ActAsync(new PreviewAction.Press(key));

    /// <summary>Scrolls what is under (x, y): sent at once, or gathered with what follows while the last is on its way.</summary>
    public async Task ScrollAsync(double x, double y, double dx, double dy)
    {
        _scrolling = (x, y, _scrolling.Dx + dx, _scrolling.Dy + dy);
        if (_scrollSending)
            return;
        _scrollSending = true;
        while (_scrolling is { Dx: not 0 } or { Dy: not 0 })
        {
            var scroll = _scrolling;
            _scrolling = (scroll.X, scroll.Y, 0, 0);
            await ActAsync(new PreviewAction.Scroll(scroll.X, scroll.Y, scroll.Dx, scroll.Dy));
        }
        _scrollSending = false;
    }

    /// <summary>Does <paramref name="action"/> after those before it; the state then, or null when it could not be done (the log says why).</summary>
    public async Task<PreviewState?> ActAsync(PreviewAction action, CancellationToken cancellationToken = default)
    {
        if (Client is null)
            return null;
        try
        {
            return await DoAsync(action, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or OperationCanceledException or System.Text.Json.JsonException)
        {
            AddLog($"preview: {ex.Message}");
            return null;
        }
    }

    /// <summary>Does <paramref name="action"/> after those before it, the user's and Claude's in one line; the state then, shown.</summary>
    /// <exception cref="InvalidOperationException">No app is shown, or bek-preview refused it (the words say why).</exception>
    internal async Task<PreviewState> DoAsync(PreviewAction action, CancellationToken cancellationToken)
    {
        var client = Client ?? throw new InvalidOperationException("No app is in the Bek Preview window: open one with bek_preview_open.");
        await _acting.WaitAsync(cancellationToken);
        try
        {
            var state = await client.ActAsync(action, cancellationToken);
            Show(state);
            return state;
        }
        finally
        {
            _acting.Release();
        }
    }

    /// <summary>
    /// Follows bek-preview: each new state as soon as it is there, its screen when its pixels changed, its new log lines.
    /// What it says that cannot be read, or a request it takes too long with, is logged and asked again; it ends with the
    /// preview, or with bek-preview.
    /// </summary>
    async Task FollowAsync(PreviewProcess preview, CancellationToken cancellationToken)
    {
        var client = preview.Client;
        long after = 0, screen = -1, lastLine = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var state = await client.StateAsync(after, cancellationToken);
                after = state.Version;
                if (state.Screen != screen)
                {
                    using var png = new MemoryStream(await client.ScreenAsync(cancellationToken));
                    ShowScreen(new Bitmap(png));
                    screen = state.Screen;
                }
                if (state.LastLog > lastLine)
                {
                    foreach (var line in await client.LogAsync(lastLine, cancellationToken))
                    {
                        AddLog($"{line.Source}: {line.Text}");
                        lastLine = Math.Max(lastLine, line.Number);
                    }
                }
                Show(state);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            // Whatever else (bek-preview gone, its answer unreadable, the client's time out) leaves the window following.
            catch (Exception ex)
            {
                if (cancellationToken.IsCancellationRequested)
                    return;
                if (preview.HasExited)
                {
                    Message = "bek-preview ended: see its output.";
                    HasApp = false;
                    return;
                }
                if (ex is not HttpRequestException)
                    AddLog($"preview: {ex.Message}");
                // A moment's wait before asking again; a stop meanwhile ends the loop at its top.
                await Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None);
            }
        }
    }

    void Show(PreviewState state)
    {
        if (State is { } shown && state.Version < shown.Version)
            return;
        State = state;
        Title = state.App.Title;
        Error = state.Running ? null : state.Error;
        Timing = $"start {state.Timing.Start:0} ms · update {state.Timing.Update:0.0} ms · draw {state.Timing.Draw:0.0} ms";
        _showing = true;
        Dark = state.Settings.Dark;
        Locale = state.Settings.Locale;
        Size = state.Settings.Size;
        _showing = false;
    }

    void AddLog(string line)
    {
        Log.Add(line);
        while (Log.Count > LogKept)
            Log.RemoveAt(0);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _acting.Dispose();
        _opening.Dispose();
    }
}
