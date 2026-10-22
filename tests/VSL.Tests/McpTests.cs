using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using VSL.Claude;
using VSL.Claude.Tools;
using VSL.Mcp;
using VSL.Mcp.ViewModels;
using VSL.Mcp.Views;
using VSL.ViewModels;
using static VSL.Tests.UiTests;

namespace VSL.Tests;

sealed class TestMcpHost(string? workspace) : IMcpHost
{
    public string ConfigDirectory { get; } = Path.Combine(Path.GetTempPath(), "vsl-mcp-config-" + Guid.NewGuid().ToString("N"));

    public string? Workspace { get; set; } = workspace;

    public string? ClaudeFile => null;

    public IReadOnlyList<IClaudeTool> BuiltInTools { get; set; } = [];

    public List<DocumentTab> Open { get; } = [];

    public List<string> Prompts { get; } = [];

    public List<string> Statuses { get; } = [];

    public IEnumerable<DocumentTab> Documents => Open;

    public void ShowDocument(DocumentTab document)
    {
        if (!Open.Contains(document))
            Open.Add(document);
    }

    public void CloseDocument(DocumentTab document)
    {
        Open.Remove(document);
        document.Dispose();
    }

    public void AskClaude(string prompt) => Prompts.Add(prompt);

    public void Status(string text) => Statuses.Add(text);
}

/// <summary>
/// The MCP Servers: every agent's configuration read and written in its own format (in the tests' home, never the
/// user's), what Claude Code sessions get, the argument form, and a real stdio server (a small Python one) from the tree
/// to its page. One class: they share the home.
/// </summary>
public class McpTests
{
    static readonly string EchoServer = Path.Combine(AppContext.BaseDirectory, "TestAssets", "FakeMcp", "echo_server.py");

    /// <summary>The tests' home, emptied of the agents' files.</summary>
    static string Home()
    {
        var home = McpConfigs.Home;
        Assert.StartsWith(Path.GetTempPath(), home);
        File.Delete(Path.Combine(home, ".claude.json"));
        foreach (var folder in new[] { ".codex", ".gemini" })
            if (Directory.Exists(Path.Combine(home, folder)))
                Directory.Delete(Path.Combine(home, folder), recursive: true);
        return home;
    }

