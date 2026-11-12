using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using Anthropic.Models.Models;

namespace Karvon.Claude;

/// <summary>
/// Claude through Anthropic's Messages API on the user's API key (Anthropic's SDK), for Karvon's own loop
/// (<see cref="ModelSession"/>): the conversation it keeps in Chat Completions' terms is said in the Messages API's, and
/// Claude's answer streamed back. Each answer's own blocks (its thinking with their signatures, its tool calls) are kept
/// with the conversation and sent back unchanged, as the API asks. Adaptive thinking (its summary shown), the panel's
/// effort, the prompt cached, and a refused request answered by Anthropic's fallback model where the model has one.
/// </summary>
public sealed class AnthropicChat : IChatEndpoint
{
    /// <summary>Where the conversation keeps an answer's own blocks (the API's words), to be sent back as they were.</summary>
    public const string BlocksKey = "claude_content";

    /// <summary>The model chosen when none is: the current Opus.</summary>
    public const string DefaultModel = "claude-opus-5-5";

    /// <summary>The models whose refused request Anthropic serves again on its fallback model (server-side fallbacks).</summary>
    static readonly string[] WithFallbacks = ["claude-fable-5-2", "claude-fable-5-1", "claude-opus-5-5", "claude-opus-5", "claude-sonnet-5-5"];

    const string FallbackBeta = "server-side-fallback-2026-07-01";

    readonly AnthropicClient _client;
    readonly string? _effort;
    readonly Dictionary<string, ModelInfo> _models = new(StringComparer.Ordinal);

