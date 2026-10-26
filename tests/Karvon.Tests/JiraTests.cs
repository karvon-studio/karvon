using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Karvon.Claude;
using Karvon.Jira;
using Karvon.Jira.ViewModels;
using Karvon.Jira.Views;
using Karvon.ViewModels;
using Karvon.Views;
using static Karvon.Tests.ClaudeIdeToolsTests;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>
/// The Jira module: a site's REST API (Cloud's and Server's), its explorer and pages, and Claude's Jira tools, which go
/// through Karvon's sign-in and never carry its secret.
/// </summary>
public class JiraTests
{
    const string Secret = "the-secret";

    static readonly JiraSite Server = new("srv", "Work", "https://jira.example.invalid", JiraAuth.PersonalAccessToken, "", "customfield_10015", "customfield_10016");

    static readonly JiraSite Cloud = new("cld", "Cloud", "https://shop.atlassian.invalid", JiraAuth.CloudToken, "me@example.com");

    static JiraClient ClientOf(JiraSite site, FakeJira fake, string secret = Secret) => new(site, secret, fake);

    sealed class TestKeyring : Bengine.Introspection.Servers.ICredentialStore
    {
        public Dictionary<string, string> Secrets { get; } = [];

        public string Name => "test keyring";

        public string? Get(string key) => Secrets.GetValueOrDefault(key);

        public bool Set(string key, string label, string secret)
        {
            Secrets[key] = secret;
            return true;
        }

        public void Delete(string key) => Secrets.Remove(key);
    }

