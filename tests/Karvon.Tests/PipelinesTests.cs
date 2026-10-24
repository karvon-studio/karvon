using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Karvon.Claude;
using Karvon.Git;
using Karvon.Modules;
using Karvon.Pipelines;
using Karvon.Pipelines.Providers;
using Karvon.Pipelines.ViewModels;
using Karvon.Pipelines.Views;
using Karvon.ViewModels;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>What the Pipelines window asked of Karvon, and the repositories it is told.</summary>
sealed class TestPipelinesHost : IPipelinesHost
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

    public List<string> Urls { get; } = [];

    public List<string> Statuses { get; } = [];

    public List<string> Prompts { get; } = [];

    public void OpenUrl(string url) => Urls.Add(url);

    public void Status(string text) => Statuses.Add(text);

    public void AskClaude(string prompt) => Prompts.Add(prompt);
}

/// <summary>Pipelines: GitHub Actions through a fake gh, GitLab CI through a fake server; the window, a run's page, Claude's tools.</summary>
public class PipelinesTests
{
    const string Repo = "repos/karvon-test/shop";

    static string At(int minutesAgo) => DateTimeOffset.UtcNow.AddMinutes(-minutesAgo).ToString("yyyy-MM-ddTHH:mm:ssZ");

    static object Run(long id, int number, string title, string status, string? conclusion, int startedAgo = 5, int? doneAgo = null, string branch = "main") => new
    {
        id, name = "CI", display_title = title, run_number = number, @event = "push", status, conclusion, head_branch = branch,
        head_sha = "abc1234def567890", html_url = $"https://github.com/karvon-test/shop/actions/runs/{id}", created_at = At(startedAgo),
        updated_at = At(doneAgo ?? 0), run_started_at = At(startedAgo), run_attempt = 1, triggering_actor = new { login = "tester" },
    };

    static object Jobs(string testStatus, string? testConclusion) => new
    {
        total_count = 2,
        jobs = new object[]
        {
            new { id = 71, name = "build", status = "completed", conclusion = "success", started_at = At(5), completed_at = At(4), html_url = "https://github.com/karvon-test/shop/actions/runs/9000/job/71",
                steps = new object[] { new { number = 1, name = "Set up job", status = "completed", conclusion = "success", started_at = At(5), completed_at = At(5) } } },
            new { id = 72, name = "test", status = testStatus, conclusion = testConclusion, started_at = At(4), completed_at = testConclusion is null ? null : At(3),
                html_url = "https://github.com/karvon-test/shop/actions/runs/9000/job/72",
                steps = new object[]
                {
                    new { number = 1, name = "Set up job", status = "completed", conclusion = "success", started_at = At(4), completed_at = At(4) },
                    new { number = 4, name = "Run tests", status = testStatus, conclusion = testConclusion, started_at = At(4), completed_at = testConclusion is null ? null : At(3) },
                },
            },
        },
    };

    const string GitHubLog = "2026-10-24T05:03:20.1234567Z ##[group]Run dotnet test\n2026-10-24T05:03:20.2234567Z dotnet test\n2026-10-24T05:03:20.3Z ##[endgroup]\n"
        + "2026-10-24T05:03:21.0Z   Failed Shop.Tests.CartTests.Total [12 ms]\n2026-10-24T05:03:21.1Z ##[error]Process completed with exit code 1.\n";

