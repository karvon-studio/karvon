using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Karvon.Browser;

/// <summary>A tab of the browser: its target id, title and address.</summary>
public sealed record BrowserTab(string Id, string Title, string Url);

/// <summary>What a page said (its console, an error, a dialog) or asked for (a request, its answer).</summary>
public sealed record BrowserLogEntry(DateTime Time, string Text);

/// <summary>
/// Karvon's own Chrome: the installed Chrome (or Chromium, or Edge) with a profile of Karvon's (its cookies, sign-ins and
/// saved passwords stay in it; Chrome's password manager works there as always), in its own window, driven through the
/// DevTools protocol. Chrome refuses remote debugging on the user's everyday profile, so this one is Karvon's. Karvon
/// drives it through the pipe it starts it with (<see cref="CdpPipe"/>), not a debugging port that any program on the
/// machine could reach: it closes when Karvon closes. On Windows it has a port (127.0.0.1, written into the profile),
/// stays open when Karvon closes, and Karvon finds it again the next time.
/// </summary>
public sealed class ManagedBrowser(string profile, Func<string?> chrome, bool headless = false) : IAsyncDisposable
{
    const int LogLimit = 300;

    readonly Lock _gate = new();
    readonly Dictionary<string, PageState> _pages = [];
    readonly HashSet<string> _visited = new(StringComparer.OrdinalIgnoreCase);
    readonly SemaphoreSlim _starting = new(1, 1);
    CdpConnection? _cdp;
    string? _current;
    /// <summary>The Chrome Karvon started (not one it found running).</summary>
    Process? _process;

    static readonly Lazy<string> Script = new(() =>
    {
        using var stream = typeof(ManagedBrowser).Assembly.GetManifestResourceStream("Karvon.Browser.Snapshot.js")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Trim().TrimEnd(';');
    });

    sealed class PageState(string id)
    {
        public string Id => id;
        public string? Session { get; set; }
        public string Title { get; set; } = "";
        public string Url { get; set; } = "";
        public List<BrowserLogEntry> Console { get; } = [];
        public List<BrowserLogEntry> Network { get; } = [];
        public TaskCompletionSource Loaded { get; set; } = NewLoaded();

        /// <summary>Its top frame (a frame's own loading is not the page's).</summary>
        public string? MainFrame { get; set; }

        /// <summary>The page began to load since the last action (a link followed, a form sent).</summary>
        public volatile bool Started;
    }

    static TaskCompletionSource NewLoaded() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The Chrome Karvon started (its process id); null when it started none.</summary>
    internal int? ProcessId => _process?.Id;

    /// <summary>The profile's folder: its cookies, sign-ins, saved passwords, history.</summary>
    public string Profile => profile;

    public bool IsRunning => _cdp?.IsOpen == true;

    /// <summary>The tabs, or whether it runs, changed (on Chrome's reading thread).</summary>
    public event Action? Changed;

    public IReadOnlyList<BrowserTab> Tabs
    {
        get
        {
            lock (_gate)
                return [.. _pages.Values.Select(p => new BrowserTab(p.Id, p.Title, p.Url))];
        }
    }

    /// <summary>The tab Claude acts on: the one it opened or chose last, else the first.</summary>
    public BrowserTab? Current
    {
        get
        {
            lock (_gate)
            {
                var page = _current is { } id && _pages.TryGetValue(id, out var p) ? p : _pages.Values.FirstOrDefault();
                return page is null ? null : new BrowserTab(page.Id, page.Title, page.Url);
            }
        }
    }

    /// <summary>Whether a page of <paramref name="origin"/> (scheme://host:port) has been open in it while Karvon ran.</summary>
    public bool HasVisited(string origin)
    {
        lock (_gate)
            return _visited.Contains(origin);
    }

    /// <summary>The address's origin (scheme://host:port), lowercase; null for one that is not http(s).</summary>
    public static string? OriginOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? $"{uri.Scheme}://{uri.Authority}".ToLowerInvariant() : null;

    /// <summary>Chrome started with Karvon's profile, or the one that runs with it already found again.</summary>
    public Task StartAsync(CancellationToken ct) => StartAsync(launch: true, ct);

    /// <summary>The Chrome of Karvon's profile that runs already (Karvon started it before) found again, none started; whether it runs.</summary>
    public async Task<bool> FindRunningAsync(CancellationToken ct)
    {
        await StartAsync(launch: false, ct);
        return IsRunning;
    }