    public AnthropicChat(string baseUrl, string? apiKey, string? effort = null)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        _client = new AnthropicClient { ApiKey = apiKey, BaseUrl = BaseUrl };
        _effort = effort;
    }

    public string BaseUrl { get; }

    /// <summary>Claude's models (Anthropic's Models API): the current Opus first, then as the API lists them (newest first).</summary>
    public async Task<IReadOnlyList<(string Id, string Name, string? Description, int? Context)>> ModelsAsync(CancellationToken cancellationToken)
    {
        ModelListPage page;
        try
        {
            page = await _client.Models.List(new ModelListParams { Limit = 1000 }, cancellationToken);
        }
        catch (AnthropicException ex)
        {
            throw new HttpRequestException(Said(ex), ex);
        }
        lock (_models)
            foreach (var model in page.Items)
                _models[model.ID] = model;
        return [.. page.Items
            .OrderBy(m => m.ID == DefaultModel ? 0 : 1)
            .Select(m => (m.ID, m.DisplayName, (string?)null, m.MaxInputTokens is { } window ? (int?)window : null))];
    }

    public async Task<ModelReply> StreamAsync(JsonObject body, Action<string> onText, Action<string> onThinking, CancellationToken cancellationToken)
    {
        var request = Request(body);
        var blocks = new SortedDictionary<long, Block>();
        string? stop = null;
        string? declined = null;
        int? prompt = null, output = null;
        try
        {
            await foreach (var ev in _client.Beta.Messages.CreateStreaming(request, cancellationToken))
            {
                if (ev.TryPickStart(out var start))
                    prompt = Tokens(start.Message.Usage.InputTokens, start.Message.Usage.CacheReadInputTokens, start.Message.Usage.CacheCreationInputTokens);
                else if (ev.TryPickContentBlockStart(out var begun))
                    blocks[begun.Index] = Block.Of(begun.ContentBlock);
                else if (ev.TryPickContentBlockDelta(out var delta) && blocks.TryGetValue(delta.Index, out var block))
                {
                    if (delta.Delta.TryPickText(out var text))
                    {
                        block.Text.Append(text.Text);
                        onText(text.Text);
                    }
                    else if (delta.Delta.TryPickThinking(out var thinking))
                    {
                        block.Text.Append(thinking.Thinking);
                        onThinking(thinking.Thinking);
                    }
                    else if (delta.Delta.TryPickInputJson(out var json))
                        block.Text.Append(json.PartialJson);
                    else if (delta.Delta.TryPickSignature(out var signature))
                        block.Signature = signature.Signature;
                }
                else if (ev.TryPickDelta(out var ended))
                {
                    stop = ended.Delta.StopReason?.Raw();
                    if (ended.Delta.StopDetails is { } details)
                        declined = string.Join(": ", new[] { details.Category?.Raw(), details.Explanation }.Where(s => s is { Length: > 0 }));
                    output = (int)ended.Usage.OutputTokens;
                }
            }
        }
        catch (AnthropicException ex)
        {
            throw new InvalidOperationException(Said(ex), ex);
        }
        // Declined (no fallback took it, or none could): what came before it is not an answer.
        if (stop == "refusal")
            throw new InvalidOperationException("Claude declined the request" + (declined is { Length: > 0 } ? $" ({declined})." : "."));
        var all = blocks.Values.ToList();
        var boundary = all.FindLastIndex(b => b.Type == "fallback");
        // A call cut off by the length limit is not run; one before a fallback was the declining model's.
        var calls = stop == "max_tokens" ? [] : all.Skip(boundary + 1).Where(b => b.Type == "tool_use")
            .Select(b => new ModelToolCall(b.Id!, b.Name!, b.Text.Length == 0 ? "{}" : b.Text.ToString())).ToList();
        return new ModelReply(
            string.Concat(all.Where(b => b.Type == "text").Select(b => b.Text)),
            string.Concat(all.Where(b => b.Type == "thinking").Select(b => b.Text)),
            calls,
            stop switch { "tool_use" => "tool_calls", "end_turn" or "stop_sequence" => "stop", "max_tokens" => "length", _ => stop },
            prompt,
            output,
            new JsonArray([.. all.Select(b => (JsonNode)b.ToJson())]));
    }

    static int? Tokens(long input, long? read, long? written) => (int)(input + (read ?? 0) + (written ?? 0));

    static string Said(AnthropicException ex) => ex switch
    {
        AnthropicUnauthorizedException => "Anthropic did not accept the API key.",
        AnthropicRateLimitException => "Anthropic's rate limit was reached: " + ex.Message,
        _ => ex.Message,
    };

    /// <summary>The request in the Messages API's words.</summary>
    internal MessageCreateParams Request(JsonObject body)
    {
        var model = body["model"]?.GetValue<string>() ?? DefaultModel;
        ModelInfo? info;
        lock (_models)
            info = _models.GetValueOrDefault(model);
        var system = new StringBuilder();
        var messages = new List<BetaMessageParam>();
        var pending = new List<BetaContentBlockParam>();
        void Flush()
        {
            if (pending.Count > 0)
                messages.Add(new BetaUserMessageParam { Content = new List<BetaContentBlockParam>(pending) });
            pending.Clear();
        }
        foreach (var message in (body["messages"] as JsonArray ?? []).OfType<JsonObject>())
        {
            switch (message["role"]?.GetValue<string>())
            {
                case "system":
                    system.Append(system.Length > 0 ? "\n\n" : "").Append(Text(message["content"]));
                    break;
                case "user":
                    pending.AddRange(UserBlocks(message["content"]));
                    break;
                case "tool":
                    pending.Add(new BetaToolResultBlockParam
                    {
                        ToolUseID = message["tool_call_id"]?.GetValue<string>() ?? "",
                        Content = Text(message["content"]),
                    });
                    break;
                case "assistant":
                    Flush();
                    if (AssistantBlocks(message) is { Count: > 0 } said)
                        messages.Add(new BetaAssistantMessageParam { Content = said });
                    break;
            }
        }
        Flush();
        var request = new MessageCreateParams
        {
            Model = model,
            MaxTokens = Math.Min(64000, info?.MaxTokens ?? 32000),
            Messages = messages,
            // The prompt and the conversation so far are cached: each step of a turn reads them again.
            CacheControl = new BetaCacheControlEphemeral(),
        };
        if (system.Length > 0)
            request = request with { System = system.ToString() };
        if (body["tools"] is JsonArray { Count: > 0 } tools)
            request = request with { Tools = [.. tools.OfType<JsonObject>().Select(Tool)] };
        if (Supports(info, "thinking", "types", "adaptive"))
            request = request with { Thinking = new BetaThinkingConfigAdaptive { Display = Display.Summarized } };
        if (_effort is { Length: > 0 } effort && Supports(info, "effort", effort) && EffortOf(effort) is { } level)
            request = request with { OutputConfig = new BetaOutputConfig { Effort = level } };
        if (WithFallbacks.Contains(model))
            request = request with { Betas = [FallbackBeta], Fallbacks = new Default() };
        return request;
    }

    static Effort? EffortOf(string effort) => effort switch
    {
        "low" => Effort.Low,
        "medium" => Effort.Medium,
        "high" => Effort.High,
        "xhigh" => Effort.Xhigh,
        "max" => Effort.Max,
        _ => null,
    };

    static MediaType? MediaTypeOf(string mediaType) => mediaType switch
    {
        "image/jpeg" => MediaType.ImageJpeg,
        "image/png" => MediaType.ImagePng,
        "image/gif" => MediaType.ImageGif,
        "image/webp" => MediaType.ImageWebP,
        _ => null,
    };

    /// <summary>Whether the model says it can (Models API capabilities: supported at the leaf); unknown is no.</summary>
    static bool Supports(ModelInfo? info, params string[] path)
    {
        if (info?.Capabilities is not { } capabilities)
            return false;
        var node = JsonSerializer.SerializeToElement(capabilities);
        foreach (var name in path)
            if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(name, out node))
                return false;
        return node.ValueKind == JsonValueKind.Object && node.TryGetProperty("supported", out var supported) && supported.ValueKind == JsonValueKind.True;
    }

    static BetaToolUnion Tool(JsonObject function)
    {
        var f = function["function"] as JsonObject ?? function;
        var parameters = f["parameters"] as JsonObject;
        return new BetaTool
        {
            Name = f["name"]?.GetValue<string>() ?? "",
            Description = f["description"]?.GetValue<string>(),
            InputSchema = new()
            {
                Properties = (parameters?["properties"] as JsonObject ?? []).ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value)),
                Required = [.. (parameters?["required"] as JsonArray ?? []).Select(r => r?.GetValue<string>() ?? "")],
            },
            // Long inputs (a file's contents) stream as they are written; ModelSession checks what it is given.
            EagerInputStreaming = true,
        };
    }

    static string Text(JsonNode? content) => content switch
    {
        JsonValue value when value.TryGetValue<string>(out var s) => s,
        JsonArray parts => string.Concat(parts.OfType<JsonObject>().Select(p => p["text"]?.GetValue<string>() ?? "")),
        null => "",
        _ => content.ToJsonString(),
    };

    static IEnumerable<BetaContentBlockParam> UserBlocks(JsonNode? content)
    {
        if (content is not JsonArray parts)
        {
            if (Text(content) is { Length: > 0 } text)
                yield return new BetaTextBlockParam { Text = text };
            yield break;
        }
        foreach (var part in parts.OfType<JsonObject>())
        {
            if (part["type"]?.GetValue<string>() == "image_url" && part["image_url"]?["url"]?.GetValue<string>() is { } url
                && url.StartsWith("data:", StringComparison.Ordinal) && url.IndexOf(";base64,", StringComparison.Ordinal) is var comma and > 5
                && MediaTypeOf(url[5..comma]) is { } mediaType)
                yield return new BetaImageBlockParam
                {
                    Source = new BetaBase64ImageSource { MediaType = mediaType, Data = url[(comma + 8)..] },
                };
            else if (part["text"]?.GetValue<string>() is { Length: > 0 } text)
                yield return new BetaTextBlockParam { Text = text };
        }
    }

    /// <summary>
    /// An answer as it is sent back: its own blocks when it was Claude's (thinking with its signature, as the API checks it),
    /// else its text and calls. After a fallback, the declining model's thinking and calls before the last switch are left
    /// out, and the switch kept where it was.
    /// </summary>
    static List<BetaContentBlockParam> AssistantBlocks(JsonObject message)
    {
        var result = new List<BetaContentBlockParam>();
        if (message[BlocksKey] is JsonArray own)
        {
            var blocks = own.OfType<JsonObject>().ToList();
            var boundary = blocks.FindLastIndex(b => b["type"]?.GetValue<string>() == "fallback");
            for (var i = 0; i < blocks.Count; i++)
            {
                var b = blocks[i];
                var type = b["type"]?.GetValue<string>();
                if (i < boundary && type is "thinking" or "redacted_thinking" or "tool_use")
                    continue;
                switch (type)
                {
                    case "text" when b["text"]?.GetValue<string>() is { Length: > 0 } text:
                        result.Add(new BetaTextBlockParam { Text = text });
                        break;
                    case "thinking":
                        result.Add(new BetaThinkingBlockParam { Thinking = b["thinking"]?.GetValue<string>() ?? "", Signature = b["signature"]?.GetValue<string>() ?? "" });
                        break;
                    case "redacted_thinking":
                        result.Add(new BetaRedactedThinkingBlockParam { Data = b["data"]?.GetValue<string>() ?? "" });
                        break;
                    case "tool_use":
                        result.Add(new BetaToolUseBlockParam
                        {
                            ID = b["id"]?.GetValue<string>() ?? "",
                            Name = b["name"]?.GetValue<string>() ?? "",
                            Input = Input(b["input"]?.ToJsonString()),
                        });
                        break;
                    case "fallback":
                        result.Add(JsonSerializer.Deserialize<BetaFallbackBlockParam>(b.ToJsonString())!);
                        break;
                }
            }
            return result;
        }
        if (Text(message["content"]) is { Length: > 0 } said)
            result.Add(new BetaTextBlockParam { Text = said });
        foreach (var call in (message["tool_calls"] as JsonArray ?? []).OfType<JsonObject>())
            result.Add(new BetaToolUseBlockParam
            {
                ID = call["id"]?.GetValue<string>() ?? "",
                Name = call["function"]?["name"]?.GetValue<string>() ?? "",
                Input = Input(call["function"]?["arguments"]?.GetValue<string>()),
            });
        return result;
    }

    /// <summary>A call's input as an object; one that does not parse (cut off) is empty.</summary>
    static Dictionary<string, JsonElement> Input(string? json)
    {
        try
        {
            return json is { Length: > 0 } && JsonDocument.Parse(json).RootElement is { ValueKind: JsonValueKind.Object } input
                ? input.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>A block of the answer as it streams: its kind, its text (or a call's input so far), a call's id and name.</summary>
    sealed class Block
    {
        public required string Type { get; init; }
        public StringBuilder Text { get; } = new();
        public string? Signature { get; set; }
        public string? Id { get; init; }
        public string? Name { get; init; }

        /// <summary>One the API told whole as it began (redacted thinking, a fallback's switch).</summary>
        public JsonObject? Whole { get; init; }

        public static Block Of(ContentBlock started)
        {
            switch (started.Value)
            {
                case BetaToolUseBlock call:
                    return new Block { Type = "tool_use", Id = call.ID, Name = call.Name };
                case BetaTextBlock text:
                    return new Block { Type = "text" }.With(text.Text);
                case BetaThinkingBlock thinking:
                    return new Block { Type = "thinking", Signature = thinking.Signature }.With(thinking.Thinking);
            }
            var whole = JsonNode.Parse(JsonSerializer.Serialize(started)) as JsonObject ?? [];
            return new Block { Type = whole["type"]?.GetValue<string>() ?? "unknown", Whole = whole };
        }

        Block With(string? text)
        {
            Text.Append(text);
            return this;
        }

        public JsonObject ToJson() => Type switch
        {
            "text" => new JsonObject { ["type"] = "text", ["text"] = Text.ToString() },
            "thinking" => new JsonObject { ["type"] = "thinking", ["thinking"] = Text.ToString(), ["signature"] = Signature ?? "" },
            "tool_use" => new JsonObject { ["type"] = "tool_use", ["id"] = Id, ["name"] = Name, ["input"] = ParsedInput() },
            _ => Whole ?? new JsonObject { ["type"] = Type },
        };

        JsonNode ParsedInput()
        {
            try
            {
                return JsonNode.Parse(Text.Length == 0 ? "{}" : Text.ToString()) is JsonObject input ? input : [];
            }
            catch (JsonException)
            {
                return new JsonObject();
            }
        }
    }
}
