using System.Globalization;

namespace Karvon.Git;

/// <summary>A commit as the history lists it: its parents, who and when, the refs at it (branches, tags, HEAD), its subject.</summary>
public sealed record GitCommit(string Hash, IReadOnlyList<string> Parents, string Author, string Email, DateTimeOffset Date, IReadOnlyList<GitRef> Refs, string Subject)
{
    public string ShortHash => Hash.Length > 8 ? Hash[..8] : Hash;

    public bool IsMerge => Parents.Count > 1;
}

public enum GitRefKind
{
    Head,
    Branch,
    Remote,
    Tag,
}

/// <summary>A ref at a commit: HEAD, a local branch (the checked-out one is <paramref name="IsCurrent"/>), a remote's branch, a tag.</summary>
public sealed record GitRef(string Name, GitRefKind Kind, bool IsCurrent = false);

/// <summary>A commit whole: its message, its committer, the files it changed against its first parent.</summary>
public sealed record GitCommitDetails(GitCommit Commit, string Message, string Committer, DateTimeOffset CommitDate, IReadOnlyList<GitCommitFile> Files);

/// <summary>A file a commit changed: its letter (A, M, D, R…), its path (and the one it was renamed from).</summary>
public sealed record GitCommitFile(string Letter, string Path, string? OldPath)
{
    public string Kind => Letter switch
    {
        "A" => "Added",
        "D" => "Deleted",
        "R" => "Renamed",
        "C" => "Copied",
        "T" => "Type changed",
        _ => "Modified",
    };
}

/// <summary>How the history's commits are read and written by git: `git log` with separators no message has.</summary>
public static class GitLog
{
    const char Field = '\u001f', Record = '\u001e';

    /// <summary>The format of each commit's line: hash, parents, author, email, date, refs, subject.</summary>
    public const string Format = "--format=%H%x1f%P%x1f%an%x1f%ae%x1f%at%x1f%D%x1f%s%x1e";

    public static IReadOnlyList<GitCommit> Parse(string output)
    {
        var commits = new List<GitCommit>();
        foreach (var record in output.Split(Record))
        {
            var fields = record.TrimStart('\n', '\r').Split(Field);
            if (fields.Length < 7 || fields[0].Length < 7)
                continue;
            var date = long.TryParse(fields[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime()
                : DateTimeOffset.MinValue;
            commits.Add(new GitCommit(fields[0], fields[1].Split(' ', StringSplitOptions.RemoveEmptyEntries), fields[2], fields[3], date, Refs(fields[5]), fields[6]));
        }
        return commits;
    }

    /// <summary>%D: "HEAD -> main, origin/main, tag: v1.0".</summary>
    static IReadOnlyList<GitRef> Refs(string decorations)
    {
        var refs = new List<GitRef>();
        foreach (var raw in decorations.Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.StartsWith("HEAD -> ", StringComparison.Ordinal))
                refs.Add(new GitRef(raw[8..], GitRefKind.Branch, IsCurrent: true));
            else if (raw == "HEAD")
                refs.Add(new GitRef("HEAD", GitRefKind.Head, IsCurrent: true));
            else if (raw.StartsWith("tag: ", StringComparison.Ordinal))
                refs.Add(new GitRef(raw[5..], GitRefKind.Tag));
            else if (raw.EndsWith("/HEAD", StringComparison.Ordinal))
                continue;
            else
                refs.Add(new GitRef(raw, raw.Contains('/') ? GitRefKind.Remote : GitRefKind.Branch));
        }
        return refs;
    }

    /// <summary>`git diff-tree --name-status`: "M\tpath", "R100\told\tnew".</summary>
    public static IReadOnlyList<GitCommitFile> Files(string output) =>
    [
        .. output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r').Split('\t')).Where(f => f.Length >= 2 && f[0].Length > 0)
            .Select(f => f[0][0] is 'R' or 'C' && f.Length >= 3 ? new GitCommitFile(f[0][..1], f[2], f[1]) : new GitCommitFile(f[0][..1], f[1], null)),
    ];
}
