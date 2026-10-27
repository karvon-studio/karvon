using System.Text.Json;
using System.Text.RegularExpressions;
using Karvon.Modules;

namespace Karvon.Debugging;

/// <summary>
/// What a Hot Reload agent in the program tells Karvon (Node.js's, which LiveEdit applies saved changes with): JSON in
/// an OSC 7700 sequence on its output, which a terminal does not show and Output leaves out. A file it applies changes
/// to ({"live"}), a change applied ({"applied", "ms"}), code that does not compile ({"error", "line", "message"}), and a
/// change it cannot apply ({"restart", "reason"}), which restarts the run.
/// </summary>
public static partial class HotReloadMessages
{
    [GeneratedRegex(@"\x1b\]7700;(?<json>[^\x07\x1b]*)(\x07|\x1b\\)\r?\n?")]
    private static partial Regex Message();

    /// <summary>The output without the agent's messages, and what they say (none: the text as it is).</summary>
    public static string Take(string text, List<RunNews> news)
    {
        if (!text.Contains("\u001b]7700;", StringComparison.Ordinal))
            return text;
        return Message().Replace(text, match =>
        {
            if (Read(match.Groups["json"].Value) is { } said)
                news.Add(said);
            return "";
        });
    }

    static RunNews? Read(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var message = doc.RootElement;
            string? Text(string name) => message.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            int? Number(string name) => message.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : null;
            if (Text("live") is { } live)
                return new RunNews(null) { Live = live };
            if (Text("applied") is { } applied)
                return new RunNews($"Hot Reload: {Path.GetFileName(applied)} applied in {Number("ms") ?? 0} ms");
            if (Text("error") is { } error)
                return new RunNews($"Hot Reload: {Path.GetFileName(error)}({Number("line") ?? 1}): {Text("message")}");
            if (Text("restart") is { } restart)
                return new RunNews($"Hot Reload: {Path.GetFileName(restart)} needs a restart ({Text("reason")})") { Restart = restart };
        }
        catch (JsonException)
        {
        }
        return null;
    }
}
