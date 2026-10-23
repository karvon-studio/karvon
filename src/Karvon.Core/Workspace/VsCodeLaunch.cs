using System.Text.Json;
using System.Text.Json.Nodes;
using Karvon.Modules;

namespace Karvon.Workspace;

/// <summary>
/// The launch configurations of a folder's .vscode/launch.json, as VS Code reads them: each is given to the debugger of its
/// type as it is, the platform's own section (linux, osx, windows) over the rest. Its variables are put in when it starts.
/// Its ${input:...} asks when it starts (promptString, pickString). Its compounds start several configurations at once.
/// Not followed yet: ${command:...}.
/// </summary>
public static class VsCodeLaunch
{
    /// <summary>A compound of launch.json: its configurations started together (stopAll: one that ends stops the rest).</summary>
    public sealed record Compound(string Name, IReadOnlyList<DebugLaunch> Launches, bool StopAll, string? PreLaunchTask);

    /// <summary>Its compounds, of the configurations it has (a name it has not is left out); none without a launch.json.</summary>
    public static IReadOnlyList<Compound> ReadCompounds(string workspace, IReadOnlyList<DebugLaunch> launches)
    {
        var file = FileOf(workspace);
        if (!File.Exists(file) || JsonNode.Parse(File.ReadAllText(file), documentOptions: Jsonc) is not JsonObject { } document || document["compounds"] is not JsonArray compounds)
            return [];
        var result = new List<Compound>();
        foreach (var node in compounds)
        {
            if (node is not JsonObject compound || compound["name"] is not JsonValue n || !n.TryGetValue<string>(out var name) || compound["configurations"] is not JsonArray names)
                continue;
            var chosen = names.Select(c => c is JsonValue v && v.TryGetValue<string>(out var text) ? text : c?["name"]?.GetValue<string>())
                .Select(label => launches.FirstOrDefault(l => l.Name == label)).OfType<DebugLaunch>().ToList();
            if (chosen.Count == 0 || result.Any(c => c.Name == name) || launches.Any(l => l.Name == name))
                continue;
            result.Add(new Compound(name, chosen, compound["stopAll"] is JsonValue s && s.TryGetValue<bool>(out var stopAll) && stopAll,
                compound["preLaunchTask"] is JsonValue p && p.TryGetValue<string>(out var pre) ? pre : null));
        }
        return result;
    }

    public const string Source = "launch.json";

    static readonly JsonDocumentOptions Jsonc = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    static readonly string[] Platforms = ["linux", "osx", "windows"];

    static string PlatformKey => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "osx" : "linux";

    public static string FileOf(string workspace) => Path.Combine(workspace, ".vscode", "launch.json");

    /// <summary>Its configurations (none without a launch.json); one that cannot be read throws a JsonException or an IOException.</summary>
    public static IReadOnlyList<DebugLaunch> Read(string workspace)
    {
        var file = FileOf(workspace);
        if (!File.Exists(file))
            return [];
        var root = JsonNode.Parse(File.ReadAllText(file), documentOptions: Jsonc);
        if (root is not JsonObject { } document || document["configurations"] is not JsonArray configurations)
            return [];
        var inputs = TaskInputs.Read(document);
        var result = new List<DebugLaunch>();
        foreach (var node in configurations)
        {
            if (node is not JsonObject configuration || configuration["type"] is null)
                continue;
            var own = (JsonObject)configuration.DeepClone();
            if (own[PlatformKey] is JsonObject platform)
                foreach (var (name, value) in platform)
                    own[name] = value?.DeepClone();
            foreach (var key in Platforms)
                own.Remove(key);
            var label = own["name"] is JsonValue n && n.TryGetValue<string>(out var text) ? text : own["type"]!.ToString();
            if (result.All(l => l.Name != label))
                result.Add(new DebugLaunch(label, own) { Source = Source, Inputs = inputs });
        }
        return result;
    }
}
