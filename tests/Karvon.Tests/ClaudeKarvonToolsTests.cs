using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Bengine.Enums;
using Bengine.Introspection.Servers;
using Microsoft.Data.Sqlite;
using Karvon.Claude;
using Karvon.Claude.Tools;
using Karvon.Docker;
using Karvon.ViewModels;
using Karvon.Views;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>A data source list (never the user's) and a fake docker, as Karvon gives them to its tools.</summary>
sealed class TestToolHost(string? solution, DockerEngine docker, params ServerProfile[] servers) : Karvon.Database.IDatabaseToolHost, Karvon.Docker.IDockerToolHost
{
    /// <summary>Karvon's tools over this host: the databases', (with <paramref name="explorer"/>) the servers', the containers'.</summary>
    public ClaudeToolServer Tools(Func<Karvon.Servers.ViewModels.ServersExplorerViewModel>? explorer = null) => KarvonTools.Create(
        [.. Karvon.Database.DatabaseClaudeTools.Create(this), .. explorer is null ? [] : Karvon.Servers.ServerClaudeTools.Create(explorer), .. Karvon.Docker.DockerClaudeTools.Create(this)]);

    public string? SolutionDirectory { get; } = solution;

    public IReadOnlyList<ServerProfile> DataSources() => servers;

    public Task<string?> PasswordForAsync(ServerProfile server) => Task.FromResult(server.Password);

    public DockerEngine Docker { get; } = docker;

    public bool AllContainers { get; set; }

    /// <summary>A SQLite file with three users.</summary>
    public static string NewDatabase()
    {
        var file = Path.Combine(Path.GetTempPath(), "karvon-tools-" + Guid.NewGuid().ToString("N") + ".db");
        using (var connection = new SqliteConnection($"Data Source={file}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE users (id INTEGER PRIMARY KEY, email TEXT NOT NULL UNIQUE, age INTEGER);
                INSERT INTO users (email, age) VALUES ('a@x.io', 31), ('b@x.io', 27), ('c@x.io', 45);
                """;
            command.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        return file;
    }

    public static long Scalar(string file, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={file};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }
}

public class ClaudeKarvonToolsTests
{
    static async Task<JsonObject> Call(ClaudeToolServer server, string tool, object arguments)
    {
        var message = JsonSerializer.SerializeToElement(new { jsonrpc = "2.0", id = 7, method = "tools/call", @params = new { name = tool, arguments } });
        return (await server.HandleAsync(message, CancellationToken.None))["result"]!.AsObject();
    }

    static string Text(JsonObject result) => result["content"]![0]!["text"]!.GetValue<string>();

    static bool IsError(JsonObject result) => result["isError"]!.GetValue<bool>();

    static ClaudeToolPlan Plan(ClaudeToolServer server, string tool, object input) =>
        server.Plan(ClaudeToolServer.Prefix + tool, JsonSerializer.SerializeToElement(input))!;

    [AvaloniaFact]
    public async Task Claude_reads_a_database_at_once_and_asks_before_it_changes_it()
    {
        var (dir, log) = FakeClaude.Workspace();
        var file = TestToolHost.NewDatabase();
        var (docker, _, _) = FakeDocker.Create();
        var host = new TestToolHost(dir, new DockerEngine(docker), new ServerProfile { Name = "shop", Provider = DatabaseProvider.Sqlite, FilePath = file });
        var vm = new ClaudeViewModel(new Karvon.Services.Settings(), () => dir, () => null, () => [], () => FakeClaude.Executable) { Tools = host.Tools() };
        var window = new Window { Width = 560, Height = 900, Content = new ClaudeView { DataContext = vm } };
        window.Show();
        await WaitUntil(() => vm.Commands.Count > 0, TimeSpan.FromSeconds(10));

        vm.Prompt = "use karvontools on the shop database";
        await vm.SendCommand.ExecuteAsync(null);

        // The SELECT ran without a card; the UPDATE asks, once, with no "don't ask again".
        var update = await WaitFor<PermissionChatItem>(vm, c => c.IsPending);
        Assert.Equal("Allow Claude to change data in shop?", update.Question);
        Assert.Equal("UPDATE users SET age = 32 WHERE id = 1", update.Code);
        Assert.False(update.HasSessionOption);
        var select = vm.Items.OfType<ToolChatItem>().First();
        Assert.Equal(("Query", "shop: SELECT email FROM users ORDER BY id"), (select.Title, select.Target));
        Assert.Contains("a@x.io", select.Result);
        Snapshot(window, "claude-karvon-tools");
        await vm.AllowCommand.ExecuteAsync(update);

        // DELETE without WHERE is destructive: it asks again, and is denied.
        var delete = await WaitFor<PermissionChatItem>(vm, c => c.IsPending && c != update);
        Assert.Equal("Allow Claude to run destructive SQL on shop?", delete.Question);
        Assert.StartsWith("DROP, TRUNCATE", delete.Description);
        await vm.DenyCommand.ExecuteAsync(delete);
        await WaitUntil(() => !vm.IsRunning, TimeSpan.FromSeconds(10));

        Assert.Equal(32, TestToolHost.Scalar(file, "SELECT age FROM users WHERE id = 1"));
        Assert.Equal(3, TestToolHost.Scalar(file, "SELECT COUNT(*) FROM users"));
        var entries = FakeClaude.Log(log);
        var argv = entries.First(e => e.Kind == "argv").Data.EnumerateArray().Select(a => a.GetString()).ToList();
        Assert.Equal("""{"mcpServers":{"karvon":{"type":"sdk","name":"karvon"}}}""", argv[argv.IndexOf("--mcp-config") + 1]);
        // Without shell rules no hook is registered.
        Assert.False(Initialize(entries).TryGetProperty("hooks", out var hooks) && hooks.ValueKind != JsonValueKind.Null);
        var mcp = entries.Where(e => e.Kind == "mcp").Select(e => e.Data).ToList();
        Assert.Equal("karvon", mcp[0].GetProperty("response").GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.Equal(10, mcp[2].GetProperty("response").GetProperty("result").GetProperty("tools").GetArrayLength());
        Assert.Contains("1 row(s) affected", mcp.Last().GetProperty("response").GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
        // The SELECT's approval was Karvon's own, without the user: allowed once, no rule remembered.
        var first = entries.First(e => e.Kind == "decision").Data;
        Assert.Equal("allow", first.GetProperty("behavior").GetString());
        Assert.False(first.TryGetProperty("updatedPermissions", out _));
        await vm.DisposeAsync();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Claude_is_sent_to_Karvons_tools_before_the_shell_once()
    {
        var (dir, log) = FakeClaude.Workspace();
        var vm = new ClaudeViewModel(new Karvon.Services.Settings(), () => dir, () => null, () => [], () => FakeClaude.Executable)
        {
            Tools = KarvonTools.Create([], IdeClaudeTools.ShellRules),
        };
        var window = new Window { Width = 560, Height = 900, Content = new ClaudeView { DataContext = vm } };
        window.Show();
        await WaitUntil(() => vm.Commands.Count > 0, TimeSpan.FromSeconds(10));

        vm.Prompt = "shell first please";
        await vm.SendCommand.ExecuteAsync(null);

        // The build is sent to run_build without a card; the same build again, and ls, ask as any Bash does.
        var again = await WaitFor<PermissionChatItem>(vm, c => c.IsPending);
        Assert.Equal("cd src && dotnet build -c Release", again.Code);
        await vm.AllowCommand.ExecuteAsync(again);
        var ls = await WaitFor<PermissionChatItem>(vm, c => c.IsPending && c != again);
        Assert.Equal("ls -la", ls.Code);
        await vm.AllowCommand.ExecuteAsync(ls);
        await WaitUntil(() => !vm.IsRunning, TimeSpan.FromSeconds(10));
        var build = vm.Items.OfType<ToolChatItem>().First();
        Assert.Contains("mcp__karvon__run_build", build.Result);

        var entries = FakeClaude.Log(log);
        var argv = entries.First(e => e.Kind == "argv").Data.EnumerateArray().Select(a => a.GetString()).ToList();
        Assert.Equal(KarvonTools.SystemPrompt, argv[argv.IndexOf("--append-system-prompt") + 1]);
        var hooks = Initialize(entries).GetProperty("hooks").GetProperty("PreToolUse")[0];
        Assert.Equal("Bash", hooks.GetProperty("matcher").GetString());
        Assert.Equal(ClaudeSession.ShellHook, hooks.GetProperty("hookCallbackIds")[0].GetString());
        var answers = entries.Where(e => e.Kind == "hook").Select(e => e.Data).ToList();
        Assert.Equal(3, answers.Count);
        var deny = answers[0].GetProperty("hookSpecificOutput");
        Assert.Equal(("PreToolUse", "deny"), (deny.GetProperty("hookEventName").GetString(), deny.GetProperty("permissionDecision").GetString()));
        Assert.EndsWith("run the same command again: Karvon lets it through then.", deny.GetProperty("permissionDecisionReason").GetString());
        Assert.All(answers.Skip(1), a => Assert.False(a.TryGetProperty("hookSpecificOutput", out _)));
        await vm.DisposeAsync();
        window.Close();
    }

    [Fact]
    public void Turned_off_a_session_is_told_nothing_and_sent_nowhere()
    {
        var first = false;
        var tools = KarvonTools.Create([], IdeClaudeTools.ShellRules, () => first);
        Assert.Null(tools.SystemPrompt);
        Assert.Empty(tools.ShellRules);
        Assert.Null(tools.ShellRuleFor("dotnet build"));

        // On again: the sessions started after have both.
        first = true;
        Assert.Equal(KarvonTools.SystemPrompt, tools.SystemPrompt);
        Assert.Equal("dotnet build", tools.ShellRuleFor("dotnet build")?.Name);
    }

    /// <summary>The initialize request Karvon sent the CLI.</summary>
    static JsonElement Initialize(List<(string Kind, JsonElement Data)> entries) => entries
        .Where(e => e.Kind == "in" && e.Data.GetProperty("type").GetString() == "control_request")
        .Select(e => e.Data.GetProperty("request"))
        .First(r => r.GetProperty("subtype").GetString() == "initialize");

    /// <summary>The shell's commands Karvon's built-in modules send to their tools (the ssh one is the Servers tests').</summary>
    static readonly ClaudeToolServer ShellRules = KarvonTools.Create([],
    [
        .. IdeClaudeTools.ShellRules, .. new Karvon.Database.DatabaseModule().ClaudeShellRules(),
        .. new Karvon.Docker.DockerModule().ClaudeShellRules(), .. new Karvon.Jira.JiraModule().ClaudeShellRules(),
    ]);

    [Theory]
    [InlineData("dotnet build", "dotnet build")]
    [InlineData("cd src && dotnet test --filter Unit", "dotnet build")]
    [InlineData("DOTNET_NOLOGO=1 dotnet run --project App", "dotnet build")]
    [InlineData("npm run test", "npm scripts")]
    [InlineData("pnpm build", "npm scripts")]
    [InlineData("PGPASSWORD=x psql -h db -U app -c 'select 1'", "database shells")]
    [InlineData("sudo mysql -e 'show databases'", "database shells")]
    [InlineData("docker ps -a", "docker")]
    [InlineData("docker compose up -d", "docker")]
    [InlineData("curl -s https://jira.example.com/rest/api/2/issue/ABC-1", "jira api")]
    [InlineData("acli jira workitem view ABC-1", "jira cli")]
    [InlineData("for i in 1 2 3; do dotnet test --no-build --filter Unit; done", "dotnet build")]
    [InlineData("if true; then npm test; fi", "npm scripts")]
    [InlineData("dotnet --version", null)]
    [InlineData("dotnet add package Foo", null)]
    [InlineData("echo dotnet build", null)]
    [InlineData("npm install", null)]
    [InlineData("docker build -t app .", null)]
    [InlineData("mongod --dbpath data", null)]
    [InlineData("curl -s https://example.com/api/2/items", null)]
    [InlineData("git status", null)]
    public void Shell_rules_name_the_commands_Karvons_tools_do(string command, string? rule) =>
        Assert.Equal(rule, ShellRules.ShellRuleFor(command)?.Name);

    [Theory]
    // The workspace's build, tests and run go to Karvon's tools, also from a folder in it or with output elsewhere...
    [InlineData("dotnet build /work/app/App.sln 2>&1 | tail -5", "dotnet build")]
    [InlineData("cd src && dotnet test", "dotnet build")]
    [InlineData("cd /work/app/src && dotnet build ../App.csproj", "dotnet build")]
    [InlineData("cd /tmp && ls; cd /work/app && dotnet build", "dotnet build")]
    [InlineData("dotnet build -o /tmp/out", "dotnet build")]
    [InlineData("dotnet test --results-directory=/tmp/results", "dotnet build")]
    [InlineData("dotnet build > /tmp/build.log 2>&1", "dotnet build")]
    [InlineData("npm run build", "npm scripts")]
    // ...but a project in another folder is the shell's: Karvon's tools build only the workspace.
    [InlineData("mkdir -p /tmp/probe && cd /tmp/probe && dotnet new console && dotnet build", null)]
    [InlineData("dotnet build /tmp/probe/Probe.csproj 2>&1 | tail -5", null)]
    [InlineData("dotnet run --project ~/other/App", null)]
    [InlineData("dotnet build \"../other app/Other.sln\"", null)]
    [InlineData("npm --prefix /tmp/site run build", null)]
    [InlineData("(cd /tmp/site && npm test)", null)]
    // Other rules are not about the workspace.
    [InlineData("cd /tmp && docker ps", "docker")]
    public void Shell_rules_for_the_workspace_leave_other_folders_to_the_shell(string command, string? rule) =>
        Assert.Equal(rule, ShellRules.ShellRuleFor(command, "/work/app")?.Name);

    [Fact]
    public void Repeated_test_runs_tell_a_flaky_failure_from_one_that_always_fails()
    {
        var text = TestReport.Repeated([
            new(8, 2, 0, new Dictionary<string, string> { ["T.Flaky"] = "Timed out", ["T.Broken"] = "Expected 1" }),
            new(9, 1, 0, new Dictionary<string, string> { ["T.Broken"] = "Expected 1" }),
            new(9, 1, 0, new Dictionary<string, string> { ["T.Broken"] = "Expected 1" }),
        ]);
        Assert.Equal("Ran 3 times:\nrun 1: 8 passed, 2 failed, 0 skipped\nrun 2: 9 passed, 1 failed, 0 skipped\nrun 3: 9 passed, 1 failed, 0 skipped\n"
            + "Flaky, failed in 1 of 3 runs: T.Flaky: Timed out\nFailed in every run: T.Broken: Expected 1", text);
        Assert.EndsWith("No test failed in any run.", TestReport.Repeated([new(3, 0, 0, new Dictionary<string, string>()), new(3, 0, 0, new Dictionary<string, string>())]));
    }

    static async Task<T> WaitFor<T>(ClaudeViewModel vm, Func<T, bool> match) where T : ChatItem
    {
        T? found = null;
        await WaitUntil(() => (found = vm.Items.OfType<T>().FirstOrDefault(match)) is not null, TimeSpan.FromSeconds(10));
        return found!;
    }

    [Fact]
    public async Task Database_tools_describe_query_and_refuse_changes_to_read_only_sources()
    {
        var file = TestToolHost.NewDatabase();
        var (docker, _, _) = FakeDocker.Create();
        var host = new TestToolHost(null, new DockerEngine(docker),
            new ServerProfile { Name = "shop", Provider = DatabaseProvider.Sqlite, FilePath = file, Comment = "Server=x;Password=hunter2" },
            new ServerProfile { Name = "reports", Provider = DatabaseProvider.Sqlite, FilePath = file, ReadOnly = true });
        var server = host.Tools();

        var connections = Text(await Call(server, "db_connections", new { }));
        Assert.Contains("shop: Sqlite, " + file + ", writable", connections);
        Assert.Contains("reports: Sqlite, " + file + ", read-only", connections);
        Assert.DoesNotContain("hunter2", connections);

        Assert.Contains("CREATE TABLE", Text(await Call(server, "db_schema", new { connection = "shop", table = "users" })));
        var rows = Text(await Call(server, "db_query", new { connection = "shop", sql = "SELECT id, email FROM users ORDER BY id", maxRows = 2 }));
        Assert.StartsWith("2+ row(s)", rows);
        Assert.Contains("id | email", rows);
        Assert.Contains("1 | a@x.io", rows);
        var missing = await Call(server, "db_query", new { connection = "nope", sql = "SELECT 1" });
        Assert.True(IsError(missing));
        Assert.Contains("Data sources: shop, reports", Text(missing));

        // What a statement may do decides whether it asks: every statement of the script counts.
        Assert.Equal(ToolAccess.Read, Plan(server, "db_query", new { connection = "shop", sql = "SELECT 1" }).Access);
        Assert.Equal(ToolAccess.Write, Plan(server, "db_query", new { connection = "shop", sql = "UPDATE users SET age = 1 WHERE id = 2" }).Access);
        Assert.Equal(ToolAccess.Destructive, Plan(server, "db_query", new { connection = "shop", sql = "SELECT 1; DROP TABLE users" }).Access);
        Assert.Equal(ToolAccess.Read, Plan(server, "db_explain", new { connection = "shop", sql = "DELETE FROM users" }).Access);
        Assert.Equal(ToolAccess.Destructive, Plan(server, "db_explain", new { connection = "shop", sql = "DELETE FROM users", analyze = true }).Access);
        Assert.Equal(ToolAccess.Read, Plan(server, "container_logs", new { name = "db" }).Access);
        Assert.Equal(ToolAccess.Destructive, Plan(server, "container_remove", new { name = "db" }).Access);
        Assert.Null(server.Plan("Bash", JsonSerializer.SerializeToElement(new { command = "ls" })));

        // A read-only source refuses a change before it runs.
        var refused = await Call(server, "db_query", new { connection = "reports", sql = "UPDATE users SET age = 0" });
        Assert.True(IsError(refused));
        Assert.Contains("read-only", Text(refused));
        Assert.Equal(0, TestToolHost.Scalar(file, "SELECT COUNT(*) FROM users WHERE age = 0"));
        Assert.Contains("Plan (", Text(await Call(server, "db_explain", new { connection = "shop", sql = "SELECT * FROM users WHERE email = 'a@x.io'" })));
    }

    [Fact]
    public async Task Docker_tools_see_the_solutions_containers_with_secrets_hidden()
    {
        var solution = Path.Combine(Path.GetTempPath(), "karvon-tools-solution-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(solution);
        File.WriteAllText(Path.Combine(solution, "compose.yaml"),
            "services:\n  web:\n    image: nginx\n    environment:\n      API: http://localhost:8080\n  api:\n    image: shop-api\n    ports: ['5000:8080']\n");
        var sample = FakeDocker.Sample(solution);
        sample["containers"]![2]!["logs"]!.AsArray().Add("connecting to postgres://shop:not-a-secret@db:5432/shop");
        var (docker, state, _) = FakeDocker.Create(sample);
        var host = new TestToolHost(solution, new DockerEngine(docker));
        var server = host.Tools();

        // The solution's compose project only: the cache and the others are another project's.
        var list = Text(await Call(server, "container_list", new { }));
        Assert.Equal(["shop-web-1", "shop-api-1", "shop-db-1"], list.Split("\n\n")[0].Split('\n').Select(l => l[..l.IndexOf(' ')]));
        Assert.Contains("shop-db-1 (service db): running", list);
        Assert.Contains("ports 5432:5432", list);
        Assert.EndsWith("Problems of the compose file:\n- web: API uses localhost, which inside a container is web itself. Use the service name: api.", list);
        var other = await Call(server, "container_logs", new { name = "cache" });
        Assert.True(IsError(other));
        host.AllContainers = true;
        Assert.Contains("cache", Text(await Call(server, "container_list", new { })));
        host.AllContainers = false;

        // By compose service name; passwords in logs and variables hidden.
        var logs = Text(await Call(server, "container_logs", new { name = "db", tail = 50 }));
        Assert.StartsWith("Logs of shop-db-1", logs);
        Assert.Contains("postgres://shop:***@db:5432/shop", logs);
        Assert.DoesNotContain("not-a-secret", logs);
        var inspect = JsonNode.Parse(Text(await Call(server, "container_inspect", new { name = "shop-db-1" })))!;
        Assert.Equal("db", inspect["service"]!.GetValue<string>());
        Assert.Contains("POSTGRES_PASSWORD=***", inspect["environment"]!.AsArray().Select(e => e!.GetValue<string>()));
        Assert.Contains("POSTGRES_USER=shop", inspect["environment"]!.AsArray().Select(e => e!.GetValue<string>()));
        var web = JsonNode.Parse(Text(await Call(server, "container_inspect", new { name = "web" })))!;
        Assert.StartsWith("web: API uses localhost", web["compose"]!["problems"]![0]!.GetValue<string>());

        // Changes: a restart, compose up in the solution's folder, a removal.
        Assert.StartsWith("shop-api-1:", Text(await Call(server, "container_restart", new { name = "api" })));
        Assert.False(IsError(await Call(server, "compose_up", new { service = "web" })));
        Assert.Equal("Removed shop-web-1.", Text(await Call(server, "container_remove", new { name = "web" })));
        var calls = FakeDocker.Read(state)["calls"]!.AsArray().Select(c => c!.ToJsonString()).ToList();
        Assert.Contains(calls, c => c == $"""["restart","{new string('b', 64)}"]""");
        Assert.Contains(calls, c => c.StartsWith("""["compose","up","-d","web"]""", StringComparison.Ordinal));
        Assert.Contains(calls, c => c == $"""["rm","-f","{new string('a', 64)}"]""");
    }
}
