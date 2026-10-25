using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Karvon.Git;
using Karvon.ViewModels;
using Karvon.Views;
using Karvon.Workspace;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>Git's other tools: stashes, worktrees, an interactive rebase, the caret line's blame.</summary>
public class GitExtrasTests
{
    sealed class Dialogs : IDialogService
    {
        public Queue<string?> Prompts { get; } = new();

        public List<string> Confirmations { get; } = [];

        public Task<string?> PickSolutionAsync() => Task.FromResult<string?>(null);

        public Task<string?> PromptAsync(string title, string label, string initial) => Task.FromResult(Prompts.Count > 0 ? Prompts.Dequeue() ?? initial : null);

        public Task<bool?> ConfirmSaveAsync(string fileName) => Task.FromResult<bool?>(false);

        public Task<bool?> ConfirmAsync(string title, string message)
        {
            Confirmations.Add(message);
            return Task.FromResult<bool?>(true);
        }

        public Task<NewItemChoice?> ChooseNewItemAsync(string title, string folder, ItemTemplate initial) => Task.FromResult<NewItemChoice?>(null);

        public Task<IReadOnlyList<string>> PickFilesAsync(string title) => Task.FromResult<IReadOnlyList<string>>([]);
    }

    static string Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)}: {p.StandardError.ReadToEnd()}");
        return output.Trim();
    }

    /// <summary>The sample in a repository: initial, "Add notes", "More notes", "Typo".</summary>
    static (string Solution, string Dir, string Notes) Repository()
    {
        var sln = SolutionExplorerTests.CopySample();
        var dir = Path.GetDirectoryName(sln)!;
        File.WriteAllText(Path.Combine(dir, ".gitignore"), "bin/\nobj/\n");
        Git(dir, "init", "-q", "-b", "main");
        Git(dir, "config", "user.name", "Karvon Tests");
        Git(dir, "config", "user.email", "tests@example.invalid");
        Git(dir, "config", "commit.gpgsign", "false");
        Git(dir, "add", "-A");
        Git(dir, "commit", "-q", "-m", "initial");
        var notes = Path.Combine(dir, "NOTES.md");
        File.WriteAllText(notes, "one\n");
        Git(dir, "add", "NOTES.md");
        Git(dir, "commit", "-q", "-m", "Add notes");
        File.WriteAllText(notes, "one\ntwo\n");
        Git(dir, "commit", "-q", "-am", "More notes");
        File.WriteAllText(notes, "one\ntwo\nthre\n");
        Git(dir, "commit", "-q", "-am", "Typo");
        return (sln, dir, notes);
    }

    static async Task<(MainViewModel Vm, MainWindow Window, Dialogs Dialogs)> OpenAsync(string sln)
    {
        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        window.Show();
        await vm.OpenSolutionAsync(sln);
        var dialogs = new Dialogs();
        vm.Dialogs = dialogs;
        await WaitUntil(() => vm.HasGit && vm.GitBranch == "main", TimeSpan.FromSeconds(10));
        return (vm, window, dialogs);
    }

    [AvaloniaFact]
    public async Task Changes_are_stashed_with_the_message_and_come_back()
    {
        var (sln, dir, notes) = Repository();
        var (vm, window, dialogs) = await OpenAsync(sln);
        File.WriteAllText(notes, "one\ntwo\nthree\n");
        File.WriteAllText(Path.Combine(dir, "LATER.md"), "later\n");
        await vm.RefreshGitAsync();

        // Stash All: untracked files too, the message box's text its message.
        vm.CommitMessage = "Half the notes";
        await vm.StashAllCommand.ExecuteAsync(null);
        Assert.Equal("", Git(dir, "status", "--porcelain"));
        await WaitUntil(() => vm.Stashes.Count == 1, TimeSpan.FromSeconds(10));
        var stash = vm.Stashes[0];
        Assert.Equal(("On main: Half the notes", "stash@{0}"), (stash.Title, stash.Stash.Ref));
        Assert.Equal("", vm.CommitMessage);
        await vm.ToggleStashCommand.ExecuteAsync(stash);
        Assert.Equal(["NOTES.md"], stash.Files.Select(f => f.Path));
        await vm.OpenStashFileAsync(stash, stash.Files[0]);
        Assert.IsType<DiffViewModel>(vm.ActiveDocument);

        // Popped: back, untracked file and all; the stash gone.
        await vm.PopStashCommand.ExecuteAsync(stash);
        Assert.Equal("one\ntwo\nthree\n", File.ReadAllText(notes));
        Assert.True(File.Exists(Path.Combine(dir, "LATER.md")));
        await WaitUntil(() => vm.Stashes.Count == 0, TimeSpan.FromSeconds(10));

        // Stash Staged: only what is staged; dropped, asked first.
        Git(dir, "add", "NOTES.md");
        await vm.RefreshGitAsync();
        await vm.StashStagedCommand.ExecuteAsync(null);
        Assert.Equal("?? LATER.md", Git(dir, "status", "--porcelain"));
        await WaitUntil(() => vm.Stashes.Count == 1, TimeSpan.FromSeconds(10));
        await vm.DropStashCommand.ExecuteAsync(vm.Stashes[0]);
        Assert.StartsWith("Drop stash@{0}", dialogs.Confirmations[^1]);
        Assert.Equal("", Git(dir, "stash", "list"));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Worktrees_are_added_and_removed()
    {
        var (sln, dir, _) = Repository();
        var (vm, window, dialogs) = await OpenAsync(sln);
        await vm.ManageWorktreesCommand.ExecuteAsync(null);
        var worktrees = Assert.IsType<GitWorktreesViewModel>(vm.ActiveDocument);
        var main = Assert.Single(worktrees.Worktrees);
        Assert.Equal((true, "main"), (main.IsMain, main.Branch));

        // A new branch's worktree, beside the repository by default.
        dialogs.Prompts.Enqueue("feature/search");
        dialogs.Prompts.Enqueue(null);
        await worktrees.AddCommand.ExecuteAsync(null);
        var folder = Path.Combine(Path.GetDirectoryName(dir)!, $"{Path.GetFileName(dir)}-feature-search");
        Assert.Equal(2, worktrees.Worktrees.Count);
        Assert.Equal((folder, "feature/search"), (worktrees.Worktrees[1].Path, worktrees.Worktrees[1].Branch));
        Assert.True(File.Exists(Path.Combine(folder, "NOTES.md")));

        await worktrees.RemoveCommand.ExecuteAsync(worktrees.Worktrees[1]);
        Assert.StartsWith("Remove the worktree", dialogs.Confirmations[^1]);
        Assert.Single(worktrees.Worktrees);
        Assert.False(Directory.Exists(folder));
        // Its branch stays.
        Assert.Contains("feature/search", Git(dir, "branch", "--list", "feature/search"));
        window.Close();
    }

    [AvaloniaFact]
    public async Task An_interactive_rebase_rewords_squashes_and_drops()
    {
        var (sln, dir, notes) = Repository();
        File.WriteAllText(Path.Combine(dir, "SCRATCH.md"), "scratch\n");
        Git(dir, "add", "SCRATCH.md");
        Git(dir, "commit", "-q", "-m", "Scratch");
        var (vm, window, _) = await OpenAsync(sln);
        vm.ShowHistoryCommand.Execute(null);
        var history = Assert.IsType<GitHistoryViewModel>(vm.ActiveDocument);
        await WaitUntil(() => history.Rows.Count == 5, TimeSpan.FromSeconds(10));

        await history.RebaseFromCommand.ExecuteAsync(history.Rows.Single(r => r.Subject == "Add notes"));
        var rebase = Assert.IsType<GitRebaseViewModel>(vm.ActiveDocument);
        Assert.Equal(["More notes", "Typo", "Scratch"], rebase.Steps.Select(s => s.Subject));

        // A squash first is refused: there is nothing before it.
        rebase.Steps[0].Action = "squash";
        await rebase.StartCommand.ExecuteAsync(null);
        Assert.StartsWith(rebase.Steps[0].ShortHash + " is the first commit kept", rebase.Error);

        rebase.Steps[0].Action = "reword";
        rebase.Steps[0].Message = "Notes, two lines";
        rebase.Steps[1].Action = "fixup";
        rebase.Steps[2].Action = "drop";
        await rebase.StartCommand.ExecuteAsync(null);
        Assert.Equal(["Notes, two lines", "Add notes", "initial"], Git(dir, "log", "--format=%s").Split('\n'));
        Assert.Equal("one\ntwo\nthre\n", File.ReadAllText(notes));
        Assert.False(File.Exists(Path.Combine(dir, "SCRATCH.md")));
        Assert.DoesNotContain(vm.Documents, d => d is GitRebaseViewModel);
        Assert.Null((await GitRepository.OpenAsync(dir))!.InProgress());
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_caret_lines_commit_shows_after_it_and_follows_edits()
    {
        var (sln, _, notes) = Repository();
        var (vm, window, _) = await OpenAsync(sln);
        Assert.True(vm.LineBlame);
        var doc = vm.OpenDocument(notes)!;
        await WaitUntil(() => doc.Host.HasGitBaseline, TimeSpan.FromSeconds(10));
        doc.Host.Editor.TextArea.Caret.Line = 2;
        await WaitUntil(() => doc.Host.LineBlameText == "Karvon Tests, just now · More notes", TimeSpan.FromSeconds(10));
        doc.Host.Editor.TextArea.Caret.Line = 1;
        Assert.Equal("Karvon Tests, just now · Add notes", doc.Host.LineBlameText);
        Snapshot(window, "line-blame");

        // An edit: gone until the edited text's blame is read; that line is not committed yet.
        doc.Host.Document.Insert(0, "zero\n");
        doc.Host.Editor.TextArea.Caret.Line = 1;
        Assert.Null(doc.Host.LineBlameText);
        await WaitUntil(() => doc.Host.LineBlameText == "Not committed yet", TimeSpan.FromSeconds(10));

        vm.LineBlame = false;
        Assert.Null(doc.Host.LineBlameText);
        vm.LineBlame = true;
        window.Close();
    }
}
