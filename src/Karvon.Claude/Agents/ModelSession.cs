using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Karvon.Claude.Tools;
using Karvon.ViewModels;

namespace Karvon.Claude;

/// <summary>
/// A conversation with a model Karvon drives itself, through an OpenAI-compatible endpoint (OpenRouter's hundreds of models,
/// a local one in Ollama or LM Studio): Karvon's own agent loop. The model answers with text and tool calls; the tools are
/// Claude Code's in name and shape (<see cref="ModelTools"/>) and Karvon's own, so the panel shows them as it shows Claude's,
/// and every call goes through Karvon first: the secret guard, the panel's mode and the user's yes on a card. What a tool
/// gives the model has the secrets Karvon knows taken out.
/// </summary>
public sealed class ModelSession : IAgentSession
{
    /// <summary>How many answers of the model a prompt may take (each tool call's result asks for another).</summary>
    public const int MaxSteps = 60;

    /// <summary>Karvon's tool that finds its other tools (not every one is in each request: there are many).</summary>
    public const string ToolSearch = "ToolSearch";

    static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>The conversations of this run of Karvon, by id: a session started again (another folder, back from the background) goes on with its own.</summary>
    static readonly ConcurrentDictionary<string, List<JsonObject>> Conversations = new();

    readonly IChatEndpoint _api;
    readonly AgentStartOptions _options;
    readonly string _agentName;
    readonly ConcurrentDictionary<string, TaskCompletionSource<ClaudePermissionDecision>> _asked = new();
    readonly HashSet<string> _allowedCommands = new(StringComparer.Ordinal);
    readonly HashSet<string> _loaded = new(StringComparer.Ordinal);
    readonly Dictionary<string, int?> _contexts = new(StringComparer.Ordinal);
    List<JsonObject> _messages = [];
    CancellationTokenSource? _turn;
    string _mode;
    string? _model;
    int? _contextTokens;
    bool _disposed;

    ModelSession(IChatEndpoint api, AgentStartOptions options, string agentName)
    {
        _api = api;
        _options = options;
        _agentName = agentName;
        _mode = options.PermissionMode;
        _model = options.Model is "default" or "" ? null : options.Model;
    }

    public event Action<ClaudeEvent>? Event;

    public string? SessionId { get; private set; }

    public string? WorkingDirectory => _options.WorkingDirectory;

    public bool HasExited => _disposed;

    public JsonElement Initialization { get; private set; }

    public IReadOnlyList<string> HiddenVariables => [];

    public ClaudeAudit? Audit { get; private init; }

    /// <summary>The model the next request goes to.</summary>
    public string? Model => _model;

    /// <summary>
    /// Asks <paramref name="api"/> for its models, then is ready for a prompt. <paramref name="agentName"/> is the provider's
    /// name (OpenRouter, Ollama...), as the model is told who it is.
    /// </summary>
    public static async Task<ModelSession> StartAsync(IChatEndpoint api, string agentName, AgentStartOptions options, CancellationToken cancellationToken = default)
    {
        var session = new ModelSession(api, options, agentName)
        {
            Audit = options.AuditDirectory is { } audit ? ClaudeAudit.Start(audit) : null,
        };
        IReadOnlyList<(string Id, string Name, string? Description, int? Context)> models;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            models = await api.ModelsAsync(timeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException && !cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException($"{agentName} did not answer at {api.BaseUrl}: {ex.Message}", ex);
        }
        foreach (var model in models)
            session._contexts[model.Id] = model.Context;
        session._model ??= models.FirstOrDefault().Id
            ?? throw new InvalidOperationException($"{agentName} has no model to answer with" + (agentName == "Ollama" ? ": pull one first (ollama pull qwen3-coder)." : "."));
        session.Initialization = JsonDocument.Parse(new JsonObject
        {
            ["models"] = new JsonArray([.. models.Select(m => (JsonNode)new JsonObject
            {
                ["value"] = m.Id,
                ["displayName"] = m.Name,
                ["description"] = m.Description,
                ["contextWindow"] = m.Context,
            })]),
        }.ToJsonString()).RootElement.Clone();
        session.Initialization = CodexSession.WithCurrentModel(session.Initialization, session._model);
        if (options.Resume is { } resume && (Conversations.TryGetValue(resume, out var earlier) || session.Saved(resume) is { } saved && (earlier = saved) is not null))
        {
            session.SessionId = resume;
            session._messages = earlier;
            Conversations[resume] = earlier;
        }
        else
        {
            session.SessionId = Guid.NewGuid().ToString();
            session._messages = [new JsonObject { ["role"] = "system", ["content"] = session.SystemPrompt() }];
            Conversations[session.SessionId] = session._messages;
        }
        session.Audit?.SessionId = session.SessionId;
        return session;
    }

