using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

using Karvon.ViewModels;

namespace Karvon.Claude;

/// <summary>
/// One conversation with OpenAI's Codex through its app-server (<c>codex app-server</c>: JSON-RPC on stdin and stdout, the
/// interface its own IDE extensions use). Codex keeps its harness, its sign-in (ChatGPT or an API key) and its sandbox; Karvon
/// shows what it does in the panel as it shows Claude Code's: its answers stream, its commands and patches are tool cards, its
/// questions (a command to run, files to change) are the panel's cards, and Karvon's own tools are given to it as dynamic
/// tools it calls back.
/// </summary>
public sealed class CodexSession : IAgentSession
{
    /// <summary>The namespace Karvon's tools have in Codex.</summary>
    public const string ToolNamespace = "karvon";

    static readonly string[] OwnPrefixes = ["OPENAI_", "CODEX_", "AZURE_OPENAI_"];

    readonly JsonRpcProcess _rpc;
    readonly AgentStartOptions _options;
    readonly ConcurrentDictionary<string, (JsonNode? Id, string Kind)> _asked = new();
    readonly ConcurrentDictionary<string, JsonElement> _changes = new();
    readonly CancellationTokenSource _closing = new();
    string _mode;
    string? _model;
    string? _effort;
    string? _turnId;
    int? _contextTokens;
    int _plans;

    CodexSession(JsonRpcProcess rpc, AgentStartOptions options)
    {
        _rpc = rpc;
        _options = options;
        _mode = options.PermissionMode;
        _model = options.Model is "default" ? null : options.Model;
        _effort = options.Effort;
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

    /// <summary>The modes Codex has: Karvon's "auto" (a classifier of Claude's) it does not.</summary>
    public static IReadOnlyList<string> Modes { get; } = ["default", "acceptEdits", "plan", "bypassPermissions"];

    public static async Task<CodexSession> StartAsync(AgentStartOptions options, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> hidden = [];
        var rpc = JsonRpcProcess.Start(options.Executable, ["app-server"], options.WorkingDirectory,
            environment =>
            {
                hidden = ClaudeEnvironment.Clean(environment, OwnPrefixes);
                AgentExecutables.WithFolderOnPath(environment, options.Executable);
            }, version: false);
        var session = new CodexSession(rpc, options)
        {
            HiddenVariables = hidden,
            Audit = options.AuditDirectory is { } audit ? ClaudeAudit.Start(audit) : null,
        };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            await rpc.RequestAsync("initialize", new JsonObject
            {
                ["clientInfo"] = new JsonObject { ["name"] = "karvon", ["title"] = "Karvon Studio", ["version"] = Version() },
                // Dynamic tools (Karvon's) are an experimental field of thread/start.
                ["capabilities"] = new JsonObject { ["experimentalApi"] = true },
            }, timeout.Token);
            await rpc.NotifyAsync("initialized");
            session.Initialization = await session.InitializationAsync(timeout.Token);
            var thread = options.Resume is { Length: > 0 } resume
                ? await rpc.RequestAsync("thread/resume", session.ThreadParameters(new JsonObject { ["threadId"] = resume }, resumed: true), timeout.Token)
                : await rpc.RequestAsync("thread/start", session.ThreadParameters([], resumed: false), timeout.Token);
            var threadInfo = thread.GetProperty("thread");
            session.SessionId = ClaudeJson.Str(threadInfo, "id");
            session.Audit?.SessionId = session.SessionId;
            session._model ??= ClaudeJson.Str(thread, "model");
            session.Initialization = WithCurrentModel(session.Initialization, ClaudeJson.Str(thread, "model"));
            session.Event?.Invoke(new ClaudeInit(session.SessionId ?? "", session._model, session._mode, options.WorkingDirectory));
        }
        catch (Exception ex)
        {
            var reason = rpc.StderrTail(6);
            await session.DisposeAsync();
            if (reason.Length > 0 && ex is not OperationCanceledException)
                throw new InvalidOperationException(reason, ex);
            throw;
        }
        return session;
    }

    /// <summary>The initialization with the model the agent answers with now.</summary>
    internal static JsonElement WithCurrentModel(JsonElement init, string? model)
    {
        if (model is null || JsonNode.Parse(init.GetRawText()) is not JsonObject node)
            return init;
        node["currentModel"] = model;
        return JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();
    }

