using System.Diagnostics;
using System.Text.Json;
using Karvon.Claude;
using Karvon.Claude.Tools;

namespace Karvon.Tests;

/// <summary>git and packages through Karvon's tools: what Karvon's sandbox keeps from the agents' shell, done with the user's own access.</summary>
public class GitPackageToolsTests
{
    static JsonElement Input(object value) => JsonSerializer.SerializeToElement(value);

    static string Git(string folder, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, output);
        return output.Trim();
    }

    [Fact]
    public async Task Git_is_read_at_once_and_committed_branched_and_pushed_when_the_user_says_so()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("karvon-git-").FullName;
        var remote = Path.Combine(root, "remote.git");
        var work = Path.Combine(root, "work");
        Directory.CreateDirectory(work);
        Git(root, "init", "--bare", "--initial-branch=main", remote);
        Git(work, "init", "--initial-branch=main");
        Git(work, "config", "user.name", "Dev");
        Git(work, "config", "user.email", "dev@example.test");
        Git(work, "config", "commit.gpgsign", "false");
        Git(work, "remote", "add", "origin", remote);
        File.WriteAllText(Path.Combine(work, "a.txt"), "one\n");
        var tools = GitClaudeTools.Create(() => work).ToDictionary(t => t.Name);
        async Task<ClaudeToolOutput> Call(string tool, object input) => await tools[tool].CallAsync(Input(input), ct);

        Assert.Equal(["git_branch", "git_commit", "git_diff", "git_fetch", "git_log", "git_pull", "git_push", "git_status"], tools.Keys.Order());
        Assert.Equal(ToolAccess.Read, tools["git_status"].Plan(Input(new { })).Access);
        Assert.Equal(ToolAccess.Read, tools["git_diff"].Plan(Input(new { })).Access);
        Assert.Equal(ToolAccess.Read, tools["git_log"].Plan(Input(new { })).Access);
        Assert.Equal(ToolAccess.Run, tools["git_fetch"].Plan(Input(new { })).Access);
        foreach (var asks in new[] { "git_commit", "git_branch", "git_pull", "git_push" })
            Assert.Equal(ToolAccess.Write, tools[asks].Plan(Input(new { message = "m", action = "create", name = "x" })).Access);

        Assert.Equal("Branch main (no remote branch yet)\nU a.txt", (await Call("git_status", new { })).Text);
        Assert.Equal("Nothing is staged: give the files to commit, or all: true.", (await Call("git_commit", new { message = "First" })).Text);
        var commit = tools["git_commit"].Plan(Input(new { message = "First\n\nThe file.", files = new[] { "a.txt" } }));
        Assert.Equal(("Commit", "First", "Make this commit?"), (commit.Title, commit.Subject, commit.Question));
        Assert.Equal("First\n\nThe file.\n\na.txt", commit.Code);
        Assert.StartsWith("Committed.", (await Call("git_commit", new { message = "First\n\nThe file.", files = new[] { "a.txt" } })).Text);
        Assert.EndsWith("Dev: First", (await Call("git_log", new { })).Text);

        File.AppendAllText(Path.Combine(work, "a.txt"), "two\n");
        Assert.Contains("+two", (await Call("git_diff", new { })).Text);
        Assert.Equal("No changes.", (await Call("git_diff", new { staged = true })).Text);
        Assert.StartsWith("Committed.", (await Call("git_commit", new { message = "Second", all = true })).Text);

        // A branch of its own, pushed (its remote branch made), then fetched and pulled again.
        Assert.StartsWith("On the new branch feature.", (await Call("git_branch", new { action = "create", name = "feature" })).Text);
        var pushed = await Call("git_push", new { });
        Assert.False(pushed.IsError, pushed.Text);
        Assert.StartsWith("Pushed feature.", pushed.Text);
        Assert.Equal("Branch feature → origin/feature (ahead 0, behind 0)\nNo changes.", (await Call("git_status", new { })).Text);
        Assert.False((await Call("git_fetch", new { })).IsError);
        Assert.StartsWith("Pulled.", (await Call("git_pull", new { })).Text);
        Assert.Equal("Second", Git(remote, "log", "-1", "--format=%s", "feature"));

        // The shell's git that writes or reaches a remote is sent to them.
        Assert.Contains(IdeClaudeTools.ShellRules, r => r.Name == "git" && r.Matches("git push origin feature") && r.Matches("cd x && git commit -m y") && !r.Matches("git status"));
    }

    [Fact]
    public async Task Packages_are_added_and_restored_by_the_projects_own_manager_without_install_scripts()
    {
        var ct = TestContext.Current.CancellationToken;
        string Folder(params (string Name, string Text)[] files)
        {
            var folder = Directory.CreateTempSubdirectory("karvon-packages-").FullName;
            foreach (var (name, text) in files)
                File.WriteAllText(Path.Combine(folder, name), text);
            return folder;
        }

        Assert.Equal("nuget", PackageClaudeTools.Detect(Folder(("App.csproj", "<Project />"))));
        Assert.Equal("npm", PackageClaudeTools.Detect(Folder(("package.json", "{}"))));
        Assert.Equal("pip", PackageClaudeTools.Detect(Folder(("requirements.txt", ""))));
        Assert.Equal("cargo", PackageClaudeTools.Detect(Folder(("Cargo.toml", ""))));
        Assert.Equal("go", PackageClaudeTools.Detect(Folder(("go.mod", "module x"))));
        Assert.Null(PackageClaudeTools.Detect(Folder()));

        var dotnet = Folder(("App.csproj", "<Project />"));
        Assert.Equal(("dotnet", "add App.csproj package Newtonsoft.Json --version 13.0.3"),
            Line(PackageClaudeTools.Command("nuget", dotnet, "Newtonsoft.Json", "13.0.3", "App.csproj")));
        Assert.Equal(("dotnet", "restore"), Line(PackageClaudeTools.Command("nuget", dotnet, null, null, null)));
        Assert.Equal(("pnpm", "add zod@3 --ignore-scripts"), Line(PackageClaudeTools.Command("npm", Folder(("package.json", "{}"), ("pnpm-lock.yaml", "")), "zod", "3", null)));
        Assert.Equal(("npm", "ci --ignore-scripts"), Line(PackageClaudeTools.Command("npm", Folder(("package.json", "{}"), ("package-lock.json", "{}")), null, null, null)));
        Assert.Equal(("npm", "install left-pad --ignore-scripts"), Line(PackageClaudeTools.Command("npm", Folder(("package.json", "{}")), "left-pad", null, null)));
        Assert.Equal(("cargo", "add serde@1"), Line(PackageClaudeTools.Command("cargo", Folder(("Cargo.toml", "")), "serde", "1", null)));
        Assert.Equal(("go", "get golang.org/x/text@v0.20.0"), Line(PackageClaudeTools.Command("go", Folder(("go.mod", "module x")), "golang.org/x/text", "v0.20.0", null)));
        Assert.Equal("The project has no virtual environment (.venv) yet.", PackageClaudeTools.Command("pip", Folder(("requirements.txt", "")), "rich", null, null).Problem);

        // A package's install scripts do not run, whichever manager it is.
        var said = await PackageClaudeTools.RunAsync("bash", ["-c", "echo $npm_config_ignore_scripts"], dotnet, ct);
        Assert.Equal("bash -c echo $npm_config_ignore_scripts: done.\ntrue", said.Text);

        var tools = PackageClaudeTools.Create(() => dotnet).ToDictionary(t => t.Name);
        var add = tools["package_add"].Plan(Input(new { name = "Newtonsoft.Json", version = "13.0.3" }));
        Assert.Equal((ToolAccess.Write, "Add package", "Newtonsoft.Json 13.0.3"), (add.Access, add.Title, add.Subject));
        Assert.Equal(ToolAccess.Run, tools["package_restore"].Plan(Input(new { })).Access);

        // pip: the project's own environment, made the first time; nothing to install from an empty requirements.txt.
        var python = Folder(("requirements.txt", ""));
        var restored = await PackageClaudeTools.Create(() => python).Single(t => t.Name == "package_restore").CallAsync(Input(new { }), ct);
        Assert.False(restored.IsError, restored.Text);
        Assert.NotNull(PackageClaudeTools.VirtualEnvironmentPython(python));

        // The shell's installs are sent to them.
        Assert.Contains(IdeClaudeTools.ShellRules, r => r.Name == "package install" && r.Matches("npm install lodash") && r.Matches("dotnet add package X")
            && r.Matches("pip install requests") && !r.Matches("npm test"));
    }

    static (string Program, string Arguments) Line((string Program, List<string> Arguments, string? Problem) command)
    {
        Assert.Null(command.Problem);
        return (command.Program, string.Join(' ', command.Arguments));
    }
}