    async Task StartAsync(bool launch, CancellationToken ct)
    {
        await _starting.WaitAsync(ct);
        try
        {
            if (IsRunning)
                return;
            // Over a pipe there is nothing to find again: the Chrome Karvon started closed with it.
            var cdp = (CdpPipe.IsSupported ? null : await AttachAsync(ct)) ?? (launch ? await LaunchAsync(ct) : null);
            if (cdp is null)
                return;
            _cdp = cdp;
            cdp.Event += OnEvent;
            cdp.Closed += OnClosed;
            await cdp.SendAsync("Target.setDiscoverTargets", new JsonObject { ["discover"] = true }, ct: ct);
            var targets = await cdp.SendAsync("Target.getTargets", ct: ct);
            foreach (var target in (targets["targetInfos"] as JsonArray ?? []).OfType<JsonObject>())
                await AddPageAsync(target, ct);
            // The tab Chrome opens with (it may come a moment after it listens) is attached before anything is done in it;
            // with none, one is opened.
            if (!await TabsAsync(TimeSpan.FromSeconds(5), ct))
            {
                await cdp.SendAsync("Target.createTarget", new JsonObject { ["url"] = "about:blank" }, ct: ct);
                await TabsAsync(TimeSpan.FromSeconds(10), ct);
            }
            Changed?.Invoke();
        }
        finally
        {
            _starting.Release();
        }
    }

    async Task<bool> TabsAsync(TimeSpan timeout, CancellationToken ct)
    {
        var end = DateTime.UtcNow + timeout;
        while (Tabs.Count == 0 && DateTime.UtcNow < end)
            await Task.Delay(50, ct);
        return Tabs.Count > 0;
    }

    string PortFile => Path.Combine(profile, "DevToolsActivePort");