    /// <summary>What the model is told first: where it is, how to work, Karvon's tools and the project's own instructions (AGENTS.md, CLAUDE.md).</summary>
    string SystemPrompt()
    {
        var prompt = new StringBuilder();
        prompt.Append($"You are a coding agent ({_agentName}) inside Karvon Studio, the user's IDE. You work on the user's code in {_options.WorkingDirectory} "
            + $"({RuntimeInformationText()}); today is {DateTime.Now:yyyy-MM-dd}.\n\n");
        prompt.Append("Do what the user asks with your tools. Find and read the code before you change it; never guess what a file holds. "
            + "Change files with Edit (an exact, unique old_string) or Write (a new file); keep changes small and in the code's own style. "
            + $"Check your work: build and run the tests. Use {ModelTools.Shell} for what the other tools do not do. "
            + "When a call is refused or denied, do not try it another way: say what you wanted to do. "
            + "Answer briefly in Markdown, in the user's language: what you did, what you checked, what is left.");
        if (_options.Tools is { } tools)
        {
            prompt.Append("\n\n").Append(tools.SystemPrompt).Append("\n\n").Append(tools.Instructions);
            // A small model does not search for what it does not know is there: every tool is named, its parameters come with its first call.
            var others = tools.Tools.Where(t => !tools.IsAlwaysLoaded(t.Name)).ToList();
            if (others.Count > 0)
                prompt.Append($"\n\nKarvon's other tools: call one by its name ({ClaudeToolServer.Prefix}<name>); its parameters come with your first call of it, "
                    + $"or ask {ToolSearch} for them first.\n").Append(string.Join("\n", others.Select(t => $"- {t.Name}: {ChatCompletions.FirstSentence(t.Description)}")));
        }
        foreach (var name in new[] { "AGENTS.md", "CLAUDE.md" })
        {
            var file = Path.Combine(_options.WorkingDirectory, name);
            try
            {
                if (File.Exists(file))
                {
                    var text = File.ReadAllText(file);
                    prompt.Append($"\n\nThe project's instructions ({name}):\n").Append(text.Length > 20_000 ? text[..20_000] : text);
                    break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return prompt.ToString();
    }

    /// <summary>The state file of a conversation (<see cref="AgentStartOptions.StateDirectory"/>); null without one.</summary>
    string? StateFile(string sessionId) =>
        _options.StateDirectory is { } folder ? AgentTranscripts.StateFile(Path.Combine(folder, AgentTranscripts.FileName(sessionId))) : null;

    /// <summary>The messages kept of an earlier run of Karvon's; null when there are none.</summary>
    List<JsonObject>? Saved(string sessionId)
    {
        try
        {
            return StateFile(sessionId) is { } file && File.Exists(file) && JsonNode.Parse(File.ReadAllText(file)) is JsonArray messages
                ? [.. messages.OfType<JsonObject>().Select(m => (JsonObject)m.DeepClone())]
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>The conversation's messages kept for a later run of Karvon (after each turn).</summary>
    void Save()
    {
        if (SessionId is not { } id || StateFile(id) is not { } file)
            return;
        try
        {
            string json;
            lock (_messages)
                json = new JsonArray([.. _messages.Select(m => m.DeepClone())]).ToJsonString();
            AgentTranscripts.PrivateFolder(Path.GetDirectoryName(file)!);
            using var stream = AgentTranscripts.OpenPrivate(file, FileMode.Create);
            stream.Write(System.Text.Encoding.UTF8.GetBytes(json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    static string RuntimeInformationText() => System.Runtime.InteropServices.RuntimeInformation.OSDescription;

    // ---- The panel asks ------------------------------------------------------------------------------------

    public Task SendUserMessageAsync(string text, IEnumerable<string>? extraBlocks = null, IEnumerable<ClaudeImage>? images = null)
    {
        var all = string.Join("\n\n", new[] { text }.Concat(extraBlocks ?? []));
        var pictures = images?.ToList() ?? [];
        JsonNode content = pictures.Count == 0
            ? JsonValue.Create(all)
            : new JsonArray([.. pictures.Select(i => (JsonNode)new JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JsonObject { ["url"] = $"data:{i.MediaType};base64,{i.Base64}" },
            }), new JsonObject { ["type"] = "text", ["text"] = all }]);
        lock (_messages)
            _messages.Add(new JsonObject { ["role"] = "user", ["content"] = content });
        return StartTurn(RunTurnAsync);
    }

    public Task SendCommandAsync(string command) =>
        command.Trim() is "/compact" ? StartTurn(CompactTurnAsync) : SendUserMessageAsync(command);

    Task StartTurn(Func<CancellationToken, Task> turn)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var cancellation = new CancellationTokenSource();
        _turn = cancellation;
        // The turn runs on its own; what it does comes as events, its end as a result.
        _ = Task.Run(() => turn(cancellation.Token));
        return Task.CompletedTask;
    }

    public Task RespondToPermissionAsync(string requestId, ClaudePermissionDecision decision, string by = "the user")
    {
        if (_asked.TryRemove(requestId, out var asked))
            asked.TrySetResult(decision);
        return Task.CompletedTask;
    }

    public Task InterruptAsync()
    {
        _turn?.Cancel();
        return Task.CompletedTask;
    }

    public Task SetPermissionModeAsync(string mode)
    {
        _mode = mode;
        return Task.CompletedTask;
    }

    public Task SetModelAsync(string? model)
    {
        if (model is { Length: > 0 } and not "default")
            _model = model;
        return Task.CompletedTask;
    }

    // ---- A turn ---------------------------------------------------------------------------------------------

    /// <summary>A turn ends: the conversation is kept, then the panel is told.</summary>
    void End(ClaudeResult result)
    {
        Save();
        Event?.Invoke(result);
    }

    async Task RunTurnAsync(CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var nudged = false;
        try
        {
            for (var step = 0; step < MaxSteps; step++)
            {
                if (TooLong())
                    await CompactAsync(cancellationToken);
                var reply = await AskModelAsync(cancellationToken);
                // A thinking model sometimes ends with its answer in its thoughts and none for the user: asked for it once.
                if (reply.ToolCalls.Count == 0 && reply.Text.Trim().Length == 0 && reply.Thinking.Trim().Length > 0 && !nudged)
                {
                    nudged = true;
                    lock (_messages)
                        _messages.Add(new JsonObject { ["role"] = "user", ["content"] = "(Karvon) Your reply had thoughts but no answer: write the answer to the user now." });
                    continue;
                }
                if (reply.ToolCalls.Count == 0)
                {
                    End(new ClaudeResult(reply.FinishReason == "length" ? "error_max_tokens" : "success", reply.FinishReason == "length",
                        clock.ElapsedMilliseconds, null, reply.FinishReason == "length" ? "The model's answer was cut off: it reached its length limit." : null,
                        SessionId, _contextTokens, Window()));
                    return;
                }
                foreach (var call in reply.ToolCalls)
                {
                    var (output, isError) = await CallAsync(call, cancellationToken);
                    Event?.Invoke(new ClaudeToolResults([new ClaudeToolResult(call.Id, output, isError)], null));
                    lock (_messages)
                        _messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = call.Id, ["content"] = Redaction.Secrets(output) });
                }
            }
            End(new ClaudeResult("error_max_turns", true, clock.ElapsedMilliseconds, null, null, SessionId, _contextTokens, Window()));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CancelQuestions();
            End(new ClaudeResult("error_during_execution", true, clock.ElapsedMilliseconds, null, null, SessionId, _contextTokens, Window()));
        }
        // Whatever went wrong ends the turn with it shown.
        catch (Exception ex)
        {
            CancelQuestions();
            End(new ClaudeResult("error", true, clock.ElapsedMilliseconds, null, $"{_agentName}: {ex.Message}", SessionId, _contextTokens, Window()));
        }
    }

    /// <summary>One answer of the model, streamed to the panel; it joins the conversation.</summary>
    async Task<ModelReply> AskModelAsync(CancellationToken cancellationToken)
    {
        var id = "msg_" + Guid.NewGuid().ToString("N")[..16];
        var text = false;
        var thinking = false;
        Event?.Invoke(new ClaudeMessageStart(id));
        var reply = await _api.StreamAsync(Body(), piece =>
        {
            if (!text)
                Event?.Invoke(new ClaudeBlockStart(id, 1, "text"));
            text = true;
            Event?.Invoke(new ClaudeBlockDelta(id, 1, "text", piece));
        }, piece =>
        {
            if (!thinking)
                Event?.Invoke(new ClaudeBlockStart(id, 0, "thinking"));
            thinking = true;
            Event?.Invoke(new ClaudeBlockDelta(id, 0, "thinking", piece));
        }, cancellationToken);
        if (reply.PromptTokens is { } prompt)
        {
            _contextTokens = prompt + (reply.OutputTokens ?? 0);
            Event?.Invoke(new ClaudeContextUsage(_contextTokens.Value, Window()));
        }
        if (reply.OutputTokens is { } output)
            Event?.Invoke(new ClaudeTokens(output));
        var blocks = new List<ClaudeBlock>();
        if (reply.Thinking.Trim().Length > 0)
            blocks.Add(new ClaudeThinkingBlock(reply.Thinking));
        if (reply.Text.Trim().Length > 0)
            blocks.Add(new ClaudeTextBlock(reply.Text));
        if (blocks.Count > 0)
            Event?.Invoke(new ClaudeAssistant(id, blocks, null));
        var message = new JsonObject { ["role"] = "assistant", ["content"] = reply.Text.Length > 0 ? reply.Text : null };
        // Claude's own blocks (its thinking, signed), sent back as they were.
        if (reply.Blocks is { } own)
            message[AnthropicChat.BlocksKey] = own.DeepClone();
        if (reply.ToolCalls.Count > 0)
            message["tool_calls"] = new JsonArray([.. reply.ToolCalls.Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.Id,
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Arguments },
            })]);
        lock (_messages)
            _messages.Add(message);
        return reply;
    }

    JsonObject Body()
    {
        JsonArray messages;
        lock (_messages)
            messages = new JsonArray([.. _messages.Select(m => m.DeepClone())]);
        return new JsonObject
        {
            ["model"] = _model,
            ["messages"] = messages,
            ["tools"] = new JsonArray([.. Functions()]),
        };
    }

    /// <summary>The tools of the next request: the agent's own, and those of Karvon's that are always there or were found.</summary>
    IEnumerable<JsonNode> Functions()
    {
        foreach (var tool in ModelTools.Definitions())
            yield return tool;
        if (_options.Tools is not { } tools)
            yield break;
        foreach (var tool in tools.Tools.Where(t => tools.IsAlwaysLoaded(t.Name) || _loaded.Contains(t.Name)))
            yield return new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = ClaudeToolServer.Prefix + tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = tool.InputSchema.DeepClone(),
                },
            };
        if (tools.Tools.Any(t => !tools.IsAlwaysLoaded(t.Name) && !_loaded.Contains(t.Name)))
            yield return new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = ToolSearch,
                    ["description"] = "Finds Karvon's other tools by what they do (database, docker, server, jira, pipeline, pull request, kubernetes, http, "
                        + "document, browser, debug...) and makes them callable from the next step.",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject { ["query"] = new JsonObject { ["type"] = "string", ["description"] = "Words for what the tool does" } },
                        ["required"] = new JsonArray("query"),
                    },
                },
            };
    }

    /// <summary>Runs a call the model asked for, as Karvon allows it: what it gave and whether it failed.</summary>
    async Task<(string Output, bool IsError)> CallAsync(ModelToolCall call, CancellationToken cancellationToken)
    {
        // One of Karvon's tools called by its bare name (as the prompt lists them): it is that tool.
        if (_options.Tools is { } named && named.Find(call.Name) is null && ModelTools.Definitions().All(d => d["function"]?["name"]?.GetValue<string>() != call.Name)
            && call.Name != ToolSearch && named.Find(ClaudeToolServer.Prefix + call.Name) is not null)
            call = call with { Name = ClaudeToolServer.Prefix + call.Name };
        JsonElement input;
        try
        {
            using var document = JsonDocument.Parse(call.Arguments.Trim().Length == 0 ? "{}" : call.Arguments);
            input = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            Event?.Invoke(new ClaudeAssistant(call.Id + "/call", [new ClaudeToolUseBlock(call.Id, call.Name, JsonDocument.Parse("{}").RootElement.Clone())], null));
            return ($"The arguments were not valid JSON ({ex.Message}): call {call.Name} again with a JSON object.", true);
        }
        Event?.Invoke(new ClaudeAssistant(call.Id + "/call", [new ClaudeToolUseBlock(call.Id, call.Name, input)], null));
        if (input.ValueKind != JsonValueKind.Object)
            return ($"The arguments of {call.Name} must be a JSON object.", true);
        if (call.Name == ToolSearch)
            return (Search(ClaudeJson.Str(input, "query") ?? ""), false);
        if (call.Name.StartsWith(ClaudeToolServer.Prefix, StringComparison.Ordinal) && _options.Tools is { } tools)
            return await CallKarvonAsync(tools, call.Name, input, cancellationToken);
        if (!ModelTools.Definitions().Any(d => d["function"]?["name"]?.GetValue<string>() == call.Name))
            return ($"There is no tool named {call.Name}.", true);
        // Karvon's secret guard reads the call as it reads Claude Code's (the shell's as Bash).
        if (_options.GuardSecrets && SecretGuard.Check(call.Name == "PowerShell" ? "Bash" : call.Name, input, _options.WorkingDirectory) is { } refusal)
        {
            Audit?.Decided(null, call.Name, "deny", "Karvon's secret guard", refusal);
            return (refusal, true);
        }
        switch (Rule(call.Name, input))
        {
            case ToolRule.Deny:
                return ("In plan mode nothing is changed: say what you would change instead.", true);
            case ToolRule.Ask:
                var decision = await AskAsync(call, input, cancellationToken);
                if (!decision.Allow)
                {
                    if (decision.Interrupt)
                        _turn?.Cancel();
                    return (decision.Message ?? "The user said no. Do not try it another way: wait for what they say.", true);
                }
                if (decision.UpdatedInput is { ValueKind: JsonValueKind.Object } changed)
                    input = changed;
                break;
        }
        var (output, isError) = await ModelTools.RunAsync(call.Name, input, _options.WorkingDirectory, cancellationToken);
        Audit?.Ran(call.Name, input, DateTime.UtcNow, TimeSpan.Zero, isError, output, Redaction.Secrets(output), null, 0);
        return (output, isError);
    }

    /// <summary>Whether a call runs at once, is asked about, or is refused, by the panel's mode.</summary>
    ToolRule Rule(string tool, JsonElement input)
    {
        if (tool is "Read" or "Glob" or "Grep")
            return ToolRule.Allow;
        if (ModelTools.Changing.Contains(tool))
            return _mode switch
            {
                "plan" => ToolRule.Deny,
                "acceptEdits" or "bypassPermissions" => ToolRule.Allow,
                _ => ToolRule.Ask,
            };
        // The shell.
        if (_mode == "bypassPermissions")
            return ToolRule.Allow;
        return ClaudeJson.Str(input, "command") is { } command && _allowedCommands.Contains(command.Trim()) ? ToolRule.Allow : ToolRule.Ask;
    }

    /// <summary>Asks the user on the panel's card; their answer may also allow more for the rest of the conversation.</summary>
    async Task<ClaudePermissionDecision> AskAsync(ModelToolCall call, JsonElement input, CancellationToken cancellationToken)
    {
        var requestId = "model-" + Guid.NewGuid().ToString("N");
        var answer = new TaskCompletionSource<ClaudePermissionDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        _asked[requestId] = answer;
        var suggestion = ModelTools.Changing.Contains(call.Name)
            ? new JsonObject { ["type"] = "setMode", ["mode"] = "acceptEdits", ["destination"] = "session" }
            : new JsonObject
            {
                ["type"] = "addRules",
                ["rules"] = new JsonArray(new JsonObject { ["toolName"] = call.Name, ["ruleContent"] = ClaudeJson.Str(input, "command")?.Trim() }),
                ["behavior"] = "allow",
                ["destination"] = "session",
            };
        Audit?.Decided(null, call.Name, "ask", "Karvon, by the panel's mode");
        Event?.Invoke(new ClaudePermissionRequest(requestId, call.Name, null, input, ClaudeJson.Str(input, "description"),
            [JsonDocument.Parse(suggestion.ToJsonString()).RootElement.Clone()], call.Id));
        ClaudePermissionDecision decision;
        using (cancellationToken.Register(() => answer.TrySetCanceled(cancellationToken)))
        {
            try
            {
                decision = await answer.Task;
            }
            finally
            {
                _asked.TryRemove(requestId, out _);
            }
        }
        Audit?.Decided(null, call.Name, decision.Allow ? "allow" : "deny", "the user, on Karvon's card");
        foreach (var permission in decision.Allow ? decision.UpdatedPermissions ?? [] : [])
            switch (ClaudeJson.Str(permission, "type"))
            {
                case "setMode" when ClaudeJson.Str(permission, "mode") is { } mode:
                    _mode = mode;
                    Event?.Invoke(new ClaudeModeChanged(mode));
                    break;
                case "addRules" when permission.TryGetProperty("rules", out var rules) && rules.ValueKind == JsonValueKind.Array:
                    foreach (var rule in rules.EnumerateArray())
                        if (ClaudeJson.Str(rule, "ruleContent") is { Length: > 0 } content)
                            _allowedCommands.Add(content);
                    break;
            }
        return decision;
    }

    void CancelQuestions()
    {
        foreach (var id in _asked.Keys.ToList())
            if (_asked.TryRemove(id, out var asked))
            {
                asked.TrySetCanceled();
                Event?.Invoke(new ClaudePermissionCancelled(id));
            }
    }

    /// <summary>One of Karvon's own tools: its server applies the user's rules and asks on its own card, as for Claude Code.</summary>
    async Task<(string, bool)> CallKarvonAsync(ClaudeToolServer tools, string name, JsonElement input, CancellationToken cancellationToken)
    {
        if (tools.Find(name) is not { } tool)
            return ($"Karvon has no tool named {name}.", true);
        // Called by its name from the system prompt's list: its parameters go with the next requests.
        _loaded.Add(tool.Name);
        if (_mode == "plan" && tools.Plan(name, input) is { Access: ToolAccess.Write or ToolAccess.Destructive })
            return ("In plan mode nothing is changed: say what you would change instead.", true);
        var call = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = Guid.NewGuid().ToString(),
            ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = tool.Name, ["arguments"] = JsonNode.Parse(input.GetRawText()) },
        };
        var answer = await tools.HandleAsync(JsonDocument.Parse(call.ToJsonString()).RootElement.Clone(), cancellationToken, this);
        var result = answer["result"];
        var text = string.Join("\n", (result?["content"]?.AsArray() ?? []).Where(c => c?["type"]?.GetValue<string>() == "text").Select(c => c!["text"]!.GetValue<string>()));
        return (text, result?["isError"]?.GetValue<bool>() == true);
    }

    /// <summary>Karvon's tools whose name or description has the query's words; they are given from the next request on.</summary>
    string Search(string query)
    {
        if (_options.Tools is not { } tools)
            return "Karvon gives no other tools here.";
        var words = query.ToLowerInvariant().Split([' ', ',', '_', '-'], StringSplitOptions.RemoveEmptyEntries);
        var found = tools.Tools
            .Select(t => (Tool: t, Score: words.Count(w => t.Name.Contains(w, StringComparison.OrdinalIgnoreCase) || t.Description.Contains(w, StringComparison.OrdinalIgnoreCase))))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Take(8)
            .Select(x => x.Tool)
            .ToList();
        if (found.Count == 0)
            return "No tool of Karvon's does that. Its tools: " + string.Join(", ", tools.Tools.Select(t => t.Name));
        foreach (var tool in found)
            _loaded.Add(tool.Name);
        return "These tools can be called now:\n" + string.Join("\n", found.Select(t => $"{ClaudeToolServer.Prefix}{t.Name}: {t.Description}"));
    }

    // ---- Compaction -----------------------------------------------------------------------------------------

    /// <summary>The model's context window, when its provider says it.</summary>
    int? Window() => _model is { } model && _contexts.TryGetValue(model, out var window) ? window : null;

    /// <summary>The context nears the model's window (when its provider says how big it is).</summary>
    bool TooLong() =>
        _contextTokens is { } used && _model is { } model && _contexts.TryGetValue(model, out var window) && window is { } size && used > size * 0.85;

    async Task CompactTurnAsync(CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            await CompactAsync(cancellationToken);
            End(new ClaudeResult("success", false, clock.ElapsedMilliseconds, null, null, SessionId, _contextTokens, Window()));
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException or OperationCanceledException)
        {
            Event?.Invoke(new ClaudeCompaction("failed", ex.Message));
            End(new ClaudeResult(cancellationToken.IsCancellationRequested ? "error_during_execution" : "error", true, clock.ElapsedMilliseconds, null,
                cancellationToken.IsCancellationRequested ? null : ex.Message, SessionId, _contextTokens, Window()));
        }
    }

    /// <summary>The conversation so far becomes a summary of it (the system prompt stays): the model goes on from there.</summary>
    async Task CompactAsync(CancellationToken cancellationToken)
    {
        Event?.Invoke(new ClaudeCompaction(null, null));
        List<JsonObject> messages;
        lock (_messages)
            messages = [.. _messages.Skip(1)];
        if (messages.Count == 0)
        {
            Event?.Invoke(new ClaudeCompaction("success", null));
            return;
        }
        var transcript = new StringBuilder();
        foreach (var message in messages)
        {
            var role = message["role"]?.GetValue<string>();
            var content = message["content"] is JsonValue value && value.TryGetValue<string>(out var s) ? s : message["content"]?.ToJsonString() ?? "";
            if (content.Length > 2000)
                content = content[..2000] + " …";
            transcript.Append(role).Append(": ").AppendLine(content);
            foreach (var call in message["tool_calls"]?.AsArray() ?? [])
                transcript.Append("  (called ").Append(call?["function"]?["name"]).Append(' ').Append(call?["function"]?["arguments"]).AppendLine(")");
        }
        var summary = await _api.CompleteAsync(new JsonObject
        {
            ["model"] = _model,
            ["messages"] = new JsonArray(
                new JsonObject
                {
                    ["role"] = "system",
                    ["content"] = "Summarize this coding conversation so that it can go on from the summary alone: what the user asked, what was "
                        + "found, what was changed (files, functions), what was checked, and what is left to do. Keep names, paths and errors exactly.",
                },
                new JsonObject { ["role"] = "user", ["content"] = Redaction.Secrets(transcript.ToString()) }),
        }, cancellationToken);
        lock (_messages)
        {
            _messages.RemoveRange(1, _messages.Count - 1);
            _messages.Add(new JsonObject { ["role"] = "user", ["content"] = "This conversation was compacted. What came before, in short:\n\n" + summary });
            _messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = "Understood: I go on from this summary." });
        }
        _contextTokens = null;
        Event?.Invoke(new ClaudeCompaction("success", null));
        Event?.Invoke(new ClaudeNotice("Conversation compacted", IsError: false));
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _turn?.Cancel();
        CancelQuestions();
        return ValueTask.CompletedTask;
    }

    /// <summary>The shared client that requests go out with (a session's own stop cancels its own).</summary>
    public static HttpClient Client => Http;
}
