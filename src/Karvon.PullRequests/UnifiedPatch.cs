using System.Text.RegularExpressions;

namespace Karvon.PullRequests;

/// <summary>
/// A file's version before a change, from its version after it and the change's unified diff (the hunks a service gives
/// per file): what the pull request's diff is against, its merge base, without asking the service for that version.
/// </summary>
public static partial class UnifiedPatch
{
    /// <summary>The text before; null when the patch is not one (empty, binary, cut by the service) or does not fit the text.</summary>
    public static string? Reverse(string after, string patch)
    {
        if (!patch.StartsWith("@@", StringComparison.Ordinal))
            return null;
        var current = after.Length == 0 ? [] : after.Replace("\r\n", "\n").Split('\n');
        var trailing = after.EndsWith('\n');
        if (trailing)
            current = current[..^1];
        var before = new List<string>();
        var at = 0;
        foreach (var line in patch.Replace("\r\n", "\n").Split('\n'))
        {
            if (Hunk().Match(line) is { Success: true } hunk)
            {
                var start = int.Parse(hunk.Groups["start"].Value);
                var count = hunk.Groups["count"].Success ? int.Parse(hunk.Groups["count"].Value) : 1;
                // A hunk that adds nothing is placed after its start line; any other starts at it.
                var to = count == 0 ? start : start - 1;
                if (to < at || to > current.Length)
                    return null;
                before.AddRange(current[at..to]);
                at = to;
                continue;
            }
            if (line.Length == 0 || line[0] == '\\')
                continue;
            switch (line[0])
            {
                case ' ':
                    if (at >= current.Length)
                        return null;
                    before.Add(current[at++]);
                    break;
                case '-':
                    before.Add(line[1..]);
                    break;
                case '+':
                    if (at >= current.Length)
                        return null;
                    at++;
                    break;
            }
        }
        before.AddRange(current[at..]);
        return before.Count == 0 ? "" : string.Join('\n', before) + (trailing || after.Length == 0 ? "\n" : "");
    }

    [GeneratedRegex(@"^@@ -\d+(?:,\d+)? \+(?<start>\d+)(?:,(?<count>\d+))? @@")]
    private static partial Regex Hunk();
}
