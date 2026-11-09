using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Karvon.Claude;
using Karvon.Claude.Tools;
using Karvon.ViewModels;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>
/// An OpenAI-compatible endpoint on localhost for the tests: /v1/models, and /v1/chat/completions streaming the replies
/// queued with <see cref="Reply"/>, in order; every request's body and headers are kept.
/// </summary>
public sealed class FakeModelServer : IAsyncDisposable
{
    readonly HttpListener _listener = new();
    readonly ConcurrentQueue<JsonObject[]> _replies = new();
    readonly CancellationTokenSource _stop = new();
    readonly Task _loop;

    public FakeModelServer()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        Url = $"http://127.0.0.1:{port}/v1";
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _loop = Task.Run(LoopAsync);
    }

    public string Url { get; }

    /// <summary>The chat requests' bodies, in order.</summary>
    public List<JsonElement> Requests { get; } = [];

    /// <summary>The Authorization header of each request.</summary>
    public List<string?> Authorizations { get; } = [];

    /// <summary>The next answer: its chunks, as the endpoint streams them (null: it never comes, until the request is dropped).</summary>
    public void Reply(params JsonObject[] chunks) => _replies.Enqueue(chunks);

    public static JsonObject Text(string text) => Delta(new JsonObject { ["content"] = text });

    public static JsonObject Thinking(string text) => Delta(new JsonObject { ["reasoning"] = text });

    public static JsonObject Call(int index, string id, string name, string arguments) => Delta(new JsonObject
    {
        ["tool_calls"] = new JsonArray(new JsonObject
        {
            ["index"] = index,
            ["id"] = id,
            ["type"] = "function",
            ["function"] = new JsonObject { ["name"] = name, ["arguments"] = arguments },
        }),
    });

    /// <summary>The rest of a call's arguments, streamed in a later chunk.</summary>
    public static JsonObject MoreArguments(int index, string arguments) => Delta(new JsonObject
    {
        ["tool_calls"] = new JsonArray(new JsonObject { ["index"] = index, ["function"] = new JsonObject { ["arguments"] = arguments } }),
    });

    public static JsonObject Usage(int prompt, int output) => new()
    {
        ["choices"] = new JsonArray(),
        ["usage"] = new JsonObject { ["prompt_tokens"] = prompt, ["completion_tokens"] = output },
    };

    /// <summary>A reply that never ends (until the request is dropped).</summary>
    public static JsonObject Hang() => new() { ["hang"] = true };

    static JsonObject Delta(JsonObject delta) => new() { ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = delta }) };

    async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }
            _ = Task.Run(() => AnswerAsync(context));
        }
    }

    async Task AnswerAsync(HttpListenerContext context)
    {
        var response = context.Response;
        try
        {
            if (context.Request.Url!.AbsolutePath.EndsWith("/models", StringComparison.Ordinal))
            {
                await WriteAsync(response, "application/json", new JsonObject
                {
                    ["data"] = new JsonArray(
                        new JsonObject { ["id"] = "fake-coder", ["name"] = "Fake: Coder", ["description"] = "A model for tests. It calls tools.", ["context_length"] = 4000,
                            ["supported_parameters"] = new JsonArray("tools", "temperature") },
                        new JsonObject { ["id"] = "no-tools", ["name"] = "Fake: Talker", ["supported_parameters"] = new JsonArray("temperature") }),
                }.ToJsonString());
                return;
            }
            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var body = JsonDocument.Parse(await reader.ReadToEndAsync()).RootElement.Clone();
            lock (Requests)
            {
                Requests.Add(body);
                Authorizations.Add(context.Request.Headers["Authorization"]);
            }
            var chunks = _replies.TryDequeue(out var queued) ? queued : [Text("(no reply queued)")];
            response.ContentType = "text/event-stream";
            response.SendChunked = true;
            var output = response.OutputStream;
            foreach (var chunk in chunks)
            {
                if (chunk["hang"] is not null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), _stop.Token);
                    continue;
                }
                var bytes = Encoding.UTF8.GetBytes("data: " + chunk.ToJsonString() + "\n\n");
                await output.WriteAsync(bytes);
                await output.FlushAsync();
            }
            await output.WriteAsync(Encoding.UTF8.GetBytes("data: [DONE]\n\n"));
            response.Close();
        }
        catch (Exception ex) when (ex is HttpListenerException or IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    static async Task WriteAsync(HttpListenerResponse response, string type, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        response.ContentType = type;
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    /// <summary>The names of the functions a request offered.</summary>
    public static List<string> Functions(JsonElement request) =>
        [.. request.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("function").GetProperty("name").GetString()!)];

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        _listener.Close();
        try
        {
            await _loop;
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
        {
        }
    }
}

