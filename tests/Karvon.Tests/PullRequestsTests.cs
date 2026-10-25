using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Karvon.Claude;
using Karvon.Git;
using Karvon.Modules;
using Karvon.Pipelines;
using Karvon.PullRequests;
using Karvon.PullRequests.Providers;
using Karvon.PullRequests.ViewModels;
using Karvon.PullRequests.Views;
using Karvon.Ui;
using Karvon.ViewModels;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>What the Pull Requests window asked of Karvon, and the repositories it is told.</summary>
sealed class TestPullRequestsHost : IPullRequestsHost
{
    public List<GitRepositoryState> States { get; } = [];

    public IReadOnlyList<GitRepositoryState> Repositories => States;

    public event Action? GitChanged;

    public event Action<GitRepository>? GitPushed;

    public void Changed() => GitChanged?.Invoke();

    public void Pushed(GitRepository repository) => GitPushed?.Invoke(repository);

    public List<DocumentTab> Open { get; } = [];

    public IEnumerable<DocumentTab> Documents => Open;

    public void ShowDocument(DocumentTab document)
    {
        if (!Open.Contains(document))
            Open.Add(document);
    }

    public List<(string Path, string Before, string After)> Diffs { get; } = [];

    public void ShowDiff(string path, string before, string after, string beforeTitle, string afterTitle, string key) => Diffs.Add((path, before, after));

    public List<string> Urls { get; } = [];

    public List<string> Statuses { get; } = [];

    public List<string> Prompts { get; } = [];

    public void OpenUrl(string url) => Urls.Add(url);

    public void Status(string text) => Statuses.Add(text);

    public void AskClaude(string prompt) => Prompts.Add(prompt);

    public List<GitRepository> Pushes { get; } = [];

    public Task<bool> PushAsync(GitRepository repository)
    {
        Pushes.Add(repository);
        return Task.FromResult(true);
    }

    /// <summary>The git commands asked for: told, never run (they would reach the remote).</summary>
    public List<string> GitRuns { get; } = [];

    public Task<bool> RunGitAsync(string title, Func<Task<GitResult>> operation, bool changesFiles)
    {
        GitRuns.Add(title);
        return Task.FromResult(true);
    }
}

/// <summary>Pull Requests: GitHub's through a fake gh, GitLab's merge requests through a fake server; the window, a page, Claude's tools.</summary>
public class PullRequestsTests
{
    const string Repo = "repos/karvon-test/shop";
    const string Patch = "@@ -1,3 +1,3 @@\n a\n-b\n+B\n c";

    static string At(int minutesAgo) => DateTimeOffset.UtcNow.AddMinutes(-minutesAgo).ToString("yyyy-MM-ddTHH:mm:ssZ");

    static object Pull(int number, string title, string author, string head, bool draft = false, string[]? reviewers = null) => new
    {
        number, title, user = new { login = author }, state = "open", draft, merged_at = (string?)null, body = "Fixes the **cart** total.",
        head = new { @ref = head, sha = "headsha1234", repo = new { full_name = "karvon-test/shop" } },
        @base = new { @ref = "main", sha = "basesha5678", repo = new { full_name = "karvon-test/shop" } },
        created_at = At(60 * 24), updated_at = At(30), html_url = $"https://github.com/karvon-test/shop/pull/{number}",
        requested_reviewers = (reviewers ?? []).Select(r => new { login = r }), labels = new[] { new { name = "bug" } },
        comments = 1, review_comments = 1, mergeable = true, mergeable_state = "clean", additions = 1, deletions = 1, changed_files = 1,
    };

