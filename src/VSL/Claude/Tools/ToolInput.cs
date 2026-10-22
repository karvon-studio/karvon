using System.Text.Json;
using System.Text.Json.Nodes;

namespace VSL.Claude.Tools;

/// <summary>Reading a tool call's arguments, and the pieces its result is made of.</summary>
static class ToolInput
{
    public static string? Str(JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: > 0 } text ? text : null;

    public static int Int(JsonElement input, string name, int fallback, int min, int max) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var value) && value.TryGetInt32(out var number)
            ? Math.Clamp(number, min, max) : fallback;

    public static bool Bool(JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>A JSON Schema object from its JSON.</summary>
    public static JsonObject Schema(string json) => JsonNode.Parse(json)!.AsObject();

    /// <summary>The first line of a statement or command, for a row in the conversation.</summary>
    public static string OneLine(string text, int max = 80)
    {
        var line = string.Join(" ", text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
        return line.Length > max ? line[..(max - 1)] + "…" : line;
    }

    /// <summary>The end of a long output, which is where an error is.</summary>
    public static string Tail(string text, int max) => text.Length <= max ? text : "…\n" + text[^max..];
}
