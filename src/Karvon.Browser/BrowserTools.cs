using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Karvon.Claude;
using static Karvon.Claude.Tools.ToolInput;

namespace Karvon.Browser;

/// <summary>
/// Claude Code's tools for Karvon's browser: a page opened, read as a short outline whose controls each have a ref,
/// clicked, typed into, chosen from, keys pressed, a picture of it, its console and network, its tabs, a sign-in Karvon
/// keeps filled in (Claude never sees the password). A site not open in the browser yet asks before Claude opens it;
/// a form sent, a script run and a sign-in ask too.
/// </summary>
public static class BrowserClaudeTools
{
    public static IEnumerable<IClaudeTool> Create(Func<CancellationToken, Task<ManagedBrowser>> browser, BrowserLogins logins)
    {
        var state = new BrowserToolState(browser, logins);
        return
        [
            new BrowserOpenTool(state), new BrowserSnapshotTool(state), new BrowserClickTool(state), new BrowserTypeTool(state),
            new BrowserSelectTool(state), new BrowserPressTool(state), new BrowserScreenshotTool(state), new BrowserEvalTool(state),
            new BrowserLogsTool(state), new BrowserTabsTool(state), new BrowserWaitTool(state), new BrowserLoginTool(state),
        ];
    }
}

/// <summary>What the tools share: the browser, the sign-ins, and the last outline's controls (for what a call will do, before it runs).</summary>
sealed class BrowserToolState(Func<CancellationToken, Task<ManagedBrowser>> browser, BrowserLogins logins)
{
    public Func<CancellationToken, Task<ManagedBrowser>> Browser => browser;

    public BrowserLogins Logins => logins;

    /// <summary>The browser once it has started (null before): what a plan reads.</summary>
    public ManagedBrowser? Started { get; set; }

    /// <summary>The controls of the last outline of each tab, by ref.</summary>
    public Dictionary<string, JsonObject> Refs { get; } = [];

    /// <summary>The control typed into last (Enter sends its form).</summary>
    public JsonObject? Typed { get; set; }

    public JsonObject? Control(string? reference)
    {
        lock (Refs)
            return reference is not null && Started?.Current is { } tab && Refs.TryGetValue(tab.Id, out var refs) ? refs[reference] as JsonObject : null;
    }
}

abstract class BrowserTool(BrowserToolState state) : IClaudeTool
{
    protected BrowserToolState State => state;

    public abstract string Name { get; }

    public abstract string Description { get; }

    public abstract JsonObject InputSchema { get; }

    public abstract ClaudeToolPlan Plan(JsonElement input);

    /// <summary>The site the call is on: the current tab's host (what a rule <c>browser_click@github.com</c> names).</summary>
    public virtual string? Target(JsonElement input) => Host(State.Started?.Current?.Url);

    public virtual string? Refusal(JsonElement input) => null;

