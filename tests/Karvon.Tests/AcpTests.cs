using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Karvon.Claude;
using Karvon.Claude.Tools;
using Karvon.ViewModels;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>The stand-in for an Agent Client Protocol agent (TestAssets/FakeAcp/acp-agent).</summary>
public static class FakeAcp
{
    static FakeAcp() => Executable = TestTools.Command(Path.Combine(AppContext.BaseDirectory, "TestAssets", "FakeAcp", "acp-agent"));

    public static string Executable { get; }

    public static AcpAgent Agent => new("fake", "Fake Agent", "A fake", [Executable], [], [], null);

    public static (string Dir, string Log) Workspace()
    {
        var dir = Path.Combine(Path.GetTempPath(), "karvon-acp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Program.cs"), "Console.WriteLine(\"Hello\");\n");
        return (dir, dir + ".fake-acp.log");
    }

    public static List<(string Kind, JsonElement Data)> Log(string log) =>
        File.Exists(log)
            ? File.ReadAllLines(log).Select(l => JsonDocument.Parse(l).RootElement).Select(e => (e.GetProperty("kind").GetString()!, e.GetProperty("data"))).ToList()
            : [];

    public static JsonElement Last(string log, string kind) => Log(log).Last(e => e.Kind == kind).Data;

    public static List<JsonElement> Sent(string log, string method) =>
        [.. Log(log).Where(e => e.Kind == "in" && e.Data.TryGetProperty("method", out var m) && m.GetString() == method)
            .Select(e => e.Data.TryGetProperty("params", out var p) ? p : default)];
}

public class AcpSessionTests
{
    sealed class Probe(string name, ToolAccess access, string output) : IClaudeTool
    {
        public string Name => name;

        public string Description => "A probe.";

        public JsonObject InputSchema => new() { ["type"] = "object" };

        public ClaudeToolPlan Plan(JsonElement input) => new(access, name, null, access == ToolAccess.Read ? "" : $"Allow Claude to {name}?");

        public Task<ClaudeToolOutput> CallAsync(JsonElement input, CancellationToken cancellationToken) => Task.FromResult(new ClaudeToolOutput(output));
    }

    static Task<AcpSession> StartAsync(string dir, string mode = "default", ClaudeToolServer? tools = null, string? resume = null, string? model = null) =>
        AcpSession.StartAsync(FakeAcp.Agent, new AgentStartOptions(FakeAcp.Executable, dir, Model: model, PermissionMode: mode, Resume: resume, Tools: tools),
            TestContext.Current.CancellationToken);

