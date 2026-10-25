using System.Diagnostics;
using System.Text;

namespace Karvon.Git;

public enum GitChangeKind
{
    Modified,
    Added,
    Deleted,
    Renamed,
    Untracked,
    Conflicted,
}

public sealed record GitBranchStatus(string? Branch, string? Upstream, int Ahead, int Behind);

/// <summary>What git can be in the middle of, its conflicts waiting.</summary>
public enum GitOperation
{
    Merge,
    CherryPick,
    Revert,
    Rebase,
}

public static class GitOperations
{
    public static string Command(this GitOperation operation) => operation switch
    {
        GitOperation.Merge => "merge",
        GitOperation.CherryPick => "cherry-pick",
        GitOperation.Revert => "revert",
        _ => "rebase",
    };

    public static string Verb(this GitOperation operation) => operation switch
    {
        GitOperation.CherryPick => "cherry-pick",
        _ => operation.Command(),
    };
}

public sealed record GitFileChange(string Path, string RelativePath, GitChangeKind Kind, bool Staged, string? OriginalPath)
{
    /// <summary>VS / Git one-letter status.</summary>
    public string Letter => Kind switch
    {
        GitChangeKind.Modified => "M",
        GitChangeKind.Added => "A",
        GitChangeKind.Deleted => "D",
        GitChangeKind.Renamed => "R",
        GitChangeKind.Untracked => "U",
        _ => "C",
    };
}

public sealed record GitStatus(GitBranchStatus Branch, IReadOnlyList<GitFileChange> Changes);

public sealed record GitResult(int ExitCode, string Output)
{
    public bool Success => ExitCode == 0;
}

/// <summary>Repository operations over the git CLI (the user's config, hooks and credential helpers apply).</summary>
public sealed class GitRepository
{
    GitRepository(string root, string gitDir)
    {
        Root = root;
        GitDir = gitDir;
    }

    public string Root { get; }

    /// <summary>The repository's git directory (usually Root/.git; elsewhere for worktrees).</summary>
    public string GitDir { get; }

    public static async Task<GitRepository?> OpenAsync(string directory)
    {
        if (!Directory.Exists(directory))
            return null;
        var result = await RunAsync(directory, ["rev-parse", "--show-toplevel", "--absolute-git-dir"]);
        var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // git writes Windows' paths with slashes (C:/repo): as the platform writes them, as the rest of Karvon compares them.
        return result.Success && lines.Length == 2 ? new GitRepository(System.IO.Path.GetFullPath(lines[0]), System.IO.Path.GetFullPath(lines[1])) : null;
    }

    public string Relative(string path) => System.IO.Path.GetRelativePath(Root, path);

    /// <summary>git init in <paramref name="directory"/> (Create Git Repository), main its first branch.</summary>
    public static Task<GitResult> InitAsync(string directory) => RunAsync(directory, ["init", "--initial-branch=main"]);

