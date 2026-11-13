using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Karvon.Git;
using static Karvon.Claude.Tools.ToolInput;

namespace Karvon.Claude.Tools;

/// <summary>
/// git for Claude, through Karvon: the status, a diff and the history read at once; a commit, a branch, a pull and a push
/// asked first. They run as the user's git does (the user's settings, hooks, credential helpers and ssh-agent), outside
/// Karvon's sandbox, which keeps the agents' own shell from the repository's settings and from the user's keys: a push
/// over SSH works from here, never from the sandbox.
/// </summary>
public static class GitClaudeTools
{
    public static IEnumerable<IClaudeTool> Create(Func<string?> workspace) =>
    [
        new GitStatusTool(workspace),
        new GitDiffTool(workspace),
        new GitLogTool(workspace),
        new GitCommitTool(workspace),
        new GitBranchTool(workspace),
        new GitFetchTool(workspace),
        new GitPullTool(workspace),
        new GitPushTool(workspace),
    ];

    /// <summary>The shell's git that writes or reaches a remote, which these tools do.</summary>
    public static IEnumerable<ClaudeShellRule> ShellRules =>
    [
        ClaudeShellRule.Command("git", @"git\s+(?:-C\s+\S+\s+)?(commit|push|pull|fetch)\b",
            "Karvon does git for you with your own credentials: use mcp__karvon__git_commit, git_push, git_pull or git_fetch (git_status, "
            + "git_diff and git_log read), not the shell: in Karvon's sandbox the shell cannot change .git or reach your keys."),
    ];
}

abstract class GitTool(Func<string?> workspace) : IClaudeTool
{
    const int MaxOutput = 60_000;

    public abstract string Name { get; }

    public abstract string Description { get; }

    public virtual JsonObject InputSchema => Schema("""{"type":"object","properties":{}}""");

    public abstract ClaudeToolPlan Plan(JsonElement input);

    public string? Refusal(JsonElement input) => workspace() is null ? "No folder is open in Karvon." : null;

    public async Task<ClaudeToolOutput> CallAsync(JsonElement input, CancellationToken cancellationToken)
    {
        if (workspace() is not { } folder || await GitRepository.OpenAsync(folder) is not { } repository)
            return ClaudeToolOutput.Error("The folder open in Karvon is not in a git repository.");
        return await RunAsync(repository, input, cancellationToken);
    }

    protected abstract Task<ClaudeToolOutput> RunAsync(GitRepository repository, JsonElement input, CancellationToken cancellationToken);

    /// <summary>What git said, or why it failed.</summary>
    protected static ClaudeToolOutput Said(GitResult result, string done)
    {
        var output = result.Output.Trim();
        if (output.Length > MaxOutput)
            output = output[..MaxOutput] + "\n… (cut)";
        return result.Success
            ? new ClaudeToolOutput(output.Length > 0 ? $"{done}\n{output}" : done)
            : ClaudeToolOutput.Error(output.Length > 0 ? output : $"git failed (exit code {result.ExitCode}).");
    }

    /// <summary>An array of strings in the input (none when it is not there).</summary>
    protected static List<string> Strings(JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).Where(v => v.Length > 0)]
            : [];

    protected static string Cut(string text) => text.Length > MaxOutput ? text[..MaxOutput] + "\n… (cut: narrow it with path)" : text;
}

/// <summary><c>git_status</c>: the branch, its remote and the changed files.</summary>
sealed class GitStatusTool(Func<string?> workspace) : GitTool(workspace)
{
    public override string Name => "git_status";

    public override string Description =>
        "The repository's branch, the remote branch it follows (ahead and behind), and each changed file: its status letter (M modified, "
        + "A added, D deleted, R renamed, U untracked, C conflicted) and whether it is staged.";

    public override ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Read, "Git status", null, "");

    protected override async Task<ClaudeToolOutput> RunAsync(GitRepository repository, JsonElement input, CancellationToken cancellationToken)
    {
        var status = await repository.StatusAsync();
        var branch = status.Branch;
        var text = new StringBuilder($"Branch {branch.Branch ?? "(detached)"}");
        text.Append(branch.Upstream is { } upstream ? $" → {upstream} (ahead {branch.Ahead}, behind {branch.Behind})" : " (no remote branch yet)").AppendLine();
        if (status.Changes.Count == 0)
            text.AppendLine("No changes.");
        foreach (var change in status.Changes)
            text.Append(change.Letter).Append(' ').Append(change.RelativePath.Replace('\\', '/'))
                .Append(change.OriginalPath is { } from ? $" (from {from})" : "").AppendLine(change.Staged ? " (staged)" : "");
        return new ClaudeToolOutput(text.ToString().TrimEnd());
    }
}

