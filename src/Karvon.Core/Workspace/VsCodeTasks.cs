using System.Text.Json;
using Karvon.Modules;

namespace Karvon.Workspace;

/// <summary>
/// The tasks of a folder's .vscode/tasks.json (version 2.0.0), as VS Code runs them: a shell task through the shell, a
/// process task as it is, an npm task as npm run in its path. Its group makes it a Build or a Test task ("build", or
/// { "kind": "build", "isDefault": true }); the platform's own section (linux, osx, windows) wins over the task's. Its
/// dependsOn runs first (together, or in sequence), and its problemMatcher reads a build's errors (VS Code's $msCompile,
/// $tsc, $gcc and $go, or its own pattern of one line; without one, file(line,col): error lines are read). Its
/// ${input:...} asks when it runs (promptString, pickString).
/// </summary>
public static class VsCodeTasks
{
    public const string Source = "tasks.json";

    static readonly JsonDocumentOptions Jsonc = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    static string PlatformKey => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "osx" : "linux";

    public static string FileOf(string workspace) => Path.Combine(workspace, ".vscode", "tasks.json");

    /// <summary>Its tasks (none without a tasks.json); one that cannot be read throws a JsonException or an IOException.</summary>
    public static IReadOnlyList<WorkspaceTask> Read(string workspace)
    {
        var file = FileOf(workspace);
        if (!File.Exists(file))
            return [];
        using var doc = JsonDocument.Parse(File.ReadAllText(file), Jsonc);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("tasks", out var tasks) || tasks.ValueKind != JsonValueKind.Array)
            return [];
        var inputs = TaskInputs.Read(System.Text.Json.Nodes.JsonNode.Parse(root.GetRawText(), documentOptions: Jsonc));
        var result = new List<WorkspaceTask>();
        foreach (var task in tasks.EnumerateArray())
            if (task.ValueKind == JsonValueKind.Object && TaskOf(workspace, root, task) is { } read && result.All(t => t.Name != read.Name))
                result.Add(read with { Inputs = inputs });
        return result;
    }

    static WorkspaceTask? TaskOf(string workspace, JsonElement root, JsonElement task)
    {
        string Expand(string text) => TaskVariables.Expand(text, workspace);
        var type = Str(Prop(task, "type")) ?? "process";
        var command = Prop(task, "command") is { } c ? Str(c) ?? Str(Prop(c, "value")) : null;
        var args = Prop(task, "args") is { ValueKind: JsonValueKind.Array } list
            ? list.EnumerateArray().Select(a => Str(a) ?? Str(Prop(a, "value"))).OfType<string>().Select(Expand).ToList()
            : [];
        var cwd = Str(Prop(task, "options") is { } options ? Prop(options, "cwd") : null) ?? Str(Prop(root, "options") is { } shared ? Prop(shared, "cwd") : null);
        var directory = cwd is null ? workspace : Path.GetFullPath(Expand(cwd), workspace);
        var label = Str(task, "label") ?? Str(task, "taskName");

        var dependsOn = Prop(task, "dependsOn") switch
        {
            { ValueKind: JsonValueKind.String } one => [one.GetString()!],
            { ValueKind: JsonValueKind.Array } many => many.EnumerateArray().Select(d => Str(d) ?? Str(Prop(d, "label"))).OfType<string>().ToList(),
            _ => new List<string>(),
        };

        string program;
        List<string> arguments;
        switch (type)
        {
            // Only its dependencies: "dependsOn" and no command.
            case not "npm" when command is null && dependsOn.Count > 0:
                program = "";
                arguments = [];
                break;
            case "npm" when Str(Prop(task, "script")) is { } script:
                var path = Str(Prop(task, "path"))?.Trim('/');
                program = "npm";
                arguments = ["run", script];
                if (path is { Length: > 0 })
                    directory = Path.GetFullPath(path, workspace);
                label ??= path is { Length: > 0 } ? $"npm: {script} - {path}" : $"npm: {script}";
                break;
            case "shell" when command is not null:
                (program, arguments) = TaskVariables.Shell(string.Join(' ', [Expand(command), .. args.Select(TaskVariables.QuoteForShell)]));
                break;
            case "process" when command is not null:
                program = Expand(command);
                arguments = args;
                break;
            default:
                // Another provider's task (gulp, a typescript task by its tsconfig...): not run yet.
                return null;
        }

        var (kind, isDefault) = Prop(task, "group") switch
        {
            { ValueKind: JsonValueKind.String } g => (KindOf(g.GetString()), false),
            { ValueKind: JsonValueKind.Object } g => (KindOf(Str(Prop(g, "kind"))), Prop(g, "isDefault") is { ValueKind: JsonValueKind.True }),
            _ => (WorkspaceTaskKind.Other, false),
        };
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var source in new[] { Prop(root, "options"), Prop(task, "options") })
            if (source is { } section && Prop(section, "env") is { ValueKind: JsonValueKind.Object } env)
                foreach (var variable in env.EnumerateObject())
                    if (Str(variable.Value) is { } value)
                        environment[variable.Name] = Expand(value);
        return new WorkspaceTask(label ?? command ?? type, kind, program, arguments, directory)
        {
            Source = Source,
            IsDefault = isDefault,
            IsBackground = Prop(task, "isBackground") is { ValueKind: JsonValueKind.True },
            Environment = environment.Count > 0 ? environment : null,
            DependsOn = dependsOn,
            DependsInSequence = Str(Prop(task, "dependsOrder")) == "sequence",
            ProblemReader = ProblemMatchers.Of(Prop(task, "problemMatcher"), Expand),
        };
    }

    static WorkspaceTaskKind KindOf(string? group) => group switch
    {
        "build" => WorkspaceTaskKind.Build,
        "test" => WorkspaceTaskKind.Test,
        _ => WorkspaceTaskKind.Other,
    };

    /// <summary>A property of the task, its platform's section first (VS Code's "linux": { "command": ... }).</summary>
    static JsonElement? Prop(JsonElement? element, string name)
    {
        if (element is not { ValueKind: JsonValueKind.Object } e)
            return null;
        if (e.TryGetProperty(PlatformKey, out var platform) && platform.ValueKind == JsonValueKind.Object && platform.TryGetProperty(name, out var own))
            return own;
        return e.TryGetProperty(name, out var value) ? value : null;
    }

    static string? Str(JsonElement? element) => element is { ValueKind: JsonValueKind.String } e ? e.GetString() : null;

    static string? Str(JsonElement element, string name) => Str(Prop(element, name));
}