    static string Version() => typeof(CodexSession).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>Codex's models and account in the shape the panel reads Claude Code's in.</summary>
    async Task<JsonElement> InitializationAsync(CancellationToken cancellationToken)
    {
        var init = new JsonObject();
        try
        {
            var list = await _rpc.RequestAsync("model/list", new JsonObject(), cancellationToken);
            var models = new JsonArray();
            if (list.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                foreach (var model in data.EnumerateArray().Where(m => !ClaudeJson.Bool(m, "hidden")))
                {
                    var efforts = model.TryGetProperty("supportedReasoningEfforts", out var e) && e.ValueKind == JsonValueKind.Array
                        ? new JsonArray([.. e.EnumerateArray().Select(x => (JsonNode?)ClaudeJson.Str(x, "reasoningEffort")).Where(x => x is not null)])
                        : null;
                    models.Add(new JsonObject
                    {
                        ["value"] = ClaudeJson.Str(model, "model") ?? ClaudeJson.Str(model, "id"),
                        ["displayName"] = ClaudeJson.Str(model, "displayName") ?? ClaudeJson.Str(model, "model"),
                        ["description"] = ClaudeJson.Str(model, "description"),
                        ["supportsEffort"] = efforts is { Count: > 0 },
                        ["supportedEffortLevels"] = efforts,
                    });
                }
            init["models"] = models;
        }
        catch (InvalidOperationException)
        {
            // An older Codex without model/list: the panel keeps the model it has.
        }
        try
        {
            var account = await _rpc.RequestAsync("account/read", new JsonObject(), cancellationToken);
            if (account.TryGetProperty("account", out var a) && a.ValueKind == JsonValueKind.Object)
                init["account"] = new JsonObject
                {
                    ["subscriptionType"] = ClaudeJson.Str(a, "type") is "chatgpt" ? "ChatGPT " + Capitalize(ClaudeJson.Str(a, "planType")) : "OpenAI API key",
                    ["email"] = ClaudeJson.Str(a, "email"),
                };
        }
        catch (InvalidOperationException)
        {
        }
        return JsonDocument.Parse(init.ToJsonString()).RootElement.Clone();
    }

    static string Capitalize(string? text) => string.IsNullOrEmpty(text) ? "" : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>thread/start's (or thread/resume's) parameters: the folder, the mode, the model, Karvon's tools and what to use them for.</summary>
    JsonObject ThreadParameters(JsonObject parameters, bool resumed)
    {
        parameters["cwd"] = _options.WorkingDirectory;
        parameters["approvalPolicy"] = ApprovalPolicy(_mode);
        parameters["sandbox"] = Sandbox(_mode);
        if (_model is not null)
            parameters["model"] = _model;
        if (_options.Tools is { } tools)
        {
            parameters["developerInstructions"] = Instructions(tools);
            // A resumed thread keeps the tools it was started with.
            if (!resumed)
                parameters["dynamicTools"] = new JsonArray(new JsonObject
                {
                    ["type"] = "namespace",
                    ["name"] = ToolNamespace,
                    ["description"] = "Karvon Studio's tools: the IDE's build, tests, debugger, code navigation and the user's connections.",
                    ["tools"] = new JsonArray([.. tools.Tools.Select(t => (JsonNode)new JsonObject
                    {
                        ["type"] = "function",
                        ["name"] = t.Name,
                        ["description"] = t.Description,
                        ["inputSchema"] = t.InputSchema.DeepClone(),
                        ["deferLoading"] = !tools.IsAlwaysLoaded(t.Name),
                    })]),
                });
        }
        return parameters;
    }

    /// <summary>Karvon's prompt for its tools, in Codex's names for them.</summary>
    internal static string Instructions(ClaudeToolServer tools) =>
        string.Join("\n\n", new[] { tools.SystemPrompt, tools.Instructions }.Where(t => t is { Length: > 0 }))
            .Replace("mcp__karvon__*", $"the {ToolNamespace} namespace's", StringComparison.Ordinal)
            .Replace("Claude Code's", "Codex's", StringComparison.Ordinal)
            .Replace("Claude", "Codex", StringComparison.Ordinal);