/// <summary><c>git_diff</c>: the changes not staged, or the staged ones, of one path or all.</summary>
sealed class GitDiffTool(Func<string?> workspace) : GitTool(workspace)
{
    public override string Name => "git_diff";

    public override string Description => "The changes as a diff: those not staged yet, or the staged ones (staged: true); of one file or folder (path) or all.";

    public override JsonObject InputSchema => Schema("""
        {"type":"object","properties":{
          "staged":{"type":"boolean","description":"The staged changes (what a commit records) instead of those not staged"},
          "path":{"type":"string","description":"A file or folder, relative to the repository"}}}
        """);

    public override ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Read, "Git diff", Str(input, "path"), "");

    protected override async Task<ClaudeToolOutput> RunAsync(GitRepository repository, JsonElement input, CancellationToken cancellationToken)
    {
        var result = await repository.DiffAsync(Bool(input, "staged"), Str(input, "path"));
        if (!result.Success)
            return ClaudeToolOutput.Error(result.Output.Trim());
        return new ClaudeToolOutput(result.Output.Trim() is { Length: > 0 } diff ? Cut(diff) : "No changes.");
    }
}

/// <summary><c>git_log</c>: the latest commits, of one file when given.</summary>
sealed class GitLogTool(Func<string?> workspace) : GitTool(workspace)
{
    public override string Name => "git_log";

    public override string Description => "The latest commits on the current branch, newest first (of one file when path is given): hash, date, author, subject.";

    public override JsonObject InputSchema => Schema("""
        {"type":"object","properties":{
          "count":{"type":"integer","description":"How many (20 when not given, 100 at most)"},
          "path":{"type":"string","description":"A file, relative to the repository"}}}
        """);

    public override ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Read, "Git log", Str(input, "path"), "");

    protected override async Task<ClaudeToolOutput> RunAsync(GitRepository repository, JsonElement input, CancellationToken cancellationToken)
    {
        var count = Int(input, "count", 20, 1, 100);
        var path = Str(input, "path") is { Length: > 0 } p ? Path.GetFullPath(p, repository.Root) : null;
        var commits = await repository.LogAsync(0, count, allRefs: false, path);
        return new ClaudeToolOutput(commits.Count == 0 ? "No commits."
            : string.Join("\n", commits.Select(c => $"{c.ShortHash} {c.Date:yyyy-MM-dd} {c.Author}: {c.Subject}")));
    }
}

/// <summary><c>git_commit</c>: the given files (or every change) committed with the message; asked first.</summary>
sealed class GitCommitTool(Func<string?> workspace) : GitTool(workspace)
{
    public override string Name => "git_commit";

    public override string Description =>
        "Commits with the message: the given files (staged first), or every change (all: true), or what is staged already. The user "
        + "is asked first. The repository's hooks run, as the user's git runs them.";

    public override JsonObject InputSchema => Schema("""
        {"type":"object","properties":{
          "message":{"type":"string","description":"The commit message"},
          "files":{"type":"array","items":{"type":"string"},"description":"Files to commit, relative to the repository"},
          "all":{"type":"boolean","description":"Every change, new files too"}},
         "required":["message"]}
        """);

    public override ClaudeToolPlan Plan(JsonElement input)
    {
        var files = Strings(input, "files");
        return new ClaudeToolPlan(ToolAccess.Write, "Commit", FirstLine(Str(input, "message")), "Make this commit?")
        {
            Code = (Str(input, "message") ?? "") + (files.Count > 0 ? "\n\n" + string.Join("\n", files) : Bool(input, "all") ? "\n\n(every change)" : "\n\n(what is staged)"),
        };
    }

