using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Karvon.Docker.ViewModels;
using Karvon.Services;
using Karvon.Skills;
using Karvon.Skills.ViewModels;
using Karvon.Skills.Views;
using Karvon.ViewModels;
using Karvon.Ui;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

sealed class TestSkillsHost(string? workspace, string claudeDirectory) : ISkillsHost
{
    public string? Workspace { get; set; } = workspace;

    public string ClaudeDirectory { get; } = claudeDirectory;

    /// <summary>Agensi (agensi.io) is off unless a test turns it on (with its catalog answered by the test).</summary>
    public Settings Settings { get; } = new() { SkillsAgensi = false };

    public int Saved { get; private set; }

    public List<DocumentTab> Open { get; } = [];

    public List<(string Path, int Line)> Files { get; } = [];

    public List<string> Prompts { get; } = [];

    public List<string> Statuses { get; } = [];

    public void SaveSettings() => Saved++;

    public IEnumerable<DocumentTab> Documents => Open;

    public void ShowDocument(DocumentTab document)
    {
        if (!Open.Contains(document))
            Open.Add(document);
    }

    public void CloseDocument(DocumentTab document) => Open.Remove(document);

    public void OpenFile(string path, int line = 0) => Files.Add((path, line));

    public void AskClaude(string prompt) => Prompts.Add(prompt);

    public List<string> Urls { get; } = [];

    public void OpenUrl(string url) => Urls.Add(url);

    public List<(string Name, string Url)> McpServers { get; } = [];

    public Task<string?> EnsureClaudeMcpServerAsync(string name, string url)
    {
        if (!McpServers.Contains((name, url)))
            McpServers.Add((name, url));
        return Task.FromResult<string?>(null);
    }

    public void Status(string text) => Statuses.Add(text);
}