    /// <summary>The fake gh with a repository of three open pull requests, the first (#12) with its conversation, reviews, checks and file.</summary>
    static FakeGh GitHub()
    {
        var gh = new FakeGh();
        gh.Route("GET", "user", new { login = "tester" });
        gh.Route("GET", Repo, new { default_branch = "main" });
        gh.Route("GET", $"{Repo}/pulls?state=open&per_page=50&sort=updated&direction=desc", new[]
        {
            Pull(12, "Fix the cart total", "ada", "fix-cart"), Pull(11, "WIP login", "tester", "login", draft: true),
            Pull(10, "Docs", "bob", "docs", reviewers: ["tester"]),
        });
        gh.Route("GET", $"{Repo}/pulls?state=open&per_page=20&sort=updated&direction=desc", new[] { Pull(12, "Fix the cart total", "ada", "fix-cart") });
        gh.Route("GET", $"{Repo}/pulls/12", Pull(12, "Fix the cart total", "ada", "fix-cart"));
        gh.Route("GET", $"{Repo}/issues/12/comments?per_page=100", new[] { new { id = 1, user = new { login = "bob" }, body = "Looks good", created_at = At(20) } });
        gh.Route("GET", $"{Repo}/pulls/12/comments?per_page=100", new[]
        {
            new { id = 2, user = new { login = "bob" }, body = "Off by one?", path = "src/Cart.cs", line = 3, created_at = At(10) },
        });
        gh.Route("GET", $"{Repo}/pulls/12/reviews?per_page=100", new[]
        {
            new { id = 5, user = new { login = "bob" }, state = "CHANGES_REQUESTED", submitted_at = At(25), body = "" },
            new { id = 6, user = new { login = "bob" }, state = "COMMENTED", submitted_at = At(15), body = "" },
            new { id = 7, user = new { login = "carol" }, state = "APPROVED", submitted_at = At(5), body = "Ship it" },
        });
        gh.Route("GET", $"{Repo}/commits/headsha1234/check-runs?per_page=100", new
        {
            check_runs = new object[]
            {
                new { name = "build", status = "completed", conclusion = "success", html_url = "https://github.com/karvon-test/shop/runs/1" },
                new { name = "test", status = "in_progress", conclusion = (string?)null, html_url = "https://github.com/karvon-test/shop/runs/2" },
            },
        });
        gh.Route("GET", $"{Repo}/pulls/12/files?per_page=100", new[] { new { filename = "src/Cart.cs", status = "modified", additions = 1, deletions = 1, patch = Patch } });
        gh.Route("GET", $"{Repo}/contents/src/Cart.cs?ref=headsha1234", new { content = Convert.ToBase64String(Encoding.UTF8.GetBytes("a\nB\nc\n")), encoding = "base64" });
        gh.Route("POST", $"{Repo}/pulls", Pull(13, "Add search", "tester", "search"));
        gh.Route("GET", $"{Repo}/pulls/13", Pull(13, "Add search", "tester", "search"));
        foreach (var empty in new[] { "issues/13/comments", "pulls/13/comments", "pulls/13/reviews", "pulls/13/files" })
            gh.Route("GET", $"{Repo}/{empty}?per_page=100", Array.Empty<object>());
        return gh;
    }

    [Theory]
    [InlineData("a\nB\nc\n", Patch, "a\nb\nc\n")]
    [InlineData("x\na\nb\n", "@@ -0,0 +1 @@\n+x", "a\nb\n")]
    [InlineData("a\nb\n", "@@ -2,0 +3,2 @@\n+c\n+d", null)]
    [InlineData("a\n", "@@ -1,2 +1 @@\n a\n-b", "a\nb\n")]
    [InlineData("", "@@ -1,2 +0,0 @@\n-a\n-b", "a\nb\n")]
    [InlineData("a\nb\n", "Binary files differ", null)]
    public void A_patch_gives_the_file_before_the_change(string after, string patch, string? before) =>
        Assert.Equal(before, UnifiedPatch.Reverse(after, patch));

