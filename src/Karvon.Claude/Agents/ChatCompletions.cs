using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Karvon.ViewModels;

namespace Karvon.Claude;

/// <summary>A tool call a model asked for: its id, the function's name and its arguments (JSON text).</summary>
public sealed record ModelToolCall(string Id, string Name, string Arguments);

/// <summary>What one streamed answer of a model came to.</summary>
/// <param name="PromptTokens">How much context the request took (the provider's count); null when it did not say.</param>
public sealed record ModelReply(string Text, string Thinking, IReadOnlyList<ModelToolCall> ToolCalls, string? FinishReason, int? PromptTokens, int? OutputTokens);

/// <summary>Where a model Karvon drives itself answers: its models, and an answer streamed (in Chat Completions' terms).</summary>
public interface IChatEndpoint
{
    string BaseUrl { get; }

    /// <summary>The models that call tools: their ids, names, descriptions and context windows.</summary>
    Task<IReadOnlyList<(string Id, string Name, string? Description, int? Context)>> ModelsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// An answer to <paramref name="body"/> (Chat Completions' request: model, messages, tools), streamed to
    /// <paramref name="onText"/> and <paramref name="onThinking"/> as it comes.
    /// </summary>
    Task<ModelReply> StreamAsync(JsonObject body, Action<string> onText, Action<string> onThinking, CancellationToken cancellationToken);

    /// <summary>A whole answer at once (no tools): a compaction's summary.</summary>
    async Task<string> CompleteAsync(JsonObject body, CancellationToken cancellationToken) =>
        (await StreamAsync(body, _ => { }, _ => { }, cancellationToken)).Text;
}

/// <summary>
/// An OpenAI-compatible Chat Completions endpoint (OpenRouter, LM Studio, vLLM...): its models, and an answer streamed as
/// server-sent events, its text, its reasoning and its tool calls as they come.
/// </summary>
public sealed class ChatCompletions(HttpClient http, string baseUrl, string? apiKey) : IChatEndpoint
{
    public string BaseUrl { get; } = baseUrl.TrimEnd('/');