    protected static string? Host(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? uri.Authority : null;

    protected string Here => Host(State.Started?.Current?.Url) ?? "the page";

    public async Task<ClaudeToolOutput> CallAsync(JsonElement input, CancellationToken cancellationToken)
    {
        try
        {
            var browser = await State.Browser(cancellationToken);
            State.Started = browser;
            return await RunAsync(browser, input, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return ClaudeToolOutput.Error(ex.Message);
        }
    }

    protected abstract Task<ClaudeToolOutput> RunAsync(ManagedBrowser browser, JsonElement input, CancellationToken ct);

    protected static string Line(BrowserTab tab) => $"Tab: {(tab.Title.Length > 0 ? tab.Title : "(no title)")} — {tab.Url}";

    /// <summary>The page as an outline (its controls' refs remembered for the next calls), after what was done.</summary>
    protected async Task<ClaudeToolOutput> SnapshotAsync(ManagedBrowser browser, string? before, CancellationToken ct)
    {
        var shot = await browser.PageAsync("snapshot", null, ct) as JsonObject ?? throw new InvalidOperationException("The page could not be read.");
        if (browser.Current is { } tab && shot["refs"] is JsonObject refs)
            lock (State.Refs)
                State.Refs[tab.Id] = refs.DeepClone().AsObject();
        var text = new StringBuilder();
        if (before is not null)
            text.AppendLine(before);
        text.AppendLine($"Page: {shot["title"]?.GetValue<string>()} — {shot["url"]?.GetValue<string>()}");
        foreach (var line in (shot["lines"] as JsonArray ?? []).Select(l => l?.GetValue<string>()))
            text.AppendLine(line);
        if (shot["truncated"]?.GetValue<bool>() == true)
            text.AppendLine("(the page goes on: what is above is its first part)");
        return new ClaudeToolOutput(text.ToString().TrimEnd()) { Outside = $"the page {Host(shot["url"]?.GetValue<string>()) ?? ""}".TrimEnd() };
    }

    /// <summary>The short answer of an action, or the page's outline after it when asked for.</summary>
    protected async Task<ClaudeToolOutput> AfterAsync(ManagedBrowser browser, JsonElement input, string done, CancellationToken ct) =>
        Bool(input, "snapshot") ? await SnapshotAsync(browser, done, ct) : new ClaudeToolOutput($"{done}\n{Line(await browser.CurrentTabAsync(ct))}");

    protected static JsonObject SnapshotProperty => new() { ["type"] = "boolean", ["description"] = "Also return the page's outline after it (as browser_snapshot does)." };

    protected static JsonObject RefProperty => new() { ["type"] = "string", ["description"] = "The control's ref from browser_snapshot (e5)." };

    protected static string Ref(JsonElement input) =>
        Str(input, "ref") is { Length: > 0 } reference ? reference : throw new InvalidOperationException("'ref' is required: a control's ref from browser_snapshot.");

    protected static string Describe(JsonObject? control, string reference) =>
        control is null ? reference : $"{control["role"]?.GetValue<string>()} \"{control["name"]?.GetValue<string>()}\" [{reference}]";

    /// <summary>Karvon's page script's answer, or what it says went wrong.</summary>
    protected static JsonObject Checked(JsonNode? answer) =>
        answer is JsonObject o ? o["error"]?.GetValue<string>() is { } error ? throw new InvalidOperationException(error) : o
            : throw new InvalidOperationException("The page did not answer.");
}

/// <summary><c>browser_open</c>: the browser started, a page opened (in a new tab), back, forward, reload.</summary>
sealed class BrowserOpenTool(BrowserToolState state) : BrowserTool(state)
{
    public override string Name => "browser_open";

    public override string Description =>
        "Opens a page in Karvon's browser (the user's Chrome with a profile of Karvon's, in its own window, which keeps its "
        + "cookies and sign-ins): starts it when it is not running. With 'action' goes back, forward or reloads instead. Use it to "
        + "try the app being written, read a site, or test a flow; then browser_snapshot reads the page. A site not open in the "
        + "browser yet asks the user first (localhost goes at once).";

    public override JsonObject InputSchema => Schema("""
        {"type":"object","properties":{
          "url":{"type":"string","description":"The address (https://..., localhost:5000/path)."},
          "new_tab":{"type":"boolean","description":"Open it in a new tab (the current one from then on)."},
          "action":{"type":"string","enum":["back","forward","reload"],"description":"Instead of an address."},
          "snapshot":{"type":"boolean","description":"Also return the page's outline (as browser_snapshot does)."}}}
        """);

    static string? Address(JsonElement input)
    {
        if (Str(input, "url") is not { Length: > 0 } url)
            return null;
        url = url.Trim();
        return url.Contains("://", StringComparison.Ordinal) || url.StartsWith("about:", StringComparison.Ordinal) ? url
            : (url.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) || url.StartsWith("127.0.0.1", StringComparison.Ordinal) ? "http://" : "https://") + url;
    }

    /// <summary>Whether Claude opens it at once: this machine's, a file, or a site the browser has had open.</summary>
    bool Open(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is "about" or "file" or "data")
            return true;
        if (uri.IsLoopback || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        return ManagedBrowser.OriginOf(url) is { } origin && State.Started?.HasVisited(origin) == true;
    }

    public override string? Target(JsonElement input) => Address(input) is { } url ? Host(url) : base.Target(input);

    public override ClaudeToolPlan Plan(JsonElement input)
    {
        var url = Address(input);
        if (url is null)
            return new(ToolAccess.Run, "Browser", Str(input, "action") ?? "start", "");
        return Open(url)
            ? new(ToolAccess.Run, "Browser", url, "")
            : new(ToolAccess.Write, "Browser", url, $"Allow Claude to open {Host(url) ?? url} in Karvon's browser?")
            {
                Warning = "A site not open in the browser yet: what Claude reads on it is the site's, and the address may carry what Claude sends.",
            };
    }

    protected override async Task<ClaudeToolOutput> RunAsync(ManagedBrowser browser, JsonElement input, CancellationToken ct)
    {
        await browser.StartAsync(ct);
        BrowserTab tab;
        if (Address(input) is { } url)
            tab = Bool(input, "new_tab") ? await browser.NewTabAsync(url, ct) : await browser.NavigateAsync(url, ct);
        else if (Str(input, "action") is { } action and ("back" or "forward" or "reload"))
            tab = await browser.GoAsync(action, ct);
        else
            tab = browser.Current ?? throw new InvalidOperationException("The browser has no tab.");
        return Bool(input, "snapshot") ? await SnapshotAsync(browser, null, ct) : new ClaudeToolOutput($"{Line(tab)}\n(browser_snapshot reads the page)");
    }
}

/// <summary><c>browser_snapshot</c>: the current tab read as an outline, each control with a ref.</summary>
sealed class BrowserSnapshotTool(BrowserToolState state) : BrowserTool(state)
{
    public override string Name => "browser_snapshot";

