using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Karvon.Git;
using Karvon.ViewModels;
using Karvon.Views;
using Karvon.Workspace;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>Merge conflicts: git's markers read, the merge editor, Git Changes' merge under way (abort it).</summary>
public class MergeEditorTests
{
    const string Marked =
        "intro\n<<<<<<< HEAD\nours one\n=======\ntheirs one\n>>>>>>> feature\nmiddle\n<<<<<<< HEAD\nours two\n||||||| base\nbase two\n=======\ntheirs two\n>>>>>>> feature\nend\n";

    [Fact]
    public void Gits_markers_give_the_runs_and_the_conflicts()
    {
        var segments = MergeConflicts.Parse(Marked);
        Assert.Equal(["intro\n", null, "middle\n", null, "end\n"], segments.Select(s => s.Conflict is null ? s.Text : null));
        var (one, two) = (segments[1].Conflict!, segments[3].Conflict!);
        Assert.Equal(("ours one\n", "theirs one\n", null, "HEAD", "feature"), (one.Current, one.Incoming, one.Base, one.CurrentLabel, one.IncomingLabel));
        Assert.Equal(("ours two\n", "theirs two\n", "base two\n"), (two.Current, two.Incoming, two.Base));
        Assert.Equal(Marked, string.Concat(segments.Select(s => s.Text)));
        // A line of = in a file is no conflict, nor a conflict git did not close.
        Assert.Empty(MergeConflicts.Parse("a\n=======\nb\n").Where(s => s.Conflict is not null));
        Assert.Empty(MergeConflicts.Parse("a\n<<<<<<< HEAD\nb\n").Where(s => s.Conflict is not null));
        Assert.Equal("a\n<<<<<<< HEAD\nb\n", string.Concat(MergeConflicts.Parse("a\n<<<<<<< HEAD\nb\n").Select(s => s.Text)));
    }

    sealed class Dialogs : IDialogService
    {
        public List<string> Confirmations { get; } = [];

        public bool Answer { get; set; } = true;

        public Task<string?> PickSolutionAsync() => Task.FromResult<string?>(null);

        public Task<string?> PromptAsync(string title, string label, string initial) => Task.FromResult<string?>(null);

        public Task<bool?> ConfirmSaveAsync(string fileName) => Task.FromResult<bool?>(false);

        public Task<bool?> ConfirmAsync(string title, string message)
        {
            Confirmations.Add(message);
            return Task.FromResult<bool?>(Answer);
        }

        public Task<NewItemChoice?> ChooseNewItemAsync(string title, string folder, ItemTemplate initial) => Task.FromResult<NewItemChoice?>(null);

        public Task<IReadOnlyList<string>> PickFilesAsync(string title) => Task.FromResult<IReadOnlyList<string>>([]);
    }

