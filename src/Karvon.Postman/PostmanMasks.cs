using System.Text.Json;
using System.Text.Json.Nodes;
using Karvon.Claude.Tools;
using Karvon.Http;

namespace Karvon.Postman;

/// <summary>
/// What came back as an agent (and its scripts) may have it: whatever the request sent of the user's is hidden in it, as it
/// is and as a server may write it back, so that no echo tells it.
/// </summary>
static class PostmanMasks
{
    /// <summary>A shorter value ("1", "dev") is no secret, and any text may hold it.</summary>
    const int MinLength = 4;

    public static HttpExchange Hide(HttpExchange exchange, IReadOnlyDictionary<string, (string Scope, string Value)> used, IEnumerable<string> derived)
    {
        var values = Values(used, derived);
        if (values.Count == 0)
            return exchange;
        string T(string text) => Hide(text, values);
        string raw = exchange.RawBody, body = exchange.Body;
        if (raw.Length > 0 && Json(raw, values) is { } json)
            (raw, body) = (json, HttpRunner.Pretty(json, "application/json"));
        else
            (raw, body) = (T(raw), T(body));
        return exchange with
        {
            Reason = T(exchange.Reason),
            Headers = [.. exchange.Headers.Select(h => (h.Name, T(h.Value)))],
            RawBody = raw,
            Body = body,
            Error = exchange.Error is { } error ? T(error) : null,
        };
    }

    public static string Hide(string text, IReadOnlyList<(string Value, string Mark)> values)
    {
        foreach (var (value, mark) in values)
            if (text.Contains(value, StringComparison.Ordinal))
                text = text.Replace(value, mark, StringComparison.Ordinal);
        return text;
    }

    /// <summary>Each value in the forms a text may have it, the longest first.</summary>
    public static IReadOnlyList<(string Value, string Mark)> Values(IReadOnlyDictionary<string, (string Scope, string Value)> used, IEnumerable<string> derived)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(string name, string value)
        {
            if (value.Length < MinLength || value is "true" or "false" or "null" or "http" or "https" || value.StartsWith("{{", StringComparison.Ordinal))
                return;
            foreach (var form in KnownSecrets.Forms(value).Where(f => f.Length >= MinLength))
                values.TryAdd(form, $"«hidden: {name}»");
        }
        foreach (var (name, (_, value)) in used)
        {
            Add("{{" + name + "}}", value);
            // An address is told by its host too ("localhost:5000" refused the connection).
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri is { Scheme: "http" or "https", Host.Length: > 0 })
            {
                Add("{{" + name + "}}", uri.Authority);
                Add("{{" + name + "}}", $"{uri.Host}:{uri.Port}");
                Add("{{" + name + "}}", uri.Host);
            }
        }
        foreach (var value in derived)
        {
            Add("its sign-in", value);
            // Without its scheme ("Basic ").
            if (value.IndexOf(' ') is > 0 and var space)
                Add("its sign-in", value[(space + 1)..]);
        }
        return [.. values.Select(v => (v.Key, v.Value)).OrderByDescending(v => v.Key.Length)];
    }

    /// <summary>A JSON text with the values hidden in its strings, and its numbers that are one; null when it is not JSON.</summary>
    static string? Json(string text, IReadOnlyList<(string Value, string Mark)> values)
    {
        JsonNode? Walk(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject o:
                    var copy = new JsonObject();
                    foreach (var (key, value) in o)
                        copy[Hide(key, values)] = Walk(value);
                    return copy;
                case JsonArray a:
                    return new JsonArray([.. a.Select(Walk)]);
                case JsonValue v when v.TryGetValue<string>(out var s):
                    return JsonValue.Create(Hide(s, values));
                case JsonValue v when v.GetValueKind() == JsonValueKind.Number && values.FirstOrDefault(x => x.Value == v.ToJsonString()) is { Mark: { } mark }:
                    return JsonValue.Create(mark);
                default:
                    return node?.DeepClone();
            }
        }
        try
        {
            return JsonNode.Parse(text) is { } root ? Walk(root)?.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) : null;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return null;
        }
    }
}
