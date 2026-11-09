using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Karvon.Claude;
using Karvon.Claude.Tools;
using Karvon.ViewModels;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>The stand-in for Codex's app-server (TestAssets/FakeCodex/codex), found through KARVON_CODEX_PATH.</summary>
public static class FakeCodex
{
    static FakeCodex()
    {
        Executable = TestTools.Command(Path.Combine(AppContext.BaseDirectory, "TestAssets", "FakeCodex", "codex"));
        Environment.SetEnvironmentVariable("KARVON_CODEX_PATH", Executable);
    }

    public static string Executable { get; }

    /// <summary>A folder with a Program.cs for the fake to work in, and the log of what it received.</summary>
    public static (string Dir, string Log) Workspace()
    {
        var dir = Path.Combine(Path.GetTempPath(), "karvon-codex-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Program.cs"), "Console.WriteLine(\"Hello\");\n");
        return (dir, dir + ".fake-codex.log");
    }

    /// <summary>Entries the fake logged: ("in" | "decision" | "tool", data).</summary>
    public static List<(string Kind, JsonElement Data)> Log(string log) =>
        File.Exists(log)
            ? File.ReadAllLines(log).Select(l => JsonDocument.Parse(l).RootElement).Select(e => (e.GetProperty("kind").GetString()!, e.GetProperty("data"))).ToList()
            : [];

    /// <summary>The params of the requests Karvon sent with <paramref name="method"/>.</summary>
    public static List<JsonElement> Sent(string log, string method) =>
        [.. Log(log).Where(e => e.Kind == "in" && e.Data.TryGetProperty("method", out var m) && m.GetString() == method)
            .Select(e => e.Data.TryGetProperty("params", out var p) ? p : default)];
}

public class CodexSessionTests
{
    sealed class Probe(string name, ToolAccess access, string output) : IClaudeTool
    {
        public string Name => name;

        public string Description => "A probe.";

        public JsonObject InputSchema => new() { ["type"] = "object" };

        public ClaudeToolPlan Plan(JsonElement input) => new(access, name, null, access == ToolAccess.Read ? "" : $"Allow Claude to {name}?");

        public Task<ClaudeToolOutput> CallAsync(JsonElement input, CancellationToken cancellationToken) => Task.FromResult(new ClaudeToolOutput(output));
    }