public class ModelAgentTests
{
    sealed class Probe(string name, ToolAccess access, string output) : IClaudeTool
    {
        public string Name => name;

        public string Description => $"The {name} probe.";

        public JsonObject InputSchema => new() { ["type"] = "object" };

        public ClaudeToolPlan Plan(JsonElement input) => new(access, name, null, access == ToolAccess.Read ? "" : $"Allow Claude to {name}?");

        public Task<ClaudeToolOutput> CallAsync(JsonElement input, CancellationToken cancellationToken) => Task.FromResult(new ClaudeToolOutput(output));
    }

    static (string Dir, string File) Workspace()
    {
        var dir = Path.Combine(Path.GetTempPath(), "karvon-model-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "Program.cs");
        File.WriteAllText(file, "Console.WriteLine(\"Hello\");\n");
        return (dir, file);
    }

    static Task<ModelSession> StartAsync(FakeModelServer server, string dir, string mode = "default", ClaudeToolServer? tools = null, bool guard = true) =>
        ModelSession.StartAsync(new ChatCompletions(ModelSession.Client, server.Url, "sk-test-key"), "Test",
            new AgentStartOptions(server.Url, dir, PermissionMode: mode, Tools: tools, GuardSecrets: guard), TestContext.Current.CancellationToken);

    static JsonElement Arguments(object value) => JsonSerializer.SerializeToElement(value);

    /// <summary>Sends a prompt (or a command) and gathers what the session tells until the turn ends; its questions are answered by <paramref name="answer"/>.</summary>
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
            if (ev is ClaudeResult)
                ended.TrySetResult();
        }
        session.Event += OnEvent;
        try
        {
            if (prompt.StartsWith('/'))
                await session.SendCommandAsync(prompt);
            else
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
    public async Task A_model_reads_a_file_with_Karvon_s_loop_and_answers_from_it()
    {
        await using var server = new FakeModelServer();
        var (dir, file) = Workspace();
        File.WriteAllText(Path.Combine(dir, "AGENTS.md"), "Use tabs.");
        server.Reply(FakeModelServer.Thinking("I should read it."), FakeModelServer.Call(0, "call_1", "Read", "{\"file_path\":"),
            FakeModelServer.MoreArguments(0, JsonSerializer.Serialize(file) + "}"), FakeModelServer.Usage(900, 20));
        server.Reply(FakeModelServer.Text("It prints "), FakeModelServer.Text("Hello."), FakeModelServer.Usage(1000, 5));
        await using var session = await StartAsync(server, dir);

        // Its models (those that call tools), the first one answering when none is chosen.
        Assert.Equal(["fake-coder"], session.Initialization.GetProperty("models").EnumerateArray().Select(m => m.GetProperty("value").GetString()));
        Assert.Equal("Fake: Coder", session.Initialization.GetProperty("models")[0].GetProperty("displayName").GetString());
        Assert.Equal("fake-coder", session.Model);

        var events = await TurnAsync(session, "What does it print?");

        var read = Assert.Single(events.OfType<ClaudeAssistant>().SelectMany(a => a.Blocks).OfType<ClaudeToolUseBlock>());
        Assert.Equal(("call_1", "Read", file), (read.Id, read.Name, read.Input.GetProperty("file_path").GetString()));
        var result = Assert.Single(events.OfType<ClaudeToolResults>().SelectMany(r => r.Results));
        Assert.Equal("     1\tConsole.WriteLine(\"Hello\");", result.Content);
        Assert.Equal("It prints Hello.", string.Concat(events.OfType<ClaudeBlockDelta>().Where(d => d.Kind == "text").Select(d => d.Text)));
        Assert.Contains(events, e => e is ClaudeAssistant a && a.Blocks.OfType<ClaudeThinkingBlock>().Any(t => t.Text == "I should read it."));
        var end = Assert.Single(events.OfType<ClaudeResult>());
        Assert.Equal(("success", false, 1005), (end.Subtype, end.IsError, end.ContextTokens));

        // The requests: the key, the tools, the system prompt (with the project's instructions), and the call's result after it.
        Assert.All(server.Authorizations, a => Assert.Equal("Bearer sk-test-key", a));
        var first = server.Requests[0];
        Assert.Equal("fake-coder", first.GetProperty("model").GetString());
        Assert.Equal(["Read", "Write", "Edit", "Glob", "Grep", ModelTools.Shell], FakeModelServer.Functions(first));
        var system = first.GetProperty("messages")[0].GetProperty("content").GetString()!;
        Assert.Contains(dir, system);
        Assert.Contains("Use tabs.", system);
        var second = server.Requests[1].GetProperty("messages");
        Assert.Equal("call_1", second[2].GetProperty("tool_calls")[0].GetProperty("id").GetString());
        Assert.Equal(("tool", "call_1"), (second[3].GetProperty("role").GetString(), second[3].GetProperty("tool_call_id").GetString()));
    }

    [Fact]
    public async Task An_edit_is_asked_on_a_card_and_made_only_when_allowed()
    {
        await using var server = new FakeModelServer();
        var (dir, file) = Workspace();
        var edit = JsonSerializer.Serialize(new { file_path = file, old_string = "Hello", new_string = "Hi" });
        server.Reply(FakeModelServer.Call(0, "call_1", "Edit", edit));
        server.Reply(FakeModelServer.Text("I could not."));
        await using var session = await StartAsync(server, dir);

        // Denied: the file stays, and the model is told.
        ClaudePermissionRequest? asked = null;
        await TurnAsync(session, "Say Hi", request =>
        {
            asked = request;
            return ClaudePermissionDecision.Deny("Not now.");
        });
        Assert.Equal(("Edit", "call_1"), (asked!.ToolName, asked.ToolUseId));
        Assert.Equal("Yes, allow all edits during this session", ClaudeViewModel.SessionLabel(asked.Suggestions));
        Assert.Equal("Console.WriteLine(\"Hello\");\n", File.ReadAllText(file));
        Assert.Equal("Not now.", server.Requests[1].GetProperty("messages")[3].GetProperty("content").GetString());

        // Allowed with "don't ask again": made, the mode changes, and the next edit is not asked.
        server.Reply(FakeModelServer.Call(0, "call_2", "Edit", edit));
        server.Reply(FakeModelServer.Call(0, "call_3", "Edit", JsonSerializer.Serialize(new { file_path = file, old_string = "Hi", new_string = "Hey" })));
        server.Reply(FakeModelServer.Text("Done."));
        var asks = 0;
        var events = await TurnAsync(session, "Say Hi now", request =>
        {
            asks++;
            return new ClaudePermissionDecision(true, request.Input, request.Suggestions);
        });
        Assert.Equal(1, asks);
        Assert.Contains(events, e => e is ClaudeModeChanged { Mode: "acceptEdits" });
        Assert.Equal("Console.WriteLine(\"Hey\");\n", File.ReadAllText(file));

        // In plan mode nothing is changed, and no one is asked.
        server.Reply(FakeModelServer.Call(0, "call_4", "Write", JsonSerializer.Serialize(new { file_path = file, content = "gone" })));
        server.Reply(FakeModelServer.Text("Planned."));
        await session.SetPermissionModeAsync("plan");
        events = await TurnAsync(session, "Rewrite it", _ => throw new InvalidOperationException("Plan mode asked."));
        Assert.True(Assert.Single(events.OfType<ClaudeToolResults>().SelectMany(r => r.Results)).IsError);
        Assert.Equal("Console.WriteLine(\"Hey\");\n", File.ReadAllText(file));
    }

    [Fact]
    public async Task A_command_is_asked_and_one_that_would_show_a_secret_is_refused_without_asking()
    {
        await using var server = new FakeModelServer();
        var (dir, _) = Workspace();
        server.Reply(FakeModelServer.Call(0, "call_1", ModelTools.Shell, "{\"command\":\"cat ~/.ssh/id_rsa\"}"),
            FakeModelServer.Call(1, "call_2", ModelTools.Shell, "{\"command\":\"echo hi\"}"));
        server.Reply(FakeModelServer.Text("Done."));
        await using var session = await StartAsync(server, dir);

        var asked = new List<string>();
        var events = await TurnAsync(session, "Look around", request =>
        {
            asked.Add(request.Input.GetProperty("command").GetString()!);
            return new ClaudePermissionDecision(true, request.Input, request.Suggestions);
        });

        Assert.Equal(["echo hi"], asked);
        var results = events.OfType<ClaudeToolResults>().SelectMany(r => r.Results).ToList();
        Assert.True(results[0].IsError);
        Assert.Equal(("hi", false), (results[1].Content, results[1].IsError));

        // Allowed for the session: the same command is not asked again.
        server.Reply(FakeModelServer.Call(0, "call_3", ModelTools.Shell, "{\"command\":\"echo hi\"}"));
        server.Reply(FakeModelServer.Text("Again."));
        await TurnAsync(session, "Again", _ => throw new InvalidOperationException("Asked again."));
    }

    [Fact]
    public async Task What_a_tool_gives_the_model_has_the_secrets_taken_out()
    {
        await using var server = new FakeModelServer();
        var (dir, _) = Workspace();
        var token = "ghp_" + new string('a', 36);
        var notes = Path.Combine(dir, "notes.txt");
        File.WriteAllText(notes, "token " + token + "\n");
        server.Reply(FakeModelServer.Call(0, "call_1", "Read", JsonSerializer.Serialize(new { file_path = notes })));
        server.Reply(FakeModelServer.Text("Read."));
        await using var session = await StartAsync(server, dir);

        await TurnAsync(session, "Read the notes");

        var sent = server.Requests[1].GetProperty("messages")[3].GetProperty("content").GetString()!;
        Assert.DoesNotContain(token, sent);
        Assert.Contains("token", sent);
    }

    [Fact]
    public async Task A_local_model_s_think_tags_are_its_thinking()
    {
        await using var server = new FakeModelServer();
        var (dir, _) = Workspace();
        server.Reply(FakeModelServer.Text("<thi"), FakeModelServer.Text("nk>Let me "), FakeModelServer.Text("see</th"), FakeModelServer.Text("ink>\nAnswer."));
        await using var session = await StartAsync(server, dir);

        var events = await TurnAsync(session, "Hi");

        Assert.Equal("Let me see", string.Concat(events.OfType<ClaudeBlockDelta>().Where(d => d.Kind == "thinking").Select(d => d.Text)));
        Assert.Equal("Answer.", string.Concat(events.OfType<ClaudeBlockDelta>().Where(d => d.Kind == "text").Select(d => d.Text)));
        // The conversation keeps the answer, not the thoughts.
        server.Reply(FakeModelServer.Text("Ok."));
        await TurnAsync(session, "And?");
        Assert.Equal("Answer.", server.Requests[1].GetProperty("messages")[2].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Karvon_s_tools_are_found_with_ToolSearch_and_go_through_the_user_s_rules()
    {
        await using var server = new FakeModelServer();
        var (dir, _) = Workspace();
        var tools = KarvonTools.Create([new Probe("db_probe", ToolAccess.Read, "42 rows"), new Probe("db_change", ToolAccess.Write, "changed"),
            new Probe("run_build", ToolAccess.Read, "Build succeeded")]);
        server.Reply(FakeModelServer.Call(0, "call_1", ModelSession.ToolSearch, "{\"query\":\"db probe\"}"));
        server.Reply(FakeModelServer.Call(0, "call_2", "mcp__karvon__db_probe", "{}"), FakeModelServer.Call(1, "call_3", "mcp__karvon__db_change", "{}"));
        server.Reply(FakeModelServer.Text("42."));
        await using var session = await StartAsync(server, dir, tools: tools);

        var events = await TurnAsync(session, "Count the rows");

        // The ones always loaded and the search at first; the found one from the next request on.
        Assert.Equal(["run_build"], FakeModelServer.Functions(server.Requests[0]).Where(f => f.StartsWith("mcp__karvon__", StringComparison.Ordinal))
            .Select(f => f["mcp__karvon__".Length..]));
        Assert.Contains(ModelSession.ToolSearch, FakeModelServer.Functions(server.Requests[0]));
        Assert.Contains("mcp__karvon__db_probe", FakeModelServer.Functions(server.Requests[1]));
        Assert.Contains("Karvon Studio", server.Requests[0].GetProperty("messages")[0].GetProperty("content").GetString());
        var results = events.OfType<ClaudeToolResults>().SelectMany(r => r.Results).ToList();
        Assert.Equal("42 rows", results[1].Content);
        // A change asks the user, and with no one to ask it does not run.
        Assert.True(results[2].IsError);
        Assert.Contains("could not ask them", results[2].Content);
    }

    [Fact]
    public async Task A_turn_stops_when_interrupted_and_a_provider_s_error_is_shown()
    {
        await using var server = new FakeModelServer();
        var (dir, _) = Workspace();
        server.Reply(FakeModelServer.Text("Thinking..."), FakeModelServer.Hang());
        await using var session = await StartAsync(server, dir);

        var events = await TurnAsync(session, "Take your time", also: async ev =>
        {
            if (ev is ClaudeBlockDelta)
                await session.InterruptAsync();
        });
        Assert.Equal("error_during_execution", Assert.Single(events.OfType<ClaudeResult>()).Subtype);

        server.Reply(new JsonObject { ["error"] = new JsonObject { ["message"] = "Rate limit exceeded" } });
        var failed = Assert.Single((await TurnAsync(session, "Again")).OfType<ClaudeResult>());
        Assert.True(failed.IsError);
        Assert.Equal("Test: Rate limit exceeded", failed.Text);
    }

    [Fact]
    public async Task Compact_puts_a_summary_in_place_of_the_conversation()
    {
        await using var server = new FakeModelServer();
        var (dir, _) = Workspace();
        server.Reply(FakeModelServer.Text("Hello there."));
        await using var session = await StartAsync(server, dir);
        await TurnAsync(session, "Hi");

        server.Reply(FakeModelServer.Text("The user said hi."));
        var events = await TurnAsync(session, "/compact");
        Assert.Contains(events, e => e is ClaudeNotice { Text: "Conversation compacted" });
        Assert.Equal("success", Assert.Single(events.OfType<ClaudeResult>()).Subtype);
        Assert.Contains("Summarize", server.Requests[1].GetProperty("messages")[0].GetProperty("content").GetString());

        server.Reply(FakeModelServer.Text("Sure."));
        await TurnAsync(session, "Go on");
        var messages = server.Requests[2].GetProperty("messages");
        Assert.Equal(4, messages.GetArrayLength());
        Assert.Contains("The user said hi.", messages[1].GetProperty("content").GetString());
        Assert.Equal("Go on", messages[3].GetProperty("content").GetString());
    }

    [Fact]
    public async Task A_provider_that_does_not_answer_says_so()
    {
        var port = new TcpListener(IPAddress.Loopback, 0);
        port.Start();
        var url = $"http://127.0.0.1:{((IPEndPoint)port.LocalEndpoint).Port}/v1";
        port.Stop();
        var (dir, _) = Workspace();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new ModelBackend(ModelProvider.Ollama)
            .StartAsync(new AgentStartOptions(url, dir), TestContext.Current.CancellationToken));
        Assert.StartsWith($"Ollama did not answer at {url}", error.Message);
    }

    [Fact]
    public void A_glob_matches_paths_as_Claude_Code_s_does()
    {
        Assert.Matches(ModelTools.GlobRegex("**/*.cs"), "src/a/b.cs");
        Assert.Matches(ModelTools.GlobRegex("**/*.cs"), "b.cs");
        Assert.DoesNotMatch(ModelTools.GlobRegex("*.cs"), "src/b.cs");
        Assert.Matches(ModelTools.GlobRegex("src/**/test_?.py"), "src/x/y/test_1.py");
    }
}

public class ModelAgentPanelTests
{
    [AvaloniaFact]
    public async Task OpenRouter_asks_for_its_key_in_the_panel_then_answers_there()
    {
        await using var server = new FakeModelServer();
        var dir = Path.Combine(Path.GetTempPath(), "karvon-model-panel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var provider = ModelProvider.OpenRouter with { Url = server.Url, EnvironmentKey = "KARVON_TEST_NO_SUCH_KEY" };
        var settings = new Karvon.Services.Settings();
        var vm = new ClaudeViewModel(settings, () => dir, () => null, () => [], () => FakeClaude.Executable) { Backends = [new ModelBackend(provider)] };

        vm.SelectedAgent = vm.Agents.Single(a => a.Id == "openrouter");
        await WaitUntil(() => !vm.IsClaude, TimeSpan.FromSeconds(10));
        Assert.True(vm.IsCliMissing);
        Assert.True(vm.NeedsKey);
        Assert.Equal("OpenRouter needs an API key", vm.MissingTitle);
        Assert.False(vm.SaveAgentKeyCommand.CanExecute(null));

        vm.AgentKey = "sk-or-test";
        vm.SaveAgentKeyCommand.Execute(null);
        Assert.Equal(server.Url, vm.CliPath);
        Assert.Equal("", vm.AgentKey);
        await WaitUntil(() => vm.Models.Any(m => m.Value == "fake-coder"), TimeSpan.FromSeconds(10));

        server.Reply(FakeModelServer.Text("Hello from OpenRouter."));
        vm.Prompt = "hi";
        vm.SendCommand.Execute(null);
        await WaitUntil(() => vm.Items.OfType<TextChatItem>().Any(t => t.Text == "Hello from OpenRouter.") && !vm.IsRunning, TimeSpan.FromSeconds(10));
        Assert.Equal("Bearer sk-or-test", server.Authorizations.Single());
        Assert.Equal("fake-coder", server.Requests.Single().GetProperty("model").GetString());
        await vm.DisposeAsync();
    }
}
