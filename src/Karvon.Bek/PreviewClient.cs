using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Karvon.Bek;

/// <summary>What bek-preview says of the app it runs (<c>GET /api/state</c>): a new version each time something shows differently.</summary>
/// <param name="Screen">The screen's version: a new one when its pixels changed.</param>
/// <param name="Error">Why the app is not running, when it is not.</param>
/// <param name="LastLog">The number of the last line of its log.</param>
public sealed record PreviewState(long Version, long Screen, PreviewApp App, bool Running, string? Error, PreviewSettings Settings, PreviewTiming Timing, long LastLog);

/// <summary>The app: its name and title (its manifest's), and its component.</summary>
public sealed record PreviewApp(string Name, string Title, string Component);

/// <summary>How the app is shown: the page's size, the theme, the phone's language, whether time stands still.</summary>
public sealed record PreviewSettings(int Width, int Height, bool Dark, string Locale, bool FixedTime)
{
    public string Size => $"{Width}x{Height}";
}

/// <summary>How long the app's last start, update and drawing took, in milliseconds.</summary>
public sealed record PreviewTiming(double Start, double Update, double Draw, long Updates);

/// <summary>A line of the log: the app's (<c>app</c>) or the preview's (<c>preview</c>).</summary>
public sealed record PreviewLine(long Number, long Millis, string Source, string Text);

/// <summary>A key that is not a letter.</summary>
public enum PreviewKey
{
    Backspace,
    Enter,
}

/// <summary>What a person does to the app, as bek-preview's API takes it.</summary>
public abstract record PreviewAction
{
    /// <summary>A tap at a place of the page, in its pixels.</summary>
    public sealed record Tap(double X, double Y) : PreviewAction;

    /// <summary>What is under (x, y) scrolled by (dx, dy): a positive dy shows what is further down.</summary>
    public sealed record Scroll(double X, double Y, double Dx, double Dy) : PreviewAction;

    /// <summary>The phone's Back.</summary>
    public sealed record Back : PreviewAction;

    /// <summary>Text typed at the end of the field in focus.</summary>
    public sealed record TypeText(string Text) : PreviewAction;

    public sealed record Press(PreviewKey Key) : PreviewAction;

    /// <summary>The app started again; its store kept.</summary>
    public sealed record Reload : PreviewAction;

    /// <summary>The app started again with its store emptied.</summary>
    public sealed record Reset : PreviewAction;

    /// <summary>The app shown another way: only what is not null changes.</summary>
    public sealed record Change(bool? Dark = null, string? Locale = null, string? Size = null, bool? FixedTime = null) : PreviewAction
    {
        /// <summary>The query that names what changes: <c>dark=true&amp;size=360x740</c>.</summary>
        internal string Query()
        {
            var parts = new List<string>();
            if (Dark is { } dark)
                parts.Add($"dark={(dark ? "true" : "false")}");
            if (Locale is { } locale)
                parts.Add("locale=" + Uri.EscapeDataString(locale));
            if (Size is { } size)
                parts.Add("size=" + Uri.EscapeDataString(size));
            if (FixedTime is { } fixedTime)
                parts.Add($"fixed_time={(fixedTime ? "true" : "false")}");
            return string.Join('&', parts);
        }
    }

    /// <summary>The request that does it: its path and query, and its body.</summary>
    internal (string Path, string? Body) Request() => this switch
    {
        Tap(var x, var y) => ($"/api/tap?x={Number(x)}&y={Number(y)}", null),
        Scroll(var x, var y, var dx, var dy) => ($"/api/scroll?x={Number(x)}&y={Number(y)}&dx={Number(dx)}&dy={Number(dy)}", null),
        Back => ("/api/back", null),
        TypeText(var text) => ("/api/type", text),
        Press(var key) => ($"/api/key?name={key.ToString().ToLowerInvariant()}", null),
        Reload => ("/api/reload", null),
        Reset => ("/api/reset", null),
        Change change => ("/api/settings?" + change.Query(), null),
        _ => throw new InvalidOperationException($"No request for {GetType().Name}."),
    };

    static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}

/// <summary>bek-preview's API, on this computer: the state (waited for when a newer one is asked), the screen, the page, the log, the store, and what a person does.</summary>
public sealed class PreviewClient(Uri address) : IDisposable
{
    /// <summary>The API's names are snake_case (<c>last_log</c>, <c>fixed_time</c>).</summary>
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>Longer than the 20 seconds bek-preview holds a request for a newer state.</summary>
    readonly HttpClient _http = new() { BaseAddress = address, Timeout = TimeSpan.FromSeconds(45) };

    public Uri Address => address;

    /// <summary>The state; with <paramref name="after"/>, once its version is past it (or after 20 seconds, as it is).</summary>
    public async Task<PreviewState> StateAsync(long? after, CancellationToken cancellationToken) =>
        await _http.GetFromJsonAsync<PreviewState>(after is { } version ? $"/api/state?after={version}" : "/api/state", Json, cancellationToken)
        ?? throw new InvalidOperationException("bek-preview gave no state.");

    /// <summary>The page as the phone draws it, a PNG.</summary>
    public Task<byte[]> ScreenAsync(CancellationToken cancellationToken) => _http.GetByteArrayAsync("/api/screen.png", cancellationToken);

    /// <summary>The page as text, a node a line, with each one's box and computed style.</summary>
    public Task<string> OutlineAsync(CancellationToken cancellationToken) => _http.GetStringAsync("/api/tree.txt", cancellationToken);

    /// <summary>The log's lines after the one numbered <paramref name="after"/>.</summary>
    public async Task<IReadOnlyList<PreviewLine>> LogAsync(long after, CancellationToken cancellationToken) =>
        await _http.GetFromJsonAsync<List<PreviewLine>>($"/api/logs?after={after}", Json, cancellationToken) ?? [];

    /// <summary>What the app keeps in its store, by key.</summary>
    public async Task<IReadOnlyList<KeyValuePair<string, string>>> StoreAsync(CancellationToken cancellationToken)
    {
        var pairs = await _http.GetFromJsonAsync<List<List<string>>>("/api/store", Json, cancellationToken) ?? [];
        return [.. pairs.Where(pair => pair.Count == 2).Select(pair => KeyValuePair.Create(pair[0], pair[1]))];
    }

    /// <summary>Does <paramref name="action"/>; the state once it shows.</summary>
    /// <exception cref="InvalidOperationException">bek-preview refused it (the words say why).</exception>
    public async Task<PreviewState> ActAsync(PreviewAction action, CancellationToken cancellationToken)
    {
        var (path, body) = action.Request();
        using var content = new StringContent(body ?? "", Encoding.UTF8, "text/plain");
        using var response = await _http.PostAsync(path, content, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException((await response.Content.ReadAsStringAsync(cancellationToken)).Trim());
        return await response.Content.ReadFromJsonAsync<PreviewState>(Json, cancellationToken) ?? throw new InvalidOperationException("bek-preview gave no state.");
    }

    public void Dispose() => _http.Dispose();
}