    /// <summary>When Codex asks before it acts, for each of the panel's modes.</summary>
    internal static string ApprovalPolicy(string mode) => mode switch
    {
        "bypassPermissions" => "never",
        "acceptEdits" or "plan" or "auto" => "on-request",
        _ => "untrusted",
    };

    /// <summary>Where Codex's commands may write, for each of the panel's modes.</summary>
    internal static string Sandbox(string mode) => mode switch
    {
        "bypassPermissions" => "danger-full-access",
        "plan" => "read-only",
        _ => "workspace-write",
    };

    static JsonObject SandboxPolicy(string mode) => mode switch
    {
        "bypassPermissions" => new JsonObject { ["type"] = "dangerFullAccess" },
        "plan" => new JsonObject { ["type"] = "readOnly" },
        _ => new JsonObject { ["type"] = "workspaceWrite" },
    };

    public async Task SendUserMessageAsync(string text, IEnumerable<string>? extraBlocks = null, IEnumerable<ClaudeImage>? images = null)
    {
        var input = new JsonArray();
        foreach (var image in images ?? [])
            input.Add(new JsonObject { ["type"] = "localImage", ["path"] = SaveImage(image) });
        input.Add(new JsonObject { ["type"] = "text", ["text"] = text });
        foreach (var block in extraBlocks ?? [])
            input.Add(new JsonObject { ["type"] = "text", ["text"] = block });
        var parameters = new JsonObject
        {
            ["threadId"] = SessionId,
            ["input"] = input,
            ["approvalPolicy"] = ApprovalPolicy(_mode),
            ["sandboxPolicy"] = SandboxPolicy(_mode),
        };
        if (_model is not null)
            parameters["model"] = _model;
        if (_effort is { Length: > 0 })
            parameters["effort"] = _effort;
        var started = await _rpc.RequestAsync("turn/start", parameters, _closing.Token);
        if (started.TryGetProperty("turn", out var turn))
            _turnId = ClaudeJson.Str(turn, "id") ?? _turnId;
    }

    /// <summary>An image of the prompt, as a file Codex reads (its input takes a path).</summary>
    static string SaveImage(ClaudeImage image)
    {
        var folder = Path.Combine(Path.GetTempPath(), "karvon-codex");
        Directory.CreateDirectory(folder);
        var extension = image.MediaType switch { "image/jpeg" => ".jpg", "image/gif" => ".gif", "image/webp" => ".webp", _ => ".png" };
        var file = Path.Combine(folder, Guid.NewGuid().ToString("N") + extension);
        File.WriteAllBytes(file, Convert.FromBase64String(image.Base64));
        return file;
    }

    public Task SendCommandAsync(string command)
    {
        // Codex's own commands are its TUI's; what it does over the app-server has methods of their own.
        if (command.Trim() is "/compact")
            return _rpc.RequestAsync("thread/compact/start", new JsonObject { ["threadId"] = SessionId }, _closing.Token);
        return SendUserMessageAsync(command);
    }

    public async Task RespondToPermissionAsync(string requestId, ClaudePermissionDecision decision, string by = "the user")
    {
        if (!_asked.TryRemove(requestId, out var asked))
            return;
        var answer = decision.Allow
            ? decision.UpdatedPermissions is { Count: > 0 } ? "acceptForSession" : "accept"
            : decision.Interrupt ? "cancel" : "decline";
        Audit?.Decided(null, asked.Kind == "command" ? "Bash" : "Patch", decision.Allow ? "allow" : "deny", by);
        await _rpc.RespondAsync(asked.Id, new JsonObject { ["decision"] = answer });
    }

    public Task InterruptAsync() =>
        _turnId is { } turn
            ? _rpc.RequestAsync("turn/interrupt", new JsonObject { ["threadId"] = SessionId, ["turnId"] = turn }, _closing.Token)
            : Task.CompletedTask;

    /// <summary>The mode holds from the next turn (each turn says its approval policy and sandbox).</summary>
    public Task SetPermissionModeAsync(string mode)
    {
        _mode = mode;
        return Task.CompletedTask;
    }

    public Task SetEffortAsync(string? effort)
    {
        _effort = effort;
        return Task.CompletedTask;
    }

    public Task SetModelAsync(string? model)
    {
        _model = model is null or "default" ? null : model;
        return Task.CompletedTask;
    }

    // ---- What Codex asks ------------------------------------------------------------------------------------

