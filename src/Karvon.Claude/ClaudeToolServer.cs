using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Karvon.Claude.Tools;

namespace Karvon.Claude;

/// <summary>
/// Karvon's tools as an MCP server inside Karvon: the CLI is given it with <c>--mcp-config</c> as an "sdk" server and talks to it
/// over its control protocol (mcp_message requests carrying JSON-RPC), as the Agent SDK's in-process servers. No port, no
/// token: only the CLI Karvon started can call it. Claude sees the tools as mcp__karvon__&lt;name&gt;.
/// </summary>
/// <param name="systemPrompt">What a session's system prompt gets (--append-system-prompt): to use these tools.</param>
/// <param name="shellRules">The shell commands these tools do: a session's Bash call of one is sent to them (a hook).</param>
/// <param name="toolsFirst">Whether the prompt and the rules hold now (a setting, read as each session starts); always by default.</param>
public sealed class ClaudeToolServer(IReadOnlyList<IClaudeTool> tools, string instructions, string? systemPrompt = null,
    IReadOnlyList<ClaudeShellRule>? shellRules = null, Func<bool>? toolsFirst = null, IReadOnlySet<string>? alwaysLoaded = null)
{
    public const string Name = KarvonToolNames.Server;

    public const string Prefix = KarvonToolNames.Prefix;

    readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new();

    public IReadOnlyList<IClaudeTool> Tools { get; } = tools;

    readonly IReadOnlyList<ClaudeShellRule> _shellRules = shellRules ?? [];

    public string? SystemPrompt => toolsFirst?.Invoke() is false ? null : systemPrompt;

    public IReadOnlyList<ClaudeShellRule> ShellRules => toolsFirst?.Invoke() is false ? [] : _shellRules;

    /// <summary>The rule a Bash command run in <paramref name="workspace"/> breaks (it runs what one of these tools does), or null.</summary>
    public ClaudeShellRule? ShellRuleFor(string command, string? workspace = null) => ShellRules.FirstOrDefault(r => r.Applies(command, workspace));

    /// <summary>A call ran (on the thread it ran on), with its input and what it gave: the host shows it where the user would have done it.</summary>
    public event Action<IClaudeTool, JsonElement, ClaudeToolOutput>? Called;

    /// <summary>The <c>--mcp-config</c> value that adds the server beside the user's own.</summary>
    public static string McpConfig => new JsonObject { ["mcpServers"] = new JsonObject { [Name] = new JsonObject { ["type"] = "sdk", ["name"] = Name } } }.ToJsonString();

    /// <summary>The tool behind a name Claude uses (mcp__karvon__db_query), or null for any other tool.</summary>
    public IClaudeTool? Find(string toolName) =>
        toolName.StartsWith(Prefix, StringComparison.Ordinal) ? Tools.FirstOrDefault(t => t.Name == toolName.Substring(Prefix.Length)) : null;

    /// <summary>The plan of a call to one of these tools; null for any other tool, or input it cannot read.</summary>
    public ClaudeToolPlan? Plan(string toolName, JsonElement input) => Find(toolName) is { } tool ? PlanOf(tool, input) : null;

    static ClaudeToolPlan? PlanOf(IClaudeTool tool, JsonElement input)
    {
        try
        {
            // A call that only reads has no question of its own: asked when the user's rules say so.
            var plan = tool.Plan(input);
            return plan.Question.Length == 0 ? plan with { Question = $"Allow Claude to use {tool.Name}?" } : plan;
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException or KeyNotFoundException)
        {
            return null;
        }
    }

    /// <summary>The user's rules for these tools (<see cref="ClaudeToolPolicy"/>), read at each call; none: as each call's access says.</summary>
    public Func<IReadOnlyDictionary<string, string>?>? Rules { get; set; }

    /// <summary>
    /// Asks the user about a call the rules say to ask for when Claude Code did not ask (Bypass permissions, auto mode, an
    /// allow rule of its own): true when they allow it. Without it such a call does not run.
    /// </summary>
    public Func<ClaudeToolRequest, CancellationToken, Task<bool>>? Ask { get; set; }

    readonly ConcurrentDictionary<string, DateTime> _approved = new();

    /// <summary>The user allowed this call on Claude Code's prompt: it runs when it comes without being asked again (once, within two minutes).</summary>
    public void Approve(string toolName, JsonElement input) => _approved[Key(toolName, input)] = DateTime.UtcNow;

    /// <summary>Whether the folder of the session a call comes from (the caller) is trusted (<see cref="WorkspaceTrust"/>); none: all are.</summary>
    public Func<object?, bool>? Trusted { get; set; }

    /// <summary>What the user's rules say of a call of one of these tools from <paramref name="caller"/>; null for any other tool.</summary>
    public ClaudeToolDecision? Decide(string toolName, JsonElement input, object? caller = null) =>
        Find(toolName) is { } tool ? Decide(tool, input, caller) : null;

    ClaudeToolDecision Decide(IClaudeTool tool, JsonElement input, object? caller)
    {
        string? target;
        try
        {
            target = tool.Target(input);
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException or KeyNotFoundException)
        {
            target = null;
        }
        string? refusal;
        try
        {
            refusal = tool.Refusal(input);
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException or KeyNotFoundException)
        {
            refusal = null;
        }
        if (refusal is not null)
            return new(tool.Name, ToolRule.Deny, PlanOf(tool, input) ?? new ClaudeToolPlan(ToolAccess.Read, tool.Name, null, ""), target) { Reason = refusal };
        // Input no plan can be made of asks.
        return PlanOf(tool, input) is { } plan
            ? new(tool.Name, ClaudeToolPolicy.Decide(tool, plan, target, Rules?.Invoke(), Trusted?.Invoke(caller) ?? true), plan, target)
            : new(tool.Name, ToolRule.Ask, new ClaudeToolPlan(ToolAccess.Write, tool.Name, null, $"Allow Claude to use {tool.Name}?"), target);
    }

    /// <summary>Why the call does not run (the rules deny it, the user said no); null when it may.</summary>
    async Task<ClaudeToolOutput?> RefuseAsync(IClaudeTool tool, JsonElement input, object? caller, CancellationToken cancellationToken)
    {
        var decision = Decide(tool, input, caller);
        var audit = (caller as ClaudeSession)?.Audit;
        var name = Prefix + tool.Name;
        if (decision.Rule == ToolRule.Allow)
            return null;
        if (decision.Rule == ToolRule.Deny)
        {
            audit?.Decided(null, name, "deny", decision.Reason is null ? "the user's rules for Karvon's tools, at the call" : "the tool, at the call", decision.Refusal);
            return ClaudeToolOutput.Error(decision.Refusal);
        }
        if (_approved.TryRemove(Key(name, input), out var at) && DateTime.UtcNow - at < TimeSpan.FromMinutes(2))
            return null;
        if (Ask is not { } ask)
        {
            audit?.Decided(null, name, "deny", "Karvon, with no one to ask");
            return ClaudeToolOutput.Error("This call needs the user's yes, and Karvon could not ask them.");
        }
        audit?.Decided(null, name, "ask", "Karvon, as Claude Code did not ask");
        var allowed = await ask(new ClaudeToolRequest(name, input, decision.Plan, decision.Target, caller), cancellationToken);
        audit?.Decided(null, name, allowed ? "allow" : "deny", "the user, on Karvon's card");
        return allowed ? null : ClaudeToolOutput.Error("The user said no to this call. Do not try it another way: wait for what they say.");
    }

    /// <summary>A call as the user allowed it: its tool and its input, whatever order and spacing the JSON has.</summary>
    static string Key(string toolName, JsonElement input) => toolName + "\n" + Canonical(input);

    static string Canonical(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => JsonSerializer.Serialize(p.Name) + ":" + Canonical(p.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", e.EnumerateArray().Select(Canonical)) + "]",
        JsonValueKind.String => JsonSerializer.Serialize(e.GetString()),
        _ => e.GetRawText(),
    };

    /// <summary>Answers one JSON-RPC message: initialize, tools/list, tools/call, a notification.</summary>
    /// <param name="caller">The session the message came from (where the user is asked).</param>
    public async Task<JsonObject> HandleAsync(JsonElement message, CancellationToken cancellationToken, object? caller = null)
    {
        var id = message.TryGetProperty("id", out var idValue) ? JsonNode.Parse(idValue.GetRawText()) : null;
        var method = message.TryGetProperty("method", out var m) ? m.GetString() : null;
        var parameters = message.TryGetProperty("params", out var p) ? p : default;
        switch (method)
        {
            case "initialize":
                return Result(id, new JsonObject
                {
                    ["protocolVersion"] = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("protocolVersion", out var version)
                        ? version.GetString() : "2025-06-18",
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = Name, ["version"] = "1.0.0" },
                    ["instructions"] = instructions,
                });
            case "tools/list":
                return Result(id, new JsonObject
                {
                    ["tools"] = new JsonArray([.. Tools.Select(t =>
                    {
                        var tool = new JsonObject
                        {
                            ["name"] = t.Name,
                            ["description"] = t.Description,
                            ["inputSchema"] = t.InputSchema.DeepClone(),
                        };
                        // In the prompt from the start, not behind Claude Code's tool search (MCP's _meta, as Claude Code reads it).
                        if (alwaysLoaded?.Contains(t.Name) == true)
                            tool["_meta"] = new JsonObject { ["anthropic/alwaysLoad"] = true };
                        return (JsonNode)tool;
                    })]),
                });
            case "tools/call":
                return Result(id, await CallAsync(id, parameters, caller, cancellationToken));
            case "notifications/cancelled":
                // The user interrupted Claude: a query or compose still running stops.
                if (parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("requestId", out var cancelled)
                    && _running.TryGetValue(cancelled.GetRawText(), out var running))
                    running.Cancel();
                return new JsonObject { ["jsonrpc"] = "2.0", ["result"] = new JsonObject() };
            case "ping":
                return Result(id, new JsonObject());
            default:
                if (id is null)
                    return new JsonObject { ["jsonrpc"] = "2.0", ["result"] = new JsonObject() };
                return Failure(id, -32601, $"Karvon's tools do not support {method}.");
        }
    }

    async Task<JsonObject> CallAsync(JsonNode? id, JsonElement parameters, object? caller, CancellationToken cancellationToken)
    {
        var name = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("name", out var n) ? n.GetString() : null;
        var input = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Object
            ? a.Clone()
            : JsonDocument.Parse("{}").RootElement.Clone();
        ClaudeToolOutput result;
        var started = DateTime.UtcNow;
        var ran = false;
        if (Tools.FirstOrDefault(t => t.Name == name) is not { } tool)
            result = ClaudeToolOutput.Error($"Karvon has no tool named {name}.");
        else
        {
            var key = id?.ToJsonString() ?? Guid.NewGuid().ToString();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _running[key] = cts;
            try
            {
                // The user's rules, whatever Claude Code's permission mode let through.
                if (await RefuseAsync(tool, input, caller, cts.Token) is { } refusal)
                    result = refusal;
                else
                {
                    started = DateTime.UtcNow;
                    ran = true;
                    result = await tool.CallAsync(input, cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                result = ClaudeToolOutput.Error("Cancelled.");
            }
            catch (Exception ex)
            {
                result = ClaudeToolOutput.Error(ex.Message);
            }
            finally
            {
                _running.TryRemove(key, out _);
            }
            Called?.Invoke(tool, input, result);
        }
        // Whatever a tool says, the secrets Karvon holds and the tokens told by their form do not reach Claude; what others
        // wrote reaches it as data.
        var text = Redaction.Secrets(result.Text);
        var sent = result.Outside is { } outside ? OutsideText.Mark(text, outside) : text;
        if (ran && (caller as ClaudeSession)?.Audit is { } audit)
            audit.Ran(Prefix + name, input, started, DateTime.UtcNow - started, result.IsError, result.Text, text, result.Outside,
                result.Outside is null ? 0 : OutsideText.Find(text).Count);
        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = sent }),
            ["isError"] = result.IsError,
        };
    }

    static JsonObject Result(JsonNode? id, JsonObject result) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    public static JsonObject Failure(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
}

/// <summary>What the user's rules say of a call: its rule, its plan (the card's question) and what it reaches.</summary>
public sealed record ClaudeToolDecision(string Tool, ToolRule Rule, ClaudeToolPlan Plan, string? Target)
{
    /// <summary>Why the tool refuses the call itself (<see cref="IClaudeTool.Refusal"/>); null when the rules decided.</summary>
    public string? Reason { get; init; }

    /// <summary>What Claude is told when the call is denied.</summary>
    public string Refusal => Reason ?? $"The user's rules for Karvon's tools deny {Tool}{(Target is { Length: > 0 } ? $" on {Target}" : "")}. "
        + "Do not try it another way: tell the user what you wanted to do.";
}

/// <summary>A call Karvon asks the user about itself: Claude Code did not.</summary>
/// <param name="Caller">The session it came from; null when not known.</param>
public sealed record ClaudeToolRequest(string ToolName, JsonElement Input, ClaudeToolPlan Plan, string? Target, object? Caller);
