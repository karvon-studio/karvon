using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Karvon.Git;

namespace Karvon.ViewModels;

/// <summary>
/// Git Changes' message written by Claude, as in the VS extension: a fast model without tools reads the diff of the
/// repositories in scope and their latest commit subjects and writes one message in their style. Only the text comes
/// back into the message box; nothing is committed until the user presses Commit, and the commit is made as the user,
/// with no Claude attribution or trailers.
/// </summary>
public partial class MainViewModel
{
    /// <summary>The diff Claude reads is cut here; the file list still names every change.</summary>
    const int MaxCommitDiffChars = 60_000;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(WriteCommitMessageCommand))]
    public partial bool IsWritingCommitMessage { get; set; }

    bool CanWriteCommitMessage => HasGit && !IsGitBusy && !IsWritingCommitMessage && GitChangeCount > 0;

    [RelayCommand(CanExecute = nameof(CanWriteCommitMessage))]
    async Task WriteCommitMessage()
    {
        var claude = Claude;
        claude.CheckCli();
        if (claude.CliPath is not { } cli)
        {
            SetStatus("Claude Code is not installed: the claude command was not found.");
            return;
        }
        IsWritingCommitMessage = true;
        SetStatus("Claude is writing the commit message...", StatusKind.Busy);
        try
        {
            var prompt = await CommitMessagePromptAsync();
            if (prompt is null)
            {
                SetStatus("There are no changes to describe.", StatusKind.Ready);
                return;
            }
            var root = (StagedChanges.Count > 0 ? StagedChanges : UnstagedChanges).Select(c => c.Repository).OfType<GitRepoItem>().FirstOrDefault()?.Root
                ?? Solution?.Directory ?? Environment.CurrentDirectory;
            var (exit, output, error) = await RunClaudePrintAsync(cli, root, prompt, claude.SelectedAccount);
            var message = CleanCommitMessage(output);
            if (exit != 0 || message.Length == 0)
            {
                SetStatus("Claude could not write the message: " + (error.Trim().Length > 0 ? error.Trim() : $"exit code {exit}"), StatusKind.Ready);
                return;
            }
            CommitMessage = message;
            SetStatus("Claude wrote the commit message: review it, then commit.", StatusKind.Ready);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            SetStatus("Claude could not write the message: " + ex.Message, StatusKind.Ready);
        }
        finally
        {
            IsWritingCommitMessage = false;
        }
    }

    /// <summary>What Claude is asked: the rules, the branch, recent subjects, the changed files and their diff.</summary>
    internal async Task<string?> CommitMessagePromptAsync()
    {
        var stagedOnly = StagedChanges.Count > 0;
        var rows = (stagedOnly ? StagedChanges : UnstagedChanges).ToList();
        var repositories = rows.Select(c => c.Repository).OfType<GitRepoItem>().Distinct().ToList();
        var several = repositories.Count > 1;
        var diff = new StringBuilder();
        var subjects = new List<string>();
        foreach (var repository in repositories)
        {
            var text = await repository.Repository.DiffForMessageAsync(stagedOnly);
            if (!stagedOnly)
            {
                // New files have no diff against HEAD: their content instead (small text files only).
                foreach (var change in repository.Changes.Where(c => c.Kind == GitChangeKind.Untracked).Take(20))
                    if (File.Exists(change.Path) && new FileInfo(change.Path).Length < 20_000)
                        text += $"\n--- new file: {change.RelativePath}\n{await File.ReadAllTextAsync(change.Path)}";
            }
            if (text.Trim().Length == 0)
                continue;
            if (several)
                diff.AppendLine($"=== Repository: {repository.Name}");
            diff.AppendLine(text);
            subjects.AddRange(await repository.Repository.RecentSubjectsAsync(12));
        }
        if (diff.ToString().Trim().Length == 0)
            return null;
        var diffText = diff.Length > MaxCommitDiffChars ? diff.ToString(0, MaxCommitDiffChars) + "\n[… the diff goes on; it was cut here]" : diff.ToString();
        var recent = string.Join("\n", subjects.Distinct().Take(15));
        var files = string.Join("\n", rows.Select(c => $"{c.Change.Kind}: {(several ? c.Repository!.Name + "/" : "")}{c.Change.RelativePath}"));
        return new StringBuilder()
            .AppendLine(several
                ? "Write one git commit message for the changes below. They span several repositories and the same message is used in each."
                : "Write a git commit message for the changes below.")
            .AppendLine("Rules:")
            .AppendLine("- First line: a short summary in the imperative mood, at most 72 characters, no period at the end.")
            .AppendLine("- If the change is more than trivial: a blank line, then a body wrapped at 72 characters that says what changed and why.")
            .AppendLine("- Follow the language, tone and conventions (prefixes, capitalization) of the recent commit messages.")
            .AppendLine("- Output only the commit message: no code fences, no quotes, no explanations, no trailers such as Co-Authored-By or Signed-off-by, and no mention of Claude or AI.")
            .AppendLine()
            .AppendLine($"Branch: {GitScopeBranch}")
            .AppendLine()
            .AppendLine("Recent commit messages:")
            .AppendLine(recent.Length > 0 ? recent : "(none yet)")
            .AppendLine()
            .AppendLine("Changed files:")
            .AppendLine(files)
            .AppendLine()
            .AppendLine("Diff:")
            .AppendLine(diffText)
            .ToString();
    }

    /// <summary>claude -p with a fast model and no tools; the run is not kept as a session.</summary>
    static async Task<(int Exit, string Output, string Error)> RunClaudePrintAsync(string cli, string directory, string prompt, Claude.ClaudeAccount? account)
    {
        var psi = new ProcessStartInfo(cli)
        {
            WorkingDirectory = directory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in new[] { "-p", "--model", "haiku", "--tools", "", "--no-session-persistence", "--output-format", "text" })
            psi.ArgumentList.Add(arg);
        // Not a nested session of the Claude Code Karvon may itself be running in.
        psi.Environment.Remove("CLAUDECODE");
        account?.Apply(psi.Environment);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("claude could not be started.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await using (var stdin = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)))
            await stdin.WriteAsync(prompt);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("claude did not answer within 2 minutes.");
        }
        return (process.ExitCode, await output, await error);
    }

    /// <summary>The message as it goes in: no code fence around it, no quotes, no trailers a model may still add.</summary>
    internal static string CleanCommitMessage(string text)
    {
        var lines = text.Replace("\r\n", "\n").Trim().Split('\n').ToList();
        if (lines.Count > 0 && lines[0].StartsWith("```", StringComparison.Ordinal))
            lines.RemoveAt(0);
        if (lines.Count > 0 && lines[^1].StartsWith("```", StringComparison.Ordinal))
            lines.RemoveAt(lines.Count - 1);
        lines.RemoveAll(l => Trailer().IsMatch(l));
        return string.Join("\n", lines).Trim().Trim('"');
    }

    [GeneratedRegex(@"^\s*(Co-Authored-By|Signed-off-by|Generated with|🤖)", RegexOptions.IgnoreCase)]
    private static partial Regex Trailer();
}
