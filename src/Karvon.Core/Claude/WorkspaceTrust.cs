namespace Karvon.Claude;

/// <summary>
/// The folders the user trusts (Settings' <c>TrustedFolders</c>, and what is in them): what a repository brings for Claude
/// is used only in them (its Claude Code settings with their hooks, allow rules and MCP approvals, its <c>.mcp.json</c>,
/// Gemini's servers, its <c>.vs</c> data sources), and until then building, testing and running its code and every change
/// ask. Claude Code does not ask in the sessions Karvon starts: its trust dialog is the interactive CLI's. The home folder
/// (no solution open) is the user's own.
/// </summary>
public static class WorkspaceTrust
{
    static readonly StringComparison Comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static bool IsTrusted(IEnumerable<string> trusted, string? folder)
    {
        if (folder is null || Full(folder) is not { } full)
            return folder is null;
        var home = Full(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        return full.Equals(home, Comparison) || trusted.Select(Full).Any(t => t is not null && (full.Equals(t, Comparison)
            || full.StartsWith(t + Path.DirectorySeparatorChar, Comparison)));
    }

    /// <summary>Trusts <paramref name="folder"/> (and what is in it) from now on.</summary>
    public static void Trust(List<string> trusted, string folder)
    {
        if (Full(folder) is { } full && !IsTrusted(trusted, full))
            trusted.Add(full);
    }

    static string? Full(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