    void OnRequest(string method, JsonElement parameters, JsonNode? id)
    {
        switch (method)
        {
            case "item/commandExecution/requestApproval":
                AskCommand(parameters, id);
                break;
            case "item/fileChange/requestApproval":
                AskChange(parameters, id);
                break;
            case "item/tool/call":
                _ = CallToolAsync(parameters, id);
                break;
            case "item/tool/requestUserInput":
                // Codex's questions come in its plan mode, which Karvon does not turn on: none answered.
                _ = _rpc.RespondAsync(id, new JsonObject { ["answers"] = new JsonObject() });
                break;
            case "mcpServer/elicitation/request":
                _ = _rpc.RespondAsync(id, new JsonObject { ["action"] = "decline" });
                break;
            case "execCommandApproval" or "applyPatchApproval":
                // The first version's questions, for its conversations: Karvon's threads are the second's.
                _ = _rpc.RespondAsync(id, new JsonObject { ["decision"] = "denied" });
                break;
            default:
                _ = _rpc.RespondErrorAsync(id, -32601, $"Karvon does not answer {method}.");
                break;
        }
    }

    void AskCommand(JsonElement parameters, JsonNode? id)
    {
        var command = ClaudeJson.Str(parameters, "command") ?? "";
        var input = JsonDocument.Parse(new JsonObject
        {
            ["command"] = command,
            ["description"] = ClaudeJson.Str(parameters, "reason"),
        }.ToJsonString()).RootElement.Clone();
        // A command that would show a secret does not run, whoever allows it.
        if (_options.GuardSecrets && SecretGuard.Check("Bash", input, _options.WorkingDirectory) is { } refusal)
        {
            Audit?.Decided(null, "Bash", "deny", "Karvon's secret guard", refusal);
            _ = _rpc.RespondAsync(id, new JsonObject { ["decision"] = "decline" });
            Event?.Invoke(new ClaudeNotice(refusal, IsError: true));
            return;
        }
        var requestId = "codex-" + id?.ToJsonString();
        _asked[requestId] = (id, "command");
        var itemId = ClaudeJson.Str(parameters, "itemId");
        Event?.Invoke(new ClaudePermissionRequest(requestId, "Bash", null, input, ClaudeJson.Str(parameters, "reason"),
            [SessionRule("Bash", command)], itemId));
    }

    void AskChange(JsonElement parameters, JsonNode? id)
    {
        var itemId = ClaudeJson.Str(parameters, "itemId") ?? "";
        var changes = _changes.TryGetValue(itemId, out var known) ? known : JsonDocument.Parse("{\"changes\":[]}").RootElement.Clone();
        if (_options.GuardSecrets && PatchPaths(changes).Select(path => SecretGuard.Check("Write", Json(new JsonObject { ["file_path"] = path }), _options.WorkingDirectory))
            .FirstOrDefault(r => r is not null) is { } refusal)
        {
            Audit?.Decided(null, "Patch", "deny", "Karvon's secret guard", refusal);
            _ = _rpc.RespondAsync(id, new JsonObject { ["decision"] = "decline" });
            Event?.Invoke(new ClaudeNotice(refusal, IsError: true));
            return;
        }
        var requestId = "codex-" + id?.ToJsonString();
        _asked[requestId] = (id, "patch");
        Event?.Invoke(new ClaudePermissionRequest(requestId, "Patch", null, changes, ClaudeJson.Str(parameters, "reason"),
            [Json(new JsonObject { ["type"] = "setMode", ["mode"] = "acceptEdits", ["destination"] = "session" })], itemId));
    }

