using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Karvon.Editor;
using Karvon.Git;
using Karvon.ViewModels;
using Karvon.Views;
using Karvon.Workspace;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>Git's history: the graph's lanes, the log read, the history document, a commit's files and what is done at a commit.</summary>
public class GitHistoryTests
{
    static GitCommit Commit(string hash, params string[] parents) => new(hash, parents, "a", "a@x", DateTimeOffset.UnixEpoch, [], hash);

    [Fact]
    public void The_graph_gives_a_branch_its_lane_and_joins_it_where_it_was_merged()
    {
        // M merges E (a branch from B) into C.
        var graph = new GitGraph();
        var m = graph.Add(Commit("m", "c", "e"));
        var e = graph.Add(Commit("e", "d"));
        var d = graph.Add(Commit("d", "b"));
        var c = graph.Add(Commit("c", "b"));
        var b = graph.Add(Commit("b", "a"));
        var a = graph.Add(Commit("a"));

        Assert.Equal((0, 2), (m.Lane, m.Lanes));
        Assert.Empty(m.Top);
        Assert.Equal([new GitGraphLine(0, 1, 1), new GitGraphLine(0, 0, 0), new GitGraphLine(1, 1, 1)], m.Bottom);
        Assert.Equal((1, 1), (e.Lane, e.Color));
        Assert.Equal([new GitGraphLine(0, 0, 0), new GitGraphLine(1, 1, 1)], e.Top);
        Assert.Equal(1, d.Lane);
        Assert.Equal(0, c.Lane);
        // B: both lanes wait for it; the right one joins it.
        Assert.Equal(0, b.Lane);
        Assert.Equal([new GitGraphLine(0, 0, 0), new GitGraphLine(1, 0, 1)], b.Top);
        Assert.Equal([new GitGraphLine(0, 0, 0)], b.Bottom);
        Assert.Equal((0, 1), (a.Lane, a.Lanes));
        Assert.Empty(a.Bottom);
    }

    [Fact]
    public void The_log_says_each_commits_refs()
    {
        var log = "abc1234def\u001fp1 p2\u001fAda\u001fada@x\u001f1700000000\u001fHEAD -> main, origin/main, origin/HEAD, tag: v1.0, feature\u001fMerge feature\u001e\n";
        var commit = Assert.Single(GitLog.Parse(log));
        Assert.Equal(["p1", "p2"], commit.Parents);
        Assert.True(commit.IsMerge);
        Assert.Equal([new GitRef("main", GitRefKind.Branch, true), new GitRef("origin/main", GitRefKind.Remote), new GitRef("v1.0", GitRefKind.Tag), new GitRef("feature", GitRefKind.Branch)],
            commit.Refs);
        Assert.Equal([new GitCommitFile("M", "a.cs", null), new GitCommitFile("R", "new.cs", "old.cs")], GitLog.Files("M\ta.cs\nR095\told.cs\tnew.cs\n"));
    }

    [Fact]
    public void Blame_gives_each_line_its_commit()
    {
        var h = new string('a', 40);
        // Each line itself after a tab.
        const string T = "\t";
        var porcelain = $"""
            {h} 1 1 2
            author Ada Lovelace
            author-mail <ada@example.invalid>
            author-time 1700000000
            author-tz +0000
            committer Ada Lovelace
            committer-mail <ada@example.invalid>
            committer-time 1700000000
            committer-tz +0000
            summary First notes
            filename NOTES.md
            {T}one
            {h} 2 2
            {T}two
            {new string('0', 40)} 3 3 1
            author Not Committed Yet
            author-mail <not.committed.yet>
            author-time 1700000100
            author-tz +0000
            committer Not Committed Yet
            committer-mail <not.committed.yet>
            committer-time 1700000100
            committer-tz +0000
            summary Version of NOTES.md from NOTES.md
            previous {h} NOTES.md
            filename NOTES.md
            {T}three

            """;
        var lines = GitBlame.Parse(porcelain);
        Assert.Equal(3, lines.Count);
        Assert.Same(lines[0], lines[1]);
        Assert.Equal(("Ada Lovelace", "ada@example.invalid", "First notes", DateTimeOffset.FromUnixTimeSeconds(1700000000)),
            (lines[0].Author, lines[0].Email, lines[0].Summary, lines[0].Date));
        Assert.False(lines[0].IsUncommitted);
        Assert.True(lines[2].IsUncommitted);
    }

