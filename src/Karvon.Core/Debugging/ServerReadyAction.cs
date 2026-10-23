using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Karvon.Debugging;

/// <summary>
/// A launch configuration's serverReadyAction, as VS Code reads it: once the program says it listens (its pattern's
/// first group: a URL, or a port), the browser opens there. Only "openExternally" opens; the others are VS Code's
/// browser debuggers, which Karvon does not have.
/// </summary>
public sealed partial class ServerReadyAction
{
    readonly Regex _pattern;
    readonly string? _uriFormat;
    int _fired;

    ServerReadyAction(Regex pattern, string? uriFormat)
    {
        _pattern = pattern;
        _uriFormat = uriFormat;
    }

    /// <summary>VS Code's default: "listening on" and then a URL or a port.</summary>
    const string DefaultPattern = @"listening on.* (https?://\S+|[0-9]+)";

    [GeneratedRegex(@"\u001b\[[0-9;?]*[A-Za-z]")]
    private static partial Regex Escapes();

    /// <summary>The configuration's action, or null when it has none; why one is left out goes to <paramref name="log"/>.</summary>
    public static ServerReadyAction? Of(JsonObject configuration, Action<string> log) =>
        configuration["serverReadyAction"] is JsonObject action ? From(action, log) : null;

    /// <summary>The action itself (a configuration's serverReadyAction, a run task's), or null when it opens nothing.</summary>
    public static ServerReadyAction? From(JsonObject action, Action<string> log)
    {
        var kind = action["action"]?.GetValue<string>() ?? "openExternally";
        if (kind != "openExternally")
        {
            log($"serverReadyAction \"{kind}\" is not supported: only \"openExternally\" opens the browser.");
            return null;
        }
        try
        {
            return new ServerReadyAction(new Regex(action["pattern"]?.GetValue<string>() ?? DefaultPattern, RegexOptions.None, TimeSpan.FromSeconds(1)),
                action["uriFormat"]?.GetValue<string>());
        }
        catch (ArgumentException ex)
        {
            log($"serverReadyAction's pattern is not a regular expression: {ex.Message}");
            return null;
        }
    }

    /// <summary>The address to open when this output is the one it waits for (once only), else null.</summary>
    public string? Match(string output)
    {
        if (Volatile.Read(ref _fired) != 0)
            return null;
        Match match;
        try
        {
            match = _pattern.Match(Escapes().Replace(output, ""));
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
        if (!match.Success)
            return null;
        var captured = match.Groups.Count > 1 ? match.Groups[1].Value : match.Value;
        if (Interlocked.Exchange(ref _fired, 1) != 0)
            return null;
        return _uriFormat is { } format ? format.Replace("%s", captured, StringComparison.Ordinal)
            : captured.All(char.IsAsciiDigit) ? $"http://localhost:{captured}"
            : captured;
    }
}
