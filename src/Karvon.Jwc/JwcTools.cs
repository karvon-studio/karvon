using System.Text.Json;
using System.Text.Json.Nodes;
using Karvon.Workspace;

namespace Karvon.Jwc;

/// <summary>The jwc the user has (on the PATH, or where its installer puts it when the PATH has none), and a project's manifest.</summary>
public static class JwcTools
{
    public const string Manifest = "jwcproj.json";

    public const string NotFound = "jwc was not found: install it (github.com/just-web-code/jwc-lang) or put it on the PATH.";

    static string Exe => OperatingSystem.IsWindows() ? "jwc.exe" : "jwc";

    /// <summary>~/.jwc/bin, where jwc's installer puts it, when the PATH has no jwc and that folder has.</summary>
    public static string? DirectoryOffPath(string? path = null, string? home = null)
    {
        path ??= Environment.GetEnvironmentVariable("PATH") ?? "";
        if (TaskVariables.Find(Exe, path, "/") is not null)
            return null;
        var dir = Path.Combine(home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".jwc", "bin");
        return File.Exists(Path.Combine(dir, Exe)) ? dir : null;
    }

    /// <summary>The PATH jwc runs with: its folder first when the PATH has none.</summary>
    public static string PathWithJwc(string path) => DirectoryOffPath(path) is { } jwc ? jwc + Path.PathSeparator + path : path;

    public static string? Find(string path) => TaskVariables.Find(Exe, PathWithJwc(path), "/");

    /// <summary>A project's name and type from its manifest; the folder's name and "app" for what it does not say.</summary>
    public static (string Name, string Type) ReadManifest(string workspace)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(workspace));
        try
        {
            var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(workspace, Manifest)));
            return (StringOf(manifest?["name"]) ?? name, StringOf(manifest?["type"]) ?? "app");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return (name, "app");
        }
    }

    static string? StringOf(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;
}