    [AvaloniaFact]
    public async Task Blame_shows_each_lines_commit_follows_edits_and_opens_the_commit()
    {
        var (sln, dir) = NewRepository();
        var notes = Path.Combine(dir, "NOTES.md");
        File.WriteAllText(notes, "one\n");
        Git(dir, "add", "NOTES.md");
        Git(dir, "commit", "-q", "-m", "Add notes");
        File.WriteAllText(notes, "one\ntwo\n");
        Git(dir, "-c", "user.name=Ada Lovelace", "-c", "user.email=ada@example.invalid", "commit", "-q", "-am", "More notes");

        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        window.Show();
        await vm.OpenSolutionAsync(sln);
        await WaitUntil(() => vm.HasGit && vm.GitBranch == "main", TimeSpan.FromSeconds(10));
        var doc = vm.OpenDocument(notes)!;
        await WaitUntil(() => doc.Host.HasGitBaseline, TimeSpan.FromSeconds(10));

        await vm.ToggleBlameCommand.ExecuteAsync(null);
        Assert.True(doc.Host.IsBlameShown);
        await WaitUntil(() => doc.Host.Blame?.Count == 2, TimeSpan.FromSeconds(10));
        Assert.Equal([("Karvon Tests", "Add notes"), ("Ada Lovelace", "More notes")], doc.Host.Blame!.Select(c => (c.Author, c.Summary)));
        await WaitUntil(() => window.GetVisualDescendants().OfType<GitBlameMargin>().Any(m => m.IsEffectivelyVisible), TimeSpan.FromSeconds(5));

        // An unsaved line is not committed yet; the others keep theirs.
        doc.Host.Document.Insert(0, "zero\n");
        await WaitUntil(() => doc.Host.Blame?.Count == 3, TimeSpan.FromSeconds(10));
        Assert.True(doc.Host.Blame![0].IsUncommitted);
        Assert.Equal(["Add notes", "More notes"], doc.Host.Blame.Skip(1).Select(c => c.Summary));
        Snapshot(window, "git-blame");

        // A commit clicked opens in the history, selected.
        var more = doc.Host.Blame[2];
        await vm.RevealCommitAsync(notes, more.Hash);
        var history = Assert.IsType<GitHistoryViewModel>(vm.ActiveDocument);
        Assert.Equal(more.Hash, history.Selected?.Commit.Hash);

        vm.ActiveDocument = doc;
        await vm.ToggleBlameCommand.ExecuteAsync(null);
        Assert.False(doc.Host.IsBlameShown);
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<GitBlameMargin>(), m => m.IsEffectivelyVisible);
        window.Close();
    }

    sealed class Dialogs : IDialogService
    {
        public Queue<string?> Prompts { get; } = new();

        public List<string> Confirmations { get; } = [];

        public Task<string?> PickSolutionAsync() => Task.FromResult<string?>(null);

        public Task<string?> PromptAsync(string title, string label, string initial) => Task.FromResult(Prompts.Count > 0 ? Prompts.Dequeue() : null);

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

    /// <summary>The sample solution in a new repository, its first commit made.</summary>
    static (string Solution, string Directory) NewRepository()
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
        return (sln, dir);
    }

    [AvaloniaFact]
    public async Task The_history_shows_the_graph_a_commits_files_and_acts_at_a_commit()
    {
        var (sln, dir) = NewRepository();
        var notes = Path.Combine(dir, "NOTES.md");
        File.WriteAllText(notes, "one\n");
        Git(dir, "add", "NOTES.md");
        Git(dir, "commit", "-q", "-m", "Add notes");
        Git(dir, "switch", "-q", "-c", "feature");
        File.WriteAllText(Path.Combine(dir, "FEATURE.md"), "feature\n");
        Git(dir, "add", "FEATURE.md");
        Git(dir, "commit", "-q", "-m", "Add the feature");
        Git(dir, "switch", "-q", "main");
        File.WriteAllText(notes, "one\ntwo\n");
        Git(dir, "commit", "-q", "-am", "More notes");
        Git(dir, "merge", "-q", "--no-ff", "--no-edit", "-m", "Merge feature", "feature");
        Git(dir, "tag", "v1.0");
        Git(dir, "switch", "-q", "-c", "unmerged");
        File.WriteAllText(Path.Combine(dir, "LATER.md"), "later\n");
        Git(dir, "add", "LATER.md");
        Git(dir, "commit", "-q", "-m", "Later work");
        Git(dir, "switch", "-q", "main");

        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        window.Show();
        await vm.OpenSolutionAsync(sln);
        // After the window, which makes itself the dialogs.
        var dialogs = new Dialogs();
        vm.Dialogs = dialogs;
        await WaitUntil(() => vm.HasGit && vm.GitBranch == "main", TimeSpan.FromSeconds(10));

        vm.ShowHistoryCommand.Execute(null);
        var history = Assert.IsType<GitHistoryViewModel>(vm.ActiveDocument);
        await WaitUntil(() => history.Rows.Count == 5 && history.Selected is not null, TimeSpan.FromSeconds(10));
        // Topological order: the merge first, its parents' lines after it (the feature's commit and main's in either order), the root last.
        Assert.Equal("Merge feature", history.Rows[0].Subject);
        Assert.Equal(["Add notes", "initial"], history.Rows.Skip(3).Select(r => r.Subject));
        Assert.Equal(["Add the feature", "More notes"], history.Rows.Skip(1).Take(2).Select(r => r.Subject).Order());
        var merge = history.Rows[0];
        Assert.True(merge.IsMerge && merge.IsHead);
        Assert.Equal(["main", "v1.0"], merge.Refs.Select(r => r.Name));
        Assert.Equal(2, history.Rows.Max(r => r.Graph.Lanes));
        Assert.Equal(history.Rows[0], history.Selected);

        // Every branch: the one not merged is there too.
        history.AllBranches = true;
        await WaitUntil(() => history.Rows.Count == 6, TimeSpan.FromSeconds(10));
        Assert.Equal("Later work", history.Rows[0].Subject);
        Assert.Equal(["unmerged"], history.Rows[0].Refs.Select(r => r.Name));
        history.Filter = "notes";
        Assert.Equal(["More notes", "Add notes"], history.Visible.Select(r => r.Subject));
        Assert.False(history.ShowsGraph);
        history.Filter = "";

        // The merge's files: against its first parent only.
        history.Selected = history.Rows.Single(r => r.Subject == "Merge feature");
        await WaitUntil(() => history.Details?.Commit.Subject == "Merge feature", TimeSpan.FromSeconds(10));
        Assert.Equal([new GitCommitFile("A", "FEATURE.md", null)], history.Details!.Files);
        Assert.Equal("Merge feature", history.Details.Message);
        await WaitUntil(() => window.GetVisualDescendants().OfType<GitGraphCell>().Any(c => c.IsEffectivelyVisible), TimeSpan.FromSeconds(5));
        Snapshot(window, "git-history");

        // A file of a commit opens as its diff against the parent.
        history.Selected = history.Rows.Single(r => r.Subject == "More notes");
        await WaitUntil(() => history.Details?.Commit.Subject == "More notes", TimeSpan.FromSeconds(10));
        await history.OpenFileCommand.ExecuteAsync(history.Details!.Files.Single());
        var diff = Assert.IsType<DiffViewModel>(vm.ActiveDocument);
        Assert.Contains("NOTES.md", diff.Title);

        // A branch at a commit, a tag; a revert (asked first) as a new commit.
        vm.ActiveDocument = history;
        dialogs.Prompts.Enqueue("from-notes");
        await history.CreateBranchCommand.ExecuteAsync(history.Rows.Single(r => r.Subject == "Add notes"));
        Assert.Equal(Git(dir, "rev-parse", "HEAD~1^1"), Git(dir, "rev-parse", "from-notes"));
        // The history follows the new branch.
        await WaitUntil(() => history.Rows.Any(r => r.Subject == "Add notes" && r.Refs.Any(f => f.Name == "from-notes")) && history.Selected is not null,
            TimeSpan.FromSeconds(10));
        await history.RevertCommand.ExecuteAsync(history.Rows.Single(r => r.Subject == "More notes"));
        Assert.StartsWith("Undo ", dialogs.Confirmations[^1]);
        Assert.Equal("Revert \"More notes\"", Git(dir, "log", "-1", "--format=%s"));
        Assert.Equal("one\n", File.ReadAllText(notes).Replace("\r\n", "\n"));
        // The history follows: the new commit on top.
        await WaitUntil(() => history.Rows.FirstOrDefault()?.Subject == "Revert \"More notes\"" || history.Rows.Any(r => r.Subject == "Revert \"More notes\""), TimeSpan.FromSeconds(10));
        Assert.Contains(history.Rows, r => r.Subject == "Revert \"More notes\"");

        // A file's history.
        vm.ShowFileHistory(notes);
        var file = Assert.IsType<GitHistoryViewModel>(vm.ActiveDocument);
        await WaitUntil(() => file.Rows.Count == 3, TimeSpan.FromSeconds(10));
        Assert.Equal(["Revert \"More notes\"", "More notes", "Add notes"], file.Rows.Select(r => r.Subject));
        Assert.Equal("History - NOTES.md", file.Title);
        window.Close();
    }
}