    /// <summary>The Chrome of this profile that runs already (Karvon started it before), by the port it wrote; null for none.</summary>
    async Task<CdpConnection?> AttachAsync(CancellationToken ct)
    {
        if (!File.Exists(PortFile))
            return null;
        try
        {
            var lines = (await File.ReadAllLinesAsync(PortFile, ct)).Where(l => l.Length > 0).ToArray();
            if (lines.Length < 2 || !int.TryParse(lines[0], out var port))
                return null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            return await CdpConnection.ConnectAsync(new Uri($"ws://127.0.0.1:{port}{lines[1]}"), timeout.Token);
        }
        catch (Exception ex) when (ex is IOException or System.Net.WebSockets.WebSocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    async Task<CdpConnection> LaunchAsync(CancellationToken ct)
    {
        var program = chrome() ?? throw new InvalidOperationException(
            "No Chrome is installed (nor Chromium, nor Edge): install Google Chrome, or set KARVON_CHROME_PATH to the browser to use.");
        Directory.CreateDirectory(profile);
        if (CdpPipe.IsSupported)
            await CloseWithPortAsync(ct);
        try
        {
            File.Delete(PortFile);
        }
        catch (IOException)
        {
        }
        List<string> arguments = [$"--user-data-dir={profile}", CdpPipe.IsSupported ? "--remote-debugging-pipe" : "--remote-debugging-port=0",
            "--no-first-run", "--no-default-browser-check"];
        if (headless)
            arguments.AddRange(["--headless=new", "--window-size=1280,800"]);
        arguments.Add("about:blank");
        _process?.Dispose();
        _process = null;
        if (CdpPipe.IsSupported)
            return await LaunchWithPipeAsync(program, arguments, ct);
        var start = new ProcessStartInfo(program) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        _process = Process.Start(start);
        // Chrome writes its port into the profile once it listens.
        var end = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < end)
        {
            await Task.Delay(150, ct);
            if (await AttachAsync(ct) is { } cdp)
                return cdp;
        }
        throw new InvalidOperationException($"{Path.GetFileName(program)} did not start with Karvon's profile ({profile}) in 30 seconds.");
    }

    async Task<CdpConnection> LaunchWithPipeAsync(string program, List<string> arguments, CancellationToken ct)
    {
        var (process, pipe) = await CdpPipe.StartAsync(program, arguments, TimeSpan.FromSeconds(30), ct);
        _process = process;
        var cdp = CdpConnection.Over(pipe);
        try
        {
            await cdp.SendAsync("Browser.getVersion", ct: ct, timeout: TimeSpan.FromSeconds(30));
            return cdp;
        }
        catch (InvalidOperationException)
        {
            await cdp.DisposeAsync();
            // Another Chrome holds the profile (one the user started with it): this one handed it its address and closed.
            throw new InvalidOperationException(process.HasExited
                ? $"Karvon's profile ({profile}) is open in another {Path.GetFileName(program)}: close it, then start Karvon's browser again."
                : $"{Path.GetFileName(program)} did not answer over its pipe.");
        }
    }

    /// <summary>
    /// The Chrome an older Karvon started with a debugging port (it stayed open after it): closed, so that the profile is
    /// free and the port shut.
    /// </summary>
    async Task CloseWithPortAsync(CancellationToken ct)
    {
        if (await AttachAsync(ct) is not { } old)
            return;
        try
        {
            await old.SendAsync("Browser.close", ct: ct, timeout: TimeSpan.FromSeconds(5));
        }
        catch (InvalidOperationException)
        {
        }
        var end = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (old.IsOpen && DateTime.UtcNow < end)
            await Task.Delay(100, ct);
        await old.DisposeAsync();
        // Chrome lets go of the profile as it exits.
        while (Directory.EnumerateFileSystemEntries(profile, "SingletonLock").Any() && DateTime.UtcNow < end)
            await Task.Delay(100, ct);
    }

    void OnClosed()
    {
        lock (_gate)
            _pages.Clear();
        _cdp = null;
        Changed?.Invoke();
    }

    readonly HashSet<string> _attaching = [];

    /// <summary>A page target attached (its session), then known as a tab, its domains on.</summary>
    async Task AddPageAsync(JsonObject target, CancellationToken ct)
    {
        if (target["type"]?.GetValue<string>() != "page" || target["targetId"]?.GetValue<string>() is not { } id || _cdp is not { } cdp)
            return;
        lock (_gate)
            if (_pages.ContainsKey(id) || !_attaching.Add(id))
                return;
        try
        {
            var page = new PageState(id) { Title = target["title"]?.GetValue<string>() ?? "", Url = target["url"]?.GetValue<string>() ?? "" };
            var attached = await cdp.SendAsync("Target.attachToTarget", new JsonObject { ["targetId"] = id, ["flatten"] = true }, ct: ct);
            page.Session = attached["sessionId"]?.GetValue<string>();
            foreach (var domain in new[] { "Page.enable", "Runtime.enable", "Network.enable", "Log.enable" })
                await cdp.SendAsync(domain, session: page.Session, ct: ct);
            var tree = await cdp.SendAsync("Page.getFrameTree", session: page.Session, ct: ct);
            page.MainFrame = tree["frameTree"]?["frame"]?["id"]?.GetValue<string>();
            // A page that has loaded already (one open before Karvon attached) is not waited for.
            page.Loaded.TrySetResult();
            lock (_gate)
            {
                _pages[id] = page;
                Visit(page.Url);
            }
        }
        finally
        {
            lock (_gate)
                _attaching.Remove(id);
        }
        Changed?.Invoke();
    }

    void Visit(string url)
    {
        if (OriginOf(url) is { } origin)
            _visited.Add(origin);
    }

    PageState? PageOf(string? session)
    {
        lock (_gate)
            return session is null ? null : _pages.Values.FirstOrDefault(p => p.Session == session);
    }

    static void Log(List<BrowserLogEntry> log, string text)
    {
        lock (log)
        {
            log.Add(new BrowserLogEntry(DateTime.Now, text));
            if (log.Count > LogLimit)
                log.RemoveRange(0, log.Count - LogLimit);
        }
    }

    void OnEvent(string method, JsonObject e, string? session)
    {
        switch (method)
        {
            case "Target.targetCreated":
                if (e["targetInfo"] is JsonObject created)
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await AddPageAsync(created, default);
                        }
                        catch (InvalidOperationException)
                        {
                        }
                    });
                break;
            case "Target.targetInfoChanged" when e["targetInfo"] is JsonObject info && info["targetId"]?.GetValue<string>() is { } changed:
                lock (_gate)
                    if (_pages.TryGetValue(changed, out var page))
                    {
                        page.Title = info["title"]?.GetValue<string>() ?? page.Title;
                        page.Url = info["url"]?.GetValue<string>() ?? page.Url;
                        Visit(page.Url);
                    }
                Changed?.Invoke();
                break;
            case "Target.targetDestroyed" when e["targetId"]?.GetValue<string>() is { } gone:
                lock (_gate)
                {
                    _pages.Remove(gone);
                    if (_current == gone)
                        _current = null;
                }
                Changed?.Invoke();
                break;
            case "Page.loadEventFired":
                PageOf(session)?.Loaded.TrySetResult();
                break;
            case "Page.frameStartedLoading" when PageOf(session) is { } loading && e["frameId"]?.GetValue<string>() == loading.MainFrame:
                loading.Started = true;
                break;
            // A page from the back-forward cache stops loading without a load event.
            case "Page.frameStoppedLoading" when PageOf(session) is { } stopped && e["frameId"]?.GetValue<string>() == stopped.MainFrame:
                stopped.Loaded.TrySetResult();
                break;
            case "Page.frameNavigated" when e["frame"] is JsonObject frame && frame["parentId"] is null && PageOf(session) is { } navigated:
                lock (_gate)
                {
                    navigated.Url = frame["url"]?.GetValue<string>() ?? navigated.Url;
                    Visit(navigated.Url);
                }
                break;
            case "Page.javascriptDialogOpening" when PageOf(session) is { } asking:
                // An alert is taken; a confirm, a prompt or "leave this page?" is turned down (a click Claude makes never
                // agrees to something it was not asked about), and said in the console.
                var type = e["type"]?.GetValue<string>() ?? "alert";
                Log(asking.Console, $"[{type}] {e["message"]?.GetValue<string>()} ({(type == "alert" ? "closed" : "dismissed")})");
                _ = _cdp?.SendAsync("Page.handleJavaScriptDialog", new JsonObject { ["accept"] = type == "alert" }, session);
                break;
            case "Runtime.consoleAPICalled" when PageOf(session) is { } page:
                var args = (e["args"] as JsonArray ?? []).OfType<JsonObject>()
                    .Select(a => a["value"] is JsonValue v ? v.ToString() : a["description"]?.GetValue<string>() ?? a["type"]?.GetValue<string>() ?? "");
                Log(page.Console, $"[{e["type"]?.GetValue<string>()}] {string.Join(" ", args)}");
                break;
            case "Runtime.exceptionThrown" when PageOf(session) is { } page:
                var details = e["exceptionDetails"] as JsonObject;
                Log(page.Console, $"[exception] {details?["exception"]?["description"]?.GetValue<string>() ?? details?["text"]?.GetValue<string>()}");
                break;
            case "Log.entryAdded" when PageOf(session) is { } page && e["entry"] is JsonObject entry:
                Log(page.Console, $"[{entry["level"]?.GetValue<string>()}] {entry["text"]?.GetValue<string>()} {entry["url"]?.GetValue<string>()}".TrimEnd());
                break;
            case "Network.requestWillBeSent" when PageOf(session) is { } page && e["request"] is JsonObject request:
                Log(page.Network, $"{request["method"]?.GetValue<string>()} {request["url"]?.GetValue<string>()}");
                break;
            case "Network.responseReceived" when PageOf(session) is { } page && e["response"] is JsonObject response:
                Log(page.Network, $"  {response["status"]} {response["url"]?.GetValue<string>()} ({response["mimeType"]?.GetValue<string>()})");
                break;
            case "Network.loadingFailed" when PageOf(session) is { } page:
                Log(page.Network, $"  failed: {e["errorText"]?.GetValue<string>()} ({e["type"]?.GetValue<string>()})");
                break;
        }
    }

    CdpConnection Cdp => _cdp ?? throw new InvalidOperationException("Karvon's browser is not running: browser_open starts it.");

    PageState CurrentPage()
    {
        lock (_gate)
            return (_current is { } id && _pages.TryGetValue(id, out var page) ? page : _pages.Values.FirstOrDefault())
                ?? throw new InvalidOperationException("The browser has no tab open: browser_open opens one.");
    }

    Task<JsonObject> Send(PageState page, string method, JsonObject? parameters = null, CancellationToken ct = default) =>
        Cdp.SendAsync(method, parameters, page.Session, ct);

    /// <summary>The current tab to <paramref name="url"/>, waited for until it has loaded (30 seconds at most).</summary>
    public async Task<BrowserTab> NavigateAsync(string url, CancellationToken ct)
    {
        var page = CurrentPage();
        page.Loaded = NewLoaded();
        var navigated = await Send(page, "Page.navigate", new JsonObject { ["url"] = url }, ct);
        if (navigated["errorText"]?.GetValue<string>() is { Length: > 0 } error)
            throw new InvalidOperationException($"{url} did not open: {error}.");
        // A move within the page (an anchor) loads nothing.
        if (navigated["loaderId"] is not null)
            await WaitLoadedAsync(page, ct);
        return await TabOfAsync(page, ct);
    }

    /// <summary>Back, forward or reload in the current tab.</summary>
    public async Task<BrowserTab> GoAsync(string action, CancellationToken ct)
    {
        var page = CurrentPage();
        // A page from the back-forward cache comes back without loading.
        Expect(page);
        if (action == "reload")
            await Send(page, "Page.reload", ct: ct);
        else
        {
            var history = await Send(page, "Page.getNavigationHistory", ct: ct);
            var index = history["currentIndex"]!.GetValue<int>() + (action == "back" ? -1 : 1);
            if ((history["entries"] as JsonArray)?.ElementAtOrDefault(index) is not JsonObject entry)
                throw new InvalidOperationException($"The tab has nothing to go {action} to.");
            await Send(page, "Page.navigateToHistoryEntry", new JsonObject { ["entryId"] = entry["id"]!.GetValue<int>() }, ct);
        }
        await SettleAsync(page, ct);
        return await TabOfAsync(page, ct);
    }

    /// <summary>The current tab as its page says it now.</summary>
    public Task<BrowserTab> CurrentTabAsync(CancellationToken ct) => TabOfAsync(CurrentPage(), ct);

    /// <summary>The tab as its page says it now (Chrome's own word on its title comes a moment later).</summary>
    async Task<BrowserTab> TabOfAsync(PageState page, CancellationToken ct)
    {
        try
        {
            if (await EvaluateOn(page, "[document.title, location.href]", ct) is JsonArray { Count: 2 } said)
                lock (_gate)
                    (page.Title, page.Url) = (said[0]?.ToString() ?? page.Title, said[1]?.ToString() ?? page.Url);
        }
        catch (InvalidOperationException)
        {
            // A page that is going (a navigation began): as Chrome last said it.
        }
        return new BrowserTab(page.Id, page.Title, page.Url);
    }

    static async Task WaitLoadedAsync(PageState page, CancellationToken ct)
    {
        try
        {
            await page.Loaded.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        }
        catch (TimeoutException)
        {
            // A page that keeps loading (a stream, a long poll) is used as it is.
        }
    }

    /// <summary>Karvon's page script (Snapshot.js) on the current tab: its answer.</summary>
    public async Task<JsonNode?> PageAsync(string op, JsonNode? argument, CancellationToken ct)
    {
        var page = CurrentPage();
        var expression = $"({Script.Value})({JsonValue.Create(op).ToJsonString()}, {argument?.ToJsonString() ?? "null"})";
        return await EvaluateOn(page, expression, ct);
    }

    async Task<JsonNode?> EvaluateOn(PageState page, string expression, CancellationToken ct)
    {
        var result = await Send(page, "Runtime.evaluate", new JsonObject
        {
            ["expression"] = expression,
            ["returnByValue"] = true,
            ["awaitPromise"] = true,
            ["userGesture"] = true,
        }, ct);
        if (result["exceptionDetails"] is JsonObject failed)
            throw new InvalidOperationException(failed["exception"]?["description"]?.GetValue<string>() ?? failed["text"]?.GetValue<string>() ?? "The script failed.");
        return result["result"]?["value"]?.DeepClone();
    }

    /// <summary>A script of Claude's in the current tab: what it returns (as JSON).</summary>
    public Task<JsonNode?> EvaluateAsync(string expression, CancellationToken ct) => EvaluateOn(CurrentPage(), expression, ct);

    /// <summary>A click at a point of the page (CSS pixels of its view), as the mouse makes it; a page it opens is waited for a little.</summary>
    public async Task ClickAsync(double x, double y, int clicks, string button, CancellationToken ct)
    {
        var page = CurrentPage();
        Expect(page);
        JsonObject Mouse(string type, int count) => new() { ["type"] = type, ["x"] = x, ["y"] = y, ["button"] = button, ["clickCount"] = count };
        await Send(page, "Input.dispatchMouseEvent", new JsonObject { ["type"] = "mouseMoved", ["x"] = x, ["y"] = y }, ct);
        for (var i = 1; i <= clicks; i++)
        {
            await Send(page, "Input.dispatchMouseEvent", Mouse("mousePressed", i), ct);
            await Send(page, "Input.dispatchMouseEvent", Mouse("mouseReleased", i), ct);
        }
        await SettleAsync(page, ct);
    }

    /// <summary>Before an action that may load another page: what it loads is waited for after it.</summary>
    static void Expect(PageState page)
    {
        page.Loaded = NewLoaded();
        page.Started = false;
    }

    /// <summary>A navigation the action began (a link, a form sent): waited for until it loads; with none, a moment only.</summary>
    static async Task SettleAsync(PageState page, CancellationToken ct)
    {
        await Task.Delay(200, ct);
        if (page.Started)
            await WaitLoadedAsync(page, ct);
    }

    /// <summary>Text typed where the focus is, as the keyboard types it.</summary>
    public Task InsertTextAsync(string text, CancellationToken ct) => Send(CurrentPage(), "Input.insertText", new JsonObject { ["text"] = text }, ct);

    /// <summary>A key (Enter, Tab, Escape, ArrowDown, a letter), with Control+, Shift+, Alt+ or Meta+ before it.</summary>
    public async Task PressAsync(string keys, CancellationToken ct)
    {
        var page = CurrentPage();
        var parts = keys.Split('+', StringSplitOptions.TrimEntries);
        var modifiers = 0;
        foreach (var modifier in parts[..^1])
            modifiers |= modifier.ToLowerInvariant() switch
            {
                "alt" => 1,
                "control" or "ctrl" => 2,
                "meta" or "cmd" or "command" => 4,
                "shift" => 8,
                _ => throw new InvalidOperationException($"{modifier} is not a modifier (Control, Shift, Alt, Meta)."),
            };
        var (key, code, keyCode, text) = Key(parts[^1].Length == 0 ? "+" : parts[^1]);
        if (modifiers is 2 or 4 or 6)
            text = "";
        if (key == "Enter")
            Expect(page);
        JsonObject Event(string type) => new()
        {
            ["type"] = type, ["key"] = key, ["code"] = code, ["windowsVirtualKeyCode"] = keyCode, ["nativeVirtualKeyCode"] = keyCode,
            ["modifiers"] = modifiers, ["text"] = type == "keyDown" ? text : "", ["unmodifiedText"] = type == "keyDown" ? text : "",
        };
        await Send(page, "Input.dispatchKeyEvent", Event("keyDown"), ct);
        await Send(page, "Input.dispatchKeyEvent", Event("keyUp"), ct);
        if (key == "Enter")
            await SettleAsync(page, ct);
    }

    static (string Key, string Code, int KeyCode, string Text) Key(string name) => name.ToLowerInvariant() switch
    {
        "enter" or "return" => ("Enter", "Enter", 13, "\r"),
        "tab" => ("Tab", "Tab", 9, ""),
        "escape" or "esc" => ("Escape", "Escape", 27, ""),
        "backspace" => ("Backspace", "Backspace", 8, ""),
        "delete" => ("Delete", "Delete", 46, ""),
        "space" or " " => (" ", "Space", 32, " "),
        "arrowup" or "up" => ("ArrowUp", "ArrowUp", 38, ""),
        "arrowdown" or "down" => ("ArrowDown", "ArrowDown", 40, ""),
        "arrowleft" or "left" => ("ArrowLeft", "ArrowLeft", 37, ""),
        "arrowright" or "right" => ("ArrowRight", "ArrowRight", 39, ""),
        "home" => ("Home", "Home", 36, ""),
        "end" => ("End", "End", 35, ""),
        "pageup" => ("PageUp", "PageUp", 33, ""),
        "pagedown" => ("PageDown", "PageDown", 34, ""),
        _ when name.Length == 1 && char.IsLetter(name[0]) => (name, "Key" + char.ToUpperInvariant(name[0]), char.ToUpperInvariant(name[0]), name),
        _ when name.Length == 1 && char.IsDigit(name[0]) => (name, "Digit" + name, name[0], name),
        _ when name.Length == 1 => (name, "", 0, name),
        _ when name.Length > 1 && name[0] is 'F' or 'f' && int.TryParse(name[1..], out var f) && f is >= 1 and <= 12 => ($"F{f}", $"F{f}", 111 + f, ""),
        _ => throw new InvalidOperationException($"{name} is not a key Karvon presses (Enter, Tab, Escape, Backspace, Delete, Space, the arrows, Home, End, PageUp, PageDown, F1-F12, a letter or digit)."),
    };

    /// <summary>The current tab as a PNG (base64): what shows, or the whole page.</summary>
    public async Task<string> ScreenshotAsync(bool fullPage, CancellationToken ct)
    {
        var page = CurrentPage();
        var parameters = new JsonObject { ["format"] = "png" };
        if (fullPage)
        {
            var metrics = await Send(page, "Page.getLayoutMetrics", ct: ct);
            var size = metrics["cssContentSize"] ?? metrics["contentSize"];
            parameters["captureBeyondViewport"] = true;
            parameters["clip"] = new JsonObject
            {
                ["x"] = 0, ["y"] = 0, ["scale"] = 1,
                ["width"] = Math.Min(size?["width"]?.GetValue<double>() ?? 1280, 4000),
                ["height"] = Math.Min(size?["height"]?.GetValue<double>() ?? 800, 8000),
            };
        }
        var shot = await Send(page, "Page.captureScreenshot", parameters, ct);
        return shot["data"]?.GetValue<string>() ?? throw new InvalidOperationException("Chrome returned no picture.");
    }

    /// <summary>What the current tab said in its console, or asked for on the network, since <paramref name="since"/>.</summary>
    public IReadOnlyList<BrowserLogEntry> Logs(string kind, DateTime? since = null)
    {
        var page = CurrentPage();
        var log = kind == "network" ? page.Network : page.Console;
        lock (log)
            return [.. log.Where(e => since is null || e.Time >= since)];
    }

    public void ClearLogs(string kind)
    {
        var page = CurrentPage();
        var log = kind == "network" ? page.Network : page.Console;
        lock (log)
            log.Clear();
    }

    /// <summary>A new tab (at <paramref name="url"/>), the current one from now on.</summary>
    public async Task<BrowserTab> NewTabAsync(string url, CancellationToken ct)
    {
        var created = await Cdp.SendAsync("Target.createTarget", new JsonObject { ["url"] = "about:blank" }, ct: ct);
        var id = created["targetId"]!.GetValue<string>();
        var end = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (PageById(id) is null)
        {
            if (DateTime.UtcNow > end)
                throw new InvalidOperationException("Chrome did not open the tab.");
            await Task.Delay(50, ct);
        }
        lock (_gate)
            _current = id;
        return url is "about:blank" or "" ? Current! : await NavigateAsync(url, ct);
    }

    PageState? PageById(string id)
    {
        lock (_gate)
            return _pages.GetValueOrDefault(id);
    }

    /// <summary>A tab made the current one, and brought to the front of the window.</summary>
    public async Task<BrowserTab> SelectTabAsync(string id, CancellationToken ct)
    {
        var page = PageById(id) ?? throw new InvalidOperationException("No such tab is open.");
        lock (_gate)
            _current = id;
        await Send(page, "Page.bringToFront", ct: ct);
        return await TabOfAsync(page, ct);
    }

    public async Task CloseTabAsync(string id, CancellationToken ct) =>
        await Cdp.SendAsync("Target.closeTarget", new JsonObject { ["targetId"] = id }, ct: ct);

    /// <summary>Chrome closed (its windows, as the user closes them).</summary>
    public async Task StopAsync()
    {
        if (_cdp is not { } cdp)
            return;
        try
        {
            await cdp.SendAsync("Browser.close", timeout: TimeSpan.FromSeconds(5));
        }
        catch (InvalidOperationException)
        {
        }
        await cdp.DisposeAsync();
        OnClosed();
        // Its profile is written until it has gone.
        if (_process is { } process)
        {
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (TimeoutException)
            {
            }
            process.Dispose();
            _process = null;
        }
    }

    /// <summary>Karvon lets go of it: over its pipe Chrome closes; with a port (Windows) it stays open (the user may be using it).</summary>
    public async ValueTask DisposeAsync()
    {
        if (_cdp is { } cdp)
            await cdp.DisposeAsync();
        _cdp = null;
        _process?.Dispose();
        _process = null;
    }
}