    [Theory]
    [InlineData("https://github.com/karvon-test/shop.git", "github.com", "karvon-test/shop", CiProvider.GitHub, "https://github.com/karvon-test/shop")]
    [InlineData("git@github.com:karvon-test/shop.git", "github.com", "karvon-test/shop", CiProvider.GitHub, "https://github.com/karvon-test/shop")]
    [InlineData("https://user:secret@gitlab.example.com/group/sub/shop.git", "gitlab.example.com", "group/sub/shop", CiProvider.GitLab, "https://gitlab.example.com/group/sub/shop")]
    [InlineData("ssh://git@gitlab.example.com:2222/group/shop.git", "gitlab.example.com", "group/shop", CiProvider.GitLab, "https://gitlab.example.com/group/shop")]
    [InlineData("http://127.0.0.1:8929/group/shop", "127.0.0.1", "group/shop", CiProvider.GitLab, "http://127.0.0.1:8929/group/shop")]
    public void A_remote_says_its_service_and_never_its_credential(string url, string host, string path, CiProvider provider, string web)
    {
        var remote = RepoRemote.Parse(url)!;
        Assert.Equal((host, path, provider, web), (remote.Host, remote.Path, remote.Provider, remote.WebUrl));
        Assert.DoesNotContain("secret", remote.ToString());
    }

    [Fact]
    public void Nothing_that_is_not_a_repository_is_a_remote()
    {
        Assert.Null(RepoRemote.Parse("/home/me/repos/shop.git"));
        Assert.Null(RepoRemote.Parse("https://github.com/shop"));
    }

    [Fact]
    public void A_log_reads_as_the_services_page_shows_it()
    {
        var github = PipelineLog.Clean(GitHubLog, CiProvider.GitHub);
        Assert.Equal("\u001b[1m▸ Run dotnet test\u001b[0m\ndotnet test\n  Failed Shop.Tests.CartTests.Total [12 ms]\n\u001b[31mError: Process completed with exit code 1.\u001b[0m\n", github);
        Assert.Equal("(the last 2 of 4 lines)\n  Failed Shop.Tests.CartTests.Total [12 ms]\nError: Process completed with exit code 1.", PipelineLog.Plain(github, 2));
        var gitlab = PipelineLog.Clean("section_start:1729746000:step_script\r\u001b[0K\u001b[32;1m$ dotnet test\u001b[0;m\r\nok\r\nsection_end:1729746010:step_script\r\u001b[0K", CiProvider.GitLab);
        Assert.Equal("\u001b[32;1m$ dotnet test\u001b[0;m\nok\n", gitlab);
    }

    [Fact]
    public async Task GitHub_Actions_is_read_through_gh()
    {
        var gh = new FakeGh();
        gh.Route("GET", $"{Repo}/actions/runs?per_page=30&branch=main", new { total_count = 1, workflow_runs = new[] { Run(9000, 41, "Add cart", "completed", "failure", 6, 3) } });
        gh.Route("GET", $"{Repo}/actions/runs/9000/jobs?per_page=100", Jobs("completed", "failure"));
        gh.Route("GET", $"{Repo}/actions/jobs/72/logs", GitHubLog);
        var remote = RepoRemote.Parse("https://github.com/karvon-test/shop.git")!;
        var connector = new PipelineConnector(new PipelineSecrets(null), () => gh.Command);
        using var actions = (await connector.ConnectAsync(remote, default))!;

        var run = Assert.Single(await actions.PipelinesAsync("main", 30, default));
        Assert.Equal(("9000", "41", "CI", "Add cart", "main", PipelineStatus.Failed, "tester"), (run.Id, run.Number, run.Name, run.Title, run.Branch, run.Status, run.Actor));
        Assert.NotNull(run.Finished);
        var jobs = await actions.JobsAsync(run, default);
        Assert.Equal([("build", PipelineStatus.Success), ("test", PipelineStatus.Failed)], jobs.Select(j => (j.Name, j.Status)));
        Assert.Equal(["Set up job", "Run tests"], jobs[1].Steps.Select(s => s.Name));
        Assert.Contains("##[error]Process completed", await actions.LogAsync(jobs[1], default));
        await actions.RetryAsync(run, failedOnly: true, default);
        await actions.CancelAsync(run, default);
        Assert.Contains($"api --hostname github.com --method POST -H Accept: application/vnd.github+json {Repo}/actions/runs/9000/rerun-failed-jobs", gh.Calls);
        Assert.Contains($"api --hostname github.com --method POST -H Accept: application/vnd.github+json {Repo}/actions/runs/9000/cancel", gh.Calls);

        // A job that never started has no log: its annotations say why.
        gh.Route("GET", $"{Repo}/check-runs/73/annotations", new[] { new { annotation_level = "failure", title = "", message = "The job was not started because your spending limit needs to be increased." } });
        var blocked = new PipelineJob("73", "deploy", "", PipelineStatus.Failed, null, null, "", []);
        Assert.Equal("GitHub has no log of this job; what it says of it:\n\u001b[31mError: The job was not started because your spending limit needs to be increased.\u001b[0m\n",
            PipelineLog.Clean(await actions.LogAsync(blocked, default), CiProvider.GitHub));

        // A job still running has no log yet; GitHub's own error is said as it is.
        var running = jobs[1] with { Status = PipelineStatus.Running };
        Assert.StartsWith("GitHub gives a job's log once the job is done", (await Assert.ThrowsAsync<PipelineException>(() => actions.LogAsync(running, default))).Message);
        Assert.Equal("GitHub: Not Found (HTTP 404)", (await Assert.ThrowsAsync<PipelineException>(() => actions.PipelineAsync("1", default))).Message);

        // Not signed in, and no token kept: a token is needed.
        gh.SignedIn = false;
        Assert.Null(await connector.ConnectAsync(remote, default));
    }

