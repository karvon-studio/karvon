using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Karvon.Claude.Tools;

/// <summary>
/// Secrets hidden so that they can be given back: each value gets a mark of its own («secret: karvon-jira/acme» for one
/// Karvon keeps by name, «secret: a password #3» for one told by its form or its key's name), the same mark each time, so
/// that what Claude writes back with a mark in it (an edit, a file, a command) gets the value again before it runs, and
/// Claude works with the secret without ever seeing it. What is told: the secrets Karvon keeps, tokens by their form,
/// private keys, passwords in connection strings and URLs, bearer tokens, and the values of secret-named keys in JSON,
/// YAML and .env lines.
/// </summary>
public static partial class SecretMasks
{
    /// <summary>A password of a settings file shorter than this is taken for a placeholder.</summary>
    public const int MinLength = 4;

    /// <summary>A value marked once is told without its key or place from this long on (a shorter one may be any word).</summary>
    public const int Remembered = 6;

    static readonly ConcurrentDictionary<string, string> MarkOf = new(StringComparer.Ordinal);
    static readonly ConcurrentDictionary<string, string> ValueOf = new(StringComparer.Ordinal);
    static readonly Lock Gate = new();
    static int _next;

    /// <summary><paramref name="text"/> with its secrets marked; as it is when it has none.</summary>
    public static string Hide(string text)
    {
        if (text.Length == 0)
            return text;
        foreach (var (value, name) in KnownSecrets.All)
            if (text.Contains(value, StringComparison.Ordinal))
                text = text.Replace(value, Mark(value, name), StringComparison.Ordinal);
        // A value marked once is marked wherever it comes again (a command's output, alone on its line), the longest first.
        foreach (var (value, mark) in MarkOf.Where(m => m.Key.Length >= Remembered).OrderByDescending(m => m.Key.Length))
            if (text.Contains(value, StringComparison.Ordinal))
                text = text.Replace(value, mark, StringComparison.Ordinal);
        foreach (var (name, pattern) in Redaction.Tokens)
            text = pattern.Replace(text, m => Mark(m.Value, name));
        text = Redaction.PasswordSetting().Replace(text, m => Value(m, m.Groups[1].Value, m.Value[m.Groups[1].Length..], "a password"));
        text = Redaction.UrlCredentials().Replace(text, m => m.Groups[1].Value + Masked(m.Value[m.Groups[1].Length..^1], "a password") + "@");
        text = Redaction.BearerToken().Replace(text, m => Value(m, m.Groups[1].Value, m.Value[m.Groups[1].Length..], "a bearer token"));
        text = JsonSecret().Replace(text, m => m.Groups["before"].Value + Masked(m.Groups["value"].Value, "a secret") + m.Groups["after"].Value);
        text = LineSecret().Replace(text, m => m.Groups["before"].Value + Masked(m.Groups["value"].Value, "a secret") + m.Groups["after"].Value);
        return text;
    }

    /// <summary><paramref name="text"/> with the marks it holds given their values again (the marks Karvon did not make stay).</summary>
    public static string Reveal(string text) =>
        text.Contains("«secret: ", StringComparison.Ordinal) ? AnyMark().Replace(text, m => ValueOf.TryGetValue(m.Value, out var value) ? value : m.Value) : text;

    /// <summary>Whether <paramref name="text"/> holds a mark Karvon made.</summary>
    public static bool HasMarks(string text) =>
        text.Contains("«secret: ", StringComparison.Ordinal) && AnyMark().Matches(text).Any(m => ValueOf.ContainsKey(m.Value));

    /// <summary>What the marks in <paramref name="text"/> stand for ("a password", "karvon-jira/acme"), each once.</summary>
    public static IReadOnlyList<string> Kinds(string text) =>
        [.. AnyMark().Matches(text).Select(m => KindOf().Replace(m.Value[9..^1], "")).Distinct(StringComparer.Ordinal)];