    [Fact]
    public async Task GitHubs_pull_requests_are_read_and_reviewed_through_gh()
    {
        var gh = GitHub();
        var remote = RepoRemote.Parse("https://github.com/karvon-test/shop.git")!;
        var connector = new PipelineConnector(new PipelineSecrets(null), () => gh.Command);
        using var github = new GitHubPullRequests(remote, (await connector.GitHubAsync(remote, default))!);

        var list = await github.ListAsync(PullRequestFilter.Open, 50, default);
        Assert.Equal([("12", PullRequestState.Open), ("11", PullRequestState.Draft), ("10", PullRequestState.Open)], list.Select(p => (p.Number, p.State)));
        Assert.Equal(["tester"], list[2].Reviewers);
        var pr = await github.GetAsync("12", default);
        Assert.Equal(("ada", "fix-cart", "main", true, false, 2), (pr.Author, pr.SourceBranch, pr.TargetBranch, pr.Mergeable, pr.IsFromFork, pr.Comments));
        Assert.Equal("tester", await github.UserAsync(default));
        Assert.Equal("main", await github.DefaultBranchAsync(default));

        var details = await github.DetailsAsync(pr, default);
        Assert.Equal(["Looks good", "Off by one?", "Ship it"], details.Comments.Select(c => c.Body));
        Assert.Equal(("src/Cart.cs", 3), (details.Comments[1].Path, details.Comments[1].Line));
        // A comment after asking for changes does not take the request back.
        Assert.Equal([("bob", ReviewVerdict.RequestChanges), ("carol", ReviewVerdict.Approve)], details.Reviews.Select(r => (r.Author, r.Verdict)));
        Assert.Equal([("build", PipelineStatus.Success), ("test", PipelineStatus.Running)], details.Checks.Select(c => (c.Name, c.Status)));
        var file = Assert.Single(await github.FilesAsync(pr, default));
        Assert.Equal(("src/Cart.cs", "M", 1, 1), (file.Path, file.Letter, file.Additions, file.Deletions));
        Assert.Equal("a\nB\nc\n", await github.FileAtAsync(pr.HeadSha, file.Path, default));
        Assert.Null(await github.FileAtAsync(pr.HeadSha, "gone.cs", default));

        var made = await github.CreateAsync("search", "main", "Add search", "Searches the shop.", draft: false, default);
        Assert.Equal("13", made.Number);
        await github.CommentAsync(pr, "Thanks", default);
        await github.ReviewAsync(pr, ReviewVerdict.Approve, "", default);
        await github.MergeAsync(pr, MergeMethod.Squash, deleteBranch: true, default);
        await github.CloseAsync(made, default);
        var calls = gh.Calls;
        void Sent(string method, string path, string body)
        {
            var at = calls.ToList().FindIndex(c => c.EndsWith($"--method {method} -H Accept: application/vnd.github+json {path} --input -"));
            Assert.True(at >= 0, $"{method} {path} was not sent");
            Assert.Equal("< " + body, calls[at + 1]);
        }
        Sent("POST", $"{Repo}/pulls", """{"base":"main","body":"Searches the shop.","draft":false,"head":"search","title":"Add search"}""");
        Sent("POST", $"{Repo}/issues/12/comments", """{"body":"Thanks"}""");
        Sent("POST", $"{Repo}/pulls/12/reviews", """{"body":"","event":"APPROVE"}""");
        Sent("PUT", $"{Repo}/pulls/12/merge", """{"merge_method":"squash"}""");
        Sent("PATCH", $"{Repo}/pulls/13", """{"state":"closed"}""");
        Assert.Contains($"api --hostname github.com --method DELETE -H Accept: application/vnd.github+json {Repo}/git/refs/heads/fix-cart", calls);
    }

    static object MergeRequest(int iid, string title, string author, string source) => new
    {
        iid, title, author = new { username = author }, state = "opened", draft = false, source_branch = source, target_branch = "main",
        created_at = At(60), updated_at = At(5), web_url = $"http://gitlab.test/group/shop/-/merge_requests/{iid}", description = "Fixes the total.",
        sha = "headsha1234", diff_refs = new { base_sha = "basesha5678", head_sha = "headsha1234", start_sha = "basesha5678" },
        source_project_id = 4, target_project_id = 4, user_notes_count = 2, reviewers = new[] { new { username = "carol" } }, labels = new[] { "bug" },
        detailed_merge_status = "mergeable", changes_count = "1",
    };

