namespace Karvon.Git;

/// <summary>A conflict git left in a file: what this side (ours) has, what the other (theirs) has, and their base when diff3 says it.</summary>
public sealed record MergeConflict(string Current, string Incoming, string? Base, string CurrentLabel, string IncomingLabel)
{
    /// <summary>The conflict as git wrote it, markers and all.</summary>
    public string Marked { get; init; } = "";
}

/// <summary>A run of a conflicted file: text both sides agree on (null conflict), or a conflict.</summary>
public sealed record MergeSegment(string Text, MergeConflict? Conflict);

/// <summary>
/// Reads the conflict markers git writes in a file it could not merge (<c>&lt;&lt;&lt;&lt;&lt;&lt;&lt; ours</c>,
/// <c>|||||||</c> base when merge.conflictStyle is diff3, <c>=======</c>, <c>&gt;&gt;&gt;&gt;&gt;&gt;&gt; theirs</c>) into the
/// runs both sides agree on and the conflicts between them.
/// </summary>
public static class MergeConflicts
{
    const int Size = 7;

    static bool Marker(string line, char c, bool label = true)
    {
        if (line.Length < Size || line.AsSpan(0, Size).IndexOfAnyExcept(c) >= 0)
            return false;
        var rest = line.AsSpan(Size).TrimEnd("\r\n");
        // ======= stands alone; the others may name their side.
        return rest.Length == 0 || label && rest[0] == ' ' || !label && rest.IsWhiteSpace();
    }

    public static bool HasMarkers(string text) => Parse(text).Any(s => s.Conflict is not null);

    /// <summary>The file's runs, in order; an unclosed conflict is left as text.</summary>
    public static IReadOnlyList<MergeSegment> Parse(string text)
    {
        var segments = new List<MergeSegment>();
        var common = new System.Text.StringBuilder();
        var lines = SplitKeepingEnds(text);
        for (var i = 0; i < lines.Count; i++)
        {
            if (!Marker(lines[i], '<'))
            {
                common.Append(lines[i]);
                continue;
            }
            // A conflict: ours until ||||||| or =======, the base until =======, theirs until >>>>>>>.
            var start = i;
            var current = new System.Text.StringBuilder();
            var incoming = new System.Text.StringBuilder();
            System.Text.StringBuilder? @base = null;
            var part = current;
            var closed = false;
            string incomingLabel = "";
            for (i++; i < lines.Count; i++)
            {
                var line = lines[i];
                if (part == current && Marker(line, '|'))
                    part = @base = new System.Text.StringBuilder();
                else if (part != incoming && Marker(line, '=', label: false))
                    part = incoming;
                else if (part == incoming && Marker(line, '>'))
                {
                    incomingLabel = line[Size..].Trim();
                    closed = true;
                    break;
                }
                else
                    part.Append(line);
            }
            if (!closed)
            {
                // Not git's: the lines stay as they are.
                for (var j = start; j < lines.Count; j++)
                    common.Append(lines[j]);
                break;
            }
            if (common.Length > 0)
                segments.Add(new MergeSegment(common.ToString(), null));
            common.Clear();
            var marked = string.Concat(lines.Skip(start).Take(i - start + 1));
            segments.Add(new MergeSegment(marked, new MergeConflict(current.ToString(), incoming.ToString(), @base?.ToString(),
                lines[start][Size..].Trim(), incomingLabel) { Marked = marked }));
        }
        if (common.Length > 0)
            segments.Add(new MergeSegment(common.ToString(), null));
        return segments;
    }

    static List<string> SplitKeepingEnds(string text)
    {
        var lines = new List<string>();
        var at = 0;
        while (at < text.Length)
        {
            var end = text.IndexOf('\n', at);
            if (end < 0)
            {
                lines.Add(text[at..]);
                break;
            }
            lines.Add(text[at..(end + 1)]);
            at = end + 1;
        }
        return lines;
    }
}