    static string Folder(string name) => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), name + "-" + Guid.NewGuid().ToString("N"))).FullName;

    static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    /// <summary>A server in each agent's file, and one in VSL's.</summary>
    static (McpConfigs Configs, string Home, string Workspace) Everywhere()
    {
        var home = Home();
        var workspace = Folder("vsl-mcp-ws");
        var config = Folder("vsl-mcp-config");
        Write(Path.Combine(workspace, ".mcp.json"), """
            {
              // The folder's servers
              "mcpServers": {
                "echo": { "command": "python3", "args": ["-I", "echo_server.py"], },
              },
            }
            """);
        Write(Path.Combine(home, ".claude.json"), JsonSerializer.Serialize(new
        {
            numStartups = 12,
            oauthAccount = new { emailAddress = "demo@example.com" },
            mcpServers = new { notes = new { type = "stdio", command = "notes-server", env = new { NOTES_TOKEN = "${VSL_TEST_MCP_TOKEN:-none}" } } },
            projects = new Dictionary<string, object> { [workspace] = new { mcpServers = new { db = new { type = "http", url = "http://127.0.0.1:9/mcp", headers = new { Authorization = "Bearer abcdefgh123" } } } } },
        }));
        Write(Path.Combine(home, ".codex", "config.toml"), """
            # my Codex
            model = "gpt"

            [mcp_servers.docs]
            command = "docs-server"
            args = ["--port", "7"]
            env = { "DOCS_KEY" = "${VSL_TEST_MCP_TOKEN}" }

            [mcp_servers.docs.tools]
            search = true

            [profiles.fast]
            model = "mini"
            """);
        Write(Path.Combine(home, ".gemini", "settings.json"), """
            { "theme": "Dark", "mcpServers": { "search": { "httpUrl": "https://example.com/mcp" }, "feed": { "url": "https://example.com/sse" } } }
            """);
        Write(Path.Combine(workspace, ".gemini", "settings.json"), "{ not json");
        Write(Path.Combine(config, "mcp-servers.json"), """
            { "mcpServers": { "mine": { "type": "stdio", "command": "mine" }, "notes": { "command": "other-notes" }, "vsl": { "command": "not-vsl" } } }
            """);
        return (new McpConfigs(config, workspace), home, workspace);
    }

    [Fact]
    public void Every_agents_servers_are_read_in_their_own_format()
    {
        var (configs, home, workspace) = Everywhere();
        var read = configs.Read();
        Assert.Equal(["Project:echo", "ClaudeUser:notes", "ClaudeLocal:db", "Codex:docs", "GeminiUser:search", "GeminiUser:feed", "Vsl:mine", "Vsl:notes", "Vsl:vsl"],
            read.Servers.Select(s => s.Key));
        var servers = read.Servers.ToDictionary(s => s.Key);
        Assert.Equal("stdio python3 -I echo_server.py", servers["Project:echo"].Transport);
        Assert.Equal("${VSL_TEST_MCP_TOKEN:-none}", servers["ClaudeUser:notes"].Env["NOTES_TOKEN"]);
        Assert.Equal("HTTP http://127.0.0.1:9/mcp", servers["ClaudeLocal:db"].Transport);
        Assert.Equal("stdio docs-server --port 7", servers["Codex:docs"].Transport);
        Assert.False(servers["GeminiUser:search"].IsSse);
        // Gemini CLI's "url" is the older SSE transport.
        Assert.Equal("SSE https://example.com/sse", servers["GeminiUser:feed"].Transport);
        // The transport line never shows a header (a token) or the environment.
        Assert.DoesNotContain("abcdefgh123", servers["ClaudeLocal:db"].Transport);
        Assert.Equal(Path.Combine(workspace, ".gemini", "settings.json"), Assert.Single(read.Problems).Split(": ")[0]);

        // Without a folder, the folder's files are not read.
        Assert.Equal(["ClaudeUser:notes", "Codex:docs", "GeminiUser:search", "GeminiUser:feed", "Vsl:mine", "Vsl:notes", "Vsl:vsl"],
            new McpConfigs(configs.ConfigDirectory, null).Read().Servers.Select(s => s.Key));
        Assert.Equal(Path.Combine(home, ".codex", "config.toml"), configs.FileOf(McpOrigin.Codex));
    }

    [Fact]
    public void Servers_are_written_in_each_file_keeping_the_rest_of_it()
    {
        var (configs, home, workspace) = Everywhere();

        // Claude Code's settings keep the account, the counters, the projects.
        configs.Write(new McpServerDefinition(McpOrigin.ClaudeUser, "github") { Command = "npx", Args = ["-y", "server-github"], Env = new Dictionary<string, string> { ["GITHUB_TOKEN"] = "${GITHUB_TOKEN}" } });
        var claude = JsonNode.Parse(File.ReadAllText(Path.Combine(home, ".claude.json")))!;
        Assert.Equal("demo@example.com", claude["oauthAccount"]!["emailAddress"]!.GetValue<string>());
        Assert.Equal(12, claude["numStartups"]!.GetValue<int>());
        Assert.Equal("""{"type":"stdio","command":"npx","args":["-y","server-github"],"env":{"GITHUB_TOKEN":"${GITHUB_TOKEN}"}}""", claude["mcpServers"]!["github"]!.ToJsonString());
        Assert.NotNull(claude["projects"]![workspace]!["mcpServers"]!["db"]);

        // This folder's entry, at another URL without headers: the headers go.
        var db = configs.Read().Servers.Single(s => s.Key == "ClaudeLocal:db");
        configs.Write(db with { Url = "https://db.example.com/mcp", Headers = new Dictionary<string, string>() });
        claude = JsonNode.Parse(File.ReadAllText(Path.Combine(home, ".claude.json")))!;
        Assert.Equal("""{"type":"http","url":"https://db.example.com/mcp"}""", claude["projects"]![workspace]!["mcpServers"]!["db"]!.ToJsonString());

        // Codex: renamed; its comments, its other tables and the rest stay, the old tables (and their sub-tables) go.
        var docs = configs.Read().Servers.Single(s => s.Key == "Codex:docs");
        configs.Write(docs with { Name = "manual", Args = ["--port", "8"] }, replacing: "docs");
        var toml = File.ReadAllText(Path.Combine(home, ".codex", "config.toml"));
        Assert.StartsWith("# my Codex\nmodel = \"gpt\"\n", toml);
        Assert.DoesNotContain("mcp_servers.docs", toml);
        Assert.Contains("[profiles.fast]\nmodel = \"mini\"", toml);
        Assert.EndsWith("[mcp_servers.manual]\ncommand = \"docs-server\"\nargs = [\"--port\", \"8\"]\nenv = { \"DOCS_KEY\" = \"${VSL_TEST_MCP_TOKEN}\" }\n", toml);
        Assert.Equal("stdio docs-server --port 8", configs.Read().Servers.Single(s => s.Key == "Codex:manual").Transport);

        // Gemini CLI: streamable HTTP is httpUrl, without a type.
        configs.Write(new McpServerDefinition(McpOrigin.GeminiUser, "api") { Url = "https://api.example.com/mcp" });
        var gemini = JsonNode.Parse(File.ReadAllText(Path.Combine(home, ".gemini", "settings.json")))!;
        Assert.Equal("Dark", gemini["theme"]!.GetValue<string>());
        Assert.Equal("""{"httpUrl":"https://api.example.com/mcp"}""", gemini["mcpServers"]!["api"]!.ToJsonString());

        // Removed from the folder's .mcp.json (which may have had comments; JSON is written plainly).
        configs.Remove(configs.Read().Servers.Single(s => s.Key == "Project:echo"));
        Assert.Equal("{}", JsonNode.Parse(File.ReadAllText(Path.Combine(workspace, ".mcp.json")))!["mcpServers"]!.ToJsonString());

        // VSL's own list is the user's alone: it may hold a token.
        configs.Write(new McpServerDefinition(McpOrigin.Vsl, "x") { Command = "x" });
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(configs.FileOf(McpOrigin.Vsl)));
    }

    [Fact]
    public void Claude_sessions_get_the_servers_switched_on_for_them()
    {
        var (configs, _, _) = Everywhere();
        // By default VSL's own; never one with a name Claude Code has already (notes), nor "vsl".
        Assert.Equal(["mine"], configs.ForClaude().Select(p => p.Key));
        Assert.True(configs.IsForClaude(configs.Read().Servers.Single(s => s.Key == "ClaudeUser:notes")));

        var docs = configs.Read().Servers.Single(s => s.Key == "Codex:docs");
        Assert.False(configs.IsForClaude(docs));
        configs.SetForClaude(docs, true);
        Environment.SetEnvironmentVariable("VSL_TEST_MCP_TOKEN", "t0ken");
        try
        {
            var given = configs.ForClaude();
            Assert.Equal(["docs", "mine"], given.Select(p => p.Key));
            // ${VAR} filled in for the session (the file keeps it as written).
            Assert.Equal("""{"type":"stdio","command":"docs-server","args":["--port","7"],"env":{"DOCS_KEY":"t0ken"}}""", given["docs"]!.ToJsonString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("VSL_TEST_MCP_TOKEN", null);
        }
        Assert.Equal("none", McpConfigs.Expand("${VSL_TEST_MCP_TOKEN:-none}"));
        Assert.Equal("", McpConfigs.Expand("${VSL_TEST_MCP_TOKEN}"));

        // The CLI gets VSL's tools and that file in one --mcp-config.
        var tools = new ClaudeToolServer([], "");
        var args = VSL.Claude.ClaudeSession.Arguments(new ClaudeStartOptions("claude", "/w", Tools: tools, McpConfigFile: "/tmp/servers.json"));
        var at = args.IndexOf("--mcp-config");
        Assert.Equal([ClaudeToolServer.McpConfig, "/tmp/servers.json"], args.Skip(at + 1).Take(2));
        Assert.Equal(["--mcp-config", "/tmp/servers.json"], VSL.Claude.ClaudeSession.Arguments(new ClaudeStartOptions("claude", "/w", McpConfigFile: "/tmp/servers.json")).TakeLast(2));
    }

    [Fact]
    public void The_form_sends_what_the_schema_asks_for()
    {
        using var schema = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "query": { "type": "string", "description": "What to find." },
                "limit": { "type": "integer", "default": 10 },
                "ratio": { "type": ["number", "null"] },
                "exact": { "type": "boolean" },
                "deep": { "type": "boolean" },
                "level": { "enum": [1, 2, 3] },
                "filter": { "type": "object" },
                "note": { "anyOf": [{ "type": "null" }, { "type": "string" }] },
                "extra": {}
              },
              "required": ["query", "deep"]
            }
            """);
        var form = McpForm.ForTool(schema.RootElement);
        Assert.Equal(["query:string:Text", "limit:integer:Text", "ratio:number:Text", "exact:boolean:Flag", "deep:boolean:Flag", "level:number:Choice", "filter:object:Json", "note:string:Text", "extra:any:Json"],
            form.Fields.Select(f => $"{f.Name}:{f.Type}:{f.Kind}"));
        Assert.Equal("default: 10", form.Fields[1].Placeholder);
        Assert.Equal(["(not set)", "1", "2", "3"], form.Fields[5].Choices.Select(c => c.Label));

        // A required argument must be given; a required boolean is false until checked; the rest is left out.
        Assert.Equal("query is required.", Assert.Throws<FormatException>(() => form.Values()).Message);
        form.Set("query", "users ");
        Assert.Equal("""{"query":"users ","deep":false}""", JsonSerializer.Serialize(form.Values()));

        form.Set("limit", "5");
        form.Set("ratio", "0.5");
        form.Set("exact", "true");
        form.Set("level", "2");
        form.Set("filter", """{ "active": true }""");
        form.Set("extra", "plain words");
        Assert.Equal("""{"query":"users ","limit":5,"ratio":0.5,"exact":true,"deep":false,"level":2,"filter":{"active":true},"extra":"plain words"}""",
            JsonSerializer.Serialize(form.Values()));

        form.Set("limit", "five");
        Assert.Equal("limit is not an integer.", Assert.Throws<FormatException>(() => form.Values()).Message);
        form.Set("limit", "");
        form.Set("filter", "{ nope");
        Assert.Equal("filter is not valid JSON.", Assert.Throws<FormatException>(() => form.Values()).Message);

        using var none = JsonDocument.Parse("""{ "type": "object" }""");
        Assert.True(McpForm.ForTool(none.RootElement).IsEmpty);
    }

    // A page's log is the UI thread's: on it, as in VSL.
    [AvaloniaFact]
    public void Another_folders_server_of_the_same_name_is_another_server()
    {
        Home();
        var (first, second) = (Folder("vsl-mcp-ws"), Folder("vsl-mcp-ws"));
        Write(Path.Combine(first, ".mcp.json"), """{ "mcpServers": { "echo": { "command": "first" } } }""");
        Write(Path.Combine(second, ".mcp.json"), """{ "mcpServers": { "echo": { "command": "second" } } }""");
        var host = new TestMcpHost(first);
        var vm = new McpExplorerViewModel(host);
        var echo = vm.Find("echo")!;
        var document = vm.Open(echo);
        Assert.Equal("stdio first", echo.Transport);

        host.Workspace = second;
        vm.Refresh();
        Assert.NotSame(echo, vm.Find("echo"));
        Assert.Equal("stdio second", vm.Find("echo")!.Transport);
        Assert.DoesNotContain(document, host.Open);

        // A file changed in the same folder: the same server, defined anew.
        var again = vm.Find("echo")!;
        Write(Path.Combine(second, ".mcp.json"), """{ "mcpServers": { "echo": { "command": "third" } } }""");
        vm.Refresh();
        Assert.Same(again, vm.Find("echo"));
        Assert.Equal("stdio third", again.Transport);
        foreach (var open in host.Open)
            open.Dispose();
        vm.Dispose();
    }

    [AvaloniaFact]
    public async Task A_server_connects_when_opened_and_its_tools_run_from_a_form()
    {
        Home();
        var workspace = Folder("vsl-mcp-ws");
        Write(Path.Combine(workspace, ".mcp.json"), JsonSerializer.Serialize(new
        {
            mcpServers = new Dictionary<string, object>
            {
                ["echo"] = new { command = "python3", args = new[] { "-I", EchoServer } },
                ["broken"] = new { command = "vsl-no-such-program" },
            },
        }));
        var (docker, _, _) = FakeDocker.Create();
        var host = new TestMcpHost(workspace) { BuiltInTools = VslTools.Create(new TestToolHost(null, new Docker.DockerEngine(docker))).Tools };
        var vm = new McpExplorerViewModel(host);
        var page = new ContentControl();
        var window = new Window
        {
            Width = 1300,
            Height = 820,
            Content = new Grid { ColumnDefinitions = new("320,*"), Children = { new McpExplorerView { DataContext = vm }, page, new McpOverlay { DataContext = vm } } },
        };
        Grid.SetColumn(page, 1);
        Grid.SetColumnSpan(((Grid)window.Content).Children[2], 2);
        window.Show();
        try
        {
            Assert.Equal(["VSL (built in)", "This folder (.mcp.json)"], vm.Roots.OfType<McpGroupNode>().Select(g => g.Title));
            var builtIn = vm.Roots.OfType<McpGroupNode>().First();
            Assert.Equal($"{host.BuiltInTools.Count} tools", builtIn.CountText);
            Assert.Equal("reads", builtIn.Items.OfType<McpBuiltInToolNode>().Single(t => t.Name == "container_list").Access);

            // Expanding it connects: its tools, resources and prompts under it.
            var echo = vm.Find("echo")!;
            Assert.Equal("not connected", echo.StatusText);
            echo.IsExpanded = true;
            await WaitUntil(() => echo.IsConnected || echo.HasFailed, TimeSpan.FromSeconds(30));
            Assert.True(echo.IsConnected, echo.StatusText);
            Assert.Equal("connected · 1 tool, 1 resource, 1 prompt", echo.StatusText);
            Assert.Equal(["echo", "readme", "greet"], echo.Children.OfType<McpItemNode>().Select(c => c.Name));

            // Its tool, from the tree: a form made from its schema; Run calls it.
            var document = vm.Open(echo, "tool", "echo");
            Assert.Same(document, Assert.Single(host.Open));
            page.Content = new McpServerPageView { DataContext = document.Page };
            var tool = document.Page.SelectedTool!;
            Assert.Equal("echo", tool.Name);
            Assert.Equal("The server says it reads only.", tool.Hints);
            Assert.Equal(["text", "times", "shout", "mood", "tags"], tool.Form.Fields.Select(f => f.Name));
            tool.Form.Set("text", "hi");
            tool.Form.Set("times", "2");
            await tool.RunCommand.ExecuteAsync(null);
            Assert.Equal("hihi", tool.Result);
            Assert.False(tool.IsError);

            // A wrong value says so, and the tool is not called.
            tool.Form.Set("times", "two");
            await tool.RunCommand.ExecuteAsync(null);
            Assert.Equal(("times is not an integer.", true), (tool.Result, tool.IsError));
            tool.Form.Set("times", "1");
            tool.Form.Set("shout", "true");
            tool.Form.Set("tags", """["a", "b"]""");
            await tool.RunCommand.ExecuteAsync(null);
            Assert.Equal("HI a,b", tool.Result);
            await WaitUntil(() => false, TimeSpan.FromMilliseconds(200));
            Snapshot(window, "mcp-server");

            // The log: what VSL asked, what it answered, and the server's stderr.
            await WaitUntil(() => document.Page.Log.Document.Text.Contains("got tools/call", StringComparison.Ordinal), TimeSpan.FromSeconds(10));
            Assert.Contains("tools/call echo {\"text\":\"hi\",\"times\":2}", document.Page.Log.Document.Text);

            // A resource read, a prompt got.
            document.Page.Select("resource", "readme");
            Assert.True(document.Page.IsResources);
            await document.Page.SelectedResource!.ReadCommand.ExecuteAsync(null);
            Assert.Equal("# Echo\nIt says things back.", document.Page.SelectedResource.Content);
            document.Page.Select("prompt", "greet");
            var greet = document.Page.SelectedPrompt!;
            await greet.GetCommand.ExecuteAsync(null);
            Assert.Equal(("who is required.", true), (greet.Messages, greet.IsError));
            greet.Form.Set("who", "VSL");
            await greet.GetCommand.ExecuteAsync(null);
            Assert.Equal("User: Hello, VSL!", greet.Messages);

            // A server that does not start says why; Claude can be asked, with where it is defined and its log.
            var broken = vm.Find("broken")!;
            broken.IsExpanded = true;
            await WaitUntil(() => broken.HasFailed, TimeSpan.FromSeconds(30));
            Assert.StartsWith("failed: ", broken.StatusText);
            broken.AskClaudeCommand.Execute(null);
            Assert.Contains(Path.Combine(workspace, ".mcp.json"), Assert.Single(host.Prompts));

            // The Claude Code sessions VSL starts: Claude reads .mcp.json itself.
            Assert.True(document.Page.ForClaude);
            Assert.False(document.Page.CanSwitchForClaude);

            // Add MCP Server: the form, then the server in .mcp.json, opened.
            var adding = vm.AddServerCommand.ExecuteAsync(null);
            var form = Assert.IsType<McpServerFormDialog>(vm.Dialog);
            Assert.Equal("Give it a name without spaces, dots or quotes.", form.Problem);
            form.Name = "echo";
            Assert.StartsWith("There is a server named echo in ", form.Problem);
            form.Name = "shared";
            Assert.Equal("Write the command that starts the server.", form.Problem);
            form.CommandLine = $"python3 -I \"{EchoServer}\"";
            form.Environment = "GREETING=${USER}";
            Assert.Null(form.Problem);
            Assert.Equal(McpOrigin.Project, form.Where.Origin);
            await WaitUntil(() => false, TimeSpan.FromMilliseconds(150));
            Snapshot(window, "mcp-add");
            form.Confirm();
            await adding;
            var shared = JsonNode.Parse(File.ReadAllText(Path.Combine(workspace, ".mcp.json")))!["mcpServers"]!["shared"]!;
            Assert.Equal($$$"""{"type":"stdio","command":"python3","args":["-I","{{{EchoServer}}}"],"env":{"GREETING":"${USER}"}}""", shared.ToJsonString());
            Assert.NotNull(vm.Find("shared"));
            Assert.Equal(2, host.Open.Count);

            // Removed (it asks first): its page closes.
            var removing = vm.Find("shared")!.RemoveCommand.ExecuteAsync(null);
            Assert.IsType<Docker.ViewModels.ConfirmDialog>(vm.Dialog).Confirm();
            await removing;
            Assert.Null(vm.Find("shared"));
            Assert.Single(host.Open);
        }
        finally
        {
            foreach (var open in host.Open)
                open.Dispose();
            vm.Dispose();
            window.Close();
        }
    }
}