    public override string Description =>
        "Reads the current tab of Karvon's browser as an outline: its headings, text, links and controls, each control with a ref "
        + "(e5) that browser_click, browser_type and browser_select take. Cheaper than a screenshot; a password's value never shows.";

    public override JsonObject InputSchema => new() { ["type"] = "object", ["properties"] = new JsonObject() };

    public override ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Read, "Browser", $"read {Here}", "");

    protected override Task<ClaudeToolOutput> RunAsync(ManagedBrowser browser, JsonElement input, CancellationToken ct) => SnapshotAsync(browser, null, ct);
}

/// <summary><c>browser_click</c>: a control clicked as the mouse clicks it.</summary>
sealed class BrowserClickTool(BrowserToolState state) : BrowserTool(state)
{
    public override string Name => "browser_click";

    public override string Description =>
        "Clicks a control of the current tab by its ref from browser_snapshot, as the mouse does; waits for a page it opens. "
        + "A button that sends a form asks the user first.";

    public override JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["ref"] = RefProperty,
            ["double"] = new JsonObject { ["type"] = "boolean", ["description"] = "A double click." },
            ["button"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("left", "right", "middle") },
            ["snapshot"] = SnapshotProperty,
        },
        ["required"] = new JsonArray("ref"),
    };

    public override ClaudeToolPlan Plan(JsonElement input)
    {
        var reference = Str(input, "ref") ?? "";
        var control = State.Control(reference);
        return control?["submit"]?.GetValue<bool>() == true
            ? new(ToolAccess.Write, "Browser", $"{Here}: {Describe(control, reference)}", $"Allow Claude to send the form on {Here} ({Describe(control, reference)})?")
            : new(ToolAccess.Run, "Browser", $"{Here}: click {Describe(control, reference)}", "");
    }

    protected override async Task<ClaudeToolOutput> RunAsync(ManagedBrowser browser, JsonElement input, CancellationToken ct)
    {
        var reference = Ref(input);
        var point = Checked(await browser.PageAsync("point", JsonValue.Create(reference), ct));
        await browser.ClickAsync(point["x"]!.GetValue<double>(), point["y"]!.GetValue<double>(), Bool(input, "double") ? 2 : 1, Str(input, "button") ?? "left", ct);
        return await AfterAsync(browser, input, $"Clicked {Describe(point, reference)}.", ct);
    }
}

/// <summary><c>browser_type</c>: text typed into a field, as the keyboard types it.</summary>
sealed class BrowserTypeTool(BrowserToolState state) : BrowserTool(state)
{
    public override string Name => "browser_type";

