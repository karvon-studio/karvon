using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Karvon.ViewModels;

namespace Karvon.Claude;

/// <summary>An agent that speaks the Agent Client Protocol: how to start it.</summary>
/// <param name="Id">What the settings keep ("gemini", or a name of the user's).</param>
/// <param name="Names">The program's names to look for (gemini); an absolute path is taken as it is.</param>
/// <param name="Arguments">What makes it speak ACP on stdin and stdout (--acp).</param>
/// <param name="OwnPrefixes">The variables of its own sign-in, kept in its environment (GEMINI_, GOOGLE_).</param>
public sealed record AcpAgent(string Id, string Name, string Description, IReadOnlyList<string> Names, IReadOnlyList<string> Arguments,
    IReadOnlyList<string> OwnPrefixes, string? InstallCommand)
{
    public static readonly AcpAgent GeminiCli = new("gemini", "Gemini CLI", "Google's Gemini CLI, through the Agent Client Protocol",
        ["gemini"], ["--acp"], ["GEMINI_", "GOOGLE_"], "npm install -g @google/gemini-cli");
}

/// <summary>
/// One conversation with an agent through the Agent Client Protocol (JSON-RPC on stdin and stdout, as Zed and JetBrains IDEs
/// talk to agents): its streamed answer and thoughts, its tool calls and its questions become the panel's, Karvon reads and
/// writes files for it (fs/read_text_file, fs/write_text_file, with the secret guard), and Karvon's own tools are given to it
/// as an MCP server over HTTP on this computer (<see cref="KarvonMcpEndpoint"/>).
/// </summary>
public sealed class AcpSession : IAgentSession
{
    readonly JsonRpcProcess _rpc;
    readonly AgentStartOptions _options;
    readonly AcpAgent _agent;
    readonly ConcurrentDictionary<string, (JsonNode? Id, JsonElement Options, string Tool)> _asked = new();
    readonly ConcurrentDictionary<string, (string Name, JsonElement Input)> _calls = new();
    readonly Lock _stream = new();
    KarvonMcpEndpoint? _endpoint;
    string _mode;
    bool _images;
    bool _loading;
    string? _textId;
    readonly StringBuilder _text = new();
    string? _thoughtId;
    readonly StringBuilder _thought = new();
    int _plans;

    AcpSession(JsonRpcProcess rpc, AgentStartOptions options, AcpAgent agent)
    {
        _rpc = rpc;
        _options = options;
        _agent = agent;
        _mode = options.PermissionMode;
        rpc.Request += OnRequest;
        rpc.Notification += OnNotification;
        rpc.Exited += code => Event?.Invoke(new ClaudeExited(code, rpc.StderrTail(6)));
    }

    public event Action<ClaudeEvent>? Event;

    public string? SessionId { get; private set; }

    public string? WorkingDirectory => _options.WorkingDirectory;

    public bool HasExited => _rpc.HasExited;

    public JsonElement Initialization { get; private set; }

    public IReadOnlyList<string> HiddenVariables { get; private init; } = [];

    public ClaudeAudit? Audit { get; private init; }