    HttpRequestMessage Request(HttpMethod method, string path, JsonNode? body = null)
    {
        var request = new HttpRequestMessage(method, BaseUrl + path);
        if (apiKey is { Length: > 0 })
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        // OpenRouter's attribution of the app a request comes from.
        request.Headers.TryAddWithoutValidation("HTTP-Referer", "https://github.com/karvon-studio/karvon");
        request.Headers.TryAddWithoutValidation("X-Title", "Karvon Studio");
        if (body is not null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return request;
    }

    /// <summary>The models the endpoint has (/models): their ids, names, descriptions and context windows; for OpenRouter only those that take tools.</summary>
    public async Task<IReadOnlyList<(string Id, string Name, string? Description, int? Context)>> ModelsAsync(CancellationToken cancellationToken)
    {
        using var response = await http.SendAsync(Request(HttpMethod.Get, "/models"), cancellationToken);
        await EnsureAsync(response, cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return [];
        return [.. data.EnumerateArray()
            .Where(m => !m.TryGetProperty("supported_parameters", out var p) || p.ValueKind != JsonValueKind.Array
                || p.EnumerateArray().Any(x => x.GetString() == "tools"))
            .Select(m => (Id: ClaudeJson.Str(m, "id") ?? "", Name: ClaudeJson.Str(m, "name") ?? ClaudeJson.Str(m, "id") ?? "",
                Description: ClaudeJson.Str(m, "description") is { } d ? FirstSentence(d) : null,
                Context: m.TryGetProperty("context_length", out var c) && c.TryGetInt32(out var n) ? n : (int?)null))
            .Where(m => m.Id.Length > 0)
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)];
    }

    internal static string FirstSentence(string text)
    {
        var line = text.Split('\n')[0].Trim();
        var end = line.IndexOf(". ", StringComparison.Ordinal);
        return end > 0 && end < 160 ? line[..(end + 1)] : line.Length > 160 ? line[..157] + "..." : line;
    }

    /// <summary>
    /// Asks for an answer and streams it: <paramref name="onText"/> and <paramref name="onThinking"/> get each piece as it
    /// comes (reasoning in its own field, or between &lt;think&gt; tags of the text, as local models write it).
    /// </summary>
    public async Task<ModelReply> StreamAsync(JsonObject body, Action<string> onText, Action<string> onThinking, CancellationToken cancellationToken)
    {
        body["stream"] = true;
        body["stream_options"] = new JsonObject { ["include_usage"] = true };
        using var response = await http.SendAsync(Request(HttpMethod.Post, "/chat/completions", body), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureAsync(response, cancellationToken);
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = new StringBuilder();
        var thinking = new StringBuilder();
        var calls = new SortedDictionary<int, (StringBuilder Id, StringBuilder Name, StringBuilder Arguments)>();
        var tags = new ThinkTags(onText, onThinking, text, thinking);
        string? finish = null;
        int? prompt = null, output = null;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;
            var data = line[5..].Trim();
            if (data == "[DONE]")
                break;
            JsonElement chunk;
            try
            {
                using var document = JsonDocument.Parse(data);
                chunk = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                continue;
            }
            if (chunk.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                throw new InvalidOperationException(ClaudeJson.Str(error, "message") ?? "The model's provider reported an error.");
            if (chunk.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                if (usage.TryGetProperty("prompt_tokens", out var p) && p.TryGetInt32(out var pt))
                    prompt = pt;
                if (usage.TryGetProperty("completion_tokens", out var o) && o.TryGetInt32(out var ot))
                    output = ot;
            }
            if (!chunk.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                continue;
            var choice = choices[0];
            if (ClaudeJson.Str(choice, "finish_reason") is { Length: > 0 } reason)
                finish = reason;
            if (!choice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
                continue;
            foreach (var field in new[] { "reasoning", "reasoning_content" })
                if (ClaudeJson.Str(delta, field) is { Length: > 0 } thought)
                {
                    thinking.Append(thought);
                    onThinking(thought);
                }
            if (ClaudeJson.Str(delta, "content") is { Length: > 0 } piece)
                tags.Add(piece);
            if (delta.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                foreach (var call in toolCalls.EnumerateArray())
                {
                    var index = call.TryGetProperty("index", out var i) && i.TryGetInt32(out var n) ? n : calls.Count;
                    if (!calls.TryGetValue(index, out var parts))
                        calls[index] = parts = (new StringBuilder(), new StringBuilder(), new StringBuilder());
                    if (ClaudeJson.Str(call, "id") is { Length: > 0 } id)
                        parts.Id.Clear().Append(id);
                    if (call.TryGetProperty("function", out var function) && function.ValueKind == JsonValueKind.Object)
                    {
                        if (ClaudeJson.Str(function, "name") is { Length: > 0 } name)
                            parts.Name.Append(name);
                        if (ClaudeJson.Str(function, "arguments") is { } arguments)
                            parts.Arguments.Append(arguments);
                    }
                }
        }
        tags.Flush();
        return new ModelReply(text.ToString(), thinking.ToString(),
            [.. calls.Values.Select(c => new ModelToolCall(c.Id.Length > 0 ? c.Id.ToString() : "call_" + Guid.NewGuid().ToString("N")[..12], c.Name.ToString(),
                c.Arguments.Length > 0 ? c.Arguments.ToString() : "{}"))],
            finish, prompt, output);
    }

    internal static async Task EnsureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        string? message = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            message = document.RootElement.TryGetProperty("error", out var error)
                ? error.ValueKind == JsonValueKind.Object ? ClaudeJson.Str(error, "message") : error.GetString()
                : ClaudeJson.Str(document.RootElement, "message");
        }
        catch (JsonException)
        {
        }
        throw new InvalidOperationException($"{(int)response.StatusCode} {response.ReasonPhrase}: {message ?? (body.Length > 300 ? body[..300] : body)}");
    }

    /// <summary>Text with a model's thoughts between &lt;think&gt; and &lt;/think&gt; (Qwen, DeepSeek in Ollama): the thoughts apart.</summary>
    internal sealed class ThinkTags(Action<string> onText, Action<string> onThinking, StringBuilder text, StringBuilder thinking)
    {
        const string Open = "<think>";
        const string Close = "</think>";
        readonly StringBuilder _pending = new();
        bool _inside;
        bool _started;

        public void Add(string piece)
        {
            _pending.Append(piece);
            while (_pending.Length > 0)
            {
                var buffered = _pending.ToString();
                // Only an answer that starts with the tag has thoughts in it.
                if (!_started)
                {
                    var trimmed = buffered.TrimStart();
                    if (trimmed.Length < Open.Length && Open.StartsWith(trimmed, StringComparison.Ordinal))
                        return;
                    _started = true;
                    if (trimmed.StartsWith(Open, StringComparison.Ordinal))
                    {
                        _inside = true;
                        _pending.Clear().Append(trimmed[Open.Length..]);
                        continue;
                    }
                }
                if (!_inside)
                {
                    Emit(buffered, inside: false);
                    _pending.Clear();
                    return;
                }
                var end = buffered.IndexOf(Close, StringComparison.Ordinal);
                if (end >= 0)
                {
                    Emit(buffered[..end], inside: true);
                    _inside = false;
                    _pending.Clear().Append(buffered[(end + Close.Length)..].TrimStart('\n'));
                    continue;
                }
                // A tag cut in two waits for its end.
                var keep = 0;
                for (var k = Math.Min(Close.Length - 1, buffered.Length); k > 0; k--)
                    if (Close.StartsWith(buffered[^k..], StringComparison.Ordinal))
                    {
                        keep = k;
                        break;
                    }
                Emit(buffered[..^keep], inside: true);
                _pending.Clear().Append(buffered[^keep..]);
                return;
            }
        }

        public void Flush()
        {
            if (_pending.Length > 0)
                Emit(_pending.ToString(), _inside);
            _pending.Clear();
        }

        void Emit(string piece, bool inside)
        {
            if (piece.Length == 0)
                return;
            (inside ? thinking : text).Append(piece);
            (inside ? onThinking : onText)(piece);
        }
    }
}
