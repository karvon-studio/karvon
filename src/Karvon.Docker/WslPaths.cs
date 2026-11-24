using System.Text.RegularExpressions;

namespace Karvon.Docker;

/// <summary>
/// A path as Windows and as a WSL distribution say it: a drive is mounted there (<c>E:\x</c> is <c>/mnt/e/x</c>), and the
/// distribution's own files are <c>\\wsl.localhost\Ubuntu\...</c> (or <c>\\wsl$\...</c>) here.
/// </summary>
public static partial class WslPaths
{
    /// <summary>The path in WSL, its <c>.</c> and <c>..</c> worked out; null for one WSL does not see (a network share), or no full path.</summary>
    public static string? ToLinux(string path)
    {
        if (Drive().IsMatch(path))
            return Normalize($"/mnt/{char.ToLowerInvariant(path[0])}/{path[2..]}");
        if (Share().Match(path) is { Success: true } share)
            return Normalize("/" + share.Groups["path"].Value);
        return path.StartsWith('/') && !path.StartsWith("//", StringComparison.Ordinal) ? Normalize(path) : null;
    }

    /// <summary>The Windows path of one on a mounted drive (<c>/mnt/e/x</c> is <c>E:\x</c>); null for the distribution's own files.</summary>
    public static string? ToWindows(string path) =>
        Mounted().Match(path) is { Success: true } mounted
            ? $"{char.ToUpperInvariant(mounted.Groups["drive"].Value[0])}:\\{mounted.Groups["path"].Value.TrimStart('/').Replace('/', '\\')}"
            : null;

    /// <summary>Whether <paramref name="path"/> is <paramref name="folder"/> or inside it, both WSL's (a drive's files whatever their case, as Windows).</summary>
    public static bool IsInside(string path, string folder)
    {
        var comparison = Mounted().IsMatch(folder) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var (inner, outer) = (Normalize(path), Normalize(folder));
        return inner.Equals(outer, comparison) || inner.StartsWith(outer.TrimEnd('/') + "/", comparison);
    }

    static string Normalize(string path)
    {
        var parts = new List<string>();
        foreach (var part in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == "..")
            {
                if (parts.Count > 0)
                    parts.RemoveAt(parts.Count - 1);
            }
            else if (part != ".")
                parts.Add(part);
        }
        return "/" + string.Join('/', parts);
    }

    [GeneratedRegex(@"^[A-Za-z]:([\\/]|$)")]
    private static partial Regex Drive();

    [GeneratedRegex(@"^[\\/]{2}(wsl\.localhost|wsl\$)[\\/]+[^\\/]+(?<path>.*)$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Share();

    [GeneratedRegex(@"^/mnt/(?<drive>[A-Za-z])(?<path>/.*)?$", RegexOptions.Singleline)]
    private static partial Regex Mounted();
}