    /// <summary>Sends a prompt and gathers what the session tells until the turn ends; its questions are answered by <paramref name="answer"/>.</summary>
    static async Task<List<ClaudeEvent>> TurnAsync(IAgentSession session, string prompt, Func<ClaudePermissionRequest, ClaudePermissionDecision>? answer = null,
        Func<ClaudeEvent, Task>? also = null)
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
            if (ev is ClaudeResult or ClaudeExited)
                ended.TrySetResult();
        }
        session.Event += OnEvent;
        try
        {
            await session.SendUserMessageAsync(prompt);
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
    public async Task Codex_streams_its_answer_and_ends_the_turn_in_the_panel_s_words()
    {
        var (dir, log) = FakeCodex.Workspace();
        await using var session = await CodexSession.StartAsync(new AgentStartOptions(FakeCodex.Executable, dir), TestContext.Current.CancellationToken);

        // Its models (the hidden one left out) and its account, in Claude Code's shape.
        Assert.Equal(["gpt-fake"], session.Initialization.GetProperty("models").EnumerateArray().Select(m => m.GetProperty("value").GetString()));
        Assert.Equal(["low", "high"], session.Initialization.GetProperty("models")[0].GetProperty("supportedEffortLevels").EnumerateArray().Select(l => l.GetString()));
        Assert.Equal("ChatGPT Plus", session.Initialization.GetProperty("account").GetProperty("subscriptionType").GetString());
        Assert.Equal("thread-1", session.SessionId);
        var start = Assert.Single(FakeCodex.Sent(log, "thread/start"));
        Assert.Equal(dir, start.GetProperty("cwd").GetString());
        Assert.Equal("untrusted", start.GetProperty("approvalPolicy").GetString());
        Assert.Equal("workspace-write", start.GetProperty("sandbox").GetString());
        // Without Karvon's tools, none are given.
        Assert.False(start.TryGetProperty("dynamicTools", out _));

        var events = await TurnAsync(session, "hello");

        Assert.Equal("Hello from Codex.", string.Concat(events.OfType<ClaudeBlockDelta>().Where(d => d.Kind == "text").Select(d => d.Text)));
        Assert.Contains(events, e => e is ClaudeBlockStart { BlockType: "text", MessageId: "msg-1" });
        Assert.Contains(events, e => e is ClaudeAssistant a && a.Blocks.OfType<ClaudeTextBlock>().Any(t => t.Text == "Hello from Codex."));
        Assert.Contains(events, e => e is ClaudeAssistant a && a.Blocks.OfType<ClaudeThinkingBlock>().Any(t => t.Text == "Thinking it over."));
        var result = Assert.Single(events.OfType<ClaudeResult>());
        Assert.Equal("success", result.Subtype);
        Assert.False(result.IsError);
        Assert.Equal(1234, result.ContextTokens);
        Assert.Equal("thread-1", result.SessionId);
        var turn = Assert.Single(FakeCodex.Sent(log, "turn/start"));
        Assert.Equal("thread-1", turn.GetProperty("threadId").GetString());
        Assert.Equal("hello", turn.GetProperty("input")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task A_command_Codex_wants_to_run_is_asked_on_a_card_and_the_answer_goes_back()
    {
        var (dir, log) = FakeCodex.Workspace();
        await using var session = await CodexSession.StartAsync(new AgentStartOptions(FakeCodex.Executable, dir), TestContext.Current.CancellationToken);

        ClaudePermissionRequest? asked = null;
        var events = await TurnAsync(session, "bash", request =>
        {
            asked = request;
            return ClaudePermissionDecision.AllowOnce(request.Input);
        });

        Assert.NotNull(asked);
        Assert.Equal("Bash", asked.ToolName);
        Assert.Equal("ls", asked.Input.GetProperty("command").GetString());
        Assert.Equal("cmd-1", asked.ToolUseId);
        // Its "don't ask again" is Codex's own: this command, for the conversation.
        Assert.Equal("Yes, and don't ask again for Bash(ls) in this session", ClaudeViewModel.SessionLabel(asked.Suggestions));
        Assert.Contains(events, e => e is ClaudeAssistant a && a.Blocks.OfType<ClaudeToolUseBlock>().Any(t => t.Id == "cmd-1" && t.Name == "Bash"));
        var ran = Assert.Single(events.OfType<ClaudeToolResults>().SelectMany(r => r.Results));
        Assert.Equal("Program.cs\n", ran.Content);
        Assert.False(ran.IsError);

        // Allowed for the session, and denied while stopping the turn: Codex's own answers.
        await TurnAsync(session, "bash again", request => new ClaudePermissionDecision(true, request.Input, request.Suggestions));
        await TurnAsync(session, "bash once more", _ => new ClaudePermissionDecision(false, Message: "No.", Interrupt: true));
        var denied = await TurnAsync(session, "bash last", _ => ClaudePermissionDecision.Deny("No."));
        Assert.Equal(["accept", "acceptForSession", "cancel", "decline"],
            FakeCodex.Log(log).Where(e => e.Kind == "decision").Select(e => e.Data.GetString()));
        Assert.True(Assert.Single(denied.OfType<ClaudeToolResults>().SelectMany(r => r.Results)).IsError);
    }

    [Fact]
    public async Task A_command_that_would_show_a_secret_is_declined_without_asking()
    {
        var (dir, log) = FakeCodex.Workspace();
        await using var session = await CodexSession.StartAsync(new AgentStartOptions(FakeCodex.Executable, dir), TestContext.Current.CancellationToken);

        var events = await TurnAsync(session, "secret", _ => ClaudePermissionDecision.AllowOnce(default));

        Assert.DoesNotContain(events, e => e is ClaudePermissionRequest);
        Assert.Contains(events, e => e is ClaudeNotice { IsError: true });
        Assert.Equal(["decline"], FakeCodex.Log(log).Where(e => e.Kind == "decision").Select(e => e.Data.GetString()));

        // Unguarded (the user turned it off), it is asked as any command.
        var (other, _) = FakeCodex.Workspace();
        await using var unguarded = await CodexSession.StartAsync(new AgentStartOptions(FakeCodex.Executable, other, GuardSecrets: false), TestContext.Current.CancellationToken);
        Assert.Contains(await TurnAsync(unguarded, "secret", _ => ClaudePermissionDecision.Deny("No.")), e => e is ClaudePermissionRequest);
    }

    [Fact]
    public async Task Codex_gets_Karvon_s_tools_and_its_calls_go_through_the_user_s_rules()
    {
        var (dir, log) = FakeCodex.Workspace();
        var tools = KarvonTools.Create([new Probe("db_probe", ToolAccess.Read, "42 rows"), new Probe("db_change", ToolAccess.Write, "changed")]);
        await using var session = await CodexSession.StartAsync(new AgentStartOptions(FakeCodex.Executable, dir, Tools: tools), TestContext.Current.CancellationToken);

        var start = Assert.Single(FakeCodex.Sent(log, "thread/start"));
        var space = Assert.Single(start.GetProperty("dynamicTools").EnumerateArray());
        Assert.Equal("karvon", space.GetProperty("name").GetString());
        Assert.Equal(["db_probe", "db_change"], space.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()));
        Assert.True(space.GetProperty("tools")[0].GetProperty("deferLoading").GetBoolean());
        // What Claude Code is told of them, in Codex's names.
        var instructions = start.GetProperty("developerInstructions").GetString()!;
        Assert.Contains("the karvon namespace's", instructions);
        Assert.DoesNotContain("mcp__karvon__", instructions);

        var events = await TurnAsync(session, "karvon db_probe");
        Assert.Contains(events, e => e is ClaudeAssistant a && a.Blocks.OfType<ClaudeToolUseBlock>().Any(t => t.Name == "mcp__karvon__db_probe"));
        var answered = FakeCodex.Log(log).Last(e => e.Kind == "tool").Data;
        Assert.True(answered.GetProperty("success").GetBoolean());
        Assert.Equal("42 rows", answered.GetProperty("contentItems")[0].GetProperty("text").GetString());

        // A change asks the user; with no one to ask it does not run.
        await TurnAsync(session, "karvon db_change");
        answered = FakeCodex.Log(log).Last(e => e.Kind == "tool").Data;
        Assert.False(answered.GetProperty("success").GetBoolean());
        Assert.Contains("could not ask them", answered.GetProperty("contentItems")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task The_panel_s_modes_become_Codex_s_approval_policy_and_sandbox_for_the_next_turn()
    {
        Assert.Equal(("untrusted", "workspace-write"), (CodexSession.ApprovalPolicy("default"), CodexSession.Sandbox("default")));
        Assert.Equal(("on-request", "workspace-write"), (CodexSession.ApprovalPolicy("acceptEdits"), CodexSession.Sandbox("acceptEdits")));
        Assert.Equal(("on-request", "read-only"), (CodexSession.ApprovalPolicy("plan"), CodexSession.Sandbox("plan")));
        Assert.Equal(("never", "danger-full-access"), (CodexSession.ApprovalPolicy("bypassPermissions"), CodexSession.Sandbox("bypassPermissions")));
        Assert.DoesNotContain("auto", CodexSession.Modes);

        var (dir, log) = FakeCodex.Workspace();
        await using var session = await CodexSession.StartAsync(new AgentStartOptions(FakeCodex.Executable, dir, Model: "gpt-fake", PermissionMode: "plan", Effort: "high"),
            TestContext.Current.CancellationToken);
        Assert.Equal("read-only", Assert.Single(FakeCodex.Sent(log, "thread/start")).GetProperty("sandbox").GetString());
        await TurnAsync(session, "hello");
        await session.SetPermissionModeAsync("bypassPermissions");
        await session.SetModelAsync("gpt-other");
        await session.SetEffortAsync("low");
        await TurnAsync(session, "hello");

        var turns = FakeCodex.Sent(log, "turn/start");
        Assert.Equal(["on-request", "never"], turns.Select(t => t.GetProperty("approvalPolicy").GetString()));
        Assert.Equal(["readOnly", "dangerFullAccess"], turns.Select(t => t.GetProperty("sandboxPolicy").GetProperty("type").GetString()));
        Assert.Equal(["gpt-fake", "gpt-other"], turns.Select(t => t.GetProperty("model").GetString()));
        Assert.Equal(["high", "low"], turns.Select(t => t.GetProperty("effort").GetString()));
    }

    [Fact]
    public async Task Codex_s_turn_stops_when_interrupted_and_a_failed_one_says_why()
    {
        var (dir, log) = FakeCodex.Workspace();
        await using var session = await CodexSession.StartAsync(new AgentStartOptions(FakeCodex.Executable, dir), TestContext.Current.CancellationToken);

        var events = await TurnAsync(session, "slow", also: async ev =>
        {
            if (ev is ClaudeBlockStart)
                await session.InterruptAsync();
        });
        Assert.Equal("error_during_execution", Assert.Single(events.OfType<ClaudeResult>()).Subtype);
        Assert.Equal("turn-1", Assert.Single(FakeCodex.Sent(log, "turn/interrupt")).GetProperty("turnId").GetString());

        var failed = Assert.Single((await TurnAsync(session, "fail")).OfType<ClaudeResult>());
        Assert.True(failed.IsError);
        Assert.Equal("The model is overloaded.", failed.Text);
    }

    [Fact]
    public async Task Codex_s_plan_is_the_panel_s_to_do_list()
    {
        var (dir, _) = FakeCodex.Workspace();
        await using var session = await CodexSession.StartAsync(new AgentStartOptions(FakeCodex.Executable, dir), TestContext.Current.CancellationToken);

        var events = await TurnAsync(session, "plan");

        var todo = Assert.Single(events.OfType<ClaudeAssistant>().SelectMany(a => a.Blocks).OfType<ClaudeToolUseBlock>(), t => t.Name == "TodoWrite");
        Assert.Equal(["completed", "in_progress"], todo.Input.GetProperty("todos").EnumerateArray().Select(t => t.GetProperty("status").GetString()));
        Assert.Equal("Read the code", todo.Input.GetProperty("todos")[0].GetProperty("content").GetString());
    }

    [Fact]
    public void A_patch_s_unified_diff_is_read_into_the_card_s_lines()
    {
        var lines = UnifiedDiff.Parse("--- a/x\n+++ b/x\n@@ -1,3 +1,3 @@\n a\n-b\n+c\n d\n@@ -10 +10 @@\n-y\n+z\n\\ No newline at end of file\n");
        Assert.Equal([(DiffLineKind.Context, "a"), (DiffLineKind.Removed, "b"), (DiffLineKind.Added, "c"), (DiffLineKind.Context, "d"),
            (DiffLineKind.Gap, "..."), (DiffLineKind.Removed, "y"), (DiffLineKind.Added, "z")], lines.Select(l => (l.Kind, l.Text)));
        // A new file's text has no hunks: every line is added.
        Assert.All(UnifiedDiff.Parse("one\ntwo\n"), l => Assert.Equal(DiffLineKind.Added, l.Kind));
    }
}

public class CodexPanelTests
{
    [AvaloniaFact]
    public async Task Codex_answers_in_the_Agents_panel_and_its_patch_is_asked_and_shown_as_an_edit()
    {
        var (dir, log) = FakeCodex.Workspace();
        var settings = new Karvon.Services.Settings();
        var vm = new ClaudeViewModel(settings, () => dir, () => null, () => [], () => null) { Backends = [new CodexBackend()] };
        Assert.False(vm.IsClaude);
        Assert.Equal(FakeCodex.Executable, vm.CliPath);
        Assert.Equal("Codex", vm.AgentName);
        // Codex has no auto mode.
        Assert.DoesNotContain(vm.Modes, m => m.Value == "auto");
        vm.WarmUp();
        await WaitUntil(() => vm.Models.Any(m => m.Value == "gpt-fake"), TimeSpan.FromSeconds(15));

        vm.Prompt = "patch Program.cs";
        vm.SendCommand.Execute(null);
        await WaitUntil(() => vm.Items.OfType<PermissionChatItem>().Any(), TimeSpan.FromSeconds(15));
        var card = vm.Items.OfType<PermissionChatItem>().Single();
        Assert.Equal("Allow Codex to make this edit to Program.cs?", card.Question);
        Assert.Equal("No, and tell Codex what to do differently", card.DenyPlaceholder);
        Assert.Contains(card.Diff, l => l.Kind == DiffLineKind.Added && l.Text == "Console.WriteLine(\"Hi\");");
        Assert.Contains(card.Diff, l => l.Kind == DiffLineKind.Removed && l.Text == "Console.WriteLine(\"Hello\");");

        vm.AllowCommand.Execute(card);
        await WaitUntil(() => !vm.IsRunning, TimeSpan.FromSeconds(15));
        Assert.Equal("Console.WriteLine(\"Hi\");\n", File.ReadAllText(Path.Combine(dir, "Program.cs")));
        var tool = vm.Items.OfType<ToolChatItem>().Single(t => t.Name == "Patch");
        Assert.Equal(ToolState.Done, tool.State);
        Assert.Equal("1 addition, 1 removal", tool.Summary);
        Assert.Equal(["accept"], FakeCodex.Log(log).Where(e => e.Kind == "decision").Select(e => e.Data.GetString()));
        await vm.DisposeAsync();
    }
}