    [Fact]
    public async Task GitLab_CI_is_read_with_its_token()
    {
        await using var gitlab = new FakeGitLab();
        const string project = "/api/v4/projects/group%2Fsub%2Fshop";
        gitlab.Route("GET", $"{project}/pipelines?per_page=30&order_by=id&sort=desc&ref=main", new object[]
        {
            new { id = 501, iid = 12, sha = "abc1234def567890", @ref = "main", status = "running", source = "push", created_at = At(2), updated_at = At(0), web_url = "http://gitlab.test/group/sub/shop/-/pipelines/501" },
        });
        gitlab.Route("GET", $"{project}/pipelines/501/jobs?per_page=100", new object[]
        {
            new { id = 3, name = "deploy", stage = "deploy", status = "manual", allow_failure = false, web_url = "http://gitlab.test/j/3" },
            new { id = 2, name = "test", stage = "test", status = "running", started_at = At(1), allow_failure = false, web_url = "http://gitlab.test/j/2" },
            new { id = 1, name = "build", stage = "build", status = "success", started_at = At(2), finished_at = At(1), allow_failure = false, web_url = "http://gitlab.test/j/1" },
        });
        gitlab.Route("GET", $"{project}/jobs/2/trace", "section_start:1:step_script\r\u001b[0K$ dotnet test\nPassed 3 of 5\n");
        var remote = RepoRemote.Parse($"http://127.0.0.1:{gitlab.Port}/group/sub/shop.git")!;
        var secrets = new PipelineSecrets(null);
        var connector = new PipelineConnector(secrets, () => null);
        Assert.Null(await connector.ConnectAsync(remote, default));
        secrets.Set(remote, "glpat-good", keep: false);
        using var ci = (await connector.ConnectAsync(remote, default))!;

        var run = Assert.Single(await ci.PipelinesAsync("main", 30, default));
        Assert.Equal(("501", "Pipeline", "", PipelineStatus.Running, "push"), (run.Id, run.Name, run.Title, run.Status, run.Event));
        var jobs = await ci.JobsAsync(run, default);
        Assert.Equal([("build", "build"), ("test", "test"), ("deploy", "deploy")], jobs.Select(j => (j.Stage, j.Name)));
        Assert.True(jobs[2].CanPlay);
        Assert.True(ci.LogsWhileRunning);
        Assert.Equal("$ dotnet test\nPassed 3 of 5\n", PipelineLog.Clean(await ci.LogAsync(jobs[1], default), CiProvider.GitLab));
        await ci.RetryAsync(run, failedOnly: true, default);
        await ci.RetryJobAsync(jobs[2], default);
        await ci.CancelAsync(run, default);
        Assert.Contains(($"POST {project}/pipelines/501/retry", "glpat-good"), gitlab.Requests);
        Assert.Contains(($"POST {project}/jobs/3/play", "glpat-good"), gitlab.Requests);
        Assert.Contains(($"POST {project}/pipelines/501/cancel", "glpat-good"), gitlab.Requests);

        // A token it does not take, a project it does not have: said as such.
        gitlab.Token = "glpat-other";
        Assert.Equal("127.0.0.1 did not accept the token (401): give a new one.", (await Assert.ThrowsAsync<PipelineException>(() => ci.PipelinesAsync("main", 30, default))).Message);
    }