    [Fact]
    public async Task GitLabs_merge_requests_are_read_and_reviewed_with_its_token()
    {
        await using var gitlab = new FakeGitLab();
        const string project = "/api/v4/projects/group%2Fshop";
        gitlab.Route("GET", "/api/v4/user", new { username = "tester" });
        gitlab.Route("GET", project, new { default_branch = "main" });
        gitlab.Route("GET", $"{project}/merge_requests?state=opened&per_page=50&order_by=updated_at&sort=desc", new[] { MergeRequest(7, "Fix cart", "ada", "fix-cart") });
        gitlab.Route("GET", $"{project}/merge_requests/7", MergeRequest(7, "Fix cart", "ada", "fix-cart"));
        gitlab.Route("GET", $"{project}/merge_requests/7/notes?sort=asc&order_by=created_at&per_page=100", new object[]
        {
            new { id = 1, body = "added 1 commit", system = true, author = new { username = "ada" }, created_at = At(50) },
            new { id = 2, body = "Why?", system = false, author = new { username = "bob" }, created_at = At(40), position = new { new_path = "src/Cart.cs", new_line = 2 } },
        });
        gitlab.Route("GET", $"{project}/merge_requests/7/approvals", new { approved_by = new[] { new { user = new { username = "carol" } } } });
        gitlab.Route("GET", $"{project}/merge_requests/7/pipelines", new[] { new { id = 88 } });
        gitlab.Route("GET", $"{project}/pipelines/88/jobs?per_page=100", new[]
        {
            new { name = "test", status = "failed", web_url = "http://gitlab.test/j/2" }, new { name = "build", status = "success", web_url = "http://gitlab.test/j/1" },
        });
        // A GitLab without /diffs: its /changes.
        gitlab.Route("GET", $"{project}/merge_requests/7/changes", new
        {
            changes = new[] { new { old_path = "src/Cart.cs", new_path = "src/Cart.cs", new_file = false, renamed_file = false, deleted_file = false, diff = Patch + "\n" } },
        });
        gitlab.Route("GET", $"{project}/repository/files/src%2FCart.cs/raw?ref=headsha1234", "a\nB\nc\n");
        gitlab.Route("POST", $"{project}/merge_requests", MergeRequest(8, "Add search", "tester", "search"));
        var remote = RepoRemote.Parse($"http://127.0.0.1:{gitlab.Port}/group/shop.git")!;
        var secrets = new PipelineSecrets(null);
        var connector = new PipelineConnector(secrets, () => null);
        Assert.Null(connector.GitLab(remote));
        secrets.Set(remote, "glpat-good", keep: false);
        using var mrs = new GitLabMergeRequests(connector.GitLab(remote)!);

        var mr = Assert.Single(await mrs.ListAsync(PullRequestFilter.Open, 50, default));
        Assert.Equal(("7", "Fix cart", "ada", PullRequestState.Open, true), (mr.Number, mr.Title, mr.Author, mr.State, mr.Mergeable));
        Assert.Equal(("tester", "main", "!"), (await mrs.UserAsync(default), await mrs.DefaultBranchAsync(default), mrs.Prefix));
        var details = await mrs.DetailsAsync(await mrs.GetAsync("7", default), default);
        var note = Assert.Single(details.Comments);
        Assert.Equal(("bob", "Why?", "src/Cart.cs", 2), (note.Author, note.Body, note.Path, note.Line));
        Assert.Equal([("carol", ReviewVerdict.Approve)], details.Reviews.Select(r => (r.Author, r.Verdict)));
        Assert.Equal([("build", PipelineStatus.Success), ("test", PipelineStatus.Failed)], details.Checks.Select(c => (c.Name, c.Status)));
        var file = Assert.Single(await mrs.FilesAsync(mr, default));
        Assert.Equal(("M", 1, 1), (file.Letter, file.Additions, file.Deletions));
        Assert.Equal("a\nb\nc\n", UnifiedPatch.Reverse((await mrs.FileAtAsync(mr.HeadSha, file.Path, default))!, file.Patch));

        Assert.Equal("8", (await mrs.CreateAsync("search", "main", "Add search", "", draft: true, default)).Number);
        await mrs.ReviewAsync(mr, ReviewVerdict.Approve, "LGTM", default);
        await mrs.MergeAsync(mr, MergeMethod.Squash, deleteBranch: true, default);
        await Assert.ThrowsAsync<PipelineException>(() => mrs.MergeAsync(mr, MergeMethod.Rebase, false, default));
        await mrs.CloseAsync(mr, default);
        var bodies = gitlab.Bodies.ToList();
        Assert.Contains(($"POST {project}/merge_requests", """{"source_branch":"search","target_branch":"main","title":"Draft: Add search","description":""}"""), bodies);
        Assert.Contains(($"POST {project}/merge_requests/7/notes", """{"body":"LGTM"}"""), bodies);
        Assert.Contains(($"PUT {project}/merge_requests/7/merge", """{"squash":true,"should_remove_source_branch":true}"""), bodies);
        Assert.Contains(($"PUT {project}/merge_requests/7", """{"state_event":"close"}"""), bodies);
        Assert.Contains(($"POST {project}/merge_requests/7/approve", "glpat-good"), gitlab.Requests);
    }

    static async Task<GitRepository> RepoAsync(string origin)
    {
        var repository = await TempRepo.CreateAsync();
        File.WriteAllText(Path.Combine(repository.Root, "README.md"), "shop\n");
        TempRepo.Git(repository.Root, "add", ".");
        TempRepo.Git(repository.Root, "commit", "-q", "-m", "Fix the cart total");
        TempRepo.Git(repository.Root, "remote", "add", "origin", origin);
        return repository;
    }