    [Fact]
    public async Task A_server_site_is_read_and_changed_as_its_api_2_has_it()
    {
        var fake = new FakeJira(cloud: false).Seed();
        using var jira = ClientOf(Server, fake);
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(("dev", "Dev Eloper"), ((await jira.MyselfAsync(ct)).Id, (await jira.MyselfAsync(ct)).DisplayName));
        Assert.Equal(("Shop Jira", "9.12.2", false), await jira.ServerInfoAsync(ct));
        Assert.Equal(["SHOP-1", "SHOP-2"], (await jira.SearchAsync("assignee = currentUser() ORDER BY updated DESC", 50, ct)).Select(i => i.Key));

        // An issue: its rendered description and comments as Markdown, its transitions now (named in Cyrillic).
        var issue = await jira.IssueAsync("SHOP-1", ct);
        Assert.Equal(("Fix the cart's total", "В работе", "indeterminate", "Bug"), (issue.Summary.Summary, issue.Summary.Status, issue.Summary.StatusCategory, issue.Summary.Type));
        Assert.Equal("The total is **off** by one.", issue.Description);
        Assert.Equal(("Dev Eloper", "Seen on staging."), (issue.Comments.Single().Author, issue.Comments.Single().Body));
        Assert.Equal(["На проверку (to QA)", "Вернуть (to К выполнению)"], issue.Transitions.Select(t => $"{t.Name} (to {t.ToStatus})"));

        // A transition by its name, or the status it goes to; a comment as plain text (API 2).
        Assert.Equal("QA", (await jira.TransitionAsync("SHOP-1", "на проверку", ct)).ToStatus);
        Assert.Equal("В работе", (await jira.TransitionAsync("SHOP-1", "В работе", ct)).ToStatus);
        var missing = await Assert.ThrowsAsync<JiraException>(() => jira.TransitionAsync("SHOP-1", "Done", ct));
        Assert.Equal("SHOP-1 has no transition \"Done\" now; it has: На проверку (to QA), Вернуть (to К выполнению).", missing.Message);
        await jira.CommentAsync("SHOP-1", "Fixed in the cart service.", ct);
        Assert.Equal("""{"body":"Fixed in the cart service."}""", fake.Requests.Last(r => r.Path.EndsWith("/comment", StringComparison.Ordinal)).Body);

        // An issue made: its type by the project's id for its name (the site refuses names), as the signed-in user's, its dates.
        var key = await jira.CreateAsync("SHOP", "задача", "Add a coupon field", "- Goal: coupons\n- Files: Cart.cs", new DateOnly(2026, 10, 24), new DateOnly(2026, 10, 27), ct);
        Assert.Equal("SHOP-3", key);
        var made = JsonNode.Parse(fake.Requests.Last(r => r.Method == "POST" && r.Path == "rest/api/2/issue").Body!)!["fields"]!;
        Assert.Equal(("10001", "dev", "2026-10-24", "2026-10-27", "- Goal: coupons\n- Files: Cart.cs"),
            (made["issuetype"]!["id"]!.GetValue<string>(), made["assignee"]!["name"]!.GetValue<string>(), made["customfield_10015"]!.GetValue<string>(),
                made["customfield_10016"]!.GetValue<string>(), made["description"]!.GetValue<string>()));
        Assert.Equal(("2026-10-24", "2026-10-27", "Задача"), ((await jira.IssueAsync(key, ct)).StartDate, (await jira.IssueAsync(key, ct)).EndDate, (await jira.IssueAsync(key, ct)).Summary.Type));
        Assert.DoesNotContain(fake.Requests, r => r.Method == "PUT");
        // A site that leaves a new issue's assignee empty: it is set again.
        fake.DropsAssignee = true;
        var other = await jira.CreateAsync("SHOP", "Bug", "Cart shows NaN", null, null, null, ct);
        Assert.Contains(fake.Requests, r => r.Method == "PUT" && r.Path == $"rest/api/2/issue/{other}/assignee" && r.Body == """{"name":"dev"}""");
        Assert.Equal("Dev Eloper", (await jira.IssueAsync(other, ct)).Summary.Assignee);
        var type = await Assert.ThrowsAsync<JiraException>(() => jira.CreateAsync("SHOP", "Epic", "x", null, null, null, ct));
        Assert.Equal("SHOP has no issue type \"Epic\"; it has: Задача, Bug.", type.Message);

        // Its filters and boards: a board's columns and a column's issues in its sprint (the first of them, and how many).
        Assert.Equal(["Shop bugs: project = SHOP AND issuetype = Bug"], (await jira.FavouriteFiltersAsync(ct)).Select(f => $"{f.Name}: {f.Jql}"));
        var board = Assert.Single(await jira.BoardsAsync(null, ct));
        var (columns, subQuery) = await jira.ColumnsAsync(board.Id, ct);
        Assert.Equal(["To Do", "In Progress", "Done"], columns.Select(c => c.Name));
        Assert.Null(subQuery);
        Assert.Equal("Sprint 3", Assert.Single(await jira.SprintsAsync(board.Id, ct)).Name);
        var todo = await jira.ColumnIssuesAsync(board.Id, 3, columns[0], subQuery, latestFirst: false, 2, ct);
        Assert.Equal(["SHOP-2", "SHOP-3"], todo.Issues.Select(i => i.Key));
        Assert.Equal(3, todo.Total);
        Assert.Equal(["SHOP-1"], (await jira.ColumnIssuesAsync(board.Id, 3, columns[1], subQuery, latestFirst: false, 2, ct)).Issues.Select(i => i.Key));

        // Every request signed in with the bearer token; bodies as UTF-8 JSON.
        Assert.All(fake.Requests, r => Assert.Equal("Bearer " + Secret, r.Authorization));
    }