    protected override async Task<ClaudeToolOutput> RunAsync(GitRepository repository, JsonElement input, CancellationToken cancellationToken)
    {
        if (Str(input, "message") is not { Length: > 0 } message)
            return ClaudeToolOutput.Error("Give the commit's message.");
        var files = Strings(input, "files");
        if (files.Count > 0)
        {
            var staged = await repository.StageAsync(files.Select(f => Path.GetFullPath(f, repository.Root)));
            if (!staged.Success)
                return ClaudeToolOutput.Error(staged.Output.Trim());
        }
        else if (!Bool(input, "all") && (await repository.StatusAsync()).Changes.All(c => !c.Staged))
            return ClaudeToolOutput.Error("Nothing is staged: give the files to commit, or all: true.");
        return Said(await repository.CommitAsync(message, Bool(input, "all") && files.Count == 0), "Committed.");
    }

    static string? FirstLine(string? text) => text?.Split('\n')[0].Trim();
}

/// <summary><c>git_branch</c>: a branch made (and switched to), or another one switched to; asked first.</summary>
sealed class GitBranchTool(Func<string?> workspace) : GitTool(workspace)
{
    public override string Name => "git_branch";

    public override string Description => "Makes a branch from the current commit and switches to it (action: create), or switches to another branch (action: switch). The user is asked first.";

    public override JsonObject InputSchema => Schema("""
        {"type":"object","properties":{
          "action":{"type":"string","enum":["create","switch"]},
          "name":{"type":"string","description":"The branch"}},
         "required":["action","name"]}
        """);

    public override ClaudeToolPlan Plan(JsonElement input) => Str(input, "action") == "create"
        ? new(ToolAccess.Write, "Create branch", Str(input, "name"), $"Create the branch {Str(input, "name")} and switch to it?")
        : new(ToolAccess.Write, "Switch branch", Str(input, "name"), $"Switch to the branch {Str(input, "name")}?");

    protected override async Task<ClaudeToolOutput> RunAsync(GitRepository repository, JsonElement input, CancellationToken cancellationToken)
    {
        if (Str(input, "name") is not { Length: > 0 } name)
            return ClaudeToolOutput.Error("Give the branch's name.");
        return Str(input, "action") switch
        {
            "create" => Said(await repository.CreateBranchAsync(name), $"On the new branch {name}."),
            "switch" => Said(await repository.SwitchAsync(name), $"On the branch {name}."),
            _ => ClaudeToolOutput.Error("action is create or switch."),
        };
    }
}

/// <summary><c>git_fetch</c>: the remotes' branches fetched (nothing of the user's changes).</summary>
sealed class GitFetchTool(Func<string?> workspace) : GitTool(workspace)
{
    public override string Name => "git_fetch";

    public override string Description => "Fetches the remotes' branches (git fetch --prune), with the user's credentials; nothing of the working tree changes.";

    public override ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Run, "Git fetch", null, "");

    protected override async Task<ClaudeToolOutput> RunAsync(GitRepository repository, JsonElement input, CancellationToken cancellationToken) =>
        Said(await repository.FetchAsync(), "Fetched.");
}

/// <summary><c>git_pull</c>: the remote branch's commits brought in (fast-forward only); asked first.</summary>
sealed class GitPullTool(Func<string?> workspace) : GitTool(workspace)
{
    public override string Name => "git_pull";

    public override string Description => "Brings in the commits of the branch's remote branch, fast-forward only (git pull --ff-only), with the user's credentials. The user is asked first.";

    public override ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Write, "Git pull", null, "Pull the remote branch's commits into this one (fast-forward only)?");

    protected override async Task<ClaudeToolOutput> RunAsync(GitRepository repository, JsonElement input, CancellationToken cancellationToken) =>
        Said(await repository.PullAsync(), "Pulled.");
}

/// <summary><c>git_push</c>: the branch's commits pushed (its remote branch made the first time); asked first, never forced.</summary>
sealed class GitPushTool(Func<string?> workspace) : GitTool(workspace)
{
    public override string Name => "git_push";

    public override string Description =>
        "Pushes the current branch's commits to its remote branch (made on origin the first time), with the user's credentials. Never "
        + "forced. The user is asked first.";

    public override ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Write, "Git push", null, "Push this branch's commits to its remote?")
    {
        Warning = "The commits become public on the remote: taking them back needs a force push.",
    };

    protected override async Task<ClaudeToolOutput> RunAsync(GitRepository repository, JsonElement input, CancellationToken cancellationToken)
    {
        var status = await repository.StatusAsync();
        if (status.Branch.Branch is null)
            return ClaudeToolOutput.Error("HEAD is detached: switch to a branch first (git_branch).");
        return Said(await repository.PushAsync(status.Branch), $"Pushed {status.Branch.Branch}.");
    }
}
