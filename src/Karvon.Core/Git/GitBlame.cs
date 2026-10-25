namespace Karvon.Git;

/// <summary>The commit a line last changed in: who, when, why. All zeros for a line not committed yet.</summary>
public sealed record GitBlameCommit(string Hash, string Author, string Email, DateTimeOffset Date, string Summary)
{
    public bool IsUncommitted => Hash.All(c => c == '0');

    public string ShortHash => Hash[..Math.Min(8, Hash.Length)];
}

/// <summary>Reads <c>git blame --porcelain</c>: a commit for each line of the file, in order.</summary>
public static class GitBlame
{
    public static IReadOnlyList<GitBlameCommit> Parse(string porcelain)
    {
        var commits = new Dictionary<string, GitBlameCommit>(StringComparer.Ordinal);
        var lines = new List<GitBlameCommit>();
        string? hash = null;
        string author = "", email = "", summary = "";
        long time = 0;
        foreach (var raw in porcelain.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith('\t'))
            {
                // The line itself ends its entry: the commit's headers came the first time it was seen.
                if (hash is null)
                    continue;
                if (!commits.TryGetValue(hash, out var commit))
                    commits[hash] = commit = new GitBlameCommit(hash, author, email, DateTimeOffset.FromUnixTimeSeconds(time).ToLocalTime(), summary);
                lines.Add(commit);
                hash = null;
                continue;
            }
            var space = line.IndexOf(' ');
            var key = space < 0 ? line : line[..space];
            var value = space < 0 ? "" : line[(space + 1)..];
            if (hash is null && key.Length == 40 && key.All(char.IsAsciiHexDigit))
            {
                hash = key;
                (author, email, summary, time) = ("", "", "", 0);
                continue;
            }
            switch (key)
            {
                case "author":
                    author = value;
                    break;
                case "author-mail":
                    email = value.Trim('<', '>');
                    break;
                case "author-time":
                    long.TryParse(value, out time);
                    break;
                case "summary":
                    summary = value;
                    break;
            }
        }
        return lines;
    }
}
