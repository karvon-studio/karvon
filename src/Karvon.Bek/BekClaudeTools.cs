using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using Karvon.Bek.ViewModels;
using Karvon.Claude;
using Karvon.Claude.Tools;
using static Karvon.Claude.Tools.ToolInput;

namespace Karvon.Bek;

/// <summary>
/// Claude Code's tools for the Bek Preview window: an app's component previewed, its screen looked at (with its page's
/// nodes), what a person does done to it, and its log. The same preview the user sees, through bek-preview's API.
/// </summary>
public static class BekClaudeTools
{
    public static IEnumerable<IClaudeTool> Create(Func<PreviewViewModel> preview, Func<string?> workspace) =>
        [new PreviewOpenTool(preview, workspace), new PreviewScreenTool(preview), new PreviewActTool(preview), new PreviewLogTool(preview)];

    /// <summary>The state as Claude reads it: the app, whether it runs (or why not), how it is shown, and how long it took.</summary>
    internal static string Line(PreviewState state)
    {
        var settings = state.Settings;
        var shown = $"{settings.Size}, {(settings.Dark ? "dark" : "light")}, {settings.Locale}{(settings.FixedTime ? ", fixed time" : "")}";
        var running = state.Running ? "running" : $"stopped: {state.Error}";
        return $"{state.App.Title} ({state.App.Component}): {running}; {shown}; start {state.Timing.Start:0} ms, update "
            + $"{state.Timing.Update:0.0} ms, draw {state.Timing.Draw:0.0} ms; log up to line {state.LastLog}.";
    }

    /// <summary>The state's line and the screen, for Claude to look at.</summary>
    internal static async Task<ClaudeToolOutput> ScreenOutput(PreviewClient client, PreviewState state, string? outline, CancellationToken cancellationToken)
    {
        var png = await client.ScreenAsync(cancellationToken);
        var text = new StringBuilder(Line(state));
        if (outline is not null)
            text.Append("\nThe page (a node a line: its box @x,y WxH in the page's pixels, then its computed style):\n").Append(outline);
        return new ClaudeToolOutput(text.ToString())
        {
            Images = [new ClaudeToolImage("image/png", Convert.ToBase64String(png))],
            Outside = outline is null ? null : "the app's page",
        };
    }
}

abstract class PreviewTool(Func<PreviewViewModel> preview) : IClaudeTool
{
    protected PreviewViewModel Preview => preview();

    public abstract string Name { get; }

    public abstract string Description { get; }

    public abstract JsonObject InputSchema { get; }

    public abstract ClaudeToolPlan Plan(JsonElement input);

    public async Task<ClaudeToolOutput> CallAsync(JsonElement input, CancellationToken cancellationToken)
    {
        try
        {
            return await Dispatcher.UIThread.InvokeAsync(() => RunAsync(input, cancellationToken));
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
        {
            return ClaudeToolOutput.Error(ex.Message);
        }
    }

    protected abstract Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken);

    /// <summary>The API of the app shown.</summary>
    /// <exception cref="InvalidOperationException">No app is shown.</exception>
    protected PreviewClient Client =>
        Preview.Client ?? throw new InvalidOperationException("No app is in the Bek Preview window: open one with bek_preview_open.");
}

/// <summary><c>bek_preview_open</c>: an app's component previewed in place of the one shown.</summary>
sealed class PreviewOpenTool(Func<PreviewViewModel> preview, Func<string?> workspace) : PreviewTool(preview)
{
    public override string Name => "bek_preview_open";

    public override string Description =>
        "Opens a Bek OS app (a WebAssembly component of the phone's world, e.g. target/wasm32-wasip2/release/app.wasm, or the app's "
        + "folder with its manifest and a cargo build) in Karvon's Bek Preview window, run by bek-preview as the phone runs it: "
        + "its page drawn by the phone's own code. A new build of the component starts it again by itself. Returns its screen.";

    public override JsonObject InputSchema => Schema("""
        {"type":"object","properties":{
          "component":{"type":"string","description":"The app's .wasm, or its folder (a manifest beside a cargo build); relative to the workspace."},
          "manifest":{"type":"string","description":"Its manifest, when it is not <name>.manifest beside the component."}},
         "required":["component"]}
        """);

    public override ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Run, "Bek Preview", Str(input, "component"), "");

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var given = Str(input, "component") ?? throw new InvalidOperationException("'component' is required: the app's .wasm or its folder.");
        var path = Path.GetFullPath(given, workspace() ?? Environment.CurrentDirectory);
        var (component, manifest) = BekModule.ComponentOf(path)
            ?? throw new InvalidOperationException($"{given} is no app's component, nor a folder with a manifest and a cargo build (cargo build --target wasm32-wasip2).");
        if (Str(input, "manifest") is { } named)
            manifest = Path.GetFullPath(named, workspace() ?? Environment.CurrentDirectory);
        await Preview.OpenAsync(component, manifest, cancellationToken);
        var state = await Client.StateAsync(null, cancellationToken);
        return await BekClaudeTools.ScreenOutput(Client, state, null, cancellationToken);
    }
}