    [Fact]
    public async Task A_cloud_site_has_its_api_3_rich_text_and_account_ids()
    {
        var fake = new FakeJira(cloud: true).Seed();
        using var jira = ClientOf(Cloud, fake);
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(("acc-1", true), ((await jira.MyselfAsync(ct)).Id, (await jira.ServerInfoAsync(ct)).Cloud));
        // Cloud's enhanced search.
        Assert.Equal(2, (await jira.SearchAsync("project = SHOP", 10, ct)).Count);
        Assert.Contains(fake.Requests, r => r.Method == "POST" && r.Path == "rest/api/3/search/jql");
        // A comment and a description in Atlassian Document Format, a hard break between lines.
        await jira.CommentAsync("SHOP-2", "Two lines\nof it.", ct);
        var comment = JsonNode.Parse(fake.Requests.Last(r => r.Path.EndsWith("/comment", StringComparison.Ordinal)).Body!)!["body"]!;
        Assert.Equal("""{"type":"doc","version":1,"content":[{"type":"paragraph","content":[{"type":"text","text":"Two lines"},{"type":"hardBreak"},{"type":"text","text":"of it."}]}]}""", comment.ToJsonString());
        var key = await jira.CreateAsync("SHOP", "Bug", "Totals", "Off by one.", null, new DateOnly(2026, 11, 1), ct);
        var fields = JsonNode.Parse(fake.Requests.Last(r => r.Method == "POST" && r.Path == "rest/api/3/issue").Body!)!["fields"]!;
        Assert.Equal(("acc-1", "doc", "2026-11-01"), (fields["assignee"]!["accountId"]!.GetValue<string>(), fields["description"]!["type"]!.GetValue<string>(), fields["duedate"]!.GetValue<string>()));
        Assert.Equal("Off by one.", (await jira.IssueAsync(key, ct)).Description);
        Assert.All(fake.Requests, r => Assert.Equal("Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("me@example.com:" + Secret)), r.Authorization));
    }

    [Fact]
    public async Task What_goes_wrong_is_said_as_jira_says_it()
    {
        var fake = new FakeJira(cloud: false).Seed();
        var ct = TestContext.Current.CancellationToken;
        using (var wrong = ClientOf(Server, fake, "not-it"))
        {
            var denied = await Assert.ThrowsAsync<JiraException>(() => wrong.MyselfAsync(ct));
            Assert.Equal((401, "Jira: You are not authenticated. Authentication required to perform this operation. (not signed in (the token or password is wrong, or has expired))"), (denied.Status, denied.Message));
            Assert.DoesNotContain("not-it", denied.Message);
        }
        using var jira = ClientOf(Server, fake);
        var none = await Assert.ThrowsAsync<JiraException>(() => jira.IssueAsync("SHOP-99", ct));
        Assert.Equal("Jira: Issue does not exist or you do not have permission to see it. (not found)", none.Message);
        var project = await Assert.ThrowsAsync<JiraException>(() => jira.ProjectAsync("NOPE", ct));
        Assert.Contains("No project could be found with key 'NOPE'.", project.Message);
        fake.Down = true;
        var down = await Assert.ThrowsAsync<JiraException>(() => jira.MyselfAsync(ct));
        Assert.Null(down.Status);
        Assert.EndsWith("a site of a company may need its VPN.", down.Message);
    }

    [Fact]
    public void Rich_text_reads_as_markdown_and_plain_text_as_adf()
    {
        Assert.Equal("## Steps\n\n1. Open the cart\n2. Add `two` items\n\nSee [the log](https://example.invalid/log) and **this**.\n\n```\nvar x = 1;\nvar y = 2;\n```",
            JiraText.FromHtml("<h2>Steps</h2><ol><li>Open the cart</li><li>Add <code>two</code> items</li></ol><p>See <a href=\"https://example.invalid/log\">the log</a> and <b>this</b>.</p><pre>var x = 1;\nvar y = 2;</pre>"));
        Assert.Equal("- one\n- two &amp; three", JiraText.FromHtml("<ul><li>one</li><li>two &amp;amp; three</li></ul>"));
        var adf = JsonSerializer.SerializeToElement(JiraText.ToAdf("First line\nsecond\n\nNext paragraph"));
        Assert.Equal("First line  \nsecond\n\nNext paragraph", JiraText.FromAdf(adf));
        var marked = JsonDocument.Parse("""{"type":"doc","content":[{"type":"heading","attrs":{"level":3},"content":[{"type":"text","text":"Fix"}]},{"type":"bulletList","content":[{"type":"listItem","content":[{"type":"paragraph","content":[{"type":"text","text":"bold","marks":[{"type":"strong"}]},{"type":"text","text":" and "},{"type":"text","text":"link","marks":[{"type":"link","attrs":{"href":"https://x.invalid"}}]}]}]}]}]}""").RootElement;
        Assert.Equal("### Fix\n\n- **bold** and [link](https://x.invalid)", JiraText.FromAdf(marked));
    }

    /// <summary>The window's side of the explorer, the test's own.</summary>
    [AvaloniaFact]
    public async Task A_board_shows_each_columns_issues_however_many_are_done()
    {
        // 250 done issues first in the board's order (as a board's old ones are), then one to do and one in progress.
        var fake = new FakeJira(cloud: false) { SubQuery = "fixVersion is EMPTY" }.WithDone(250).Seed();
        var keyring = new TestKeyring();
        keyring.Secrets["karvon-jira/" + Server.Id] = Secret;
        var explorer = new JiraExplorerViewModel(new TestJiraHost(), keyring, (site, secret) => new JiraClient(site, secret, fake));
        var board = new JiraBoardPageViewModel(explorer, Server, new JiraBoard(7, "SHOP board", "kanban"));

        await board.RefreshAsync();
        Assert.Null(board.Error);
        // The done ones leave the other columns theirs; the done column shows its latest first, and how many it has.
        Assert.Equal(["To Do  1", "In Progress  1", $"Done  {JiraBoardPageViewModel.CardsPerColumn} of 250"], board.Columns.Select(c => c.Header));
        Assert.Equal(["Add a wish list"], board.Columns[0].Cards.Select(c => c.Summary));
        Assert.Equal(["Done task 250", "Done task 249"], board.Columns[2].Cards.Take(2).Select(c => c.Summary));
        // Each column read through the board's sub-filter.
        var reads = fake.Requests.Where(r => r.Path.Contains("/issue?", StringComparison.Ordinal)).Select(r => Uri.UnescapeDataString(r.Path)).ToList();
        Assert.Equal(3, reads.Count);
        Assert.All(reads, r => Assert.Contains("jql=(fixVersion is EMPTY) AND status in (", r));
    }

    sealed class TestJiraHost : IJiraHost
    {
        public string ConfigDirectory { get; } = Directory.CreateTempSubdirectory("karvon-jira-").FullName;

        public List<DocumentTab> Opened { get; } = [];

        public List<string> Urls { get; } = [];

        public List<string> Prompts { get; } = [];

        public List<string> Statuses { get; } = [];

        public IEnumerable<DocumentTab> Documents => Opened;

        public void ShowDocument(DocumentTab document)
        {
            if (!Opened.Contains(document))
                Opened.Add(document);
        }

        public void OpenUrl(string url) => Urls.Add(url);

        public void AskClaude(string prompt) => Prompts.Add(prompt);

        public void Status(string text) => Statuses.Add(text);
    }

    [AvaloniaFact]
    public async Task The_explorer_adds_a_site_keeps_its_token_in_the_keyring_and_opens_its_issues()
    {
        var host = new TestJiraHost();
        var keyring = new TestKeyring();
        var fake = new FakeJira(cloud: false).Seed();
        var vm = new JiraExplorerViewModel(host, keyring, (site, secret) => new JiraClient(site, secret, fake));
        var window = new Window { Width = 1100, Height = 760, Content = new Grid { Children = { new JiraExplorerView { DataContext = vm }, new JiraOverlay { DataContext = vm } } } };
        window.Show();
        try
        {
            Assert.True(vm.IsEmpty);

            // Add Jira Site: Server's personal access token, tested, then kept in the keyring (never in jira.json).
            var adding = vm.AddSiteCommand.ExecuteAsync(null);
            var form = Assert.IsType<JiraSiteDialog>(vm.Dialog);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            // The Add button follows the form: off until it is filled in.
            var add = window.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Add");
            Assert.False(add.IsEnabled);
            (form.Name, form.Url, form.AuthIndex) = ("Work", "https://jira.example.invalid/", 1);
            Assert.False(form.CanConfirm);
            Assert.Equal(("Personal access token", false), (form.SecretLabel, form.NeedsUser));
            form.Secret = "wrong";
            await form.TestCommand.ExecuteAsync(null);
            Assert.True(form.TestFailed);
            Assert.StartsWith("Jira: You are not authenticated.", form.TestMessage);
            form.Secret = Secret;
            await form.TestCommand.ExecuteAsync(null);
            Assert.Equal(("Signed in as Dev Eloper (Shop Jira 9.12.2).", false), (form.TestMessage, form.TestFailed));
            // Cloud's way for a Server site: it says so.
            form.AuthIndex = 0;
            form.User = "me@example.com";
            await form.TestCommand.ExecuteAsync(null);
            Assert.True(form.TestFailed);
            form.AuthIndex = 1;
            Snapshot(window, "jira-add-site");
            Assert.True(add.IsEnabled);
            add.Command!.Execute(null);
            await adding;
            var saved = File.ReadAllText(Path.Combine(host.ConfigDirectory, "jira.json"));
            Assert.Contains("\"Url\": \"https://jira.example.invalid\"", saved);
            Assert.Contains("\"Auth\": \"PersonalAccessToken\"", saved);
            Assert.DoesNotContain(Secret, saved);
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(host.ConfigDirectory, "jira.json")));
            Assert.Equal(Secret, Assert.Single(keyring.Secrets).Value);

            // Its tree: the user's issues, read as it opens; its filters and boards.
            var site = Assert.Single(vm.Sites);
            Assert.Equal(["Assigned to me", "Reported by me", "Filters", "Boards"], site.Children.Select(c => c.Title));
            var mine = site.Children[0];
            mine.IsExpanded = true;
            await WaitUntil(() => mine.Children.Any(c => c.IsIssue), TimeSpan.FromSeconds(5));
            Assert.Equal(["SHOP-1 Fix the cart's total", "SHOP-2 Add a wish list"], mine.Children.Select(c => c.Title));
            site.Children[2].IsExpanded = true;
            await WaitUntil(() => site.Children[2].Children.Any(c => c.Kind == JiraNodeKind.Query), TimeSpan.FromSeconds(5));
            Assert.Equal("Shop bugs", site.Children[2].Children.Single().Title);
            Snapshot(window, "jira-explorer");

            // An issue's page: its fields, description, comments; moved, commented, assigned; asked of Claude.
            vm.Open(mine.Children[0]);
            var page = Assert.IsType<JiraIssueDocument>(Assert.Single(host.Opened)).Page;
            await WaitUntil(() => page.HasIssue, TimeSpan.FromSeconds(5));
            Assert.Equal("The total is **off** by one.", page.Issue!.Description);
            Assert.Contains(page.Fields, f => f is { Name: "Status", Value: "В работе" });
            Assert.Contains(page.Fields, f => f is { Name: "Labels", Value: "web" });
            Assert.Equal(["На проверку", "Вернуть"], page.Transitions.Select(t => t.Name));
            await page.TransitionCommand.ExecuteAsync(page.Transitions[0]);
            await WaitUntil(() => page.Issue?.Summary.Status == "QA", TimeSpan.FromSeconds(5));
            page.NewComment = "Ready for QA.";
            await page.CommentCommand.ExecuteAsync(null);
            Assert.Equal(("", "Ready for QA."), (page.NewComment, page.Issue!.Comments[^1].Body));
            page.OpenInBrowserCommand.Execute(null);
            Assert.Equal("https://jira.example.invalid/browse/SHOP-1", host.Urls.Single());
            page.AskClaudeCommand.Execute(null);
            Assert.StartsWith("Work on the Jira issue SHOP-1 (Work): read it with jira_issue first.", host.Prompts.Single());
            var view = new JiraIssuePageView { DataContext = page };
            var pageWindow = new Window { Width = 1100, Height = 760, Content = view };
            pageWindow.Show();
            // The Markdown views draw a moment later.
            await WaitUntil(() => false, TimeSpan.FromMilliseconds(400));
            Snapshot(pageWindow, "jira-issue");
            pageWindow.Close();

            // A board: its columns and their cards; a card to another column, by its workflow's transition.
            var boards = site.Children[3];
            boards.IsExpanded = true;
            await WaitUntil(() => boards.Children.Any(c => c.Kind == JiraNodeKind.Board), TimeSpan.FromSeconds(5));
            vm.Open(boards.Children[0]);
            var board = host.Opened.OfType<JiraBoardDocument>().Single().Page;
            await WaitUntil(() => board.Columns.Count == 3 && board.Columns.Sum(c => c.Cards.Count) == 2, TimeSpan.FromSeconds(5));
            Assert.Equal(["To Do  1", "In Progress  1", "Done  0"], board.Columns.Select(c => c.Header));
            Assert.Equal("Sprint 3", board.Sprint?.Name);
            var wish = board.Columns[0].Cards.Single();
            Assert.True(await board.MoveAsync(wish, board.Columns[1]));
            Assert.Equal(["To Do  0", "In Progress  2", "Done  0"], board.Columns.Select(c => c.Header));
            var boardWindow = new Window { Width = 1100, Height = 600, Content = new JiraBoardPageView { DataContext = board } };
            boardWindow.Show();
            await WaitUntil(() => false, TimeSpan.FromMilliseconds(200));
            Snapshot(boardWindow, "jira-board");
            boardWindow.Close();

            // A search; a new issue, the signed-in user's.
            var search = vm.OpenSearch(site.Site!, "project = SHOP AND issuetype = Bug").Page;
            await WaitUntil(() => search.Results.Count > 0, TimeSpan.FromSeconds(5));
            Assert.Equal(["SHOP-1"], search.Results.Select(r => r.Key));
            var creating = vm.CreateIssueCommand.ExecuteAsync(site);
            await WaitUntil(() => vm.Dialog is JiraCreateDialog { Types.Count: > 0 }, TimeSpan.FromSeconds(5));
            var create = (JiraCreateDialog)vm.Dialog!;
            Assert.Equal(["Задача", "Bug"], create.Types.Select(t => t.Name));
            // The site has no date fields of its own: its end goes to the due date.
            Assert.False(create.HasDates);
            create.Summary = "Add a coupon field";
            create.End = "2026-10-27";
            create.Confirm();
            await creating;
            Assert.Contains(host.Opened.OfType<JiraIssueDocument>(), d => d.Page.IssueKey == "SHOP-3");

            // Removed: Karvon forgets it, and its token.
            var removing = vm.RemoveSiteCommand.ExecuteAsync(site);
            Assert.IsType<Karvon.Ui.ConfirmDialog>(vm.Dialog).Confirm();
            await removing;
            Assert.True(vm.IsEmpty);
            Assert.Empty(keyring.Secrets);
        }
        finally
        {
            window.Close();
            Directory.Delete(host.ConfigDirectory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Without_a_keyring_the_token_is_asked_once_a_session_and_kept_nowhere()
    {
        var host = new TestJiraHost();
        var fake = new FakeJira(cloud: true).Seed();
        new JiraSiteList(Path.Combine(host.ConfigDirectory, "jira.json")).Save(Cloud);
        var vm = new JiraExplorerViewModel(host, null, (site, secret) => new JiraClient(site, secret, fake));
        var asking = vm.ClientAsync(Cloud);
        var dialog = Assert.IsType<JiraSecretDialog>(vm.Dialog);
        Assert.Equal(("The API token of me@example.com at https://shop.atlassian.invalid.", false), (dialog.Question, dialog.CanKeep));
        dialog.Secret = Secret;
        dialog.Confirm();
        var client = await asking;
        Assert.Equal("acc-1", (await client.MyselfAsync(TestContext.Current.CancellationToken)).Id);
        // Not asked again this session; nothing of it written.
        Assert.Same(client, await vm.ClientAsync(Cloud));
        Assert.DoesNotContain(Secret, File.ReadAllText(Path.Combine(host.ConfigDirectory, "jira.json")));
        var refused = new JiraExplorerViewModel(host, null, (site, secret) => new JiraClient(site, secret, fake));
        var again = refused.ClientAsync(Cloud);
        Assert.IsType<JiraSecretDialog>(refused.Dialog).Cancel();
        Assert.Equal("Not signed in to Cloud: no token or password was given.", (await Assert.ThrowsAsync<JiraException>(() => again)).Message);
        Directory.Delete(host.ConfigDirectory, recursive: true);
    }

    [AvaloniaFact]
    public async Task Claude_works_with_jira_through_karvon_and_never_sees_its_token()
    {
        var fake = new FakeJira(cloud: false).Seed();
        var keyring = new TestKeyring();
        var jira = new JiraModule { Clients = (site, secret) => new JiraClient(site, secret, fake), Keyring = keyring };
        var vm = NewViewModel(modules: [.. Karvon.BuiltInModules.Create().Where(m => m is not JiraModule), jira]);
        var server = ClaudeOf(vm).ToolServer();
        var outputs = new List<string>();
        async Task<string> Tool(string name, object arguments)
        {
            var (text, isError) = await Call(server, name, arguments);
            outputs.Add(text);
            Assert.False(isError, text);
            return text;
        }
        try
        {
            // No site: Claude is told where the user adds one.
            Assert.Equal("There are no Jira sites. The user adds them in Karvon's Jira window (View > Jira).", await Tool("jira_sites", new { }));
            new JiraSiteList(Path.Combine(Karvon.Services.Settings.ConfigDirectory, "jira.json")).Save(Server);
            jira.EnsureExplorer().Refresh();
            jira.Explorer!.Secrets.Set(Server, Secret, keep: true);

            Assert.Equal("Work: https://jira.example.invalid, Jira Server/Data Center, dates in customfield_10015 and customfield_10016", await Tool("jira_sites", new { }));
            Assert.Equal("SHOP-1 [В работе] Bug: Fix the cart's total (Dev Eloper)\nSHOP-2 [К выполнению] Задача: Add a wish list (Dev Eloper)",
                await Tool("jira_search", new { jql = "assignee = currentUser()" }));
            var issue = await Tool("jira_issue", new { key = "SHOP-1" });
            Assert.StartsWith("SHOP-1: Fix the cart's total\nStatus: В работе; type: Bug; priority: Medium; assignee: Dev Eloper; reporter: Dev Eloper", issue);
            Assert.Contains("Description:\nThe total is **off** by one.", issue);
            Assert.Contains("- Dev Eloper, 2026-10-20", issue);
            Assert.EndsWith("Transitions now: На проверку (to QA), Вернуть (to К выполнению)", issue);
            Assert.Equal("SHOP: Shop\nIssue types: Задача, Bug, Sub-task (subtask)", await Tool("jira_projects", new { project = "SHOP" }));

            // Reading runs at once; what changes Jira asks the user each time.
            Assert.Equal(ToolAccess.Read, server.Plan(ClaudeToolServer.Prefix + "jira_issue", JsonSerializer.SerializeToElement(new { key = "SHOP-1" })).Access);
            var plan = server.Plan(ClaudeToolServer.Prefix + "jira_create", JsonSerializer.SerializeToElement(new { project = "SHOP", type = "Задача", summary = "Coupons", description = "- Goal: coupons" }));
            Assert.Equal((ToolAccess.Write, "Allow Claude to make this Задача in Jira?", "Coupons\n\n- Goal: coupons"), (plan.Access, plan.Question, plan.Code));
            foreach (var tool in new[] { "jira_transition", "jira_comment", "jira_assign" })
                Assert.Equal(ToolAccess.Write, server.Plan(ClaudeToolServer.Prefix + tool, JsonSerializer.SerializeToElement(new { key = "SHOP-1", transition = "QA", text = "x", to = "me" })).Access);

            Assert.Equal("SHOP-3 made, assigned to the signed-in user: https://jira.example.invalid/browse/SHOP-3",
                await Tool("jira_create", new { project = "SHOP", type = "Задача", summary = "Coupons", description = "- Goal: coupons", start = "2026-10-24", end = "2026-10-26" }));
            Assert.Equal("SHOP-3: Начать работу → В работе.", await Tool("jira_transition", new { key = "SHOP-3", transition = "В работе" }));
            Assert.Equal("Commented on SHOP-3.", await Tool("jira_comment", new { key = "SHOP-3", text = "Done: coupons in the cart." }));
            Assert.Equal("SHOP-3 is unassigned.", await Tool("jira_assign", new { key = "SHOP-3", to = "nobody" }));
            Assert.Equal("SHOP-3 is assigned to Dev Eloper.", await Tool("jira_assign", new { key = "SHOP-3", to = "me" }));
            var (wrong, failed) = await Call(server, "jira_transition", new { key = "SHOP-3", transition = "Released" });
            outputs.Add(wrong);
            Assert.True(failed);
            Assert.StartsWith("SHOP-3 has no transition \"Released\" now; it has: ", wrong);

            // What Claude read or changed shows in Karvon.
            await jira.ShowClaudeToolAsync("jira_issue", JsonSerializer.SerializeToElement(new { key = "SHOP-1" }), new ClaudeToolOutput("x"));
            Assert.Contains(vm.Documents.OfType<JiraIssueDocument>(), d => d.Page.IssueKey == "SHOP-1");

            // The token is in no output, and Karvon's instructions send Claude to these tools rather than curl.
            Assert.All(outputs, o => Assert.DoesNotContain(Secret, o));
            var initialized = await server.HandleAsync(JsonSerializer.SerializeToElement(new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { } }), CancellationToken.None);
            Assert.Contains("Jira sites", initialized!["result"]!["instructions"]!.GetValue<string>());
        }
        finally
        {
            File.Delete(Path.Combine(Karvon.Services.Settings.ConfigDirectory, "jira.json"));
        }
    }
}