    [AvaloniaFact]
    public async Task The_window_lists_them_and_one_opens_as_its_page_to_review_merge_and_make_another()
    {
        var gh = GitHub();
        var repository = await RepoAsync("https://github.com/karvon-test/shop.git");
        var host = new TestPullRequestsHost();
        host.States.Add(new GitRepositoryState(repository, "fix-cart"));
        using var prs = new PullRequestsViewModel(host, new PipelineConnector(new PipelineSecrets(null), () => gh.Command));
        var window = new Window { Width = 420, Height = 560, Content = new PullRequestsView { DataContext = prs } };
        window.Show();

        await WaitUntil(() => prs.Rows.Count == 3, TimeSpan.FromSeconds(10));
        Assert.Equal("GitHub · github.com/karvon-test/shop", prs.Heading);
        Assert.Equal(["Fix the cart total", "WIP login", "Docs"], prs.Rows.Select(r => r.Title));
        Assert.StartsWith("#12 · ada · fix-cart → main · ", prs.Rows[0].Detail);
        // The branch's own in the status bar.
        Assert.Equal((true, "PR #12", "PullRequest"), (prs.StatusItem.IsVisible, prs.StatusItem.Text, prs.StatusItem.Icon));
        Snapshot(window, "pull-requests");

        prs.Filter = prs.Filters.Single(f => f.Filter == PullRequestFilter.Mine);
        await WaitUntil(() => prs.Rows.Select(r => r.Number).SequenceEqual(["11"]), TimeSpan.FromSeconds(10));
        prs.Filter = prs.Filters.Single(f => f.Filter == PullRequestFilter.ReviewRequested);
        await WaitUntil(() => prs.Rows.Select(r => r.Number).SequenceEqual(["10"]), TimeSpan.FromSeconds(10));
        prs.Filter = prs.Filters[0];
        await WaitUntil(() => prs.Rows.Count == 3, TimeSpan.FromSeconds(10));

        // Its page: the conversation, the reviewers' say, the checks, the files.
        prs.Open(prs.Rows[0]);
        var document = Assert.IsType<PullRequestDocument>(Assert.Single(host.Open));
        var page = document.Page;
        await WaitUntil(() => page.Files.Count == 1 && page.Comments.Count == 3, TimeSpan.FromSeconds(10));
        Assert.Equal(("PR #12", "Fix the cart total #12", "Open"), (document.Title, page.Heading, page.StateText));
        Assert.StartsWith("ada wants to merge fix-cart into main", page.Facts);
        Assert.Equal(["requested changes", "approved"], page.Reviews.Select(r => r.Text));
        Assert.Equal(["build", "test"], page.Checks.Select(c => c.Name));
        Assert.Equal("src/Cart.cs:3", page.Comments[1].Where);
        Assert.True(page.CanMerge);
        var pageWindow = new Window { Width = 1100, Height = 760, Content = new PullRequestPageView { DataContext = page } };
        pageWindow.Show();
        Snapshot(pageWindow, "pull-request");

        // A file opens as its diff against what the change started from.
        await page.OpenFileCommand.ExecuteAsync(page.Files[0]);
        Assert.Equal((Path.Combine(repository.Root, "src/Cart.cs"), "a\nb\nc\n", "a\nB\nc\n"), Assert.Single(host.Diffs));

        // A comment; a squash merge, asked first, its branch deleted.
        page.NewComment = "Thanks";
        await page.CommentCommand.ExecuteAsync(null);
        Assert.Equal("", page.NewComment);
        Assert.Contains("""< {"body":"Thanks"}""", gh.Calls);
        page.Merge = page.MergeChoices.Single(c => c.Method == MergeMethod.Squash);
        var merging = page.MergeCommand.ExecuteAsync(null);
        await WaitUntil(() => prs.Dialog is ConfirmDialog, TimeSpan.FromSeconds(5));
        Assert.Contains("and delete fix-cart on the remote", ((ConfirmDialog)prs.Dialog!).Message);
        prs.Dialog!.Confirm();
        await merging;
        Assert.Contains("""< {"merge_method":"squash"}""", gh.Calls);
        Assert.Contains(gh.Calls, c => c.EndsWith($"--method DELETE -H Accept: application/vnd.github+json {Repo}/git/refs/heads/fix-cart"));

        // Its branch is the one checked out: nothing to do.
        await prs.CheckoutAsync(page.PullRequest);
        Assert.Empty(host.GitRuns);
        Assert.Contains("fix-cart is checked out already.", host.Statuses);

        // Another branch: a new one, the branch pushed first.
        host.States[0] = new GitRepositoryState(repository, "search");
        host.Changed();
        await WaitUntil(() => prs.Repository?.Branch == "search", TimeSpan.FromSeconds(5));
        Assert.False(prs.StatusItem.IsVisible);
        var creating = prs.NewCommand.ExecuteAsync(null);
        await WaitUntil(() => prs.Dialog is NewPullRequestDialog, TimeSpan.FromSeconds(5));
        var dialog = (NewPullRequestDialog)prs.Dialog!;
        Assert.Equal(("search", "main"), (dialog.Source, dialog.Target));
        dialog.Title = "Add search";
        dialog.Confirm();
        await creating;
        Assert.Equal([repository], host.Pushes);
        Assert.Contains("PR #13 made: Add search", host.Statuses);
        Assert.Contains(host.Open, d => d is PullRequestDocument { Page.PullRequest.Number: "13" });

        // Another's branch checked out from here: fetched and switched to, as Git Changes runs it.
        await prs.CheckoutAsync(page.PullRequest);
        Assert.Equal(["Checkout #12 (fix-cart)"], host.GitRuns);
        pageWindow.Close();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Claude_reads_them_and_asks_before_it_changes_one()
    {
        var gh = GitHub();
        var repository = await RepoAsync("git@github.com:karvon-test/shop.git");
        var host = new TestPullRequestsHost();
        host.States.Add(new GitRepositoryState(repository, "fix-cart"));
        using var prs = new PullRequestsViewModel(host, new PipelineConnector(new PipelineSecrets(null), () => gh.Command));
        await WaitUntil(() => prs.Rows.Count == 3, TimeSpan.FromSeconds(10));
        var tools = KarvonTools.Create(PullRequestsClaudeTools.Create(() => prs));
        static JsonElement Input(object value) => JsonSerializer.SerializeToElement(value);
        async Task<ClaudeToolOutput> Call(string name, object input) => await tools.Tools.Single(t => t.Name == name).CallAsync(Input(input), default);

        Assert.StartsWith("github.com/karvon-test/shop's pull requests:\n#12 [open] Fix the cart total (ada, fix-cart -> main, updated 30 min ago, 2 comments)",
            (await Call("pr_list", new { })).Text);
        var view = (await Call("pr_view", new { number = "#12" })).Text;
        Assert.Contains("State: open; ada wants to merge fix-cart into main.", view);
        Assert.Contains("Review: bob requested changes", view);
        Assert.Contains("Checks: build passed; test running", view);
        Assert.Contains("- bob on src/Cart.cs:3: Off by one?", view);
        var diff = (await Call("pr_diff", new { number = 12 })).Text;
        Assert.Contains("M src/Cart.cs (+1 -1)", diff);
        Assert.Contains("-b\n+B", diff);
        Assert.True((await Call("pr_review", new { number = "12", verdict = "comment" })).IsError);

        var merge = tools.Plan(ClaudeToolServer.Prefix + "pr_merge", Input(new { number = "12", method = "squash", delete_branch = true }))!;
        Assert.Equal((ToolAccess.Write, "Allow Claude to merge 12 (squash) and delete its branch?"), (merge.Access, merge.Question));
        Assert.Equal(ToolAccess.Read, tools.Plan(ClaudeToolServer.Prefix + "pr_diff", Input(new { number = "12" }))!.Access);
        Assert.Equal("#12: approved.", (await Call("pr_review", new { number = "12", verdict = "approve" })).Text);
        Assert.Contains("""< {"body":"","event":"APPROVE"}""", gh.Calls);
    }

    [Theory]
    [InlineData("gh pr view 12", true)]
    [InlineData("gh pr create --fill", true)]
    [InlineData("glab mr list", true)]
    [InlineData("curl -s https://api.github.com/repos/o/r/pulls/12", true)]
    [InlineData("curl -H 'PRIVATE-TOKEN: x' https://gitlab.example.com/api/v4/projects/1/merge_requests", true)]
    [InlineData("gh run list", false)]
    [InlineData("gh repo clone o/r", false)]
    public void A_pull_request_CLI_goes_to_the_pull_request_tools(string command, bool redirected) =>
        Assert.Equal(redirected, new PullRequestsModule().ClaudeShellRules().Any(r => r.Matches(command)));
}
