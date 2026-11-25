using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Karvon.Postman;

/// <summary>A collection or an environment that cannot be read, or a request that cannot be sent, and why.</summary>
public sealed class PostmanException(string message) : Exception(message);

/// <summary>A Postman JSON file as it is written: read whole, changed where it changes, written as it was indented.</summary>
static class PostmanJson
{
    static readonly JsonDocumentOptions Reading = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    public static JsonObject Read(string file)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(file), documentOptions: Reading) as JsonObject ?? throw new PostmanException($"{Path.GetFileName(file)} is not a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new PostmanException($"{Path.GetFileName(file)} is not JSON: {ex.Message}");
        }
    }

    /// <summary>Written over the file, with its indentation (a tab, as Postman exports, for a new one) and its last line's end.</summary>
    public static void Write(string file, JsonObject root)
    {
        var before = File.Exists(file) ? File.ReadAllText(file) : null;
        var indent = before?.Split('\n').Select(l => l[..(l.Length - l.TrimStart(' ', '\t').Length)]).FirstOrDefault(i => i.Length > 0) ?? "\t";
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            IndentCharacter = indent[0],
            IndentSize = indent[0] == '\t' ? 1 : Math.Clamp(indent.Length, 1, 8),
            NewLine = before?.Contains("\r\n", StringComparison.Ordinal) == true ? "\r\n" : "\n",
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temporary = $"{file}.{Environment.ProcessId}.tmp";
        File.WriteAllText(temporary, root.ToJsonString(options) + (before?.EndsWith('\n') == true ? options.NewLine : ""));
        File.Move(temporary, file, overwrite: true);
    }

    /// <summary>A member as text: a string's, a number's or a boolean's; null for none (or an object).</summary>
    public static string? Text(this JsonNode? node, string name) => (node as JsonObject)?[name].Text();

    public static string? Text(this JsonNode? node) => node is not JsonValue value ? null
        : value.TryGetValue<string>(out var text) ? text : value.ToJsonString();

    public static bool Flag(this JsonNode? node, string name) => (node as JsonObject)?[name] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    /// <summary>A description: a text, or an object with its content.</summary>
    public static string Description(this JsonNode? node) => (node as JsonObject)?["description"] switch
    {
        JsonObject described => described.Text("content") ?? "",
        var plain => plain.Text() ?? "",
    };

    public static IEnumerable<JsonObject> Objects(this JsonNode? node, string name) => ((node as JsonObject)?[name] as JsonArray)?.OfType<JsonObject>() ?? [];

    /// <summary>An array of a member, made when there is none.</summary>
    public static JsonArray List(this JsonObject node, string name)
    {
        if (node[name] is not JsonArray list)
            node[name] = list = [];
        return list;
    }

    /// <summary>A member set, or gone when there is nothing to keep; one with the same text stays as it was written.</summary>
    public static void Put(this JsonObject node, string name, string? value)
    {
        if (string.IsNullOrEmpty(value))
            node.Remove(name);
        else if (node.Text(name) != value)
            node[name] = value;
    }
}
