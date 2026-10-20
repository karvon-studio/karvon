using System.Diagnostics;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using VSL.ViewModels;
using VSL.Views;
using static VSL.Tests.UiTests;

namespace VSL.Tests;

/// <summary>Git Changes as in the VS extension: every repository of the solution, trees, and a message written by Claude.</summary>
public class GitChangesTests
{
    static string Output(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output.Trim();
    }

    static void Init(string dir)
    {
        TempRepo.Git(dir, "init", "-q", "-b", "main");
        TempRepo.Git(dir, "config", "user.name", "VSL Tests");
        TempRepo.Git(dir, "config", "user.email", "tests@example.invalid");
        TempRepo.Git(dir, "config", "commit.gpgsign", "false");
        TempRepo.Git(dir, "add", "-A");
        TempRepo.Git(dir, "commit", "-q", "-m", "initial");
    }

    [AvaloniaFact]
    public async Task Lists_every_repository_and_commits_a_message_written_by_Claude()
    {
        // The solution's repository, a library inside it with a repository of its own, and a remote with a branch.
        var sln = SolutionExplorerTests.CopySample();
        var dir = Path.GetDirectoryName(sln)!;
        File.WriteAllText(Path.Combine(dir, ".gitignore"), "bin/\nobj/\nLib/\n");
        Init(dir);
        var lib = Path.Combine(dir, "Lib");
        Directory.CreateDirectory(Path.Combine(lib, "docs"));
        File.WriteAllText(Path.Combine(lib, "docs", "README.md"), "lib\n");
        Init(lib);
        var remote = Path.Combine(Path.GetTempPath(), "vsl-remote-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(remote);
        TempRepo.Git(remote, "init", "-q", "--bare", "-b", "main");
        TempRepo.Git(dir, "remote", "add", "origin", remote);
        TempRepo.Git(dir, "push", "-q", "-u", "origin", "main");
        TempRepo.Git(dir, "push", "-q", "origin", "main:feature");
        TempRepo.Git(dir, "fetch", "-q");

        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        window.Show();
        await vm.OpenSolutionAsync(sln);
        await WaitUntil(() => vm.GitRepositories.Count == 2 && vm.GitRepositories.All(r => r.Status is not null), TimeSpan.FromSeconds(10));
        Assert.Equal([Path.GetFileName(dir), "Lib"], vm.GitRepositories.Select(r => r.Name));
        Assert.True(vm.IsMultiRepository);
        Assert.Equal("2 Repositories", vm.GitRepositoryLabel);
        Assert.Equal("main", vm.GitScopeBranch);
        Assert.Equal("main", vm.GitBranch);
        Assert.Equal("VSL Tests <tests@example.invalid>", vm.GitAuthor);

        // A change in each: one tree under the folder they share, a repository each, folders, then files.
        File.AppendAllText(Path.Combine(dir, "Web", "Program.cs"), "// changed\n");
        File.WriteAllText(Path.Combine(lib, "docs", "README.md"), "lib 2\n");
        await vm.RefreshGitAsync();
        Assert.Equal(2, vm.UnstagedChanges.Count);
        Assert.Equal("Commit All Repos", vm.CommitButtonText);
        Assert.Equal([GitRowKind.Root, GitRowKind.Repository, GitRowKind.Folder, GitRowKind.File, GitRowKind.Repository, GitRowKind.Folder, GitRowKind.File],
            vm.UnstagedRows.Select(r => r.Kind));
        Assert.Equal(["Lib", "docs", "README.md"], vm.UnstagedRows.Skip(1).Take(3).Select(r => r.Name));
        Assert.Equal(["Web", "Program.cs"], vm.UnstagedRows.Skip(5).Select(r => r.Name));
        // Collapsing a repository hides its rows, and stays collapsed across refreshes.
        await vm.ActivateGitRowCommand.ExecuteAsync(vm.UnstagedRows[1]);
        Assert.Equal(5, vm.UnstagedRows.Count);
        await vm.RefreshGitAsync();
        Assert.Equal(5, vm.UnstagedRows.Count);
        await vm.ActivateGitRowCommand.ExecuteAsync(vm.UnstagedRows[1]);
        Assert.Equal(7, vm.UnstagedRows.Count);

        vm.ShowToolWindow(ToolWindows.GitChanges);
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(200));
        Snapshot(window, "git-changes-repositories");

        // Claude writes one message for both from their diffs, without its trailers; nothing is committed yet.
        await vm.WriteCommitMessageCommand.ExecuteAsync(null);
        Assert.Equal("Add the multi-repository commit panel\n\nLists every repository of the solution.", vm.CommitMessage);
        using (var print = JsonDocument.Parse(File.ReadAllText(Path.Combine(FakeClaude.ConfigDirectory, "last-print.json"))))
        {
            var args = print.RootElement.GetProperty("args").EnumerateArray().Select(a => a.GetString()).ToList();
            Assert.Equal(["-p", "--model", "haiku", "--tools", "", "--no-session-persistence", "--output-format", "text"], args);
            var prompt = print.RootElement.GetProperty("prompt").GetString()!;
            Assert.Contains("They span several repositories", prompt);
            Assert.Contains("=== Repository: Lib", prompt);
            Assert.Contains("+// changed", prompt);
            Assert.Contains("initial", prompt);
        }
        Assert.Equal(2, vm.UnstagedChanges.Count);

        // Commit All Repos: the same message in each, as the user, with no trailer.
        await vm.CommitCommand.ExecuteAsync(null);
        Assert.Equal(0, vm.GitChangeCount);
        Assert.Equal("", vm.CommitMessage);
        foreach (var repo in new[] { dir, lib })
        {
            Assert.Equal("Add the multi-repository commit panel", Output(repo, "log", "-1", "--format=%s"));
            Assert.Equal("VSL Tests", Output(repo, "log", "-1", "--format=%an"));
            Assert.DoesNotContain("Co-Authored-By", Output(repo, "log", "-1", "--format=%B"));
        }

        // The repository menu: Lib alone.
        File.WriteAllText(Path.Combine(lib, "docs", "README.md"), "lib 3\n");
        File.AppendAllText(Path.Combine(dir, "Web", "Program.cs"), "// again\n");
        await vm.RefreshGitAsync();
        await vm.SelectGitRepositoryCommand.ExecuteAsync(vm.GitRepositories[1]);
        Assert.Equal("Lib", vm.GitRepositoryLabel);
        Assert.Equal(["README.md"], vm.UnstagedChanges.Select(c => c.FileName));
        Assert.Equal("Commit All", vm.CommitButtonText);
        Assert.Equal(GitRowKind.Repository, vm.UnstagedRows[0].Kind);
        await vm.StageRowCommand.ExecuteAsync(vm.UnstagedRows[0]);
        Assert.Equal(["README.md"], vm.StagedChanges.Select(c => c.FileName));
        await vm.UnstageAllCommand.ExecuteAsync(null);
        await vm.SelectGitRepositoryCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.UnstagedChanges.Count);

