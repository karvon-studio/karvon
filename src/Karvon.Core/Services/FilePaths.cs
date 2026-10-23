namespace Karvon.Services;

/// <summary>
/// Full paths compared as the platform's file system does: below a folder is after one of its separators ('/', and '\'
/// on Windows too), and on Windows the case does not matter.
/// </summary>
public static class FilePaths
{
    public static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    static bool IsSeparator(char c) => c == '/' || OperatingSystem.IsWindows() && c == '\\';

    /// <summary>Whether <paramref name="path"/> is in <paramref name="directory"/> or deeper (not the folder itself).</summary>
    public static bool IsUnder(string path, string directory)
    {
        // A folder written with its separator ("/", "C:\", "/repo/").
        if (directory.Length > 0 && IsSeparator(directory[^1]))
            return path.Length > directory.Length && path.StartsWith(directory, Comparison);
        return path.Length > directory.Length + 1 && path.StartsWith(directory, Comparison) && IsSeparator(path[directory.Length]);
    }

    /// <summary>Whether <paramref name="path"/> is the folder itself, or in it.</summary>
    public static bool IsSameOrUnder(string path, string directory) => string.Equals(path, directory, Comparison) || IsUnder(path, directory);
}