    public static async Task<AcpSession> StartAsync(AcpAgent agent, AgentStartOptions options, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> hidden = [];
        var rpc = JsonRpcProcess.Start(options.Executable, agent.Arguments, options.WorkingDirectory, environment =>
        {
            hidden = ClaudeEnvironment.Clean(environment, agent.OwnPrefixes);
            AgentExecutables.WithFolderOnPath(environment, options.Executable);
        });
        var session = new AcpSession(rpc, options, agent)
        {
            HiddenVariables = hidden,
            Audit = options.AuditDirectory is { } audit ? ClaudeAudit.Start(audit) : null,
        };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            var init = await rpc.RequestAsync("initialize", new JsonObject
            {
                ["protocolVersion"] = 1,
                ["clientCapabilities"] = new JsonObject
                {
                    ["fs"] = new JsonObject { ["readTextFile"] = true, ["writeTextFile"] = true },
                    ["terminal"] = false,
                },
                ["clientInfo"] = new JsonObject { ["name"] = "karvon", ["title"] = "Karvon Studio", ["version"] = typeof(AcpSession).Assembly.GetName().Version?.ToString(3) },
            }, timeout.Token);
            var capabilities = init.TryGetProperty("agentCapabilities", out var c) ? c : default;
            session._images = capabilities.ValueKind == JsonValueKind.Object && capabilities.TryGetProperty("promptCapabilities", out var prompt) && ClaudeJson.Bool(prompt, "image");
            var servers = new JsonArray();
            // Karvon's tools over HTTP, when the agent takes an MCP server that way.
            if (options.Tools is { } tools && capabilities.ValueKind == JsonValueKind.Object && capabilities.TryGetProperty("mcpCapabilities", out var mcp) && ClaudeJson.Bool(mcp, "http"))
            {
                session._endpoint = KarvonMcpEndpoint.Start(tools, session);
                servers.Add(new JsonObject
                {
                    ["type"] = "http",
                    ["name"] = ClaudeToolServer.Name,
                    ["url"] = session._endpoint.Url,
                    ["headers"] = new JsonArray(new JsonObject { ["name"] = "Authorization", ["value"] = "Bearer " + session._endpoint.Token }),
                });
            }
            JsonElement opened;
            if (options.Resume is { Length: > 0 } resume && capabilities.ValueKind == JsonValueKind.Object && ClaudeJson.Bool(capabilities, "loadSession"))
            {
                // The agent tells the conversation again as it loads it: the panel already shows it.
                session._loading = true;
                try
                {
                    opened = await rpc.RequestAsync("session/load", new JsonObject { ["sessionId"] = resume, ["cwd"] = options.WorkingDirectory, ["mcpServers"] = servers }, timeout.Token);
                }
                finally
                {
                    session._loading = false;
                }
                session.SessionId = resume;
            }
            else
            {
                opened = await rpc.RequestAsync("session/new", new JsonObject { ["cwd"] = options.WorkingDirectory, ["mcpServers"] = servers }, timeout.Token);
                session.SessionId = ClaudeJson.Str(opened, "sessionId");
            }
            session.Audit?.SessionId = session.SessionId;
            session.Initialization = CodexSession.WithCurrentModel(Models(opened), Current(opened));
            if (options.Model is { Length: > 0 } model && model != "default" && Current(opened) != model)
                await session.SetModelAsync(model);
        }
        catch (Exception ex)
        {
            var reason = rpc.StderrTail(4);
            await session.DisposeAsync();
            if (ex is InvalidOperationException)
                throw new InvalidOperationException($"{agent.Name}: {ex.Message}", ex);
            if (reason.Length > 0 && ex is not OperationCanceledException)
                throw new InvalidOperationException(reason, ex);
            throw;
        }
        return session;
    }

    /// <summary>The agent's models (session/new's, where it has them) in the shape the panel reads Claude Code's in.</summary>
    static JsonElement Models(JsonElement opened)
    {
        var models = new JsonArray();
        if (opened.ValueKind == JsonValueKind.Object && opened.TryGetProperty("models", out var m) && m.ValueKind == JsonValueKind.Object
            && m.TryGetProperty("availableModels", out var available) && available.ValueKind == JsonValueKind.Array)
            foreach (var model in available.EnumerateArray())
                models.Add(new JsonObject
                {
                    ["value"] = ClaudeJson.Str(model, "modelId"),
                    ["displayName"] = ClaudeJson.Str(model, "name") ?? ClaudeJson.Str(model, "modelId"),
                    ["description"] = ClaudeJson.Str(model, "description"),
                });
        return JsonDocument.Parse(new JsonObject { ["models"] = models }.ToJsonString()).RootElement.Clone();
    }

    static string? Current(JsonElement opened) =>
        opened.ValueKind == JsonValueKind.Object && opened.TryGetProperty("models", out var m) && m.ValueKind == JsonValueKind.Object ? ClaudeJson.Str(m, "currentModelId") : null;

    // ---- The panel asks ------------------------------------------------------------------------------------

    public Task SendUserMessageAsync(string text, IEnumerable<string>? extraBlocks = null, IEnumerable<ClaudeImage>? images = null)
    {
        var prompt = new JsonArray();
        if (_images)
            foreach (var image in images ?? [])
                prompt.Add(new JsonObject { ["type"] = "image", ["mimeType"] = image.MediaType, ["data"] = image.Base64 });
        prompt.Add(new JsonObject { ["type"] = "text", ["text"] = text });
        foreach (var block in extraBlocks ?? [])
            prompt.Add(new JsonObject { ["type"] = "text", ["text"] = block });
        _ = PromptAsync(prompt);
        return Task.CompletedTask;
    }

    /// <summary>A prompt runs until the agent answers it (with why it stopped); meanwhile its updates come as notifications.</summary>
    async Task PromptAsync(JsonArray prompt)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            var answer = await _rpc.RequestAsync("session/prompt", new JsonObject { ["sessionId"] = SessionId, ["prompt"] = prompt });
            Flush();
            Event?.Invoke(ClaudeJson.Str(answer, "stopReason") switch
            {
                "end_turn" or null => new ClaudeResult("success", false, clock.ElapsedMilliseconds, null, null, SessionId),
                "cancelled" => new ClaudeResult("error_during_execution", true, clock.ElapsedMilliseconds, null, null, SessionId),
                "max_turn_requests" => new ClaudeResult("error_max_turns", true, clock.ElapsedMilliseconds, null, null, SessionId),
                "max_tokens" => new ClaudeResult("error", true, clock.ElapsedMilliseconds, null, $"{_agent.Name} stopped: the answer reached the model's length limit.", SessionId),
                "refusal" => new ClaudeResult("error", true, clock.ElapsedMilliseconds, null, $"{_agent.Name} refused to go on.", SessionId),
                var other => new ClaudeResult("error", true, clock.ElapsedMilliseconds, null, $"{_agent.Name} stopped ({other}).", SessionId),
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            Flush();
            CancelQuestions();
            Event?.Invoke(new ClaudeResult("error", true, clock.ElapsedMilliseconds, null, $"{_agent.Name}: {ex.Message}", SessionId));
        }
    }

    public async Task RespondToPermissionAsync(string requestId, ClaudePermissionDecision decision, string by = "the user")
    {
        if (!_asked.TryRemove(requestId, out var asked))
            return;
        Audit?.Decided(null, asked.Tool, decision.Allow ? "allow" : "deny", by);
        await AnswerAsync(asked.Id, asked.Options, decision.Allow
            ? decision.UpdatedPermissions is { Count: > 0 } ? ["allow_always", "allow_once"] : ["allow_once", "allow_always"]
            : ["reject_once", "reject_always"]);
        if (!decision.Allow && decision.Interrupt)
            await InterruptAsync();
    }

    /// <summary>Picks the first of the agent's options of <paramref name="kinds"/>; none of them: the question is cancelled.</summary>
    Task AnswerAsync(JsonNode? id, JsonElement options, IReadOnlyList<string> kinds)
    {
        var list = options.ValueKind == JsonValueKind.Array ? options.EnumerateArray().ToList() : [];
        var chosen = kinds.Select(k => list.FirstOrDefault(o => ClaudeJson.Str(o, "kind") == k)).FirstOrDefault(o => o.ValueKind == JsonValueKind.Object);
        return _rpc.RespondAsync(id, new JsonObject
        {
            ["outcome"] = chosen.ValueKind == JsonValueKind.Object
                ? new JsonObject { ["outcome"] = "selected", ["optionId"] = ClaudeJson.Str(chosen, "optionId") }
                : new JsonObject { ["outcome"] = "cancelled" },
        });
    }

    public Task InterruptAsync() => _rpc.NotifyAsync("session/cancel", new JsonObject { ["sessionId"] = SessionId });

    /// <summary>The panel's mode is Karvon's: it answers the agent's questions by it (edits in Accept edits, all in Bypass, none in Plan).</summary>
    public Task SetPermissionModeAsync(string mode)
    {
        _mode = mode;
        return Task.CompletedTask;
    }

    public async Task SetModelAsync(string? model)
    {
        if (model is not { Length: > 0 } or "default")
            return;
        try
        {
            await _rpc.RequestAsync("session/set_model", new JsonObject { ["sessionId"] = SessionId, ["modelId"] = model });
        }
        catch (InvalidOperationException ex)
        {
            Event?.Invoke(new ClaudeNotice($"{_agent.Name} did not change its model: {ex.Message}", IsError: true));
        }
    }

    // ---- What the agent asks --------------------------------------------------------------------------------

    void OnRequest(string method, JsonElement parameters, JsonNode? id)
    {
        switch (method)
        {
            case "session/request_permission":
                Ask(parameters, id);
                break;
            case "fs/read_text_file":
                _ = ReadFileAsync(parameters, id);
                break;
            case "fs/write_text_file":
                _ = WriteFileAsync(parameters, id);
                break;
            default:
                _ = _rpc.RespondErrorAsync(id, -32601, $"Karvon does not answer {method}.");
                break;
        }
    }

    void Ask(JsonElement parameters, JsonNode? id)
    {
        var call = parameters.TryGetProperty("toolCall", out var c) ? c : default;
        var callId = ClaudeJson.Str(call, "toolCallId") ?? Guid.NewGuid().ToString("N");
        var (name, input) = Describe(call, _calls.TryGetValue(callId, out var known) ? known : null);
        var options = parameters.TryGetProperty("options", out var o) ? o.Clone() : default;
        // The secret guard, then the panel's mode, answer before the user is asked.
        if (_options.GuardSecrets && SecretGuard.Check(name, input, _options.WorkingDirectory) is { } refusal)
        {
            Audit?.Decided(null, name, "deny", "Karvon's secret guard", refusal);
            Event?.Invoke(new ClaudeNotice(refusal, IsError: true));
            _ = AnswerAsync(id, options, ["reject_once", "reject_always"]);
            return;
        }
        var edits = name is "Write" or "Edit";
        var changes = edits || name == "Bash";
        if (_mode == "bypassPermissions" || _mode == "acceptEdits" && edits)
        {
            _ = AnswerAsync(id, options, ["allow_once", "allow_always"]);
            return;
        }
        if (_mode == "plan" && changes)
        {
            _ = AnswerAsync(id, options, ["reject_once", "reject_always"]);
            return;
        }
        var requestId = "acp-" + id?.ToJsonString();
        _asked[requestId] = (id, options, name);
        var always = options.ValueKind == JsonValueKind.Array && options.EnumerateArray().Any(x => ClaudeJson.Str(x, "kind") == "allow_always");
        IReadOnlyList<JsonElement> suggestions = always
            ? [JsonDocument.Parse(new JsonObject
            {
                ["type"] = "addRules",
                ["rules"] = new JsonArray(new JsonObject { ["toolName"] = name }),
                ["behavior"] = "allow",
                ["destination"] = "session",
            }.ToJsonString()).RootElement.Clone()]
            : [];
        Flush();
        Event?.Invoke(new ClaudePermissionRequest(requestId, name, ClaudeJson.Str(call, "title"), input, ClaudeJson.Str(call, "title"), suggestions, callId));
    }

    async Task ReadFileAsync(JsonElement parameters, JsonNode? id)
    {
        var path = ClaudeJson.Str(parameters, "path") ?? "";
        var input = JsonSerializer.SerializeToElement(new { file_path = path });
        if (_options.GuardSecrets && SecretGuard.Check("Read", input, _options.WorkingDirectory) is { } refusal)
        {
            await _rpc.RespondErrorAsync(id, -32000, refusal);
            return;
        }
        try
        {
            // The text as it is (its line ends, its last newline): the agent writes back what it read.
            var text = await File.ReadAllTextAsync(path);
            var first = parameters.TryGetProperty("line", out var l) && l.TryGetInt32(out var line) ? Math.Max(1, line) : 1;
            var count = parameters.TryGetProperty("limit", out var n) && n.TryGetInt32(out var limit) ? limit : (int?)null;
            if (first > 1 || count is not null)
            {
                var lines = System.Text.RegularExpressions.Regex.Split(text, "(?<=\n)");
                text = string.Concat(lines.Skip(first - 1).Take(count ?? lines.Length));
            }
            await _rpc.RespondAsync(id, new JsonObject { ["content"] = text });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            await _rpc.RespondErrorAsync(id, -32000, ex.Message);
        }
    }

    async Task WriteFileAsync(JsonElement parameters, JsonNode? id)
    {
        var path = ClaudeJson.Str(parameters, "path") ?? "";
        var content = ClaudeJson.Str(parameters, "content") ?? "";
        var input = JsonSerializer.SerializeToElement(new { file_path = path, content });
        if (_options.GuardSecrets && SecretGuard.Check("Write", input, _options.WorkingDirectory) is { } refusal)
        {
            await _rpc.RespondErrorAsync(id, -32000, refusal);
            return;
        }
        if (_mode == "plan")
        {
            await _rpc.RespondErrorAsync(id, -32000, "In plan mode Karvon changes no file.");
            return;
        }
        try
        {
            if (Path.GetDirectoryName(path) is { Length: > 0 } folder)
                Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(path, content);
            await _rpc.RespondAsync(id, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            await _rpc.RespondErrorAsync(id, -32000, ex.Message);
        }
    }

    void CancelQuestions()
    {
        foreach (var key in _asked.Keys.ToList())
            if (_asked.TryRemove(key, out _))
                Event?.Invoke(new ClaudePermissionCancelled(key));
    }

    // ---- What the agent tells -------------------------------------------------------------------------------

    void OnNotification(string method, JsonElement parameters)
    {
        if (method != "session/update" || _loading || !parameters.TryGetProperty("update", out var update))
            return;
        switch (ClaudeJson.Str(update, "sessionUpdate"))
        {
            case "agent_message_chunk":
                Chunk(update, thinking: false);
                break;
            case "agent_thought_chunk":
                Chunk(update, thinking: true);
                break;
            case "tool_call":
                ToolCall(update);
                break;
            case "tool_call_update":
                ToolCallUpdate(update);
                break;
            case "plan":
                Plan(update);
                break;
            case "current_mode_update" when ClaudeJson.Str(update, "currentModeId") is { } mode && Known(mode) is { } known:
                _mode = known;
                Event?.Invoke(new ClaudeModeChanged(known));
                break;
        }
    }

    /// <summary>The panel's mode an agent's mode id is (Claude Code's ids, Gemini's autoEdit and yolo); null for one it has not.</summary>
    static string? Known(string mode) => mode switch
    {
        "default" or "acceptEdits" or "plan" or "bypassPermissions" => mode,
        "autoEdit" or "auto_edit" => "acceptEdits",
        "yolo" => "bypassPermissions",
        _ => null,
    };

    void Chunk(JsonElement update, bool thinking)
    {
        var text = update.TryGetProperty("content", out var content) ? ClaudeJson.Str(content, "text") : null;
        if (string.IsNullOrEmpty(text))
            return;
        lock (_stream)
        {
            if (thinking)
            {
                if (_textId is not null)
                    FlushText();
                if (_thoughtId is null)
                {
                    _thoughtId = "thought_" + Guid.NewGuid().ToString("N")[..12];
                    Event?.Invoke(new ClaudeBlockStart(_thoughtId, 0, "thinking"));
                }
                _thought.Append(text);
                Event?.Invoke(new ClaudeBlockDelta(_thoughtId, 0, "thinking", text));
            }
            else
            {
                if (_thoughtId is not null)
                    FlushThought();
                if (_textId is null)
                {
                    _textId = "msg_" + Guid.NewGuid().ToString("N")[..12];
                    Event?.Invoke(new ClaudeMessageStart(_textId));
                    Event?.Invoke(new ClaudeBlockStart(_textId, 0, "text"));
                }
                _text.Append(text);
                Event?.Invoke(new ClaudeBlockDelta(_textId, 0, "text", text));
            }
        }
    }

    /// <summary>What streamed so far ends as complete blocks (a tool call or the end of the turn came).</summary>
    void Flush()
    {
        lock (_stream)
        {
            FlushThought();
            FlushText();
        }
    }

    void FlushText()
    {
        if (_textId is { } id)
            Event?.Invoke(new ClaudeAssistant(id, [new ClaudeTextBlock(_text.ToString())], null));
        _textId = null;
        _text.Clear();
    }

    void FlushThought()
    {
        if (_thoughtId is { } id)
            Event?.Invoke(new ClaudeAssistant(id, [new ClaudeThinkingBlock(_thought.ToString())], null));
        _thoughtId = null;
        _thought.Clear();
    }

    void ToolCall(JsonElement update)
    {
        Flush();
        var id = ClaudeJson.Str(update, "toolCallId") ?? Guid.NewGuid().ToString("N");
        var (name, input) = Describe(update, null);
        _calls[id] = (name, input);
        Event?.Invoke(new ClaudeAssistant(id + "/call", [new ClaudeToolUseBlock(id, name, input)], null));
        if (ClaudeJson.Str(update, "status") is "completed" or "failed")
            ToolCallUpdate(update);
    }

    void ToolCallUpdate(JsonElement update)
    {
        var id = ClaudeJson.Str(update, "toolCallId") ?? "";
        var status = ClaudeJson.Str(update, "status");
        if (status is not ("completed" or "failed"))
            return;
        var text = new List<string>();
        if (update.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            foreach (var item in content.EnumerateArray())
                switch (ClaudeJson.Str(item, "type"))
                {
                    case "content" when item.TryGetProperty("content", out var inner) && ClaudeJson.Str(inner, "text") is { } t:
                        text.Add(t);
                        break;
                    case "diff":
                        text.Add(ClaudeJson.Str(item, "path") ?? "");
                        break;
                }
        if (text.Count == 0 && update.TryGetProperty("rawOutput", out var raw))
            text.Add(raw.ValueKind == JsonValueKind.String ? raw.GetString()! : raw.GetRawText());
        Event?.Invoke(new ClaudeToolResults([new ClaudeToolResult(id, string.Join("\n", text), status == "failed")], null));
    }

    /// <summary>
    /// An ACP tool call as one of Claude Code's tools, so the panel shows it so: a diff as Edit (or Write for a new file), a
    /// command as Bash, a read as Read, a search as Grep, a fetch as WebFetch; any other by its title.
    /// </summary>
    internal static (string Name, JsonElement Input) Describe(JsonElement call, (string Name, JsonElement Input)? known)
    {
        var kind = ClaudeJson.Str(call, "kind");
        var title = ClaudeJson.Str(call, "title");
        var raw = call.ValueKind == JsonValueKind.Object && call.TryGetProperty("rawInput", out var r) && r.ValueKind == JsonValueKind.Object ? r : default;
        string? Raw(params string[] names) => raw.ValueKind == JsonValueKind.Object ? names.Select(n => ClaudeJson.Str(raw, n)).FirstOrDefault(v => v is not null) : null;
        var diff = call.ValueKind == JsonValueKind.Object && call.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array
            ? content.EnumerateArray().FirstOrDefault(i => ClaudeJson.Str(i, "type") == "diff")
            : default;
        var location = call.ValueKind == JsonValueKind.Object && call.TryGetProperty("locations", out var locations) && locations.ValueKind == JsonValueKind.Array
            ? locations.EnumerateArray().Select(l => ClaudeJson.Str(l, "path")).FirstOrDefault(p => p is not null)
            : null;
        if (diff.ValueKind == JsonValueKind.Object)
        {
            var path = ClaudeJson.Str(diff, "path") ?? location ?? "";
            var before = ClaudeJson.Str(diff, "oldText");
            var after = ClaudeJson.Str(diff, "newText") ?? "";
            return string.IsNullOrEmpty(before)
                ? ("Write", JsonSerializer.SerializeToElement(new { file_path = path, content = after }))
                : ("Edit", JsonSerializer.SerializeToElement(new { file_path = path, old_string = before, new_string = after }));
        }
        if (kind is null && title is null && known is { } earlier)
            return earlier;
        return kind switch
        {
            "execute" => ("Bash", JsonSerializer.SerializeToElement(new { command = Raw("command", "cmd") ?? title ?? "", description = title })),
            "read" => ("Read", JsonSerializer.SerializeToElement(new { file_path = location ?? Raw("path", "file_path", "absolute_path") ?? title ?? "" })),
            "search" => ("Grep", JsonSerializer.SerializeToElement(new { pattern = Raw("pattern", "query") ?? title ?? "", path = location })),
            "fetch" => ("WebFetch", JsonSerializer.SerializeToElement(new { url = Raw("url") ?? title ?? "" })),
            _ when known is { } same => same,
            _ => (title ?? "Tool", raw.ValueKind == JsonValueKind.Object ? raw.Clone() : JsonSerializer.SerializeToElement(new { })),
        };
    }

    void Plan(JsonElement update)
    {
        if (!update.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
            return;
        Flush();
        var todos = new JsonArray([.. entries.EnumerateArray().Select(e => (JsonNode)new JsonObject
        {
            ["content"] = ClaudeJson.Str(e, "content") ?? "",
            ["status"] = ClaudeJson.Str(e, "status") switch { "in_progress" => "in_progress", "completed" => "completed", _ => "pending" },
        })]);
        var id = $"plan-{SessionId}-{++_plans}";
        Event?.Invoke(new ClaudeAssistant(id + "/call", [new ClaudeToolUseBlock(id, "TodoWrite", JsonDocument.Parse(new JsonObject { ["todos"] = todos }.ToJsonString()).RootElement.Clone())], null));
        Event?.Invoke(new ClaudeToolResults([new ClaudeToolResult(id, "", false)], null));
    }

    public async ValueTask DisposeAsync()
    {
        CancelQuestions();
        await _rpc.DisposeAsync();
        if (_endpoint is not null)
            await _endpoint.DisposeAsync();
    }
}

/// <summary>An Agent Client Protocol agent in the panel (<see cref="AcpSession"/>).</summary>
public sealed class AcpBackend(AcpAgent agent) : IAgentBackend
{
    public AcpAgent Agent => agent;

    public string Id => "acp:" + agent.Id;

    public string Name => agent.Name;

    public string Description => agent.Description;

    public string MissingTitle => $"{agent.Name} is not installed";

    public string MissingHint => $"Install {agent.Name}, sign in once by running it in a terminal, then retry.";

    public string? InstallCommand => agent.InstallCommand;

    /// <summary>Karvon answers the agent's questions by these (it has no auto mode of Claude's).</summary>
    public IReadOnlyList<string> Modes => CodexSession.Modes;

    public string? Find() =>
        agent.Names.FirstOrDefault(Path.IsPathRooted) is { } path ? File.Exists(path) ? path : null : AgentExecutables.Find([.. agent.Names]);

    /// <summary>An ACP agent says its models as a session starts: one is started (no prompt, no model called) and let go.</summary>
    public async Task<IReadOnlyList<AgentModel>> ModelsAsync(string executable, string workingDirectory, CancellationToken cancellationToken = default)
    {
        await using var session = await AcpSession.StartAsync(agent, new AgentStartOptions(executable, workingDirectory), cancellationToken);
        return AgentModels.Of(session.Initialization);
    }

    public async Task<IAgentSession> StartAsync(AgentStartOptions options, CancellationToken cancellationToken = default) =>
        await AcpSession.StartAsync(agent, options, cancellationToken);
}