    /// <summary>Every string of <paramref name="node"/> (a tool's input or output) hidden or given back; null when none changed.</summary>
    public static JsonNode? Map(JsonNode? node, Func<string, string> change)
    {
        var changed = false;
        JsonNode? Walk(JsonNode? n)
        {
            switch (n)
            {
                case JsonObject o:
                    var copy = new JsonObject();
                    foreach (var (key, value) in o)
                        copy[key] = Walk(value);
                    return copy;
                case JsonArray a:
                    return new JsonArray([.. a.Select(Walk)]);
                case JsonValue v when v.TryGetValue<string>(out var text):
                    // An image's or a file's bytes are not text a secret is told in.
                    var after = text.Length > 1_000_000 ? text : change(text);
                    changed |= after != text;
                    return JsonValue.Create(after);
                default:
                    return n?.DeepClone();
            }
        }
        var result = Walk(node);
        return changed ? result : null;
    }

    static string Value(Match m, string before, string value, string kind) => before + Masked(value, kind);

    /// <summary>A value told by its key's name or its place: marked, unless it is a placeholder or a mark already.</summary>
    static string Masked(string value, string kind) =>
        value.Length < MinLength || value.Contains('«') || value.Contains('»') || value.StartsWith("${", StringComparison.Ordinal)
            || value.StartsWith("{{", StringComparison.Ordinal) || value.StartsWith('<') || value.StartsWith('%') || value == "***"
            || value.Equals("null", StringComparison.OrdinalIgnoreCase)
            ? value
            : Mark(value, kind);

    /// <summary>The value's mark: the same each time; a kind told by form gets a number, a known name its own.</summary>
    static string Mark(string value, string kind)
    {
        // A mark, or a piece of one, is never a value of its own.
        if (value.Contains('«') || value.Contains('»'))
            return value;
        if (MarkOf.TryGetValue(value, out var mark))
            return mark;
        lock (Gate)
        {
            if (MarkOf.TryGetValue(value, out mark))
                return mark;
            mark = $"«secret: {kind}»";
            if (kind.StartsWith("a ", StringComparison.Ordinal) || kind.StartsWith("an ", StringComparison.Ordinal) || ValueOf.ContainsKey(mark))
                mark = $"«secret: {kind} #{++_next}»";
            ValueOf[mark] = value;
            MarkOf[value] = mark;
            return mark;
        }
    }

    const string SecretKey = "(?:password|passwd|pwd|passphrase|secret|token|api[_-]?key|access[_-]?key|private[_-]?key|credential)";

    /// <summary>"Password": "value" (JSON, as a file has it or a tool shows it).</summary>
    [GeneratedRegex("(?<before>\"[^\"\\n]*" + SecretKey + "[^\"\\n]*\"\\s*:\\s*\")(?<value>(?:[^\"\\\\\\n]|\\\\.){1,2000})(?<after>\")", RegexOptions.IgnoreCase)]
    private static partial Regex JsonSecret();

    /// <summary>
    /// password: value (YAML) and PASSWORD=value (.env, a shell's export), a line's whole value, its quotes kept; a line
    /// may begin with its number, as Read shows a file.
    /// </summary>
    [GeneratedRegex(@"(?im)(?<before>^(?:\s*\d+(?:\t|→))?\s*(?:export\s+|-\s+)?[\w.-]*" + SecretKey + @"[\w.-]*\s*[:=]\s*(?<quote>['""]?))(?<value>[^'""\s#][^'""\r\n]*?)(?<after>\k<quote>)\s*$")]
    private static partial Regex LineSecret();

    [GeneratedRegex("«secret: [^»\\n]{1,200}»")]
    private static partial Regex AnyMark();

    [GeneratedRegex(" #\\d+$")]
    private static partial Regex KindOf();

    /// <summary>For the tests: no mark made yet.</summary>
    internal static void Clear()
    {
        lock (Gate)
        {
            MarkOf.Clear();
            ValueOf.Clear();
        }
    }
}