    static async Task<List<ClaudeEvent>> TurnAsync(IAgentSession session, string prompt, Func<ClaudePermissionRequest, ClaudePermissionDecision>? answer = null,
        Func<ClaudeEvent, Task>? also = null, IEnumerable<ClaudeImage>? images = null)
    {
        var events = new List<ClaudeEvent>();
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnEvent(ClaudeEvent ev)
        {
            lock (events)
                events.Add(ev);
            if (ev is ClaudePermissionRequest request && answer is not null)
                _ = session.RespondToPermissionAsync(request.RequestId, answer(request));
            if (also is not null)
                _ = also(ev);
            if (ev is ClaudeResult)
                ended.TrySetResult();
        }
        session.Event += OnEvent;
        try
        {
            await session.SendUserMessageAsync(prompt, null, images);
            await ended.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally
        {
            session.Event -= OnEvent;
        }
        lock (events)
            return [.. events];
    }

    [Fact]
    public async Task An_ACP_agent_s_answer_streams_into_the_panel_s_words()
    {
        var (dir, log) = FakeAcp.Workspace();
        await using var session = await StartAsync(dir, model: "fake-2");

        Assert.Equal("acp-session-1", session.SessionId);
        Assert.Equal(["fake-1", "fake-2"], session.Initialization.GetProperty("models").EnumerateArray().Select(m => m.GetProperty("value").GetString()));
        Assert.Equal("fake-2", Assert.Single(FakeAcp.Sent(log, "session/set_model")).GetProperty("modelId").GetString());
        var init = Assert.Single(FakeAcp.Sent(log, "initialize"));
        Assert.True(init.GetProperty("clientCapabilities").GetProperty("fs").GetProperty("readTextFile").GetBoolean());
        // Without Karvon's tools no server is given.
        Assert.Equal(0, Assert.Single(FakeAcp.Sent(log, "session/new")).GetProperty("mcpServers").GetArrayLength());

        var events = await TurnAsync(session, "hello", images: [new ClaudeImage("image/png", "aGk=")]);

        Assert.Equal("Hello from ACP.", string.Concat(events.OfType<ClaudeBlockDelta>().Where(d => d.Kind == "text").Select(d => d.Text)));
        Assert.Contains(events, e => e is ClaudeAssistant a && a.Blocks.OfType<ClaudeTextBlock>().Any(t => t.Text == "Hello from ACP."));
        Assert.Contains(events, e => e is ClaudeAssistant a && a.Blocks.OfType<ClaudeThinkingBlock>().Any(t => t.Text == "Hmm."));
        Assert.Equal(("success", false), (Assert.Single(events.OfType<ClaudeResult>()).Subtype, events.OfType<ClaudeResult>().Single().IsError));
        Assert.Equal(["image/png"], FakeAcp.Last(log, "images").EnumerateArray().Select(m => m.GetString()));
    }

    [Fact]
    public async Task A_command_the_agent_asks_to_run_is_a_card_and_the_answer_is_its_option()
    {
        var (dir, log) = FakeAcp.Workspace();
        await using var session = await StartAsync(dir);

        ClaudePermissionRequest? asked = null;
        var events = await TurnAsync(session, "run", request =>
        {
            asked = request;
            return ClaudePermissionDecision.AllowOnce(request.Input);
        });

        Assert.Equal(("Bash", "t1", "ls"), (asked!.ToolName, asked.ToolUseId, asked.Input.GetProperty("command").GetString()));
        Assert.Equal("Yes, and don't ask again for Bash in this session", ClaudeViewModel.SessionLabel(asked.Suggestions));
        Assert.Contains(events, e => e is ClaudeAssistant a && a.Blocks.OfType<ClaudeToolUseBlock>().Any(t => t is { Id: "t1", Name: "Bash" }));
        Assert.Equal(("Program.cs", false), (events.OfType<ClaudeToolResults>().Single().Results[0].Content, events.OfType<ClaudeToolResults>().Single().Results[0].IsError));
        Assert.Equal("yes", FakeAcp.Last(log, "permission").GetProperty("optionId").GetString());

        await TurnAsync(session, "run again", request => new ClaudePermissionDecision(true, request.Input, request.Suggestions));
        Assert.Equal("always", FakeAcp.Last(log, "permission").GetProperty("optionId").GetString());
        await TurnAsync(session, "run no", _ => ClaudePermissionDecision.Deny("No."));
        Assert.Equal("no", FakeAcp.Last(log, "permission").GetProperty("optionId").GetString());

        // Bypass permissions: Karvon answers yes itself.
        await session.SetPermissionModeAsync("bypassPermissions");
        Assert.DoesNotContain(await TurnAsync(session, "run bypassed", _ => throw new InvalidOperationException("Asked.")), e => e is ClaudePermissionRequest);
        Assert.Equal("yes", FakeAcp.Last(log, "permission").GetProperty("optionId").GetString());
    }

    [Fact]
    public async Task A_secret_is_kept_from_the_agent_s_commands_and_its_file_reads()
    {
        var (dir, log) = FakeAcp.Workspace();
        await using var session = await StartAsync(dir, mode: "bypassPermissions");

        var events = await TurnAsync(session, "secret");
        Assert.DoesNotContain(events, e => e is ClaudePermissionRequest);
        Assert.Contains(events, e => e is ClaudeNotice { IsError: true });
        Assert.Equal("no", FakeAcp.Last(log, "permission").GetProperty("optionId").GetString());

        await TurnAsync(session, "readkey");
        Assert.True(FakeAcp.Last(log, "readkey").TryGetProperty("error", out _));
    }

    [Fact]
    public async Task An_edit_is_shown_as_one_with_its_diff_and_Karvon_writes_the_file_when_allowed()
    {
        var (dir, log) = FakeAcp.Workspace();
        await using var session = await StartAsync(dir);

        ClaudePermissionRequest? asked = null;
        await TurnAsync(session, "edit", request =>
        {
            asked = request;
            return ClaudePermissionDecision.AllowOnce(request.Input);
        });

        Assert.Equal("Console.WriteLine(\"Hello\");\n", FakeAcp.Last(log, "read").GetString());
        Assert.Equal("Edit", asked!.ToolName);
        Assert.Equal("Console.WriteLine(\"Hi\");\n", asked.Input.GetProperty("new_string").GetString());
        Assert.Equal("Console.WriteLine(\"Hi\");\n", File.ReadAllText(Path.Combine(dir, "Program.cs")));

        // In plan mode Karvon rejects it itself.
        File.WriteAllText(Path.Combine(dir, "Program.cs"), "Console.WriteLine(\"Hello\");\n");
        await session.SetPermissionModeAsync("plan");
        await TurnAsync(session, "edit in plan", _ => throw new InvalidOperationException("Asked."));
        Assert.Equal("no", FakeAcp.Last(log, "permission").GetProperty("optionId").GetString());
        Assert.Equal("Console.WriteLine(\"Hello\");\n", File.ReadAllText(Path.Combine(dir, "Program.cs")));
    }

    [Fact]
    public async Task The_agent_gets_Karvon_s_tools_over_HTTP_with_a_token_of_its_own()
    {
        var (dir, log) = FakeAcp.Workspace();
        var tools = KarvonTools.Create([new Probe("db_probe", ToolAccess.Read, "42 rows")]);
        await using var session = await StartAsync(dir, tools: tools);

        var server = Assert.Single(Assert.Single(FakeAcp.Sent(log, "session/new")).GetProperty("mcpServers").EnumerateArray());
        Assert.Equal(("http", "karvon"), (server.GetProperty("type").GetString(), server.GetProperty("name").GetString()));
        Assert.StartsWith("http://127.0.0.1:", server.GetProperty("url").GetString());

        await TurnAsync(session, "karvon");
        Assert.Equal(401, FakeAcp.Last(log, "unauthorized").GetInt32());
        Assert.Equal(202, FakeAcp.Last(log, "notified").GetInt32());
        Assert.Equal(["db_probe"], FakeAcp.Last(log, "tools").EnumerateArray().Select(t => t.GetString()));
        var called = FakeAcp.Last(log, "called");
        Assert.False(called.GetProperty("isError").GetBoolean());
        Assert.Equal("42 rows", called.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task A_turn_is_cancelled_a_plan_is_the_to_do_list_and_a_loaded_conversation_is_not_told_again()
    {
        var (dir, log) = FakeAcp.Workspace();
        await using (var session = await StartAsync(dir))
        {
            var events = await TurnAsync(session, "slow", also: async ev =>
            {
                if (ev is ClaudeBlockDelta)
                    await session.InterruptAsync();
            });
            Assert.Equal("error_during_execution", Assert.Single(events.OfType<ClaudeResult>()).Subtype);
            Assert.Single(FakeAcp.Sent(log, "session/cancel"));

            var plan = await TurnAsync(session, "plan");
            var todo = plan.OfType<ClaudeAssistant>().SelectMany(a => a.Blocks).OfType<ClaudeToolUseBlock>().Single(t => t.Name == "TodoWrite");
            Assert.Equal(["completed", "in_progress"], todo.Input.GetProperty("todos").EnumerateArray().Select(t => t.GetProperty("status").GetString()));
        }

        var told = new List<ClaudeEvent>();
        await using var resumed = await StartAsync(dir, resume: "acp-session-1");
        Assert.Equal("acp-session-1", Assert.Single(FakeAcp.Sent(log, "session/load")).GetProperty("sessionId").GetString());
        resumed.Event += told.Add;
        await TurnAsync(resumed, "hello");
        Assert.DoesNotContain(told, e => e is ClaudeBlockDelta { Text: "An old answer." });
    }

    [Fact]
    public void An_ACP_tool_call_is_shown_as_Claude_Code_s_tool_of_its_kind()
    {
        static (string, JsonElement) Of(object call) => AcpSession.Describe(JsonSerializer.SerializeToElement(call), null);

        Assert.Equal("Write", Of(new { kind = "edit", content = new object[] { new { type = "diff", path = "/w/a.cs", newText = "x" } } }).Item1);
        Assert.Equal("Read", Of(new { kind = "read", title = "a.cs", locations = new[] { new { path = "/w/a.cs" } } }).Item1);
        Assert.Equal("/w/a.cs", Of(new { kind = "read", locations = new[] { new { path = "/w/a.cs" } } }).Item2.GetProperty("file_path").GetString());
        Assert.Equal("Grep", Of(new { kind = "search", title = "TODO" }).Item1);
        Assert.Equal("WebFetch", Of(new { kind = "fetch", rawInput = new { url = "https://example.com" } }).Item1);
        Assert.Equal("Do things", Of(new { kind = "other", title = "Do things" }).Item1);
    }
}

public class AcpPanelTests
{
    [AvaloniaFact]
    public async Task An_ACP_agent_is_chosen_in_the_panel_and_its_edit_is_asked_as_an_edit()
    {
        var (dir, _) = FakeAcp.Workspace();
        var settings = new Karvon.Services.Settings();
        var vm = new ClaudeViewModel(settings, () => dir, () => null, () => [], () => FakeClaude.Executable) { Backends = [new AcpBackend(FakeAcp.Agent)] };

        vm.SelectedAgent = vm.Agents.Single(a => a.Id == "acp:fake");
        await WaitUntil(() => vm.Models.Any(m => m.Value == "fake-2"), TimeSpan.FromSeconds(15));
        Assert.Equal(FakeAcp.Executable, vm.CliPath);

        vm.Prompt = "edit it";
        vm.SendCommand.Execute(null);
        await WaitUntil(() => vm.Items.OfType<PermissionChatItem>().Any(), TimeSpan.FromSeconds(15));
        var card = vm.Items.OfType<PermissionChatItem>().Single();
        Assert.Equal("Allow Fake Agent to make this edit to Program.cs?", card.Question);
        Assert.Contains(card.Diff, l => l.Kind == DiffLineKind.Added && l.Text == "Console.WriteLine(\"Hi\");");

        vm.AllowCommand.Execute(card);
        await WaitUntil(() => !vm.IsRunning, TimeSpan.FromSeconds(15));
        Assert.Equal("Console.WriteLine(\"Hi\");\n", File.ReadAllText(Path.Combine(dir, "Program.cs")));
        Assert.Equal(ToolState.Done, vm.Items.OfType<ToolChatItem>().Single(t => t.ToolUseId == "t2").State);
        await vm.DisposeAsync();
    }
}
