using System.Text.RegularExpressions;

namespace Karvon.Python;

/// <summary>
/// A uv project (a uv.lock, or pyproject.toml's [tool.uv]): uv makes its .venv (uv sync, which builds the project, so
/// only when asked) and runs its scripts (pyproject.toml's [project.scripts]) in it.
/// </summary>
public static partial class UvProject
{
    public static bool Is(string workspace)
    {
        if (File.Exists(Path.Combine(workspace, "uv.lock")))
            return true;
        return Pyproject(workspace) is { } text && text.Contains("[tool.uv", StringComparison.Ordinal);
    }

    static string? Pyproject(string workspace)
    {
        var file = Path.Combine(workspace, "pyproject.toml");
        try
        {
            return File.Exists(file) ? File.ReadAllText(file) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"^\s*\[\s*project\.scripts\s*\]\s*$((?:\r?\n(?!\s*\[).*)*)", RegexOptions.Multiline)]
    private static partial Regex ScriptsTable();

    [GeneratedRegex(@"^\s*(?:""([^""]+)""|([A-Za-z0-9_.-]+))\s*=", RegexOptions.Multiline)]
    private static partial Regex Key();

    /// <summary>The names of its scripts, as [project.scripts] lists them.</summary>
    public static IReadOnlyList<string> Scripts(string workspace) =>
        Pyproject(workspace) is { } text && ScriptsTable().Match(text) is { Success: true } table
            ? [.. Key().Matches(table.Groups[1].Value).Select(k => k.Groups[1].Success ? k.Groups[1].Value : k.Groups[2].Value)]
            : [];
}