    static async Task<(GitRepository Repository, string Sha)> RepoAsync(string origin)
    {
        var repository = await TempRepo.CreateAsync();
        File.WriteAllText(Path.Combine(repository.Root, "README.md"), "shop\n");
        TempRepo.Git(repository.Root, "add", ".");
        TempRepo.Git(repository.Root, "commit", "-q", "-m", "Fix the cart total");
        TempRepo.Git(repository.Root, "remote", "add", "origin", origin);
        var sha = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", ["rev-parse", "HEAD"]) { WorkingDirectory = repository.Root, RedirectStandardOutput = true })!;
        var head = (await sha.StandardOutput.ReadToEndAsync()).Trim();
        await sha.WaitForExitAsync();
        return (repository, head);
    }

    [AvaloniaFact]
    public async Task The_window_follows_the_branchs_runs_and_a_run_opens_as_its_page()
    {
        var intervals = (PipelinesViewModel.ActiveInterval, PipelinesViewModel.IdleInterval);
        PipelinesViewModel.ActiveInterval = TimeSpan.FromMilliseconds(300);
        try
        {
            var gh = new FakeGh();
            gh.Route("GET", $"{Repo}/actions/runs?per_page=30&branch=main", new
            {
                total_count = 2,
                workflow_runs = new[] { Run(9001, 42, "Fix login", "in_progress", null, 1), Run(9000, 41, "Add cart", "completed", "failure", 6, 3) },
            });
            gh.Route("GET", $"{Repo}/actions/runs/9000", Run(9000, 41, "Add cart", "completed", "failure", 6, 3));
            gh.Route("GET", $"{Repo}/actions/runs/9000/jobs?per_page=100", Jobs("completed", "failure"));
            gh.Route("GET", $"{Repo}/actions/jobs/72/logs", GitHubLog);
            var (repository, _) = await RepoAsync("https://github.com/karvon-test/shop.git");
            var host = new TestPipelinesHost();
            host.States.Add(new GitRepositoryState(repository, "main"));
            using var pipelines = new PipelinesViewModel(host, keyring: null, () => gh.Command);
            var window = new Window { Width = 420, Height = 560, Content = new PipelinesView { DataContext = pipelines } };
            window.Show();

            await WaitUntil(() => pipelines.Runs.Count == 2, TimeSpan.FromSeconds(10));
            Assert.Equal("GitHub Actions · github.com/karvon-test/shop", pipelines.Heading);
            Assert.Equal([("Fix login", PipelineStatus.Running), ("Add cart", PipelineStatus.Failed)], pipelines.Runs.Select(r => (r.Heading, r.Status)));
            Assert.StartsWith("CI #42 · main · push · tester", pipelines.Runs[0].Detail);
            // The branch's latest run in the status bar.
            Assert.Equal((true, "CI running", "Running"), (pipelines.StatusItem.IsVisible, pipelines.StatusItem.Text, pipelines.StatusItem.Icon));
            Snapshot(window, "pipelines");

            // It is done: read again soon (a run goes), the status bar and a line say so.
            gh.Route("GET", $"{Repo}/actions/runs?per_page=30&branch=main", new
            {
                total_count = 2,
                workflow_runs = new[] { Run(9001, 42, "Fix login", "completed", "success", 1, 0), Run(9000, 41, "Add cart", "completed", "failure", 6, 3) },
            });
            await WaitUntil(() => pipelines.Runs[0].IsPassed, TimeSpan.FromSeconds(10));
            Assert.Equal("CI passed", pipelines.StatusItem.Text);
            Assert.Contains("GitHub Actions: CI #42 passed on main.", host.Statuses);

            // Another branch: its runs (none yet).
            gh.Route("GET", $"{Repo}/actions/runs?per_page=30&branch=feature", new { total_count = 0, workflow_runs = Array.Empty<object>() });
            host.States[0] = new GitRepositoryState(repository, "feature");
            host.Changed();
            await WaitUntil(() => pipelines.Message == "No runs on feature yet.", TimeSpan.FromSeconds(10));
            Assert.Empty(pipelines.Runs);
            Assert.False(pipelines.StatusItem.IsVisible);
            host.States[0] = new GitRepositoryState(repository, "main");
            host.Changed();
            await WaitUntil(() => pipelines.Runs.Count == 2, TimeSpan.FromSeconds(10));

            // The failed run's page: its jobs, the failed one chosen, its steps and log.
            pipelines.Open(pipelines.Runs[1]);
            var document = Assert.IsType<PipelineDocument>(Assert.Single(host.Open));
            var page = document.Page;
            await WaitUntil(() => page.Log.Document.TextLength > 0, TimeSpan.FromSeconds(10));
            Assert.Equal(("CI #41", "Add cart"), (document.Title, page.Heading));
            Assert.Equal(["build", "test"], page.Jobs.Select(j => j.Name));
            Assert.Equal("test", page.SelectedJob!.Name);
            Assert.Equal(["Set up job", "Run tests"], page.SelectedJob.Steps.Select(s => s.Name));
            Assert.Contains("Failed Shop.Tests.CartTests.Total", page.Log.Document.Text);
            var pageWindow = new Window { Width = 1200, Height = 700, Content = new PipelinePageView { DataContext = page } };
            pageWindow.Show();
            Snapshot(pageWindow, "pipeline-run");

            // Re-run failed jobs; Ask Claude names the failed job.
            await page.RetryFailedCommand.ExecuteAsync(null);
            Assert.Contains(gh.Calls, c => c.EndsWith($"{Repo}/actions/runs/9000/rerun-failed-jobs"));
            page.AskClaudeCommand.Execute(null);
            Assert.StartsWith("The CI run CI #41 of karvon-test/shop (pipeline id 9000, branch main) failed in test", host.Prompts.Single());
            pageWindow.Close();
            window.Close();
        }
        finally
        {
            (PipelinesViewModel.ActiveInterval, PipelinesViewModel.IdleInterval) = intervals;
        }
    }