    static IEnumerable<string> PatchPaths(JsonElement changes) =>
        changes.TryGetProperty("changes", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(c => ClaudeJson.Str(c, "path")).OfType<string>()
            : [];

    /// <summary>The "don't ask again" Codex has: this command, for the rest of the conversation.</summary>
    static JsonElement SessionRule(string tool, string content) => Json(new JsonObject
    {
        ["type"] = "addRules",
        ["rules"] = new JsonArray(new JsonObject { ["toolName"] = tool, ["ruleContent"] = content }),
        ["behavior"] = "allow",
        ["destination"] = "session",
    });

    static JsonElement Json(JsonNode node) => JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();

    /// <summary>Codex calls one of Karvon's tools: it runs as Claude Code's calls of it do (the user's rules, the cards, the audit).</summary>
    async Task CallToolAsync(JsonElement parameters, JsonNode? id)
    {
        var tool = ClaudeJson.Str(parameters, "tool") ?? "";
        if (_options.Tools is not { } tools)
        {
            await _rpc.RespondAsync(id, new JsonObject
            {
                ["success"] = false,
                ["contentItems"] = new JsonArray(new JsonObject { ["type"] = "inputText", ["text"] = $"Karvon has no tool named {tool}." }),
            });
            return;
        }
        var call = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = ClaudeJson.Str(parameters, "callId") ?? Guid.NewGuid().ToString(),
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = tool,
                ["arguments"] = parameters.TryGetProperty("arguments", out var arguments) && arguments.ValueKind == JsonValueKind.Object
                    ? JsonNode.Parse(arguments.GetRawText())
                    : new JsonObject(),
            },
        };
        JsonObject answer;
        try
        {
            answer = await tools.HandleAsync(Json(call), _closing.Token, this);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        var result = answer["result"];
        var items = new JsonArray();
        foreach (var content in result?["content"]?.AsArray() ?? [])
            if (content?["type"]?.GetValue<string>() == "image")
                items.Add(new JsonObject { ["type"] = "inputImage", ["imageUrl"] = $"data:{content["mimeType"]};base64,{content["data"]}" });
            else
                items.Add(new JsonObject { ["type"] = "inputText", ["text"] = content?["text"]?.GetValue<string>() ?? "" });
        await _rpc.RespondAsync(id, new JsonObject { ["success"] = result?["isError"]?.GetValue<bool>() != true, ["contentItems"] = items });
    }

    // ---- What Codex tells ------------------------------------------------------------------------------------

    void OnNotification(string method, JsonElement parameters)
    {
        switch (method)
        {
            case "turn/started" when parameters.TryGetProperty("turn", out var turn):
                _turnId = ClaudeJson.Str(turn, "id") ?? _turnId;
                break;
            case "item/started" when parameters.TryGetProperty("item", out var item):
                Started(item);
                break;
            case "item/completed" when parameters.TryGetProperty("item", out var item):
                Completed(item);
                break;
            case "item/agentMessage/delta":
                Event?.Invoke(new ClaudeBlockDelta(ClaudeJson.Str(parameters, "itemId") ?? "", 0, "text", ClaudeJson.Str(parameters, "delta") ?? ""));
                break;
            case "item/reasoning/summaryTextDelta" or "item/reasoning/textDelta":
                Event?.Invoke(new ClaudeBlockDelta(ClaudeJson.Str(parameters, "itemId") ?? "", 0, "thinking", ClaudeJson.Str(parameters, "delta") ?? ""));
                break;
            case "turn/plan/updated":
                Plan(parameters);
                break;
            case "thread/tokenUsage/updated" when parameters.TryGetProperty("tokenUsage", out var usage):
                if (usage.TryGetProperty("last", out var last) && last.TryGetProperty("totalTokens", out var total) && total.TryGetInt32(out var tokens))
                    _contextTokens = tokens;
                if (usage.TryGetProperty("last", out last) && last.TryGetProperty("outputTokens", out var output) && output.TryGetInt32(out var outputTokens))
                    Event?.Invoke(new ClaudeTokens(outputTokens));
                break;
            case "account/rateLimits/updated" when parameters.TryGetProperty("rateLimits", out var limits):
                Event?.Invoke(new ClaudeRateLimits(RateWindows(limits)));
                break;
            case "error" when parameters.TryGetProperty("error", out var error):
                if (!ClaudeJson.Bool(parameters, "willRetry"))
                    Event?.Invoke(new ClaudeNotice(ClaudeJson.Str(error, "message") ?? "Codex reported an error.", IsError: true));
                break;
            case "thread/compacted":
                Event?.Invoke(new ClaudeCompaction("success", null));
                Event?.Invoke(new ClaudeNotice("Conversation compacted", IsError: false));
                break;
            case "serverRequest/resolved" when parameters.TryGetProperty("requestId", out var resolved):
                // Answered (or dropped) on Codex's side: the card goes.
                var key = "codex-" + resolved.GetRawText();
                if (_asked.TryRemove(key, out _))
                    Event?.Invoke(new ClaudePermissionCancelled(key));
                break;
            case "warning" or "configWarning" or "deprecationNotice" or "model/rerouted":
                if ((ClaudeJson.Str(parameters, "message") ?? ClaudeJson.Str(parameters, "summary")) is { Length: > 0 } message)
                    Event?.Invoke(new ClaudeNotice(message, IsError: false));
                break;
            case "turn/completed" when parameters.TryGetProperty("turn", out var turn):
                TurnCompleted(turn);
                break;
        }
    }