    public override string Description =>
        "Types text into a field of the current tab by its ref from browser_snapshot (clearing it first unless 'append'), as the "
        + "keyboard does; 'submit' presses Enter after it, which asks the user first. Never a password: browser_login signs in.";

    public override JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["ref"] = RefProperty,
            ["text"] = new JsonObject { ["type"] = "string" },
            ["append"] = new JsonObject { ["type"] = "boolean", ["description"] = "Keep what the field has and type after it." },
            ["submit"] = new JsonObject { ["type"] = "boolean", ["description"] = "Press Enter after it (a form is sent)." },
            ["snapshot"] = SnapshotProperty,
        },
        ["required"] = new JsonArray("ref", "text"),
    };

    public override string? Refusal(JsonElement input) =>
        State.Control(Str(input, "ref"))?["password"]?.GetValue<bool>() == true
            ? "A password field: Claude does not type passwords. browser_login fills a sign-in Karvon keeps for the site, without Claude seeing it."
            : null;

    public override ClaudeToolPlan Plan(JsonElement input)
    {
        var reference = Str(input, "ref") ?? "";
        var control = State.Control(reference);
        var subject = $"{Here}: {Describe(control, reference)} ← {OneLine(Str(input, "text") ?? "", 60)}";
        return Bool(input, "submit")
            ? new(ToolAccess.Write, "Browser", subject, $"Allow Claude to type into {Describe(control, reference)} and send the form on {Here}?") { Code = Str(input, "text") }
            : new(ToolAccess.Run, "Browser", subject, "");
    }

    protected override async Task<ClaudeToolOutput> RunAsync(ManagedBrowser browser, JsonElement input, CancellationToken ct)
    {
        var reference = Ref(input);
        var text = Str(input, "text") ?? "";
        var field = Checked(await browser.PageAsync("focus", new JsonObject { ["ref"] = reference, ["clear"] = !Bool(input, "append") }, ct));
        if (field["password"]?.GetValue<bool>() == true)
            throw new InvalidOperationException("A password field: browser_login fills a sign-in Karvon keeps, without Claude seeing it.");
        await browser.InsertTextAsync(text, ct);
        State.Typed = field;
        if (Bool(input, "submit"))
            await browser.PressAsync("Enter", ct);
        return await AfterAsync(browser, input, $"Typed into {Describe(field, reference)}{(Bool(input, "submit") ? " and pressed Enter" : "")}.", ct);
    }
}

/// <summary><c>browser_select</c>: options of a list chosen.</summary>
sealed class BrowserSelectTool(BrowserToolState state) : BrowserTool(state)
{
    public override string Name => "browser_select";

    public override string Description => "Chooses options of a drop-down list (a select) of the current tab by its ref from browser_snapshot, by their text or value.";

    public override JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["ref"] = RefProperty,
            ["values"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
            ["snapshot"] = SnapshotProperty,
        },
        ["required"] = new JsonArray("ref", "values"),
    };

    public override ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Run, "Browser", $"{Here}: choose in {Describe(State.Control(Str(input, "ref")), Str(input, "ref") ?? "")}", "");

    protected override async Task<ClaudeToolOutput> RunAsync(ManagedBrowser browser, JsonElement input, CancellationToken ct)
    {
        var reference = Ref(input);
        var values = input.TryGetProperty("values", out var v) && v.ValueKind == JsonValueKind.Array
            ? new JsonArray([.. v.EnumerateArray().Select(x => (JsonNode?)JsonValue.Create(x.ToString()))]) : [];
        var chosen = Checked(await browser.PageAsync("select", new JsonObject { ["ref"] = reference, ["values"] = values }, ct));
        var names = (chosen["chosen"] as JsonArray ?? []).Select(c => c?.GetValue<string>()).ToList();
        if (names.Count == 0)
            throw new InvalidOperationException($"No option of {Describe(chosen, reference)} is {string.Join(" or ", values.Select(x => x?.ToString()))}.");
        return await AfterAsync(browser, input, $"Chose {string.Join(", ", names)} in {Describe(chosen, reference)}.", ct);
    }
}

