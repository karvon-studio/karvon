using Karvon.Claude;
using Karvon.ViewModels;

namespace Karvon.Tests;

/// <summary>Claude on the user's Anthropic API key, for real: only with a key given (each run costs a few requests).</summary>
public class LiveClaudeApiTests
{
    [Fact]
    public async Task Live_Claude_on_an_API_key_lists_its_models_reads_a_file_with_a_tool_and_answers()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("KARVON_LIVE_ANTHROPIC_KEY") is { Length: > 0 },
            "Set KARVON_LIVE_ANTHROPIC_KEY to an Anthropic API key to talk to Claude (it costs a few requests).");
        var ct = TestContext.Current.CancellationToken;
        var provider = ModelProvider.Claude with { EnvironmentKey = "KARVON_LIVE_ANTHROPIC_KEY" };
        var backend = new ModelBackend(provider);
        Assert.Equal(provider.Url, backend.Find());

        // Its models, the current Opus first.
        var models = await backend.ModelsAsync(provider.Url, Path.GetTempPath(), ct);
        Assert.Equal(AnthropicChat.DefaultModel, models[0].Id);

        // A turn with a tool: Claude thinks, reads the file, and answers from it (its signed thinking sent back with the call's result).
        var (dir, _) = ModelAgentTests.Workspace();
        await using var session = await backend.StartAsync(new AgentStartOptions(provider.Url, dir, Effort: "low"), ct);
        Assert.Equal(AnthropicChat.DefaultModel, ((ModelSession)session).Model);
        var events = await ModelAgentTests.TurnAsync(session, "Read Program.cs with your Read tool, then say in one short sentence what it prints.",
            timeout: TimeSpan.FromMinutes(3));

        var end = Assert.Single(events.OfType<ClaudeResult>());
        Assert.False(end.IsError, end.Text);
        Assert.Contains(events.OfType<ClaudeAssistant>().SelectMany(a => a.Blocks).OfType<ClaudeToolUseBlock>(), b => b.Name == "Read");
        Assert.Contains("Hello", string.Concat(events.OfType<ClaudeBlockDelta>().Where(d => d.Kind == "text").Select(d => d.Text)));
        Assert.True(end.ContextTokens > 0);
    }
}

