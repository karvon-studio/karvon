using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Karvon.Modules;

namespace Karvon.Workspace;

/// <summary>An input of tasks.json or launch.json, which ${input:id} asks for when a task or a configuration starts.</summary>
/// <param name="Type">"promptString" (a text), "pickString" (one of <paramref name="Options"/>), or "command" (an extension's command's answer).</param>
public sealed record TaskInput(string Id, string Type, string? Description, string? Default, IReadOnlyList<string> Options, bool Password)
{
    /// <summary>A "command" input's command ("shellCommand.execute").</summary>
    public string? Command { get; init; }

    /// <summary>A "command" input's arguments.</summary>
    public JsonObject? Args { get; init; }
}

/// <summary>The inputs of a tasks.json or a launch.json, and the ${input:id} of a task or a configuration, as VS Code has them.</summary>
public static partial class TaskInputs
{
    [GeneratedRegex(@"\$\{input:([^}]+)\}")]
    private static partial Regex Input();

    /// <summary>A file's "inputs", by their ids.</summary>
    public static IReadOnlyDictionary<string, TaskInput> Read(JsonNode? root)
    {
        var inputs = new Dictionary<string, TaskInput>(StringComparer.Ordinal);
        if (root?["inputs"] is not JsonArray list)
            return inputs;
        foreach (var node in list)
        {
            if (node is not JsonObject input || Text(input["id"]) is not { Length: > 0 } id)
                continue;
            var options = input["options"] is JsonArray choices
                ? choices.Select(c => c is JsonObject o ? Text(o["value"]) : Text(c)).OfType<string>().ToList()
                : [];
            inputs.TryAdd(id, new TaskInput(id, Text(input["type"]) ?? "promptString", Text(input["description"]), Text(input["default"]), options,
                input["password"] is JsonValue p && p.TryGetValue<bool>(out var password) && password)
            {
                Command = Text(input["command"]),
                Args = input["args"] is JsonObject args ? (JsonObject)args.DeepClone() : null,
            });
        }
        return inputs;
    }

    static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;

    /// <summary>The ids of the inputs some texts ask for, each once, in the order they come.</summary>
    public static IReadOnlyList<string> AskedIn(IEnumerable<string> texts) =>
        [.. texts.SelectMany(t => Input().Matches(t)).Select(m => m.Groups[1].Value).Distinct()];

    /// <summary>The texts of a task (its program, arguments, folder and environment), where its inputs may be.</summary>
    public static IEnumerable<string> TextsOf(WorkspaceTask task) =>
        [task.Program, .. task.Arguments, task.WorkingDirectory, .. task.Environment?.Values ?? []];

    /// <summary>The texts of a launch configuration, in its arrays and objects too.</summary>
    public static IEnumerable<string> TextsOf(JsonNode? node) => node switch
    {
        JsonObject o => o.SelectMany(p => TextsOf(p.Value)),
        JsonArray a => a.SelectMany(TextsOf),
        JsonValue v when v.TryGetValue<string>(out var text) => [text],
        _ => [],
    };

    public static string Put(string text, IReadOnlyDictionary<string, string> answers) =>
        Input().Replace(text, m => answers.TryGetValue(m.Groups[1].Value, out var answer) ? answer : m.Value);

    /// <summary>The task with the answers to its inputs put in.</summary>
    public static WorkspaceTask Put(WorkspaceTask task, IReadOnlyDictionary<string, string> answers) => answers.Count == 0 ? task : task with
    {
        Program = Put(task.Program, answers),
        Arguments = [.. task.Arguments.Select(a => Put(a, answers))],
        WorkingDirectory = Put(task.WorkingDirectory, answers),
        Environment = task.Environment?.ToDictionary(e => e.Key, e => Put(e.Value, answers)),
    };

    /// <summary>A copy of the configuration with the answers to its inputs put in.</summary>
    public static JsonNode? Put(JsonNode? node, IReadOnlyDictionary<string, string> answers) => node switch
    {
        JsonObject o => new JsonObject(o.Select(p => KeyValuePair.Create(p.Key, Put(p.Value, answers)))),
        JsonArray a => new JsonArray([.. a.Select(e => Put(e, answers))]),
        JsonValue v when v.TryGetValue<string>(out var text) => JsonValue.Create(Put(text, answers)),
        _ => node?.DeepClone(),
    };
}