        // A remote branch gets a local branch tracking it where it exists; Lib has none and stays on main.
        var (local, remoteBranches) = await vm.ScopeBranchesAsync();
        Assert.Contains("main", local);
        Assert.Contains("origin/feature", remoteBranches);
        TempRepo.Git(dir, "stash", "-q");
        await vm.SwitchRemoteBranchCommand.ExecuteAsync("origin/feature");
        Assert.Equal("feature", Output(dir, "branch", "--show-current"));
        Assert.Equal("origin/feature", Output(dir, "rev-parse", "--abbrev-ref", "feature@{u}"));
        Assert.Equal("main", Output(lib, "branch", "--show-current"));
        Assert.Equal("[multiple]", vm.GitScopeBranch);
        Assert.True(await vm.CloseSolutionCoreAsync());
        window.Close();
    }

    [Fact]
    public void A_written_message_loses_fences_quotes_and_trailers()
    {
        Assert.Equal("Fix the build", MainViewModel.CleanCommitMessage("```\nFix the build\n```"));
        Assert.Equal("Fix the build\n\nBody.", MainViewModel.CleanCommitMessage("\"Fix the build\n\nBody.\nSigned-off-by: A <a@b>\n🤖 Generated with Claude\""));
    }

    [AvaloniaFact]
    public async Task Solution_explorer_asks_Claude_about_a_file()
    {
        var sln = SolutionExplorerTests.CopySample();
        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        window.Show();
        await vm.OpenSolutionAsync(sln);
        var web = vm.ExplorerRoots[0].Children.First(n => n.Name == "Web");
        web.IsExpanded = true;
        var program = web.Children.First(n => n.Name == "Program.cs");
        var menu = window.ExplorerMenuItems(program);
        var ask = Assert.IsType<Avalonia.Controls.MenuItem>(menu[0]);
        Assert.Equal("Ask Claude", ask.Header);
        ask.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.MenuItem.ClickEvent));
        Assert.Equal("@Web/Program.cs ", vm.Claude.Prompt);
        Assert.True(window.IsToolWindowShowing(ToolWindows.Claude));
        // A project goes in as its folder.
        var projectAsk = (Avalonia.Controls.MenuItem)window.ExplorerMenuItems(web)[0];
        projectAsk.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.MenuItem.ClickEvent));
        Assert.Equal("@Web/Program.cs @Web ", vm.Claude.Prompt);
        Assert.True(await vm.CloseSolutionCoreAsync());
        window.Close();
    }
}