    static (int Code, string Output) Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output.Trim());
    }

    /// <summary>The sample in a repository whose main and feature changed NOTES.md's two lines (four apart) each their own way, merged: two conflicts.</summary>
    static (string Solution, string Notes) Conflicted()
    {
        var sln = SolutionExplorerTests.CopySample();
        var dir = Path.GetDirectoryName(sln)!;
        File.WriteAllText(Path.Combine(dir, ".gitignore"), "bin/\nobj/\n");
        string[][] steps =
        [
            ["init", "-q", "-b", "main"], ["config", "user.name", "Karvon Tests"], ["config", "user.email", "tests@example.invalid"],
            ["config", "commit.gpgsign", "false"], ["config", "merge.conflictStyle", "merge"],
        ];
        foreach (var step in steps)
            Git(dir, step);
        var notes = Path.Combine(dir, "NOTES.md");
        File.WriteAllText(notes, "intro\none\nm1\nm2\nm3\nm4\ntwo\nend\n");
        Git(dir, "add", "-A");
        Git(dir, "commit", "-q", "-m", "initial");
        Git(dir, "switch", "-q", "-c", "feature");
        File.WriteAllText(notes, "intro\ntheirs one\nm1\nm2\nm3\nm4\ntheirs two\nend\n");
        Git(dir, "commit", "-q", "-am", "Theirs");
        Git(dir, "switch", "-q", "main");
        File.WriteAllText(notes, "intro\nours one\nm1\nm2\nm3\nm4\nours two\nend\n");
        Git(dir, "commit", "-q", "-am", "Ours");
        Assert.NotEqual(0, Git(dir, "merge", "feature").Code);
        return (sln, notes);
    }

    [AvaloniaFact]
    public async Task A_conflict_is_resolved_in_the_merge_editor_and_staged()
    {
        var (sln, notes) = Conflicted();
        var dir = Path.GetDirectoryName(sln)!;
        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        window.Show();
        await vm.OpenSolutionAsync(sln);
        var dialogs = new Dialogs();
        vm.Dialogs = dialogs;
        await WaitUntil(() => vm.IsGitOperationInProgress && vm.UnstagedChanges.Any(c => c.Change.Kind == GitChangeKind.Conflicted), TimeSpan.FromSeconds(10));
        Assert.Equal("Merging: 1 conflicted file. Resolve them, then commit.", vm.GitInProgressText);
        Assert.False(vm.CanContinueGitOperation);

        // The conflicted file opens in the merge editor, not as a diff.
        await vm.OpenDiffCommand.ExecuteAsync(vm.UnstagedChanges.Single(c => c.Change.Kind == GitChangeKind.Conflicted));
        var merge = Assert.IsType<MergeEditorViewModel>(vm.ActiveDocument);
        Assert.Equal((2, 2, "Current (HEAD)", "Incoming (feature)"), (merge.Conflicts.Count, merge.Remaining, merge.CurrentLabel, merge.IncomingLabel));
        Assert.Equal("intro\ntheirs one\nm1\nm2\nm3\nm4\ntheirs two\nend\n", merge.Incoming);
        Assert.Equal([(2, 1), (7, 1)], merge.IncomingRanges);
        await WaitUntil(() => window.GetVisualDescendants().OfType<MergeEditorView>().Any(v => v.IsEffectivelyVisible), TimeSpan.FromSeconds(5));
        Snapshot(window, "merge-editor");

        // An edit of the result moves the conflicts' places: a choice still lands on its conflict.
        merge.Result.Insert(0, "# Notes\n");
        Assert.Equal(3, merge.Conflicts[0].Line);
        merge.TakeIncomingCommand.Execute(merge.Conflicts[0]);
        merge.TakeBothCommand.Execute(merge.Conflicts[1]);
        Assert.Equal(("Incoming", "Both", 0), (merge.Conflicts[0].State, merge.Conflicts[1].State, merge.Remaining));
        Assert.Equal("# Notes\nintro\ntheirs one\nm1\nm2\nm3\nm4\nours two\ntheirs two\nend\n", merge.Result.Text);

        // Accepted: written, staged, closed; the merge is left to commit.
        await merge.AcceptMergeCommand.ExecuteAsync(null);
        Assert.Equal("# Notes\nintro\ntheirs one\nm1\nm2\nm3\nm4\nours two\ntheirs two\nend\n", File.ReadAllText(notes));
        Assert.Equal("", Git(dir, "diff", "--name-only", "--diff-filter=U").Output);
        Assert.DoesNotContain(vm.Documents, d => d is MergeEditorViewModel);
        await WaitUntil(() => vm.GitInProgressText == "Merging: commit to finish it.", TimeSpan.FromSeconds(10));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Claudes_resolution_is_read_back_and_a_merge_can_be_aborted()
    {
        var (sln, notes) = Conflicted();
        var dir = Path.GetDirectoryName(sln)!;
        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        window.Show();
        await vm.OpenSolutionAsync(sln);
        var dialogs = new Dialogs();
        vm.Dialogs = dialogs;
        await WaitUntil(() => vm.IsGitOperationInProgress, TimeSpan.FromSeconds(10));
        var merge = vm.OpenMergeEditor(notes)!;
        Assert.Equal(2, merge.Remaining);

        // The file resolved by someone else (Claude's edit): the editor reads it again.
        File.WriteAllText(notes, "intro\nboth one\nm1\nm2\nm3\nm4\nboth two\nend\n");
        await WaitUntil(() => merge.Remaining == 0 && merge.Conflicts.Count == 0, TimeSpan.FromSeconds(10));
        Assert.Equal("No conflicts left in the file.", merge.Summary);

        // Aborted: asked first; the branch as it was, the editor closed.
        await vm.AbortGitOperationCommand.ExecuteAsync(null);
        Assert.StartsWith("Abort the merge?", dialogs.Confirmations[^1]);
        Assert.Equal("intro\nours one\nm1\nm2\nm3\nm4\nours two\nend\n", File.ReadAllText(notes));
        Assert.False(File.Exists(Path.Combine(dir, ".git", "MERGE_HEAD")));
        Assert.DoesNotContain(vm.Documents, d => d is MergeEditorViewModel);
        await WaitUntil(() => !vm.IsGitOperationInProgress, TimeSpan.FromSeconds(10));
        window.Close();
    }
}