/// <summary><c>bek_preview_screen</c>: the screen, and with <c>tree</c> the page's nodes.</summary>
sealed class PreviewScreenTool(Func<PreviewViewModel> preview) : PreviewTool(preview)
{
    public override string Name => "bek_preview_screen";

    public override string Description =>
        "The screen of the app in Karvon's Bek Preview window, as the phone draws it, and its state (running, or why it stopped; "
        + "theme, language, size; timings). With tree, also its page's nodes: each element and text with its box in the page's "
        + "pixels and its computed style, to find where to tap and why something looks as it does.";

    public override JsonObject InputSchema => Schema("""
        {"type":"object","properties":{"tree":{"type":"boolean","description":"Also the page's nodes, boxes and styles."}}}
        """);

    public override ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Read, "Bek Preview", null, "");

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var client = Client;
        var state = await client.StateAsync(null, cancellationToken);
        var outline = Bool(input, "tree") ? await client.OutlineAsync(cancellationToken) : null;
        return await BekClaudeTools.ScreenOutput(client, state, outline, cancellationToken);
    }
}

/// <summary><c>bek_preview_act</c>: a tap, a scroll, Back, typing, a key, a restart, or another way to show it.</summary>
sealed class PreviewActTool(Func<PreviewViewModel> preview) : PreviewTool(preview)
{
    public override string Name => "bek_preview_act";

    public override string Description =>
        "Does to the app in Karvon's Bek Preview window what a person does on the phone, then returns its screen: tap (x, y), "
        + "scroll (x, y, dx, dy; a positive dy shows what is further down), back, type (text, at the end of the field in focus), "
        + "enter, backspace, reload (started again, its store kept), reset (its store emptied), or settings (dark, locale, "
        + "size like 360x740, fixed_time). Places are in the page's pixels, as bek_preview_screen's tree gives them.";

    public override JsonObject InputSchema => Schema("""
        {"type":"object","properties":{
          "action":{"type":"string","enum":["tap","scroll","back","type","enter","backspace","reload","reset","settings"]},
          "x":{"type":"number"},"y":{"type":"number"},"dx":{"type":"number"},"dy":{"type":"number"},
          "text":{"type":"string","description":"For type."},
          "dark":{"type":"boolean"},"locale":{"type":"string","description":"en-US, uz-UZ, ru-RU..."},
          "size":{"type":"string","description":"The page's size, like 536x984."},"fixed_time":{"type":"boolean"}},
         "required":["action"]}
        """);

    public override ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Run, "Bek Preview", Str(input, "action"), "");

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var client = Client;
        // In the window's line of actions, after the user's: a tap of Claude's does not land in the middle of their typing.
        var state = await Preview.DoAsync(Action(input), cancellationToken);
        return await BekClaudeTools.ScreenOutput(client, state, null, cancellationToken);
    }

    static PreviewAction Action(JsonElement input) => Str(input, "action") switch
    {
        "tap" => new PreviewAction.Tap(Number(input, "x"), Number(input, "y")),
        "scroll" => new PreviewAction.Scroll(Number(input, "x"), Number(input, "y"), Number(input, "dx", 0), Number(input, "dy", 0)),
        "back" => new PreviewAction.Back(),
        "type" => new PreviewAction.TypeText(Str(input, "text") ?? throw new InvalidOperationException("'text' is required to type.")),
        "enter" => new PreviewAction.Press(PreviewKey.Enter),
        "backspace" => new PreviewAction.Press(PreviewKey.Backspace),
        "reload" => new PreviewAction.Reload(),
        "reset" => new PreviewAction.Reset(),
        "settings" => new PreviewAction.Change(Flag(input, "dark"), Str(input, "locale"), Str(input, "size"), Flag(input, "fixed_time")),
        var other => throw new InvalidOperationException($"No action '{other}'."),
    };

    static double Number(JsonElement input, string name, double? fallback = null) =>
        input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble()
        : fallback ?? throw new InvalidOperationException($"'{name}' is required: a number of the page's pixels.");

    static bool? Flag(JsonElement input, string name) =>
        input.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
}

/// <summary><c>bek_preview_log</c>: what the app logged, and what the preview did.</summary>
sealed class PreviewLogTool(Func<PreviewViewModel> preview) : PreviewTool(preview)
{
    public override string Name => "bek_preview_log";

    public override string Description =>
        "The log of the app in Karvon's Bek Preview window: what it logged (app) and what the preview did (preview: started, "
        + "stopped and why, a new build). With after, only the lines after that number.";

    public override JsonObject InputSchema => Schema("""
        {"type":"object","properties":{"after":{"type":"integer","description":"Only the lines numbered after this."}}}
        """);

    public override ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Read, "Bek Preview", null, "");

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var lines = await Client.LogAsync(Int(input, "after", 0, 0, int.MaxValue), cancellationToken);
        var text = lines.Count == 0 ? "No lines." : string.Join('\n', lines.Select(line => $"{line.Number} [{line.Millis / 1000.0:0.0}s] {line.Source}: {line.Text}"));
        return new ClaudeToolOutput(text) { Outside = "the app's log" };
    }
}
