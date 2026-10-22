using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VSL.Claude;

/// <summary>
/// How much a tool call may change: reads and runs (a build, the tests, a debugger step: nothing of the user's changes)
/// go at once; writes ask; destructive changes ask and are never remembered.
/// </summary>
public enum ToolAccess
{
    Read,
    Run,
    Write,
    Destructive,
}

/// <summary>
/// What a call will do, before it runs: its access, its row in the conversation ("Query", "shop: SELECT ..."), and for a
/// change the approval card's question and warning.
/// </summary>
public sealed record ClaudeToolPlan(ToolAccess Access, string Title, string? Subject, string Question)
{
    /// <summary>The SQL or command, shown in full on the card and the row.</summary>
    public string? Code { get; init; }

    /// <summary>Under the question: what the change does, or why it cannot be undone.</summary>
    public string? Warning { get; init; }
}

public sealed record ClaudeToolOutput(string Text, bool IsError = false)
{
    public static ClaudeToolOutput Error(string message) => new(message, true);
}

/// <summary>A tool of VSL's own that Claude can call (mcp__vsl__name).</summary>
public interface IClaudeTool
{
    string Name { get; }

    string Description { get; }

    /// <summary>The input's JSON Schema (an object).</summary>
    JsonObject InputSchema { get; }

    /// <summary>What the call would do; computed again for the approval card and when it runs.</summary>
    ClaudeToolPlan Plan(JsonElement input);

    Task<ClaudeToolOutput> CallAsync(JsonElement input, CancellationToken cancellationToken);
}

/// <summary>
/// VSL's tools as an MCP server inside VSL: the CLI is given it with <c>--mcp-config</c> as an "sdk" server and talks to it
/// over its control protocol (mcp_message requests carrying JSON-RPC), as the Agent SDK's in-process servers. No port, no
/// token: only the CLI VSL started can call it. Claude sees the tools as mcp__vsl__&lt;name&gt;.
/// </summary>
public sealed class ClaudeToolServer(IReadOnlyList<IClaudeTool> tools, string instructions)
{
    public const string Name = "vsl";

    public const string Prefix = "mcp__" + Name + "__";

    readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new();

    public IReadOnlyList<IClaudeTool> Tools { get; } = tools;

    /// <summary>The <c>--mcp-config</c> value that adds the server beside the user's own.</summary>
    public static string McpConfig => new JsonObject { ["mcpServers"] = new JsonObject { [Name] = new JsonObject { ["type"] = "sdk", ["name"] = Name } } }.ToJsonString();

    /// <summary>The tool behind a name Claude uses (mcp__vsl__db_query), or null for any other tool.</summary>
    public IClaudeTool? Find(string toolName) =>
        toolName.StartsWith(Prefix, StringComparison.Ordinal) ? Tools.FirstOrDefault(t => t.Name == toolName.Substring(Prefix.Length)) : null;

    /// <summary>The plan of a call to one of these tools; null for any other tool, or input it cannot read.</summary>
    public ClaudeToolPlan? Plan(string toolName, JsonElement input)
    {
        try
        {
            return Find(toolName)?.Plan(input);
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException or KeyNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Answers one JSON-RPC message: initialize, tools/list, tools/call, a notification.</summary>
    public async Task<JsonObject> HandleAsync(JsonElement message, CancellationToken cancellationToken)
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
                    ["tools"] = new JsonArray([.. Tools.Select(t => (JsonNode)new JsonObject
                    {
                        ["name"] = t.Name,
                        ["description"] = t.Description,
                        ["inputSchema"] = t.InputSchema.DeepClone(),
                    })]),
                });
            case "tools/call":
                return Result(id, await CallAsync(id, parameters, cancellationToken));
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
                return Failure(id, -32601, $"VSL's tools do not support {method}.");
        }
    }

    async Task<JsonObject> CallAsync(JsonNode? id, JsonElement parameters, CancellationToken cancellationToken)
    {
        var name = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("name", out var n) ? n.GetString() : null;
        var input = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Object
            ? a.Clone()
            : JsonDocument.Parse("{}").RootElement.Clone();
        ClaudeToolOutput result;
        if (Tools.FirstOrDefault(t => t.Name == name) is not { } tool)
            result = ClaudeToolOutput.Error($"VSL has no tool named {name}.");
        else
        {
            var key = id?.ToJsonString() ?? Guid.NewGuid().ToString();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _running[key] = cts;
            try
            {
                result = await tool.CallAsync(input, cts.Token);
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
        }
        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = result.Text }),
            ["isError"] = result.IsError,
        };
    }

    static JsonObject Result(JsonNode? id, JsonObject result) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    public static JsonObject Failure(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
}