    [AvaloniaFact]
    public async Task A_GitLab_asks_for_its_token_once_and_follows_a_running_job()
    {
        await using var gitlab = new FakeGitLab();
        const string project = "/api/v4/projects/group%2Fshop";
        var (repository, sha) = await RepoAsync($"http://127.0.0.1:{gitlab.Port}/group/shop.git");
        var running = new { id = 501, sha, @ref = "main", status = "running", source = "push", created_at = At(2), updated_at = At(0), started_at = At(2), web_url = "http://gitlab.test/p/501" };
        gitlab.Route("GET", $"{project}/pipelines?per_page=30&order_by=id&sort=desc&ref=main", new[] { running });
        gitlab.Route("GET", $"{project}/pipelines/501", running);
        gitlab.Route("GET", $"{project}/pipelines/501/jobs?per_page=100", new object[]
        {
            new { id = 2, name = "test", stage = "test", status = "running", started_at = At(1), web_url = "http://gitlab.test/j/2" },
            new { id = 1, name = "build", stage = "build", status = "success", started_at = At(2), finished_at = At(1), web_url = "http://gitlab.test/j/1" },
        });
        gitlab.Route("GET", $"{project}/jobs/2/trace", "$ dotnet test\n");
        var host = new TestPipelinesHost();
        host.States.Add(new GitRepositoryState(repository, "main"));
        var interval = PipelinePageViewModel.Interval;
        PipelinePageViewModel.Interval = TimeSpan.FromMilliseconds(300);
        try
        {
            using var pipelines = new PipelinesViewModel(host, keyring: null, () => null);
            var window = new Window { Width = 420, Height = 560, Content = new Grid { Children = { new PipelinesView { DataContext = pipelines }, new PipelinesOverlay { DataContext = pipelines } } } };
            window.Show();
            await WaitUntil(() => pipelines.NeedsToken, TimeSpan.FromSeconds(10));
            Assert.Empty(gitlab.Requests);

            // Sign In: the token page has the scopes; the token is kept (the session's here: no keyring).
            var signing = pipelines.SignInCommand.ExecuteAsync(null);
            var dialog = Assert.IsType<PipelineTokenDialog>(pipelines.Dialog);
            Assert.Equal($"http://127.0.0.1:{gitlab.Port}/-/user_settings/personal_access_tokens?name=Karvon%20Studio&scopes=api", dialog.TokenPage);
            Snapshot(window, "pipelines-token");
            dialog.Token = "glpat-good";
            dialog.Confirm();
            await signing;
            await WaitUntil(() => pipelines.Runs.Count == 1, TimeSpan.FromSeconds(10));
            // Its title is the commit's, from the repository's own log.
            Assert.Equal(("Fix the cart total", PipelineStatus.Running), (pipelines.Runs[0].Heading, pipelines.Runs[0].Status));

            // Its page: by stage, the running job's trace as it grows.
            pipelines.Open(pipelines.Runs[0]);
            var page = ((PipelineDocument)host.Open.Single()).Page;
            await WaitUntil(() => page.Log.Document.Text == "$ dotnet test\n", TimeSpan.FromSeconds(10));
            Assert.Equal([("build", true), ("test", true)], page.Jobs.Select(j => (j.Stage, j.ShowsStage)));
            Assert.Equal("test", page.SelectedJob!.Name);
            gitlab.Route("GET", $"{project}/jobs/2/trace", "$ dotnet test\nPassed 5 of 5\n");
            await WaitUntil(() => page.Log.Document.Text.Contains("Passed 5 of 5"), TimeSpan.FromSeconds(10));
            Assert.Equal("$ dotnet test\nPassed 5 of 5\n", page.Log.Document.Text);
            window.Close();
        }
        finally
        {
            PipelinePageViewModel.Interval = interval;
        }
    }