    /// <summary>git clone into <paramref name="directory"/> (which must not exist or be empty), streaming progress.</summary>
    public static async Task<GitResult> CloneAsync(string url, string directory, Action<string>? output = null)
    {
        var parent = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(directory))!;
        Directory.CreateDirectory(parent);
        return await RunAsync(parent, ["clone", "--progress", "--", url, directory], output: output);
    }

    public async Task<GitStatus> StatusAsync()
    {
        var result = await RunAsync(Root, ["status", "--porcelain=v2", "--branch", "--untracked-files=all", "-z"], optionalLocks: false);
        return Parse(result.Output);
    }

    internal GitStatus Parse(string porcelain)
    {
        string? branch = null, upstream = null;
        int ahead = 0, behind = 0;
        var changes = new List<GitFileChange>();
        var tokens = porcelain.Split('\0');
        for (var i = 0; i < tokens.Length; i++)
        {
            var t = tokens[i];
            if (t.Length == 0)
                continue;
            if (t.StartsWith("# branch.head ", StringComparison.Ordinal))
            {
                branch = t["# branch.head ".Length..];
                if (branch == "(detached)")
                    branch = null;
            }
            else if (t.StartsWith("# branch.upstream ", StringComparison.Ordinal))
            {
                upstream = t["# branch.upstream ".Length..];
            }
            else if (t.StartsWith("# branch.ab ", StringComparison.Ordinal))
            {
                var parts = t["# branch.ab ".Length..].Split(' ');
                ahead = int.Parse(parts[0].TrimStart('+'));
                behind = int.Parse(parts[1].TrimStart('-'));
            }
            else if (t.StartsWith("? ", StringComparison.Ordinal))
            {
                Add(t[2..], GitChangeKind.Untracked, staged: false, null);
            }
            else if (t[0] is '1' or '2' or 'u')
            {
                var fieldCount = t[0] switch { '1' => 8, '2' => 9, _ => 10 };
                var fields = t.Split(' ', fieldCount + 1);
                var xy = fields[1];
                var path = fields[fieldCount];
                string? original = null;
                if (t[0] == '2' && i + 1 < tokens.Length)
                    original = tokens[++i];
                if (t[0] == 'u')
                {
                    Add(path, GitChangeKind.Conflicted, staged: false, null);
                    continue;
                }
                if (xy[0] != '.')
                    Add(path, KindOf(xy[0]), staged: true, original);
                if (xy[1] != '.')
                    Add(path, KindOf(xy[1]), staged: false, original);
            }
        }
        return new GitStatus(new GitBranchStatus(branch, upstream, ahead, behind), changes);

        void Add(string relative, GitChangeKind kind, bool staged, string? original) =>
            changes.Add(new GitFileChange(System.IO.Path.GetFullPath(System.IO.Path.Combine(Root, relative)), relative, kind, staged,
                original is null ? null : System.IO.Path.GetFullPath(System.IO.Path.Combine(Root, original))));

        static GitChangeKind KindOf(char c) => c switch
        {
            'A' or 'C' => GitChangeKind.Added,
            'D' => GitChangeKind.Deleted,
            'R' => GitChangeKind.Renamed,
            'U' => GitChangeKind.Conflicted,
            _ => GitChangeKind.Modified,
        };
    }

    public Task<GitResult> StageAsync(IEnumerable<string> paths) => RunAsync(Root, ["add", "--", .. paths.Select(Relative)]);

    public Task<GitResult> UnstageAsync(IEnumerable<string> paths) => RunAsync(Root, ["restore", "--staged", "--", .. paths.Select(Relative)]);

    /// <summary>Undo working-tree changes; untracked files are deleted like VS's "Undo Changes".</summary>
    public async Task<GitResult> DiscardAsync(GitFileChange change)
    {
        if (change.Kind == GitChangeKind.Untracked)
        {
            File.Delete(change.Path);
            return new GitResult(0, "");
        }
        return await RunAsync(Root, ["restore", "--", Relative(change.Path)]);
    }

    /// <summary>Commits staged changes, or every change when <paramref name="all"/> (VS "Commit All").</summary>
    public async Task<GitResult> CommitAsync(string message, bool all)
    {
        if (all)
        {
            var add = await RunAsync(Root, ["add", "--all"]);
            if (!add.Success)
                return add;
        }
        return await RunAsync(Root, ["commit", "-m", message]);
    }

    public Task<GitResult> FetchAsync(Action<string>? output = null) => RunAsync(Root, ["fetch", "--prune"], output: output);

    public Task<GitResult> PullAsync(Action<string>? output = null) => RunAsync(Root, ["pull", "--ff-only"], output: output);

    /// <summary>One ref fetched from a remote ("feature", "pull/7/head:pr/7"); a fast-forward only, as git fetches a branch.</summary>
    public Task<GitResult> FetchRefAsync(string remote, string refspec, Action<string>? output = null) =>
        RunAsync(Root, ["fetch", remote, refspec], output: output);

    /// <summary>The subjects of the commits on HEAD that a ref ("origin/main") does not have, oldest first; null when the ref is not there.</summary>
    public async Task<IReadOnlyList<string>?> SubjectsSinceAsync(string reference)
    {
        var result = await RunAsync(Root, ["log", "--reverse", "--format=%s", $"{reference}..HEAD"], optionalLocks: false);
        return result.Success ? result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries) : null;
    }

    public async Task<GitResult> PushAsync(GitBranchStatus branch, Action<string>? output = null) =>
        branch.Upstream is null && branch.Branch is { } name
            ? await RunAsync(Root, ["push", "--set-upstream", "origin", name], output: output)
            : await RunAsync(Root, ["push"], output: output);

    /// <summary>A remote's URL as git has it, or null. It may carry a credential: never show it as it is.</summary>
    public async Task<string?> RemoteUrlAsync(string remote = "origin")
    {
        var result = await RunAsync(Root, ["remote", "get-url", remote]);
        return result.Success && result.Output.Trim() is { Length: > 0 } url ? url : null;
    }

    /// <summary>The subjects of commits by their hashes (those the repository has; the others are not in it).</summary>
    public async Task<IReadOnlyDictionary<string, string>> SubjectsAsync(IEnumerable<string> hashes)
    {
        var list = hashes.Where(h => h.Length >= 7 && h.All(char.IsAsciiHexDigit)).Distinct().ToList();
        if (list.Count == 0)
            return new Dictionary<string, string>();
        var result = await RunAsync(Root, ["log", "--no-walk=unsorted", "--ignore-missing", "--format=%H%x09%s", .. list]);
        var subjects = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            if (line.Split('\t', 2) is [var hash, var subject])
                subjects[hash.Trim()] = subject.TrimEnd('\r');
        return subjects;
    }

    public async Task<IReadOnlyList<string>> BranchesAsync()
    {
        var result = await RunAsync(Root, ["for-each-ref", "--format=%(refname:short)", "refs/heads"]);
        return result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>Remote branches ("origin/feature"), without the remotes' HEAD.</summary>
    public async Task<IReadOnlyList<string>> RemoteBranchesAsync()
    {
        var result = await RunAsync(Root, ["for-each-ref", "--format=%(refname:short)", "refs/remotes"]);
        return result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(b => !b.EndsWith("/HEAD", StringComparison.Ordinal) && b.Contains('/')).Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>Whether a ref exists ("refs/heads/main", "refs/remotes/origin/main").</summary>
    public async Task<bool> HasRefAsync(string reference) =>
        (await RunAsync(Root, ["rev-parse", "--verify", "--quiet", reference], optionalLocks: false)).Success;

    public Task<GitResult> SwitchAsync(string branch) => RunAsync(Root, ["switch", branch]);

    /// <summary>A local branch made to track a remote one ("origin/feature" → "feature"), as Git Changes does.</summary>
    public Task<GitResult> SwitchTrackingAsync(string local, string remote) => RunAsync(Root, ["switch", "-c", local, "--track", remote]);

    /// <summary>The changes a commit would record, for Claude to describe: the staged ones, or everything against HEAD.</summary>
    public async Task<string> DiffForMessageAsync(bool staged) =>
        (await RunAsync(Root, staged ? ["diff", "--no-color", "--cached"] : ["diff", "--no-color", "HEAD"], optionalLocks: false)).Output;

    /// <summary>The latest commit subjects, newest first (the style a written message follows).</summary>
    public async Task<IReadOnlyList<string>> RecentSubjectsAsync(int count)
    {
        var result = await RunAsync(Root, ["log", "-n", count.ToString(System.Globalization.CultureInfo.InvariantCulture), "--format=%s"], optionalLocks: false);
        return result.Success ? result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [];
    }

    /// <summary>"Name &lt;email&gt;" from git config: who commits are made as (null when no name is set).</summary>
    public async Task<string?> AuthorAsync()
    {
        var name = (await RunAsync(Root, ["config", "user.name"], optionalLocks: false)).Output.Trim();
        var email = (await RunAsync(Root, ["config", "user.email"], optionalLocks: false)).Output.Trim();
        return name.Length == 0 ? null : email.Length > 0 ? $"{name} <{email}>" : name;
    }

    public Task<GitResult> CreateBranchAsync(string name) => RunAsync(Root, ["switch", "-c", name]);

    /// <summary>The file's content at HEAD, or null when it is not tracked there.</summary>
    public async Task<string?> ShowHeadAsync(string path)
    {
        var rel = Relative(path).Replace('\\', '/');
        var result = await RunAsync(Root, ["show", $"HEAD:{rel}"], optionalLocks: false);
        return result.Success ? result.Output : null;
    }

    /// <summary>A file's content at a revision (a commit's hash, "HEAD~1"), or null when it is not there.</summary>
    public async Task<string?> ShowAtAsync(string revision, string relativePath)
    {
        var result = await RunAsync(Root, ["show", $"{revision}:{relativePath.Replace('\\', '/')}"], optionalLocks: false);
        return result.Success ? result.Output : null;
    }

    /// <summary>
    /// The history, newest first in topological order (children before their parents, as the graph draws them): of HEAD
    /// or of every branch, remote and tag; of one file (followed through renames) when given.
    /// </summary>
    public async Task<IReadOnlyList<GitCommit>> LogAsync(int skip, int count, bool allRefs, string? path = null)
    {
        List<string> args = ["log", "--topo-order", GitLog.Format, $"--skip={skip}", $"-n{count}"];
        if (allRefs && path is null)
            args.AddRange(["--branches", "--remotes", "--tags", "HEAD"]);
        if (path is not null)
            args.AddRange(["--follow", "--", Relative(path)]);
        var result = await RunAsync(Root, args, optionalLocks: false);
        return result.Success ? GitLog.Parse(result.Output) : [];
    }

    /// <summary>A commit whole: its message and committer, and the files it changed against its first parent (all of them for the first commit).</summary>
    public async Task<GitCommitDetails?> CommitDetailsAsync(GitCommit commit)
    {
        var message = await RunAsync(Root, ["show", "-s", "--format=%B%x1f%cn <%ce>%x1f%ct", commit.Hash], optionalLocks: false);
        if (!message.Success || message.Output.Split('\u001f') is not [var body, var committer, var at])
            return null;
        // A merge's changes against its first parent (diff-tree's -m would list them against each), the first commit's all.
        var files = await RunAsync(Root, commit.Parents.Count > 0
            ? ["diff-tree", "--no-commit-id", "-r", "-M", "--name-status", commit.Parents[0], commit.Hash]
            : ["diff-tree", "--no-commit-id", "-r", "-M", "--name-status", "--root", commit.Hash], optionalLocks: false);
        var date = long.TryParse(at.Trim(), out var seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime() : commit.Date;
        return new GitCommitDetails(commit, body.TrimEnd(), committer, date, files.Success ? GitLog.Files(files.Output) : []);
    }

    /// <summary>Where HEAD and every ref point: the same until one moves (a commit, a fetch, a checkout, a tag).</summary>
    public async Task<string> RefsSignatureAsync()
    {
        var head = await RunAsync(Root, ["rev-parse", "HEAD"], optionalLocks: false);
        var refs = await RunAsync(Root, ["for-each-ref", "--format=%(objectname) %(refname)"], optionalLocks: false);
        return head.Output.Trim() + "\n" + refs.Output;
    }

    /// <summary>A branch made at a commit (not switched to).</summary>
    public Task<GitResult> BranchAtAsync(string name, string commit) => RunAsync(Root, ["branch", name, commit]);

    /// <summary>The commit checked out by itself (a detached HEAD).</summary>
    public Task<GitResult> CheckoutDetachedAsync(string commit) => RunAsync(Root, ["switch", "--detach", commit]);

    public Task<GitResult> CherryPickAsync(string commit) => RunAsync(Root, ["cherry-pick", commit]);

    /// <summary>A commit undone by a new one (a merge's against its first parent).</summary>
    public Task<GitResult> RevertAsync(GitCommit commit) =>
        RunAsync(Root, commit.IsMerge ? ["revert", "--no-edit", "-m", "1", commit.Hash] : ["revert", "--no-edit", commit.Hash]);

    /// <summary>The branch reset to a commit: soft (its changes staged), mixed (unstaged) or hard (gone).</summary>
    public Task<GitResult> ResetAsync(string commit, string mode) => RunAsync(Root, ["reset", "--" + mode, commit]);

    /// <summary>A tag at a commit.</summary>
    public Task<GitResult> TagAsync(string name, string commit) => RunAsync(Root, ["tag", name, commit]);

    /// <summary>
    /// The commit each line of a file last changed in; of the text given (the editor's, unsaved edits included: those
    /// lines are not committed yet) or else the file on disk. Null when the file is not tracked.
    /// </summary>
    public async Task<IReadOnlyList<GitBlameCommit>?> BlameAsync(string path, string? contents = null)
    {
        List<string> args = ["blame", "--porcelain"];
        if (contents is not null)
            args.AddRange(["--contents", "-"]);
        args.AddRange(["--", Relative(path)]);
        var result = await RunAsync(Root, args, optionalLocks: false, input: contents);
        return result.Success ? GitBlame.Parse(result.Output) : null;
    }

    /// <summary>What git is in the middle of (its state files say), or null.</summary>
    public GitOperation? InProgress()
    {
        bool Has(string name) => File.Exists(System.IO.Path.Combine(GitDir, name)) || Directory.Exists(System.IO.Path.Combine(GitDir, name));
        return Has("rebase-merge") || Has("rebase-apply") ? GitOperation.Rebase
            : Has("MERGE_HEAD") ? GitOperation.Merge
            : Has("CHERRY_PICK_HEAD") ? GitOperation.CherryPick
            : Has("REVERT_HEAD") ? GitOperation.Revert
            : null;
    }

    public Task<GitResult> AbortAsync(GitOperation operation) => RunAsync(Root, [operation.Command(), "--abort"]);

    /// <summary>The operation on to its next step, its commit message as git wrote it (no editor is opened).</summary>
    public Task<GitResult> ContinueAsync(GitOperation operation) => RunAsync(Root, ["-c", "core.editor=true", operation.Command(), "--continue"]);

    /// <summary>The file's staged content (the index), or null when it is not in the index.</summary>
    public async Task<string?> ShowIndexAsync(string path)
    {
        var rel = Relative(path).Replace('\\', '/');
        var result = await RunAsync(Root, ["show", $":{rel}"], optionalLocks: false);
        return result.Success ? result.Output : null;
    }

    /// <summary>Unified diff of the working file (or the staged version) against HEAD, for the diff view.</summary>
    public async Task<string> DiffAsync(GitFileChange change)
    {
        var rel = Relative(change.Path);
        var result = change.Kind == GitChangeKind.Untracked
            ? await RunAsync(Root, ["diff", "--no-color", "--no-index", "--", "/dev/null", rel], optionalLocks: false)
            : await RunAsync(Root, change.Staged ? ["diff", "--no-color", "--cached", "--", rel] : ["diff", "--no-color", "--", rel], optionalLocks: false);
        return result.Output;
    }

    static async Task<GitResult> RunAsync(string directory, IReadOnlyList<string> args, bool optionalLocks = true, Action<string>? output = null,
        string? input = null)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = input is not null,
            UseShellExecute = false,
            WorkingDirectory = directory,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        // Never block on a credential prompt the user cannot see; keep output parseable.
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["LC_ALL"] = "C";
        if (!optionalLocks)
            psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("git could not be started.");
        if (input is not null)
            _ = Task.Run(async () =>
            {
                try
                {
                    // UTF-8 without a byte order mark, as the file is.
                    await process.StandardInput.BaseStream.WriteAsync(new UTF8Encoding(false).GetBytes(input));
                    process.StandardInput.Close();
                }
                catch (IOException)
                {
                    // git exited before reading it all (an untracked file): its exit code says so.
                }
            });
        var stdout = new StringBuilder();
        var stderrTask = Task.Run(async () =>
        {
            var sb = new StringBuilder();
            while (await process.StandardError.ReadLineAsync() is { } line)
            {
                sb.AppendLine(line);
                output?.Invoke(line);
            }
            return sb.ToString();
        });
        var buffer = new char[8192];
        int n;
        while ((n = await process.StandardOutput.ReadAsync(buffer)) > 0)
        {
            stdout.Append(buffer, 0, n);
            if (output is not null)
                foreach (var line in new string(buffer, 0, n).Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    output(line);
        }
        var stderr = await stderrTask;
        await process.WaitForExitAsync();
        return new GitResult(process.ExitCode, process.ExitCode == 0 ? stdout.ToString() : stdout + stderr);
    }
}