/// <summary><c>browser_press</c>: a key pressed where the focus is.</summary>
sealed class BrowserPressTool(BrowserToolState state) : BrowserTool(state)
{
    public override string Name => "browser_press";

    public override string Description =>
        "Presses a key in the current tab where the focus is: Enter, Tab, Escape, Backspace, Delete, Space, the arrows, Home, End, "
        + "PageUp, PageDown, F1-F12, a letter or digit, with Control+, Shift+, Alt+ or Meta+ before it. Enter in a form's field asks first.";

    public override JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject { ["key"] = new JsonObject { ["type"] = "string", ["description"] = "Enter, Control+A..." }, ["snapshot"] = SnapshotProperty },
        ["required"] = new JsonArray("key"),
    };

    public override ClaudeToolPlan Plan(JsonElement input)
    {
        var key = Str(input, "key") ?? "";
        return key.Equals("Enter", StringComparison.OrdinalIgnoreCase) && State.Typed?["form"]?.GetValue<bool>() == true
            ? new(ToolAccess.Write, "Browser", $"{Here}: Enter", $"Allow Claude to send the form on {Here} (Enter in {Describe(State.Typed, "the field")})?")
            : new(ToolAccess.Run, "Browser", $"{Here}: {key}", "");
    }

    protected override async Task<ClaudeToolOutput> RunAsync(ManagedBrowser browser, JsonElement input, CancellationToken ct)
    {
        var key = Str(input, "key") is { Length: > 0 } k ? k : throw new InvalidOperationException("'key' is required.");
        await browser.PressAsync(key, ct);
        return await AfterAsync(browser, input, $"Pressed {key}.", ct);
    }
}

/// <summary><c>browser_screenshot</c>: a picture of the current tab.</summary>
sealed class BrowserScreenshotTool(BrowserToolState state) : BrowserTool(state)
{
    public override string Name => "browser_screenshot";

    public override string Description => "A picture of the current tab of Karvon's browser (what shows, or the whole page), to look at the layout; browser_snapshot reads its text for less.";

    public override JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject { ["full_page"] = new JsonObject { ["type"] = "boolean", ["description"] = "The whole page, not only what shows." } },
    };

    public override ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Read, "Browser", $"picture of {Here}", "");

    protected override async Task<ClaudeToolOutput> RunAsync(ManagedBrowser browser, JsonElement input, CancellationToken ct)
    {
        var png = await browser.ScreenshotAsync(Bool(input, "full_page"), ct);
        return new ClaudeToolOutput(Line(await browser.CurrentTabAsync(ct))) { Images = [new ClaudeToolImage("image/png", png)] };
    }
}

/// <summary><c>browser_eval</c>: a script of Claude's run in the page.</summary>
sealed class BrowserEvalTool(BrowserToolState state) : BrowserTool(state)
{
    public override string Name => "browser_eval";

    public override string Description =>
        "Runs a JavaScript expression in the current tab (a promise is waited for) and returns its value as JSON: to read what "
        + "the outline does not show (a variable, localStorage, an element's style). Asks the user first.";

    public override JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject { ["expression"] = new JsonObject { ["type"] = "string", ["description"] = "document.title, await fetch('/api').then(r => r.status)..." } },
        ["required"] = new JsonArray("expression"),
    };

    public override ClaudeToolPlan Plan(JsonElement input) =>
        new(ToolAccess.Write, "Browser", $"{Here}: {OneLine(Str(input, "expression") ?? "")}", $"Allow Claude to run a script on {Here}?")
        {
            Code = Str(input, "expression"),
            Warning = "The script runs as the page: it can read and change what the page can (its cookies too).",
        };

    protected override async Task<ClaudeToolOutput> RunAsync(ManagedBrowser browser, JsonElement input, CancellationToken ct)
    {
        var expression = Str(input, "expression") is { Length: > 0 } e ? e : throw new InvalidOperationException("'expression' is required.");
        var value = await browser.EvaluateAsync(expression, ct);
        var text = value?.ToJsonString() ?? "undefined";
        return new ClaudeToolOutput(text.Length > 20_000 ? text[..20_000] + "…" : text) { Outside = $"the page {Here}" };
    }
}

