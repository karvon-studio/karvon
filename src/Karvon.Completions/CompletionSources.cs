using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Anthropic;
using Anthropic.Models.Beta.Messages;
using Karvon.Editor;

namespace Karvon.Completions;

/// <summary>Where a suggestion comes from: its text for a request (as the model wrote it), or null for none.</summary>
public interface ICompletionSource
{
    Task<string?> CompleteAsync(InlineCompletionRequest request, string path, CancellationToken ct);
}

/// <summary>What a model wrote, as a suggestion: without Markdown's fences or the marker, at most so many lines; null when empty.</summary>
public static class CompletionText
{
    public const int MaxLines = 20;

    public const string Cursor = "<CURSOR/>";

    public static string? Clean(string? text, bool truncated = false)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var lines = text.Replace("\r\n", "\n").Replace(Cursor, "").Split('\n').ToList();
        if (lines[0].TrimStart().StartsWith("```", StringComparison.Ordinal))
            lines.RemoveAt(0);
        if (lines.Count > 0 && lines[^1].Trim() == "```")
            lines.RemoveAt(lines.Count - 1);
        // A line the model was cut off in is no line to put in.
        if (truncated && lines.Count > 1)
            lines.RemoveAt(lines.Count - 1);
        var cleaned = string.Join("\n", lines.Take(MaxLines)).TrimEnd();
        return cleaned.Trim().Length == 0 ? null : cleaned;
    }
}

/// <summary>
/// Claude through Anthropic's API: the file around the caret with a marker at it, and only the text to put in there
/// asked for. Little thinking (low effort), no retries (a late suggestion is no use), and the API's own fallback model when
/// the one asked declines.
/// </summary>
public sealed class ClaudeCompletionSource(string apiKey, string model, HttpClient? http = null) : ICompletionSource
{
    const string FallbackBeta = "server-side-fallback-2026-07-01";

    internal const string SystemPrompt =
        "You complete code in an editor while the user types. The user's message is a file with the marker " + CompletionText.Cursor
        + " where the caret is. Reply with only the text to insert at the marker: no explanation, no Markdown fences, and nothing "
        + "from before or after the marker repeated.\n\n"
        + "Insert what the user most likely types next: the rest of the current line, or, when the caret starts a block or a blank "
        + "line, the next few lines (at most about 15). The text after the marker stays where it is, so the insertion must lead "
        + "into it. The first line of the insertion continues the caret's line as it is; any further lines carry their full "
        + "indentation, in the file's style. When nothing fits, reply with nothing.";

    readonly AnthropicClient _client = http is null
        ? new() { ApiKey = apiKey, MaxRetries = 0, Timeout = TimeSpan.FromSeconds(30) }
        : new() { ApiKey = apiKey, MaxRetries = 0, Timeout = TimeSpan.FromSeconds(30), HttpClient = http };

    public string Model => model;

    internal static string Prompt(InlineCompletionRequest request, string path) =>
        $"<file path=\"{path}\">\n{request.Prefix}{CompletionText.Cursor}{request.Suffix}\n</file>";

    public async Task<string?> CompleteAsync(InlineCompletionRequest request, string path, CancellationToken ct)
    {
        var response = await _client.Beta.Messages.Create(new MessageCreateParams
        {
            Model = model,
            MaxTokens = 2048,
            OutputConfig = new() { Effort = Effort.Low },
            System = SystemPrompt,
            Messages = [new BetaUserMessageParam { Content = Prompt(request, path) }],
            Betas = [FallbackBeta],
            Fallbacks = new Default(),
        }, ct);
        var stop = response.StopReason?.Raw();
        if (stop == "refusal")
            return null;
        var text = string.Concat(response.Content.Select(block => block.Value).OfType<BetaTextBlock>().Select(block => block.Text));
        return CompletionText.Clean(text, truncated: stop == "max_tokens");
    }
}

/// <summary>
/// A local model through Ollama's generate API, filling in the middle (the text before the caret as the prompt, the text
/// after it as the suffix): the model has to be one that can (Qwen2.5-Coder, StarCoder2, CodeGemma...).
/// </summary>
public sealed class OllamaCompletionSource(HttpClient http, string url, string model) : ICompletionSource
{
    sealed record Options([property: JsonPropertyName("temperature")] double Temperature, [property: JsonPropertyName("num_predict")] int NumPredict);

    sealed record Request(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("prompt")] string Prompt,
        [property: JsonPropertyName("suffix")] string Suffix,
        [property: JsonPropertyName("stream")] bool Stream,
        [property: JsonPropertyName("options")] Options Options);

    sealed record Response([property: JsonPropertyName("response")] string? Text, [property: JsonPropertyName("done_reason")] string? DoneReason);

    public async Task<string?> CompleteAsync(InlineCompletionRequest request, string path, CancellationToken ct)
    {
        using var reply = await http.PostAsJsonAsync(url.TrimEnd('/') + "/api/generate",
            new Request(model, request.Prefix, request.Suffix, false, new Options(0.2, 256)), ct);
        if (!reply.IsSuccessStatusCode)
            throw new HttpRequestException($"Ollama answered {(int)reply.StatusCode}: {(await reply.Content.ReadAsStringAsync(ct)).Trim()}", null, reply.StatusCode);
        var answer = await reply.Content.ReadFromJsonAsync<Response>(ct);
        return CompletionText.Clean(answer?.Text, truncated: answer?.DoneReason == "length");
    }
}
