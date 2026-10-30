using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Karvon.Claude.Tools;

namespace Karvon.Claude;

/// <summary>
/// A session's audit trail: a JSON line for each tool call Claude asked for (<c>requested</c>), what decided it
/// (<c>decided</c>: Claude Code itself, Karvon's hooks, the user's rules, the user on a prompt, plan mode, another device),
/// what Karvon's tools ran (<c>ran</c>: when, how long, its outcome, its output's size, what was hidden of it) and what
/// Claude was given back (<c>result</c>), so that what was requested and what was executed can be compared. Inputs are
/// kept with their secrets hidden and cut; outputs are not kept. In Karvon's settings folder (audit/, the user's alone),
/// thirty days of them.
/// </summary>
public sealed partial class ClaudeAudit
{
    /// <summary>How long a trail is kept.</summary>
    public static readonly TimeSpan Kept = TimeSpan.FromDays(30);

    const int MaxInput = 4000;

    readonly Lock _lock = new();
    readonly ConcurrentDictionary<string, string> _requested = new();
    readonly ConcurrentDictionary<string, bool> _decided = new();

    ClaudeAudit(string file) => File = file;

    /// <summary>The trail's file.</summary>
    public string File { get; }

    /// <summary>The session Claude Code gave the conversation, once it said (each line after has it).</summary>
    public string? SessionId { get; set; }

    /// <summary>A new trail in <paramref name="directory"/>, the trails older than <see cref="Kept"/> taken away; null when it cannot be written.</summary>
    public static ClaudeAudit? Start(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            if (!OperatingSystem.IsWindows())
                System.IO.File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            foreach (var old in Directory.EnumerateFiles(directory, "*.jsonl"))
                if (DateTime.UtcNow - System.IO.File.GetLastWriteTimeUtc(old) > Kept)
                    System.IO.File.Delete(old);
            var file = Path.Combine(directory, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}.jsonl");
            System.IO.File.WriteAllText(file, "");
            if (!OperatingSystem.IsWindows())
                System.IO.File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return new ClaudeAudit(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Claude asked for a call (a tool_use), once for each.</summary>
    public void Requested(string toolUseId, string tool, JsonElement input)
    {
        if (_requested.TryAdd(toolUseId, tool))
            Write("requested", new JsonObject { ["toolUseId"] = toolUseId, ["tool"] = tool, ["input"] = Input(input) });
    }

    /// <summary>What decided a call: <paramref name="decision"/> (allow, deny, ask) and who (<paramref name="by"/>), with why.</summary>
    public void Decided(string? toolUseId, string tool, string decision, string by, string? reason = null)
    {
        if (toolUseId is not null)
            _decided[toolUseId] = true;
        var entry = new JsonObject { ["toolUseId"] = toolUseId, ["tool"] = tool, ["decision"] = decision, ["by"] = by };
        if (reason is { Length: > 0 })
            entry["reason"] = Redaction.Text(reason);
        Write("decided", entry);
    }

    /// <summary>
    /// One of Karvon's tools ran: when, how long, whether it failed, the size of what it gave, the secrets hidden of it
    /// (<paramref name="raw"/> as the tool gave it, <paramref name="sent"/> as Claude got it), and what of it others wrote.
    /// </summary>
    public void Ran(string tool, JsonElement input, DateTime started, TimeSpan took, bool isError, string raw, string sent, string? outside, int instructions)
    {
        var hidden = new JsonArray();
        foreach (var kind in Hidden(raw, sent))
            hidden.Add(kind);
        var entry = new JsonObject
        {
            ["tool"] = tool,
            ["input"] = Input(input),
            ["started"] = started.ToUniversalTime().ToString("O"),
            ["ms"] = (long)took.TotalMilliseconds,
            ["isError"] = isError,
            ["outputChars"] = raw.Length,
            ["hidden"] = hidden,
        };
        if (outside is not null)
        {
            entry["outside"] = outside;
            entry["instructions"] = instructions;
        }
        Write("ran", entry);
    }

    /// <summary>
    /// What Claude was given back for a call; a call nothing was heard of being decided was let through by Claude Code itself
    /// (its permission mode, its allow rules, a tool that does not ask).
    /// </summary>
    public void Result(string toolUseId, bool isError, int chars)
    {
        var tool = _requested.GetValueOrDefault(toolUseId) ?? "";
        if (!_decided.ContainsKey(toolUseId))
            Decided(toolUseId, tool, "allow", "Claude Code");
        Write("result", new JsonObject { ["toolUseId"] = toolUseId, ["tool"] = tool, ["isError"] = isError, ["chars"] = chars });
    }

    /// <summary>What Karvon's gateway hid of a request to the model (what Claude Code's own tools read among it).</summary>
    public void Scanned(IReadOnlyList<string> hidden)
    {
        var kinds = new JsonArray();
        foreach (var kind in hidden)
            kinds.Add(kind);
        Write("scanned", new JsonObject { ["hidden"] = kinds });
    }

    /// <summary>A trail's lines.</summary>
    public static IReadOnlyList<JsonObject> Read(string file) =>
        [.. System.IO.File.ReadAllLines(file).Where(l => l.Length > 0).Select(l => JsonNode.Parse(l)!.AsObject())];

    void Write(string kind, JsonObject entry)
    {
        var line = new JsonObject { ["at"] = DateTime.UtcNow.ToString("O"), ["kind"] = kind, ["session"] = SessionId };
        foreach (var (name, value) in entry.ToList())
        {
            entry.Remove(name);
            line[name] = value;
        }
        lock (_lock)
        {
            try
            {
                System.IO.File.AppendAllText(File, line.ToJsonString() + "\n");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A trail that cannot be written does not stop the session.
            }
        }
    }

    /// <summary>The call's input as the trail keeps it: its secrets hidden, cut when long.</summary>
    static string Input(JsonElement input)
    {
        var text = Redaction.Text(input.ValueKind == JsonValueKind.Undefined ? "{}" : input.GetRawText());
        return text.Length <= MaxInput ? text : text[..MaxInput] + "…";
    }

    /// <summary>What was hidden of a tool's output: the «secret: …» marks Claude got that the tool did not write itself.</summary>
    static IEnumerable<string> Hidden(string raw, string sent)
    {
        var before = SecretMark().Matches(raw).Select(m => m.Groups[1].Value).ToList();
        foreach (var kind in SecretMark().Matches(sent).Select(m => m.Groups[1].Value))
            if (!before.Remove(kind))
                yield return kind;
    }

    [GeneratedRegex("«secret: ([^»]+)»")]
    private static partial Regex SecretMark();
}