/// <summary>Karvon's strict sandbox in the real Claude Code, on an Anthropic API key: only with a key given (a request or two).</summary>
public class LiveStrictSandboxTests
{
    [Fact]
    public async Task Live_Claude_Codes_shell_in_the_strict_sandbox_reads_the_project_and_not_the_home_folder()
    {
        var key = Environment.GetEnvironmentVariable("KARVON_LIVE_ANTHROPIC_KEY");
        Assert.SkipUnless(key is { Length: > 0 } && ClaudeSandbox.Problem() is null,
            "Set KARVON_LIVE_ANTHROPIC_KEY to an Anthropic API key (and have bubblewrap) to run Claude Code in Karvon's sandbox.");
        var claude = Karvon.Workspace.TaskVariables.Find("claude", Environment.GetEnvironmentVariable("PATH") ?? "", Environment.CurrentDirectory);
        Assert.SkipUnless(claude is not null, "Claude Code is not installed.");
        var project = Directory.CreateTempSubdirectory("karvon-strict-").FullName;
        File.WriteAllText(Path.Combine(project, "Program.cs"), "Console.WriteLine(\"Hello\");");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var sandbox = new ProjectSandbox(project, extraReadable: ClaudeSandbox.ShellSnapshots(home));
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", key);
        try
        {
            await using var session = await ClaudeSession.StartAsync(new ClaudeStartOptions(claude!, project, Confine: sandbox), TestContext.Current.CancellationToken);
            var events = await ModelAgentTests.TurnAsync(session,
                "Run exactly this one Bash command, then reply with its output only: cat Program.cs; "
                + "(ls $HOME >/dev/null 2>&1 && ls -A $HOME | head -c 1 | grep -q . && echo HOME-SEEN) || echo HOME-HIDDEN; "
                + "(ls $HOME/.ssh >/dev/null 2>&1 && echo SSH-SEEN) || echo SSH-HIDDEN; echo made > out.txt && echo WROTE",
                _ => new ClaudePermissionDecision(true), timeout: TimeSpan.FromMinutes(3));

            var output = string.Join("\n", events.OfType<ClaudeToolResults>().SelectMany(r => r.Results).Select(r => r.Content));
            Assert.Contains("Hello", output);
            Assert.Contains("HOME-HIDDEN", output);
            Assert.Contains("SSH-HIDDEN", output);
            Assert.Contains("WROTE", output);
            Assert.True(File.Exists(Path.Combine(project, "out.txt")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
        }
    }
}

/// <summary>Anthropic's API on localhost for the tests: /v1/models, and /v1/messages streaming the queued answers (server-sent events).</summary>
public sealed class FakeAnthropicServer : IAsyncDisposable
{
    readonly System.Net.HttpListener _listener = new();
    readonly System.Collections.Concurrent.ConcurrentQueue<string[]> _answers = new();
    readonly Task _loop;

    public FakeAnthropicServer()
    {
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        Url = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(Url + "/");
        _listener.Start();
        _loop = Task.Run(LoopAsync);
    }

    public string Url { get; }

    /// <summary>Each /v1/messages request: its body and headers.</summary>
    public List<(System.Text.Json.JsonElement Body, System.Collections.Specialized.NameValueCollection Headers)> Requests { get; } = [];

    /// <summary>The next answer, as its events (type and data, the data as JSON).</summary>
    public void Answer(params string[] events) => _answers.Enqueue(events);

    public static string Event(string type, string data) => $"event: {type}\ndata: {data}\n\n";

    public static string Start(int input, int cacheRead = 0) => Event("message_start",
        """{"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","model":"claude-opus-5-5","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":"""
        + input + ""","output_tokens":1,"cache_read_input_tokens":""" + cacheRead + ""","cache_creation_input_tokens":0}}}""");

    public static string BlockStart(int index, string block) => Event("content_block_start", $$"""{"type":"content_block_start","index":{{index}},"content_block":{{block}}}""");

    public static string Delta(int index, string delta) => Event("content_block_delta", $$"""{"type":"content_block_delta","index":{{index}},"delta":{{delta}}}""");

    public static string BlockStop(int index) => Event("content_block_stop", $$"""{"type":"content_block_stop","index":{{index}}}""");

    public static string End(string stop, int output, string? details = null) => Event("message_delta",
        """{"type":"message_delta","delta":{"stop_reason":""" + "\"" + stop + "\",\"stop_sequence\":null" + (details is null ? "" : ",\"stop_details\":" + details)
        + """},"usage":{"output_tokens":""" + output + "}}")
        + Event("message_stop", """{"type":"message_stop"}""");

    async Task LoopAsync()
    {
        while (_listener.IsListening)
        {
            System.Net.HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is System.Net.HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }
            var response = context.Response;
            try
            {
                if (context.Request.Url!.AbsolutePath == "/v1/models")
                {
                    await Write(response, "application/json", """
                        {"data":[
                          {"type":"model","id":"claude-haiku-5-5","display_name":"Claude Haiku 5.5","created_at":"2026-10-01T00:00:00Z","max_input_tokens":1000000,"max_tokens":128000,
                           "capabilities":{"thinking":{"supported":true,"types":{"adaptive":{"supported":true}}},"effort":{"supported":true,"low":{"supported":true}}}},
                          {"type":"model","id":"claude-opus-5-5","display_name":"Claude Opus 5.5","created_at":"2026-09-01T00:00:00Z","max_input_tokens":1000000,"max_tokens":128000,
                           "capabilities":{"thinking":{"supported":true,"types":{"adaptive":{"supported":true}}},"effort":{"supported":true,"low":{"supported":true}}}}
                        ],"has_more":false,"first_id":"claude-haiku-5-5","last_id":"claude-opus-5-5"}
                        """);
                    continue;
                }
                using var reader = new StreamReader(context.Request.InputStream, System.Text.Encoding.UTF8);
                var body = System.Text.Json.JsonDocument.Parse(await reader.ReadToEndAsync()).RootElement.Clone();
                lock (Requests)
                    Requests.Add((body, context.Request.Headers));
                var events = _answers.TryDequeue(out var queued) ? queued : [Start(1), End("end_turn", 1)];
                await Write(response, "text/event-stream", string.Concat(events));
            }
            catch (Exception ex) when (ex is System.Net.HttpListenerException or IOException)
            {
            }
        }
    }

    static async Task Write(System.Net.HttpListenerResponse response, string type, string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        response.ContentType = type;
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    public async ValueTask DisposeAsync()
    {
        _listener.Stop();
        _listener.Close();
        await _loop.WaitAsync(TimeSpan.FromSeconds(2)).ContinueWith(_ => { });
    }
}

public class ClaudeApiTests
{
    [Fact]
    public async Task Claude_on_an_API_key_thinks_calls_a_tool_and_gets_its_signed_thinking_back_with_the_result()
    {
        await using var server = new FakeAnthropicServer();
        var ct = TestContext.Current.CancellationToken;
        var backend = new ModelBackend(ModelProvider.Claude with { Url = server.Url, EnvironmentKey = "KARVON_TEST_NO_SUCH_KEY" });
        Assert.Null(backend.Find());
        Assert.True(backend.SetKey("sk-ant-test"));

        // Its models: the current Opus first, whatever the API's order; their windows.
        var models = await new AnthropicChat(server.Url, "sk-ant-test").ModelsAsync(ct);
        Assert.Equal([("claude-opus-5-5", 1000000), ("claude-haiku-5-5", 1000000)], models.Select(m => (m.Id, m.Context ?? 0)));

        var (dir, file) = ModelAgentTests.Workspace();
        server.Answer(
            FakeAnthropicServer.Start(900, cacheRead: 600),
            FakeAnthropicServer.BlockStart(0, """{"type":"thinking","thinking":"","signature":""}"""),
            FakeAnthropicServer.Delta(0, """{"type":"thinking_delta","thinking":"I should read it."}"""),
            FakeAnthropicServer.Delta(0, """{"type":"signature_delta","signature":"sig-123"}"""),
            FakeAnthropicServer.BlockStop(0),
            FakeAnthropicServer.BlockStart(1, """{"type":"tool_use","id":"toolu_1","name":"Read","input":{}}"""),
            FakeAnthropicServer.Delta(1, """{"type":"input_json_delta","partial_json":"{\"file_path\":"}"""),
            FakeAnthropicServer.Delta(1, $$"""{"type":"input_json_delta","partial_json":{{System.Text.Json.JsonSerializer.Serialize(System.Text.Json.JsonSerializer.Serialize(file) + "}")}}}"""),
            FakeAnthropicServer.BlockStop(1),
            FakeAnthropicServer.End("tool_use", 30));
        server.Answer(
            FakeAnthropicServer.Start(1000),
            FakeAnthropicServer.BlockStart(0, """{"type":"text","text":""}"""),
            FakeAnthropicServer.Delta(0, """{"type":"text_delta","text":"It prints Hello."}"""),
            FakeAnthropicServer.BlockStop(0),
            FakeAnthropicServer.End("end_turn", 5));
        await using var session = await backend.StartAsync(new AgentStartOptions(server.Url, dir, Effort: "low"), ct);

        var events = await ModelAgentTests.TurnAsync(session, "What does it print?");

        var read = Assert.Single(events.OfType<ClaudeAssistant>().SelectMany(a => a.Blocks).OfType<ClaudeToolUseBlock>());
        Assert.Equal(("toolu_1", "Read", file), (read.Id, read.Name, read.Input.GetProperty("file_path").GetString()));
        Assert.Contains(events, e => e is ClaudeAssistant a && a.Blocks.OfType<ClaudeThinkingBlock>().Any(t => t.Text == "I should read it."));
        Assert.Equal("It prints Hello.", string.Concat(events.OfType<ClaudeBlockDelta>().Where(d => d.Kind == "text").Select(d => d.Text)));
        var end = Assert.Single(events.OfType<ClaudeResult>());
        Assert.Equal(("success", false, 1005), (end.Subtype, end.IsError, end.ContextTokens));

        // The first request: the key, the system prompt, adaptive thinking shown in summary, the panel's effort, the
        // prompt cached, the tools' input streamed, and Anthropic's fallback for a refusal.
        var (first, headers) = server.Requests[0];
        // The key given, not a credential the machine has elsewhere (the SDK sends it as a bearer token).
        Assert.True(headers["x-api-key"] == "sk-ant-test" || headers["Authorization"] == "Bearer sk-ant-test");
        Assert.Contains(FakeAnthropicServerBeta, headers["anthropic-beta"] ?? "");
        Assert.Equal("claude-opus-5-5", first.GetProperty("model").GetString());
        Assert.Contains(dir, first.GetProperty("system").GetString());
        Assert.Equal(("adaptive", "summarized"), (first.GetProperty("thinking").GetProperty("type").GetString(), first.GetProperty("thinking").GetProperty("display").GetString()));
        Assert.Equal("low", first.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.Equal("ephemeral", first.GetProperty("cache_control").GetProperty("type").GetString());
        Assert.Equal("default", first.GetProperty("fallbacks").GetString());
        Assert.All(first.GetProperty("tools").EnumerateArray(), t => Assert.True(t.GetProperty("eager_input_streaming").GetBoolean()));
        Assert.Contains(first.GetProperty("tools").EnumerateArray(), t => t.GetProperty("name").GetString() == "Read");

        // The second: Claude's answer sent back as it was, its thinking signed, then the call's result in the user's turn.
        var messages = server.Requests[1].Body.GetProperty("messages");
        var answer = messages[1];
        Assert.Equal("assistant", answer.GetProperty("role").GetString());
        var thinking = answer.GetProperty("content")[0];
        Assert.Equal(("thinking", "I should read it.", "sig-123"),
            (thinking.GetProperty("type").GetString(), thinking.GetProperty("thinking").GetString(), thinking.GetProperty("signature").GetString()));
        var call = answer.GetProperty("content")[1];
        Assert.Equal(("tool_use", "toolu_1", file), (call.GetProperty("type").GetString(), call.GetProperty("id").GetString(), call.GetProperty("input").GetProperty("file_path").GetString()));
        var result = messages[2].GetProperty("content")[0];
        Assert.Equal(("user", "tool_result", "toolu_1"), (messages[2].GetProperty("role").GetString(), result.GetProperty("type").GetString(), result.GetProperty("tool_use_id").GetString()));
        Assert.Contains("Console.WriteLine", result.GetProperty("content").GetString());
    }

    const string FakeAnthropicServerBeta = "server-side-fallback-2026-07-01";

    [Fact]
    public async Task A_request_Claude_declines_ends_the_turn_saying_so_and_no_call_cut_off_by_the_limit_is_run()
    {
        await using var server = new FakeAnthropicServer();
        var ct = TestContext.Current.CancellationToken;
        var backend = new ModelBackend(ModelProvider.Claude with { Url = server.Url, EnvironmentKey = "KARVON_TEST_NO_SUCH_KEY" });
        backend.SetKey("sk-ant-test");
        var (dir, _) = ModelAgentTests.Workspace();
        server.Answer(
            FakeAnthropicServer.Start(500),
            FakeAnthropicServer.BlockStart(0, """{"type":"text","text":""}"""),
            FakeAnthropicServer.Delta(0, """{"type":"text_delta","text":"Here is how"}"""),
            FakeAnthropicServer.BlockStop(0),
            FakeAnthropicServer.End("refusal", 3, """{"type":"refusal","category":"cyber","explanation":"It reads as malware."}"""));
        server.Answer(
            FakeAnthropicServer.Start(500),
            FakeAnthropicServer.BlockStart(0, """{"type":"tool_use","id":"toolu_9","name":"Write","input":{}}"""),
            FakeAnthropicServer.Delta(0, """{"type":"input_json_delta","partial_json":"{\"file_path\":\"a.txt\",\"content\":\"partly"}"""),
            FakeAnthropicServer.End("max_tokens", 64000));
        await using var session = await backend.StartAsync(new AgentStartOptions(server.Url, dir), ct);

        var declined = Assert.Single((await ModelAgentTests.TurnAsync(session, "Write it.")).OfType<ClaudeResult>());
        Assert.True(declined.IsError);
        Assert.Contains("Claude declined the request (cyber: It reads as malware.)", declined.Text);

        var cut = await ModelAgentTests.TurnAsync(session, "Again.");
        Assert.Empty(cut.OfType<ClaudeToolResults>());
        Assert.Equal("error_max_tokens", Assert.Single(cut.OfType<ClaudeResult>()).Subtype);
        Assert.False(File.Exists(Path.Combine(dir, "a.txt")));
    }
}