    [AvaloniaFact]
    public async Task Claude_reads_the_runs_and_asks_before_it_runs_one_again()
    {
        var gh = new FakeGh();
        gh.Route("GET", $"{Repo}/actions/runs?per_page=30&branch=main", new { total_count = 1, workflow_runs = new[] { Run(9000, 41, "Add cart", "completed", "failure", 6, 3) } });
        gh.Route("GET", $"{Repo}/actions/runs?per_page=15&branch=main", new { total_count = 1, workflow_runs = new[] { Run(9000, 41, "Add cart", "completed", "failure", 6, 3) } });
        gh.Route("GET", $"{Repo}/actions/runs/9000", Run(9000, 41, "Add cart", "completed", "failure", 6, 3));
        gh.Route("GET", $"{Repo}/actions/runs/9000/jobs?per_page=100", Jobs("completed", "failure"));
        gh.Route("GET", $"{Repo}/actions/jobs/72/logs", GitHubLog);
        var (repository, _) = await RepoAsync("git@github.com:karvon-test/shop.git");
        var host = new TestPipelinesHost();
        host.States.Add(new GitRepositoryState(repository, "main"));
        using var pipelines = new PipelinesViewModel(host, keyring: null, () => gh.Command);
        await WaitUntil(() => pipelines.Runs.Count == 1, TimeSpan.FromSeconds(10));
        var tools = KarvonTools.Create(PipelinesClaudeTools.Create(() => pipelines));
        static JsonElement Input(object value) => JsonSerializer.SerializeToElement(value);
        async Task<ClaudeToolOutput> Call(string name, object input) => await tools.Tools.Single(t => t.Name == name).CallAsync(Input(input), default);

        var list = await Call("pipeline_list", new { });
        Assert.StartsWith("GitHub Actions (github.com/karvon-test/shop), main:\nid 9000: CI #41 [failed] Add cart (main, abc1234d, push, by tester", list.Text);
        var jobs = await Call("pipeline_jobs", new { pipeline = "9000" });
        Assert.Contains("- job 72: test [failed]", jobs.Text);
        Assert.Contains("    step 4 Run tests [failed]", jobs.Text);
        Assert.DoesNotContain("Set up job", jobs.Text);
        var log = await Call("pipeline_log", new { pipeline = "41" });
        Assert.Equal("job 72 test [failed] of CI #41:\n▸ Run dotnet test\ndotnet test\n  Failed Shop.Tests.CartTests.Total [12 ms]\nError: Process completed with exit code 1.", log.Text);

        var retry = tools.Plan(ClaudeToolServer.Prefix + "pipeline_retry", Input(new { pipeline = "9000" }))!;
        Assert.Equal((ToolAccess.Write, "Allow Claude to run the failed jobs of run 9000 again?"), (retry.Access, retry.Question));
        Assert.Contains("CI time", retry.Warning);
        Assert.Equal(ToolAccess.Read, tools.Plan(ClaudeToolServer.Prefix + "pipeline_log", Input(new { pipeline = "9000" }))!.Access);
        Assert.Equal("CI #41 runs again (its failed jobs): see pipeline_jobs for how it goes.", (await Call("pipeline_retry", new { pipeline = "9000" })).Text);
        Assert.True((await Call("pipeline_cancel", new { pipeline = "9000" })).IsError);

        // Without a sign-in Claude is told where the user gives one.
        gh.SignedIn = false;
        using var signedOut = new PipelinesViewModel(host, keyring: null, () => gh.Command);
        await WaitUntil(() => signedOut.NeedsToken, TimeSpan.FromSeconds(10));
        var none = await KarvonTools.Create(PipelinesClaudeTools.Create(() => signedOut)).Tools.Single(t => t.Name == "pipeline_list").CallAsync(Input(new { }), default);
        Assert.Equal("Karvon has no token for GitHub Actions on github.com: the user gives one in View > Pipelines (Sign In).", none.Text);
    }

