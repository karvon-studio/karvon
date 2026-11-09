using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Karvon.ViewModels;

namespace Karvon.Claude;

/// <summary>
/// Ollama through its own API (/api/chat, /api/tags, /api/show), not its OpenAI-compatible one: only its own takes the
/// context window a request needs (options.num_ctx). Loaded the usual way a model gets a few thousand tokens, and Ollama
/// cuts the start of what does not fit (the system prompt and the tools first): Karvon asks for <paramref name="context"/>
/// tokens (the model's own window when smaller). Only the models that call tools are listed; those that think are asked to.
/// </summary>
public sealed class OllamaChat(HttpClient http, string baseUrl, int context = OllamaChat.DefaultContext) : IChatEndpoint
{
    /// <summary>What Karvon's prompt, its tools and a coding conversation need, on a computer a 7–14B model runs on.</summary>
    public const int DefaultContext = 32768;

    readonly ConcurrentDictionary<string, (int Context, bool Thinks)> _models = new(StringComparer.Ordinal);

    public string BaseUrl { get; } = baseUrl.TrimEnd('/');

    public async Task<IReadOnlyList<(string Id, string Name, string? Description, int? Context)>> ModelsAsync(CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(BaseUrl + "/api/tags", cancellationToken);
        await ChatCompletions.EnsureAsync(response, cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var names = document.RootElement.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array
            ? models.EnumerateArray().Select(m => ClaudeJson.Str(m, "name") ?? ClaudeJson.Str(m, "model")).OfType<string>().ToList()
            : [];
        var listed = new List<(string, string, string?, int?)>();
        foreach (var name in names.Order(StringComparer.OrdinalIgnoreCase))
        {
            var (tools, window, thinks, size) = await ShowAsync(name, cancellationToken);
            if (!tools)
                continue;
            _models[name] = (window, thinks);
            listed.Add((name, name, size is null ? null : $"{size} parameters{(thinks ? ", thinks" : "")}", window));
        }
        return listed;
    }

    /// <summary>What Ollama says of a model: whether it calls tools, the window Karvon gives it, whether it thinks, its size.</summary>
    async Task<(bool Tools, int Window, bool Thinks, string? Size)> ShowAsync(string model, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsync(BaseUrl + "/api/show",
            new StringContent(new JsonObject { ["model"] = model }.ToJsonString(), Encoding.UTF8, "application/json"), cancellationToken);
        await ChatCompletions.EnsureAsync(response, cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = document.RootElement;
        var capabilities = root.TryGetProperty("capabilities", out var c) && c.ValueKind == JsonValueKind.Array
            ? c.EnumerateArray().Select(x => x.GetString()).ToHashSet()
            : [];
        var own = root.TryGetProperty("model_info", out var info) && info.ValueKind == JsonValueKind.Object
            ? info.EnumerateObject().Where(p => p.Name.EndsWith(".context_length", StringComparison.Ordinal) && p.Value.TryGetInt32(out _))
                .Select(p => p.Value.GetInt32()).FirstOrDefault()
            : 0;
        var size = root.TryGetProperty("details", out var details) ? ClaudeJson.Str(details, "parameter_size") : null;
        return (capabilities.Contains("tools"), own > 0 ? Math.Min(own, context) : context, capabilities.Contains("thinking"), size);
    }

    public async Task<ModelReply> StreamAsync(JsonObject body, Action<string> onText, Action<string> onThinking, CancellationToken cancellationToken)
    {
        var model = body["model"]?.GetValue<string>() ?? "";
        if (!_models.TryGetValue(model, out var known))
        {
            var (_, window, thinks, _) = await ShowAsync(model, cancellationToken);
            _models[model] = known = (window, thinks);
        }
        var request = new JsonObject
        {
            ["model"] = model,
            ["messages"] = Messages(body["messages"] as JsonArray ?? []),
            ["stream"] = true,
            ["options"] = new JsonObject { ["num_ctx"] = known.Context },
        };
        if (body["tools"] is JsonArray { Count: > 0 } tools)
            request["tools"] = tools.DeepClone();
        if (known.Thinks)
            request["think"] = true;
        using var response = await http.SendAsync(new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/api/chat")
        {
            Content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"),
        }, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await ChatCompletions.EnsureAsync(response, cancellationToken);
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = new StringBuilder();
        var thinking = new StringBuilder();
        var tags = new ChatCompletions.ThinkTags(onText, onThinking, text, thinking);
        var calls = new List<ModelToolCall>();
        string? finish = null;
        int? prompt = null, output = null;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Trim().Length == 0)
                continue;
            JsonElement chunk;
            try
            {
                using var document = JsonDocument.Parse(line);
                chunk = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                continue;
            }
            if (ClaudeJson.Str(chunk, "error") is { } error)
                throw new InvalidOperationException(error);
            if (chunk.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object)
            {
                if (ClaudeJson.Str(message, "thinking") is { Length: > 0 } thought)
                {
                    thinking.Append(thought);
                    onThinking(thought);
                }
                if (ClaudeJson.Str(message, "content") is { Length: > 0 } piece)
                    tags.Add(piece);
                if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                    foreach (var call in toolCalls.EnumerateArray())
                        if (call.TryGetProperty("function", out var function) && ClaudeJson.Str(function, "name") is { Length: > 0 } name)
                            calls.Add(new ModelToolCall(ClaudeJson.Str(call, "id") ?? "call_" + Guid.NewGuid().ToString("N")[..12], name,
                                function.TryGetProperty("arguments", out var arguments)
                                    ? arguments.ValueKind == JsonValueKind.String ? arguments.GetString() ?? "{}" : arguments.GetRawText()
                                    : "{}"));
            }
            if (chunk.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.True)
            {
                finish = ClaudeJson.Str(chunk, "done_reason");
                prompt = chunk.TryGetProperty("prompt_eval_count", out var p) && p.TryGetInt32(out var pt) ? pt : null;
                output = chunk.TryGetProperty("eval_count", out var o) && o.TryGetInt32(out var ot) ? ot : null;
            }
        }
        tags.Flush();
        return new ModelReply(text.ToString(), thinking.ToString(), calls, calls.Count > 0 ? "tool_calls" : finish, prompt, output);
    }

    /// <summary>
    /// Chat Completions' messages in Ollama's words: tool calls' arguments as objects, a tool's result named by its tool, a
    /// prompt's images apart from its text.
    /// </summary>
    internal static JsonArray Messages(JsonArray messages)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var converted = new JsonArray();
        foreach (var node in messages.OfType<JsonObject>())
        {
            var role = node["role"]?.GetValue<string>() ?? "user";
            var message = new JsonObject { ["role"] = role };
            switch (node["content"])
            {
                case JsonArray parts:
                    message["content"] = string.Join("\n\n", parts.OfType<JsonObject>().Where(p => p["type"]?.GetValue<string>() == "text")
                        .Select(p => p["text"]?.GetValue<string>() ?? ""));
                    var images = parts.OfType<JsonObject>().Select(p => p["image_url"]?["url"]?.GetValue<string>())
                        .OfType<string>().Select(url => url[(url.IndexOf(',') + 1)..]).ToList();
                    if (images.Count > 0)
                        message["images"] = new JsonArray([.. images.Select(i => (JsonNode)i)]);
                    break;
                case JsonValue value when value.TryGetValue<string>(out var content):
                    message["content"] = content;
                    break;
                default:
                    message["content"] = "";
                    break;
            }
            if (node["tool_calls"] is JsonArray toolCalls)
            {
                var calls = new JsonArray();
                foreach (var call in toolCalls.OfType<JsonObject>())
                {
                    var name = call["function"]?["name"]?.GetValue<string>() ?? "";
                    if (call["id"]?.GetValue<string>() is { } id)
                        names[id] = name;
                    JsonNode? arguments;
                    try
                    {
                        arguments = JsonNode.Parse(call["function"]?["arguments"]?.GetValue<string>() ?? "{}");
                    }
                    catch (JsonException)
                    {
                        arguments = new JsonObject();
                    }
                    calls.Add(new JsonObject { ["function"] = new JsonObject { ["name"] = name, ["arguments"] = arguments as JsonObject ?? new JsonObject() } });
                }
                message["tool_calls"] = calls;
            }
            if (role == "tool" && node["tool_call_id"]?.GetValue<string>() is { } callId && names.TryGetValue(callId, out var tool))
                message["tool_name"] = tool;
            converted.Add(message);
        }
        return converted;
    }
}
