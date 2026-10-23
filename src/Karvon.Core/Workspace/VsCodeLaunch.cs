using System.Text.Json;
using System.Text.Json.Nodes;
using Karvon.Modules;

namespace Karvon.Workspace;

/// <summary>
/// The launch configurations of a folder's .vscode/launch.json, as VS Code reads them: each is given to the debugger of its
/// type as it is, the platform's own section (linux, osx, windows) over the rest. Its variables are put in when it starts.
/// Not followed yet: compounds (several configurations at once), and ${input:...} and ${command:...}.
/// </summary>
public static class VsCodeLaunch
{
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
                result.Add(new DebugLaunch(label, own) { Source = Source });
        }
        return result;
    }
}