    [Theory]
    [InlineData("gh run list", true)]
    [InlineData("gh run view 9000 --log-failed", true)]
    [InlineData("glab ci status", true)]
    [InlineData("curl -s -H 'PRIVATE-TOKEN: x' https://gitlab.example.com/api/v4/projects/1/pipelines", true)]
    [InlineData("gh pr create", false)]
    [InlineData("gh repo view", false)]
    public void A_CI_CLI_goes_to_the_pipeline_tools(string command, bool redirected) =>
        Assert.Equal(redirected, new PipelinesModule().ClaudeShellRules().Any(r => r.Matches(command)));

    [AvaloniaFact]
    public async Task The_status_bar_shows_a_modules_item()
    {
        var vm = NewViewModel();
        var window = new Karvon.Views.MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        window.Show();
        var item = vm.Module<PipelinesModule>()!.StatusItem!;
        Assert.Contains(item, vm.StatusItems);
        Assert.False(item.IsVisible);
        var opened = 0;
        item.Text = "CI failed";
        item.Icon = "Error";
        item.Click = () => opened++;
        item.IsVisible = true;
        await WaitUntil(() => window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "CI failed" && t.IsEffectivelyVisible), TimeSpan.FromSeconds(5));
        var button = window.GetVisualDescendants().OfType<Button>().Single(b => b.DataContext == item);
        button.Command!.Execute(null);
        Assert.Equal(1, opened);
        window.Close();
    }
}