/// <summary><c>browser_logs</c>: what the current tab said in its console, or asked for on the network.</summary>
sealed class BrowserLogsTool(BrowserToolState state) : BrowserTool(state)
{
    public override string Name => "browser_logs";

    public override string Description => "The current tab's console (its logs, errors, dialogs) or its network (each request and its status), the latest last; 'clear' empties it after.";

    public override JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("console", "network") },
            ["last"] = new JsonObject { ["type"] = "integer", ["description"] = "At most this many, the latest (default 100)." },
            ["clear"] = new JsonObject { ["type"] = "boolean" },
        },
    };

    public override ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Read, "Browser", $"{Str(input, "kind") ?? "console"} of {Here}", "");

    protected override Task<ClaudeToolOutput> RunAsync(ManagedBrowser browser, JsonElement input, CancellationToken ct)
    {
        var kind = Str(input, "kind") == "network" ? "network" : "console";
        var entries = browser.Logs(kind);
        var last = Int(input, "last", 100, 1, 300);
        var text = entries.Count == 0 ? $"Nothing in the {kind} yet."
            : string.Join("\n", entries.TakeLast(last).Select(e => $"{e.Time:HH:mm:ss} {e.Text}"));
        if (Bool(input, "clear"))
            browser.ClearLogs(kind);
        return Task.FromResult(new ClaudeToolOutput(text) { Outside = $"the {kind} of {Here}" });
    }
}

/// <summary><c>browser_tabs</c>: the tabs listed, one chosen, opened or closed.</summary>
sealed class BrowserTabsTool(BrowserToolState state) : BrowserTool(state)
{
    public override string Name => "browser_tabs";

    public override string Description => "The browser's tabs (the current one marked); 'select' makes one by its number the current one, 'close' closes it.";

    public override JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["action"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("list", "select", "close") },
            ["tab"] = new JsonObject { ["type"] = "integer", ["description"] = "The tab's number in the list (1 is the first)." },
        },
    };

    public override ClaudeToolPlan Plan(JsonElement input) => new(Str(input, "action") is "select" or "close" ? ToolAccess.Run : ToolAccess.Read, "Browser",
        $"{Str(input, "action") ?? "list"} tabs", "");

    protected override async Task<ClaudeToolOutput> RunAsync(ManagedBrowser browser, JsonElement input, CancellationToken ct)
    {
        await browser.StartAsync(ct);
        var tabs = browser.Tabs;
        var action = Str(input, "action") ?? "list";
        if (action is "select" or "close")
        {
            var number = Int(input, "tab", 0, 0, 1000);
            if (number < 1 || number > tabs.Count)
                throw new InvalidOperationException($"'tab' is a number from 1 to {tabs.Count}.");
            if (action == "select")
                await browser.SelectTabAsync(tabs[number - 1].Id, ct);
            else
                await browser.CloseTabAsync(tabs[number - 1].Id, ct);
            await Task.Delay(100, ct);
            tabs = browser.Tabs;
        }
        var current = browser.Current?.Id;
        return new ClaudeToolOutput(string.Join("\n", tabs.Select((t, i) => $"{i + 1}. {(t.Id == current ? "* " : "")}{(t.Title.Length > 0 ? t.Title : "(no title)")} — {t.Url}")));
    }
}

/// <summary><c>browser_wait</c>: until a text shows on the page, or a while.</summary>
sealed class BrowserWaitTool(BrowserToolState state) : BrowserTool(state)
{
    public override string Name => "browser_wait";

    public override string Description => "Waits until a text shows in the current tab (at most 'seconds', default 10), or with no text for 'seconds'.";