/// <summary>
/// The Skills: the skills, subagents, commands and hooks of a folder, a Claude Code folder and its plugins (all the
/// tests' own), the checks and "Try a prompt", the memory files, and the marketplace with a team's registry on disk.
/// </summary>
public class SkillsTests
{
    static string Folder(string name) => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), name + "-" + Guid.NewGuid().ToString("N"))).FullName;

    static void Write(string path, string text, bool executable = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        if (executable && !OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute);
    }

    /// <summary>A folder with a good skill and a poor one, a subagent, a command and a hook; a Claude Code folder with a skill of its own and a plugin's.</summary>
    static (string Workspace, string Claude) Sample()
    {
        var workspace = Folder("karvon-skills-ws");
        var claude = Folder("karvon-skills-claude");
        var skills = Path.Combine(workspace, ".claude", "skills");
        Write(Path.Combine(skills, "release-notes", "SKILL.md"), """
            ---
            name: release-notes
            description: Writes the release notes from the merged changes. Use when preparing a release.
            ---

            # Release notes

            Follow [the rules](reference/rules.md) and run [the script](scripts/changes.sh).
            """);
        Write(Path.Combine(skills, "release-notes", "reference", "rules.md"), "One line a change.\n");
        Write(Path.Combine(skills, "release-notes", "scripts", "changes.sh"), "#!/bin/sh\ngit log --oneline\n", executable: true);
        Write(Path.Combine(skills, "BadName", "SKILL.md"), """
            ---
            name: Bad Name
            ---

            See [the guide](guide.md).
            """);
        Write(Path.Combine(skills, "BadName", "tool.py"), "#!/usr/bin/env python3\nprint('hi')\n");
        Write(Path.Combine(workspace, ".claude", "agents", "reviewer.md"), "---\nname: reviewer\ndescription: Reviews a change before it is merged.\n---\nYou review.\n");
        Write(Path.Combine(workspace, ".claude", "commands", "frontend", "test.md"), "Run the frontend's tests.\n");
        Write(Path.Combine(workspace, ".claude", "settings.json"), """
            { // the folder's
              "hooks": { "PreToolUse": [ { "matcher": "Bash", "hooks": [ { "type": "command", "command": "./check.sh" } ] } ] },
            }
            """);
        Write(Path.Combine(claude, "skills", "pdf-forms", "SKILL.md"), """
            ---
            name: pdf-forms
            description: >
              Fills PDF forms.
              Use when a form needs filling.
            ---
            Fill it.
            """);
        Write(Path.Combine(claude, "plugins", "cache", "market", "docs", "1.0.0", "skills", "pptx", "SKILL.md"),
            "---\nname: pptx\ndescription: \"Makes slide decks. Use for any presentation.\"\n---\nSlides.\n");
        return (workspace, claude);
    }

    [Fact]
    public void Skills_agents_commands_and_hooks_are_read_and_checked()
    {
        var (workspace, claude) = Sample();
        var library = new SkillLibrary(workspace, claude);
        var skills = library.Skills();
        Assert.Equal(["project:BadName", "project:release-notes", "user:pdf-forms", "plugin:docs:pptx"], skills.Select(s => s.Id));
        Assert.Equal("Fills PDF forms. Use when a form needs filling.", skills[2].Description);
        Assert.Equal("Makes slide decks. Use for any presentation.", skills[3].Description);
        Assert.Equal(["this folder", "this folder", "your skills", "plugin docs"], skills.Select(s => s.Where));
        Assert.False(skills[3].IsYours);

        // A good skill passes every check; a poor one says what is wrong.
        Assert.All(SkillLibrary.Checks(skills[1]), c => Assert.True(c.Passed, c.Text));
        // Whether its scripts can run is Unix's file mode: Windows has none.
        Assert.Equal(OperatingSystem.IsWindows() ? "Linked files exist" : "Scripts are executable", SkillLibrary.Checks(skills[1])[^1].Text);
        Assert.Equal(
        [
            "The name \"Bad Name\" is not the folder's, \"BadName\"",
            "The name should be lowercase letters, digits and hyphens, at most 64",
            "There is no description: agents choose skills by it",
            "Missing linked files: guide.md",
            .. OperatingSystem.IsWindows() ? Array.Empty<string>() : ["Scripts that cannot run (chmod +x): tool.py"],
        ], SkillLibrary.Checks(skills[0]).Select(c => c.Text));

        Assert.Equal(["reviewer: Reviews a change before it is merged."], library.Subagents().Select(a => $"{a.Name}: {a.Description}"));
        Assert.Equal(["/frontend:test: Run the frontend's tests."], library.Commands().Select(c => $"{c.Name}: {c.Description}"));
        Assert.Equal(["PreToolUse Bash ./check.sh"], library.Hooks().Select(h => $"{h.Event} {h.Matcher} {h.Command}"));

        // Try a prompt: whether it gives an agent a reason to load the skill.
        // Both sides are stemmed alike ("notes" and "note" are "not", "preparing" "prepar").
        var (verdict, shared) = SkillLibrary.Try(skills[1], "Write the release notes, we are preparing 2.0");
        Assert.Equal("Likely to load", verdict);
        Assert.Equal(["not", "prepar", "release"], shared);
        Assert.Equal("Might load", SkillLibrary.Try(skills[1], "what changed since the last release?").Verdict);
        Assert.Equal("Unlikely to load", SkillLibrary.Try(skills[1], "rename this method").Verdict);
        Assert.Equal("release-notes", SkillLibrary.NameFor("Release Notes!"));
    }

    [Fact]
    public void Memory_files_are_written_from_one_source_and_kept_in_step()
    {
        var (workspace, claude) = Sample();
        var library = new SkillLibrary(workspace, claude);
        var memory = new MemoryFiles(workspace, "CLAUDE.md", () => [.. library.Skills().Where(s => s.Scope == SkillScope.Project)]);
        Assert.False(memory.HasSource);
        Assert.Empty(memory.Sync(createMissing: true, replaceHandWritten: false).Written);

        File.WriteAllText(Path.Combine(workspace, "CLAUDE.md"), "# Shop\n\nUse tabs.\n");
        Assert.Equal(MemoryState.Missing, memory.StateOf("AGENTS.md"));
        Assert.Equal(["AGENTS.md", "GEMINI.md"], memory.Sync(createMissing: true, replaceHandWritten: false).Written);
        var agents = File.ReadAllText(Path.Combine(workspace, "AGENTS.md"));
        Assert.StartsWith("<!-- Generated by Karvon from CLAUDE.md: edit CLAUDE.md, and Karvon writes this file again. -->\n\n# Shop\n\nUse tabs.\n", agents);
        // The other agents do not load skills: the file says where the folder's are.
        Assert.Contains("- `.claude/skills/release-notes/SKILL.md`: Writes the release notes from the merged changes. Use when preparing a release.", agents);
        Assert.DoesNotContain("pdf-forms", agents);
        Assert.Equal(MemoryState.UpToDate, memory.StateOf("GEMINI.md"));

        // The source changes: the generated files follow; one written by hand is left until the user says.
        File.WriteAllText(Path.Combine(workspace, "CLAUDE.md"), "# Shop\n\nUse spaces.\n");
        File.WriteAllText(Path.Combine(workspace, "GEMINI.md"), "Mine.\n");
        Assert.Equal(MemoryState.OutOfDate, memory.StateOf("AGENTS.md"));
        Assert.Equal(MemoryState.HandWritten, memory.StateOf("GEMINI.md"));
        var (written, left) = memory.Sync(createMissing: false, replaceHandWritten: false);
        Assert.Equal(["AGENTS.md"], written);
        Assert.Equal(["GEMINI.md"], left);
        Assert.Contains("Use spaces.", File.ReadAllText(Path.Combine(workspace, "AGENTS.md")));
        Assert.Equal("Mine.\n", File.ReadAllText(Path.Combine(workspace, "GEMINI.md")));
        Assert.Equal(["GEMINI.md"], memory.Sync(createMissing: true, replaceHandWritten: true).Written);
    }

    /// <summary>A team's registry on disk: a skill that reaches the network through its script, and one that does not.</summary>
    static string Registry(string version = "1.0.0")
    {
        var registry = Folder("karvon-skills-registry");
        File.WriteAllText(Path.Combine(registry, "registry.json"), new JsonObject
        {
            ["name"] = "Team skills",
            ["skills"] = new JsonArray(
                new JsonObject { ["name"] = "deploy-check", ["description"] = "Checks a deployment's health. Use after deploying.", ["version"] = version, ["category"] = "ops", ["rating"] = 4.5, ["installs"] = 12, ["path"] = "skills/deploy-check" },
                new JsonObject { ["name"] = "sql-style", ["description"] = "Formats SQL the team's way. Use when writing SQL.", ["version"] = "2.1.0", ["category"] = "data", ["path"] = "skills/sql-style" }),
        }.ToJsonString());
        Write(Path.Combine(registry, "skills", "deploy-check", "SKILL.md"), "---\nname: deploy-check\ndescription: Checks a deployment's health. Use after deploying.\nallowed-tools: Bash, WebFetch\n---\nRun [the probe](probe.sh).\n");
        Write(Path.Combine(registry, "skills", "deploy-check", "probe.sh"), "#!/bin/sh\ncurl -fsS https://status.example.com/health\n");
        Write(Path.Combine(registry, "skills", "sql-style", "SKILL.md"), "---\nname: sql-style\ndescription: Formats SQL the team's way. Use when writing SQL.\n---\nUppercase keywords.\n");
        return registry;
    }

    [Fact]
    public async Task The_marketplace_shows_what_a_skill_asks_for_and_installs_it()
    {
        var (workspace, claude) = Sample();
        var library = new SkillLibrary(workspace, claude);
        var registry = Registry();
        var (skills, problems) = await SkillMarketplace.ListAsync([registry, "github:not a repo", Path.Combine(registry, "missing.json")], CancellationToken.None);
        Assert.Equal(["deploy-check", "sql-style"], skills.Select(s => s.Skill.Name));
        Assert.Equal("Team skills", skills[0].Skill.Source);
        Assert.Equal(2, problems.Count);
        Assert.StartsWith("A GitHub source is github:owner/repo", problems[0]);
        // The words it shares, and its name twice.
        Assert.Equal(2, SkillMarketplace.Score(skills[1].Skill, "sql style"));
        Assert.Equal(3, SkillMarketplace.Score(skills[1].Skill, "sql"));
        Assert.Equal(0, SkillMarketplace.Score(skills[1].Skill, "docker"));

        // Before it is installed: the tools it allows, its scripts, and the network.
        var (source, deploy) = skills[0];
        var review = SkillMarketplace.Review(await source.FilesAsync(deploy, CancellationToken.None));
        Assert.Equal(["Bash", "WebFetch"], review.AllowedTools);
        Assert.Equal(["probe.sh"], review.Scripts.Select(s => s.Path));
        Assert.Equal(["it allows WebFetch", "probe.sh uses curl"], review.Network);

        // Installed in the folder's skills: its files, its script runnable, where it came from.
        var folder = SkillMarketplace.Install(library, deploy, review, forFolder: true);
        Assert.Equal(Path.Combine(workspace, ".claude", "skills", "deploy-check"), folder);
        if (!OperatingSystem.IsWindows())
            Assert.True(File.GetUnixFileMode(Path.Combine(folder, "probe.sh")).HasFlag(UnixFileMode.UserExecute));
        var (installed, version, hash) = SkillMarketplace.Installed(library, deploy);
        Assert.Equal(("project:deploy-check", "1.0.0", review.Hash), (installed!.Id, version, hash));
        Assert.All(SkillLibrary.Checks(installed), c => Assert.True(c.Passed, c.Text));
        Assert.False(SkillMarketplace.HasUpdate(deploy, version, hash, review.Hash));

        // A newer version in the registry is an update; without versions, other content is.
        var newer = (await SkillMarketplace.ListAsync([Registry("1.1.0")], CancellationToken.None)).Skills[0].Skill;
        Assert.True(SkillMarketplace.HasUpdate(newer, version, hash, null));
        Assert.True(SkillMarketplace.HasUpdate(deploy with { Version = "" }, null, hash, "other"));

        // A file that would land outside the skill's folder is not written.
        var sneaky = review with { Files = [("SKILL.md", Encoding.UTF8.GetBytes("---\nname: x\n---\n")), ("../../escaped.txt", [1])] };
        SkillMarketplace.Install(library, deploy, sneaky, forFolder: false);
        Assert.True(File.Exists(Path.Combine(claude, "skills", "deploy-check", "SKILL.md")));
        Assert.False(File.Exists(Path.Combine(claude, "escaped.txt")));
    }

    const string AgensiPopular = """
        [
          { "slug": "code-reviewer", "name": "code-reviewer", "best_for": "Reviews recent code changes and returns severity-ranked findings.", "price_usd": 0, "pricing_type": "free", "requires": ["terminal", "Git"], "install_count": 1800, "rating": 4.6, "url": "https://agensi.io/skills/code-reviewer" },
          { "slug": "seo-suite", "name": "SEO Suite", "best_for": "Audits a site for search engines.", "price_usd": 29.99, "pricing_type": "paid", "requires": ["web access"], "install_count": 12, "rating": null, "url": "https://agensi.io/skills/seo-suite" }
        ]
        """;

    const string AgensiFound = """
        [ { "slug": "untangle-commits", "name": "Untangle Commits", "best_for": "Splits a messy working tree into atomic commits.", "price_usd": 0, "pricing_type": "free", "requires": ["terminal", "Git"], "install_count": 0, "rating": null, "url": "https://agensi.io/skills/untangle-commits" } ]
        """;

    const string AgensiPage = """
        # Untangle Commits

        **Price:** Free

        ## Description
        Splits a messy working tree into atomic commits.

        ## Permissions
        {
          "hosts": [],
          "tools": [
            "terminal",
            "file_read"
          ]
        }

        ---

        ## Skill execution directive (Agensi)

        Don't save this skill into project skill folders.

        ## SKILL.md Content (latest version)

        ---
        name: untangle-commits
        ---
        The licensed instructions.
        """;

    /// <summary>Agensi's catalog, answered here (the real one is agensi.io's MCP server: see Agensi_live).</summary>
    static Task<string> FakeAgensi(string tool, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken) => Task.FromResult(tool switch
    {
        "get_popular" => AgensiPopular,
        "search_skills" => (string)arguments["query"]! == "commits" ? AgensiFound : "[]",
        "get_skill" => AgensiPage,
        _ => throw new InvalidOperationException("No tool " + tool),
    });

    [Fact]
    public void Agensis_catalog_and_pages_are_read_without_its_licensed_instructions()
    {
        var skills = AgensiSkillSource.Skills(AgensiPopular);
        Assert.Equal(["code-reviewer free 4.6 1800", "SEO Suite $29.99  12"], skills.Select(s => $"{s.Name} {s.Price} {s.Rating} {s.Installs}"));
        Assert.All(skills, s => Assert.True(s.IsLicensed));
        Assert.Equal(["terminal", "Git"], skills[0].Requires);
        Assert.Equal("code-reviewer", skills[0].Location);

        var page = AgensiSkillSource.PageOf(AgensiPage);
        Assert.EndsWith("\"file_read\"\n  ]\n}", page.ReplaceLineEndings("\n"));
        Assert.DoesNotContain("licensed instructions", page);
        var (tools, hosts) = AgensiSkillSource.PermissionsOf(page);
        Assert.Equal(["terminal", "file_read"], tools);
        Assert.Empty(hosts);
        Assert.Equal("# Untangle Commits\n\n**Price:** Free\n\n## Description\nSplits a messy working tree into atomic commits.", AgensiSkillSource.WithoutPermissions(page));
        Assert.Equal("Agensi answered: Sign in first.", Assert.Throws<InvalidOperationException>(() => AgensiSkillSource.Skills("Sign in first.")).Message);
    }

    /// <summary>agensi.io's real catalog, read anonymously (popular, a search, a page). Skipped unless KARVON_AGENSI_LIVE=1.</summary>
    [Fact]
    public async Task Agensi_live()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("KARVON_AGENSI_LIVE") == "1", "Set KARVON_AGENSI_LIVE=1 to read agensi.io's catalog.");
        var agensi = new AgensiSkillSource();
        var popular = await agensi.ListAsync(CancellationToken.None);
        Assert.NotEmpty(popular);
        var found = await agensi.SearchAsync("code review", CancellationToken.None);
        Assert.NotEmpty(found);
        var free = popular.First(s => s.IsFree);
        var page = await agensi.DetailsAsync(free, CancellationToken.None);
        Assert.StartsWith("# ", page);
        Assert.DoesNotContain("Skill execution directive", page);
    }

    [AvaloniaFact]
    public async Task Agensi_skills_are_chosen_by_source_searched_there_and_used_in_claude_code()
    {
        var (workspace, claude) = Sample();
        var host = new TestSkillsHost(workspace, claude);
        host.Settings.SkillsSyncMemoryFiles = false;
        host.Settings.SkillSources = [Registry()];
        host.Settings.SkillsAgensi = true;
        AgensiSkillSource.CallTool = FakeAgensi;
        var vm = new SkillsExplorerViewModel(host);
        try
        {
            var market = vm.OpenMarketplace().Marketplace;
            var window = new Window { Width = 1300, Height = 820, Content = new SkillsMarketplaceView { DataContext = market } };
            window.Show();
            await market.Listed;
            Assert.Equal(["Every source", "Team skills", "Agensi"], market.SourceNames);
            // Every source's, the best rated first.
            Assert.Equal(["code-reviewer", "deploy-check", "SEO Suite", "sql-style"], market.Shown.Select(r => r.Skill.Name));
            Assert.Equal(["free", "", "$29.99", ""], market.Shown.Select(r => r.Badge));

            // The source select: only Agensi's; then the free ones.
            market.Source = "Agensi";
            Assert.Equal(["code-reviewer", "SEO Suite"], market.Shown.Select(r => r.Skill.Name));
            market.Filter = "Free";
            Assert.Equal(["code-reviewer"], market.Shown.Select(r => r.Skill.Name));
            market.Filter = "All";

            // Agensi's catalog is searched at Agensi.
            market.Search = "commits";
            Assert.Equal("Searching Agensi…", market.Status);
            await market.Searched;
            Assert.Equal(["Untangle Commits"], market.Shown.Select(r => r.Skill.Name));
            Assert.Null(market.Status);

            // Its page: what it uses, how it is used; nothing is installed.
            market.Select("Untangle Commits");
            await market.Opened;
            var detail = market.Detail!;
            Assert.True(detail.IsLicensed);
            Assert.False(detail.IsNew);
            Assert.Equal("terminal, file_read", detail.Tools);
            Assert.Equal("terminal, Git", detail.Requires);
            Assert.DoesNotContain("licensed instructions", detail.Page);
            Assert.DoesNotContain("Permissions", detail.Page);
            await detail.UseInClaudeCommand.ExecuteAsync(null);
            Assert.Equal([("agensi", AgensiSkillSource.ConnectorUrl)], host.McpServers);
            Assert.Contains("(slug untangle-commits)", Assert.Single(host.Prompts));
            detail.OpenPageCommand.Execute(null);
            Assert.Equal(["https://agensi.io/skills/untangle-commits"], host.Urls);
            Assert.False(Directory.Exists(Path.Combine(workspace, ".claude", "skills", "untangle-commits")));
            await WaitUntil(() => false, TimeSpan.FromMilliseconds(150));
            Snapshot(window, "skills-agensi");

            // Agensi off: only the other sources.
            market.UsesAgensi = false;
            await market.Listed;
            Assert.Equal(["Every source", "Team skills"], market.SourceNames);
            Assert.Equal(1, host.Saved);
            window.Close();
        }
        finally
        {
            AgensiSkillSource.CallTool = AgensiSkillSource.CallOverMcpAsync;
            vm.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task The_tool_window_opens_skills_writes_memory_files_and_installs_from_the_marketplace()
    {
        var (workspace, claude) = Sample();
        File.WriteAllText(Path.Combine(workspace, "CLAUDE.md"), "# Shop\n");
        File.WriteAllText(Path.Combine(workspace, "GEMINI.md"), "Mine.\n");
        var host = new TestSkillsHost(workspace, claude);
        host.Settings.SkillsSyncMemoryFiles = false;
        host.Settings.SkillSources = [Registry()];
        var vm = new SkillsExplorerViewModel(host);
        var page = new ContentControl();
        var window = new Window
        {
            Width = 1300,
            Height = 820,
            Content = new Grid { ColumnDefinitions = new("320,*"), Children = { new SkillsExplorerView { DataContext = vm }, page, new SkillsOverlay { DataContext = vm } } },
        };
        Grid.SetColumn(page, 1);
        Grid.SetColumnSpan(((Grid)window.Content).Children[2], 2);
        window.Show();
        try
        {
            var groups = vm.Roots.OfType<SkillsGroupNode>().ToList();
            Assert.IsType<SkillsMarketplaceNode>(vm.Roots[0]);
            Assert.Equal(["Skills", "Subagents", "Commands", "Hooks", "Memory files"], groups.Select(g => g.Title));
            Assert.Equal("4 · 1 with problems", groups[0].Detail);
            Assert.Equal(["CLAUDE.md the source", "AGENTS.md not generated", "GEMINI.md written by hand"],
                groups[4].Items.OfType<SkillsFileNode>().Select(f => $"{f.Name} {f.Detail}"));

            // A skill's page: its checks, a prompt tried against it (and the skill that matches it better).
            var bad = groups[0].Items.OfType<SkillNode>().First();
            Assert.Equal($"this folder · {(OperatingSystem.IsWindows() ? 4 : 5)} problems", bad.Detail);
            bad.OpenCommand.Execute(null);
            var skillPage = Assert.IsType<SkillDocument>(Assert.Single(host.Open)).Page;
            page.Content = new SkillPageView { DataContext = skillPage };
            Assert.True(skillPage.HasProblems);
            skillPage.Prompt = "write the release notes";
            skillPage.TryCommand.Execute(null);
            Assert.Equal("Unlikely to load: they share no words that matter. release-notes matches it better.", skillPage.Verdict);
            skillPage.AskClaudeCommand.Execute(null);
            Assert.Contains("There is no description", Assert.Single(host.Prompts));
            await WaitUntil(() => false, TimeSpan.FromMilliseconds(150));
            Snapshot(window, "skills-page");

            // New Skill: in the folder, opened in the editor at its description.
            var making = vm.NewSkillCommand.ExecuteAsync(null);
            var name = Assert.IsType<InputDialog>(vm.Dialog);
            Assert.Equal("This folder (.claude/skills)", name.Choice);
            name.Text = "Changelog Writer";
            name.Confirm();
            await making;
            var made = Path.Combine(workspace, ".claude", "skills", "changelog-writer", "SKILL.md");
            Assert.Equal((made, 3), host.Files[^1]);
            Assert.Contains(vm.Roots.OfType<SkillsGroupNode>().First().Items.OfType<SkillNode>(), s => s.Name == "changelog-writer");

            // The memory files: AGENTS.md written, GEMINI.md (by hand) replaced only when the user says so.
            var generating = vm.GenerateMemoryFilesCommand.ExecuteAsync(null);
            var replace = Assert.IsType<ConfirmDialog>(vm.Dialog);
            Assert.Equal("GEMINI.md was written by hand", replace.Title);
            replace.Confirm();
            await generating;
            Assert.StartsWith("<!-- Generated by Karvon", File.ReadAllText(Path.Combine(workspace, "GEMINI.md")));
            Assert.Equal("Wrote AGENTS.md and GEMINI.md from CLAUDE.md.", host.Statuses[^1]);
            Assert.Equal("generated, up to date", vm.Roots.OfType<SkillsGroupNode>().Last().Items.OfType<SkillsFileNode>().Last().Detail);

            // The marketplace: the registry's skills, searched; a skill's page says what it asks for, then installs it.
            var market = vm.OpenMarketplace().Marketplace;
            page.Content = new SkillsMarketplaceView { DataContext = market };
            await market.Listed;
            Assert.Equal(["deploy-check", "sql-style"], market.Shown.Select(r => r.Skill.Name));
            market.Search = "format sql";
            Assert.Equal(["sql-style"], market.Shown.Select(r => r.Skill.Name));
            market.Search = "";
            market.Select("deploy-check");
            await market.Opened;
            var detail = market.Detail!;
            Assert.True(detail.UsesNetwork);
            Assert.Equal("it allows WebFetch; probe.sh uses curl", detail.Network);
            Assert.True(detail.ForFolder);
            detail.InstallCommand.Execute(null);
            Assert.StartsWith("Installed in ", detail.Result);
            Assert.Contains(vm.Roots.OfType<SkillsGroupNode>().First().Items.OfType<SkillNode>(), s => s.Name == "deploy-check");
            await market.Listed;
            await WaitUntil(() => market.Shown.FirstOrDefault()?.Installed == true, TimeSpan.FromSeconds(5));
            market.Select("deploy-check");
            await market.Opened;
            Assert.Equal("Install again", market.Detail!.InstallText);
            await WaitUntil(() => false, TimeSpan.FromMilliseconds(150));
            Snapshot(window, "skills-marketplace");

            // The sources are Karvon's settings.
            market.Sources = "github:anthropics/skills\n\n" + host.Settings.SkillSources[0];
            await market.SaveSourcesCommand.ExecuteAsync(null);
            Assert.Equal(2, host.Settings.SkillSources.Count);
            Assert.Equal(1, host.Saved);
        }
        finally
        {
            vm.Dispose();
            window.Close();
        }
    }
}