    void Started(JsonElement item)
    {
        var id = ClaudeJson.Str(item, "id") ?? "";
        switch (ClaudeJson.Str(item, "type"))
        {
            case "agentMessage":
                Event?.Invoke(new ClaudeMessageStart(id));
                Event?.Invoke(new ClaudeBlockStart(id, 0, "text"));
                break;
            case "reasoning":
                Event?.Invoke(new ClaudeBlockStart(id, 0, "thinking"));
                break;
            case "commandExecution":
                Tool(id, "Bash", new JsonObject { ["command"] = ClaudeJson.Str(item, "command") ?? "" });
                break;
            case "fileChange":
                var changes = Json(new JsonObject { ["changes"] = item.TryGetProperty("changes", out var c) ? JsonNode.Parse(c.GetRawText()) : new JsonArray() });
                _changes[id] = changes;
                Event?.Invoke(new ClaudeAssistant(id + "/call", [new ClaudeToolUseBlock(id, "Patch", changes)], null));
                break;
            case "mcpToolCall":
                Tool(id, $"mcp__{ClaudeJson.Str(item, "server")}__{ClaudeJson.Str(item, "tool")}", Arguments(item));
                break;
            case "dynamicToolCall":
                Tool(id, ClaudeJson.Str(item, "namespace") is ToolNamespace or null
                    ? ClaudeToolServer.Prefix + ClaudeJson.Str(item, "tool")
                    : $"{ClaudeJson.Str(item, "namespace")}__{ClaudeJson.Str(item, "tool")}", Arguments(item));
                break;
            case "webSearch":
                Tool(id, "WebSearch", new JsonObject { ["query"] = ClaudeJson.Str(item, "query") ?? "" });
                break;
            case "contextCompaction":
                Event?.Invoke(new ClaudeCompaction(null, null));
                break;
        }
    }

    static JsonNode Arguments(JsonElement item) =>
        item.TryGetProperty("arguments", out var arguments) && arguments.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(arguments.GetRawText())!
            : new JsonObject();

    void Tool(string id, string name, JsonNode input) =>
        Event?.Invoke(new ClaudeAssistant(id + "/call", [new ClaudeToolUseBlock(id, name, Json(input))], null));

    void Result(string id, string content, bool isError) =>
        Event?.Invoke(new ClaudeToolResults([new ClaudeToolResult(id, content, isError)], null));

    void Completed(JsonElement item)
    {
        var id = ClaudeJson.Str(item, "id") ?? "";
        var status = ClaudeJson.Str(item, "status");
        switch (ClaudeJson.Str(item, "type"))
        {
            case "agentMessage":
                Event?.Invoke(new ClaudeAssistant(id, [new ClaudeTextBlock(ClaudeJson.Str(item, "text") ?? "")], null));
                break;
            case "reasoning":
                var thought = string.Join("\n\n", Strings(item, "summary").Concat(Strings(item, "content")).Where(t => t.Trim().Length > 0));
                if (thought.Length > 0)
                    Event?.Invoke(new ClaudeAssistant(id, [new ClaudeThinkingBlock(thought)], null));
                break;
            case "commandExecution":
                var exit = item.TryGetProperty("exitCode", out var code) && code.TryGetInt32(out var exitCode) ? exitCode : (int?)null;
                var output = ClaudeJson.Str(item, "aggregatedOutput") ?? "";
                Result(id, status == "declined" ? "The user declined this command." : exit is { } x and not 0 && output.Trim().Length == 0 ? $"Exit code {x}" : output,
                    status is "failed" or "declined" || exit is not (null or 0));
                break;
            case "fileChange":
                _changes.TryRemove(id, out _);
                Result(id, status switch
                {
                    "declined" => "The user declined this change.",
                    "failed" => "The change could not be applied.",
                    _ => string.Join("\n", PatchPaths(Json(new JsonObject { ["changes"] = item.TryGetProperty("changes", out var c) ? JsonNode.Parse(c.GetRawText()) : new JsonArray() }))),
                }, status is "failed" or "declined");
                break;
            case "mcpToolCall":
                Result(id, item.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
                    ? ClaudeJson.Str(error, "message") ?? "Failed."
                    : ContentText(item.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object && result.TryGetProperty("content", out var content) ? content : default),
                    status == "failed");
                break;
            case "dynamicToolCall":
                Result(id, ContentText(item.TryGetProperty("contentItems", out var items) ? items : default), status == "failed" || item.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False);
                break;
            case "webSearch":
                Result(id, "Searched the web.", false);
                break;
            case "contextCompaction":
                Event?.Invoke(new ClaudeCompaction("success", null));
                break;
        }
    }