    public override JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["text"] = new JsonObject { ["type"] = "string" },
            ["seconds"] = new JsonObject { ["type"] = "integer", ["description"] = "1 to 60." },
        },
    };

    public override ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Read, "Browser", Str(input, "text") is { } t ? $"wait for \"{OneLine(t, 40)}\"" : "wait", "");

    protected override async Task<ClaudeToolOutput> RunAsync(ManagedBrowser browser, JsonElement input, CancellationToken ct)
    {
        var seconds = Int(input, "seconds", 10, 1, 60);
        if (Str(input, "text") is not { Length: > 0 } text)
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), ct);
            return new ClaudeToolOutput($"Waited {seconds} s.");
        }
        var end = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
        while (DateTime.UtcNow < end)
        {
            if (await browser.PageAsync("text", JsonValue.Create(text), ct) is JsonValue shown && shown.GetValue<bool>())
                return new ClaudeToolOutput($"\"{text}\" shows.");
            await Task.Delay(250, ct);
        }
        return ClaudeToolOutput.Error($"\"{text}\" did not show in {seconds} s.");
    }
}

/// <summary><c>browser_login</c>: a sign-in Karvon keeps for the page's site filled in (and sent), its password never Claude's.</summary>
sealed class BrowserLoginTool(BrowserToolState state) : BrowserTool(state)
{
    public override string Name => "browser_login";

    public override string Description =>
        "Signs in on the current tab with a sign-in Karvon keeps for its site (the user saves them in the Browser window): Karvon "
        + "fills the name and password into the page's sign-in fields itself, so Claude never sees the password, and sends the form "
        + "unless 'submit' is false. Asks the user first. Without one kept, says so: ask the user to sign in or to save one.";

    public override JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["username"] = new JsonObject { ["type"] = "string", ["description"] = "Which of the site's sign-ins (default its first)." },
            ["submit"] = new JsonObject { ["type"] = "boolean", ["description"] = "Send the form after (default true)." },
            ["snapshot"] = SnapshotProperty,
        },
    };

    BrowserLogin? LoginOf(JsonElement input)
    {
        var logins = State.Logins.For(ManagedBrowser.OriginOf(State.Started?.Current?.Url ?? ""));
        return Str(input, "username") is { Length: > 0 } name ? logins.FirstOrDefault(l => l.Username == name) : logins.FirstOrDefault();
    }

    public override string? Refusal(JsonElement input) =>
        State.Started?.Current is { } tab && LoginOf(input) is null
            ? $"Karvon keeps no sign-in{(Str(input, "username") is { } name ? $" of {name}" : "")} for {Host(tab.Url) ?? tab.Url}: ask the user to sign in in the browser, or to save one in the Browser window (Saved Logins)."
            : null;

    public override ClaudeToolPlan Plan(JsonElement input) =>
        new(ToolAccess.Write, "Browser", $"sign in to {Here}", $"Allow Claude to sign in to {Here}{(LoginOf(input) is { } login ? $" as {login.Username}" : "")} with the password Karvon keeps?");

    protected override async Task<ClaudeToolOutput> RunAsync(ManagedBrowser browser, JsonElement input, CancellationToken ct)
    {
        var login = LoginOf(input) ?? throw new InvalidOperationException(Refusal(input) ?? "No sign-in is kept for the page's site.");
        var password = State.Logins.Password(login) ?? throw new InvalidOperationException($"The password of {login.Username} at {login.Host} is not in the vault (is it locked?).");
        var fields = Checked(await browser.PageAsync("login", null, ct));
        if (fields["password"]?.GetValue<bool>() != true)
            throw new InvalidOperationException("The page has no password field to fill: open the site's sign-in page first.");
        if (fields["user"]?.GetValue<bool>() == true)
        {
            Checked(await browser.PageAsync("focus", new JsonObject { ["ref"] = "karvon-user", ["clear"] = true }, ct));
            await browser.InsertTextAsync(login.Username, ct);
        }
        var field = Checked(await browser.PageAsync("focus", new JsonObject { ["ref"] = "karvon-password", ["clear"] = true }, ct));
        await browser.InsertTextAsync(password, ct);
        var submit = !input.TryGetProperty("submit", out var s) || s.ValueKind != JsonValueKind.False;
        if (submit)
        {
            State.Typed = field;
            await browser.PressAsync("Enter", ct);
        }
        return await AfterAsync(browser, input, $"Filled in the sign-in of {login.Username} at {login.Host}{(submit ? " and sent it" : "")}.", ct);
    }
}