    static IEnumerable<string> Strings(JsonElement item, string name) =>
        item.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)
            : [];

    /// <summary>The text of a tool's content items (MCP's "text", a dynamic tool's "inputText").</summary>
    static string ContentText(JsonElement content) =>
        content.ValueKind == JsonValueKind.Array
            ? string.Join("\n", content.EnumerateArray().Select(c => ClaudeJson.Str(c, "text")).OfType<string>())
            : "";

    /// <summary>Codex's plan as the panel's to-do list.</summary>
    void Plan(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("plan", out var steps) || steps.ValueKind != JsonValueKind.Array)
            return;
        var todos = new JsonArray([.. steps.EnumerateArray().Select(s => (JsonNode)new JsonObject
        {
            ["content"] = ClaudeJson.Str(s, "step") ?? "",
            ["status"] = ClaudeJson.Str(s, "status") switch { "inProgress" => "in_progress", "completed" => "completed", _ => "pending" },
        })]);
        var id = $"plan-{_turnId}-{++_plans}";
        Tool(id, "TodoWrite", new JsonObject { ["todos"] = todos });
        Result(id, "", false);
    }

    /// <summary>Codex's limits (its primary and secondary windows) under the names the panel shows Claude's by.</summary>
    internal static IReadOnlyList<ClaudeRateWindow> RateWindows(JsonElement limits)
    {
        var windows = new List<ClaudeRateWindow>();
        foreach (var which in new[] { "primary", "secondary" })
            if (limits.TryGetProperty(which, out var window) && window.ValueKind == JsonValueKind.Object
                && window.TryGetProperty("usedPercent", out var used) && used.TryGetDouble(out var percent))
            {
                var minutes = window.TryGetProperty("windowDurationMins", out var d) && d.TryGetInt64(out var m) ? m : 0;
                var name = minutes switch { 300 => "five_hour", 10_080 => "seven_day", > 0 => $"{minutes / 1440}_day", _ => which };
                var resets = window.TryGetProperty("resetsAt", out var r) && r.TryGetInt64(out var at) ? DateTimeOffset.FromUnixTimeSeconds(at) : (DateTimeOffset?)null;
                windows.Add(new ClaudeRateWindow(name, percent / 100, resets));
            }
        return windows;
    }

    void TurnCompleted(JsonElement turn)
    {
        var status = ClaudeJson.Str(turn, "status");
        var duration = turn.TryGetProperty("durationMs", out var d) && d.TryGetInt64(out var ms) ? ms : 0;
        var error = turn.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object ? ClaudeJson.Str(e, "message") : null;
        _turnId = null;
        // Questions of the turn left unanswered go with it.
        foreach (var asked in _asked.Keys.ToList())
            if (_asked.TryRemove(asked, out _))
                Event?.Invoke(new ClaudePermissionCancelled(asked));
        Event?.Invoke(status switch
        {
            "completed" => new ClaudeResult("success", false, duration, null, null, SessionId, _contextTokens),
            "interrupted" => new ClaudeResult("error_during_execution", true, duration, null, null, SessionId, _contextTokens),
            _ => new ClaudeResult("error", true, duration, null, error ?? "Codex could not finish the turn.", SessionId, _contextTokens),
        });
    }

    public async ValueTask DisposeAsync()
    {
        await _closing.CancelAsync();
        await _rpc.DisposeAsync();
        _closing.Dispose();
    }
}
