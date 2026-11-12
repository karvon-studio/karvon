using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Karvon.ViewModels;

namespace Karvon.Claude;

/// <summary>
/// Karvon's transcript of a conversation with an agent other than Claude Code: what the panel showed (the prompts, the
/// answers, the tool calls and their results, the notices), a JSON line each, so that it can be shown again whichever agent
/// it was; the agent goes on with its own conversation (Codex's thread, an ACP session, the messages Karvon kept of a model's).
/// </summary>
public sealed class AgentTranscript
{
    readonly string _file;
    readonly Lock _lock = new();

    AgentTranscript(string file) => _file = file;

    /// <summary>The conversation's file (made with its header when new).</summary>
    public static AgentTranscript Open(string directory, string agentId, string agentName, string workingDirectory, string sessionId)
    {
        var folder = AgentTranscripts.Folder(directory, workingDirectory);
        AgentTranscripts.PrivateFolder(directory);
        AgentTranscripts.PrivateFolder(folder);
        var file = Path.Combine(folder, AgentTranscripts.FileName(sessionId));
        var transcript = new AgentTranscript(file);
        if (!File.Exists(file))
            transcript.Write(new JsonObject { ["kind"] = "start", ["agent"] = agentId, ["name"] = agentName, ["session"] = sessionId, ["cwd"] = workingDirectory });
        return transcript;
    }

    /// <summary>A prompt the user sent (the panel shows it itself; the agent does not tell it).</summary>
    public void Prompt(string text, IEnumerable<ClaudeImage>? images) => Write(new JsonObject
    {
        ["kind"] = "user",
        ["text"] = text,
        ["images"] = new JsonArray([.. (images ?? []).Select(i => (JsonNode)new JsonObject { ["mediaType"] = i.MediaType, ["data"] = i.Base64 })]),
    });

    /// <summary>What the agent told, as far as it stays in the conversation (not what streamed, nor the questions of the moment).</summary>
    public void Record(ClaudeEvent ev)
    {
        switch (ev)
        {
            case ClaudeAssistant { ParentToolUseId: null } assistant:
                Write(new JsonObject
                {
                    ["kind"] = "assistant",
                    ["id"] = assistant.MessageId,
                    ["blocks"] = new JsonArray([.. assistant.Blocks.Select(Block).OfType<JsonNode>()]),
                });
                break;
            case ClaudeToolResults { ParentToolUseId: null } results:
                Write(new JsonObject
                {
                    ["kind"] = "results",
                    ["results"] = new JsonArray([.. results.Results.Select(r => (JsonNode)new JsonObject { ["id"] = r.ToolUseId, ["content"] = r.Content, ["error"] = r.IsError })]),
                });
                break;
            case ClaudeNotice notice:
                Write(new JsonObject { ["kind"] = "notice", ["text"] = notice.Text, ["error"] = notice.IsError });
                break;
            case ClaudeResult { IsError: true } result:
                Write(new JsonObject
                {
                    ["kind"] = "notice",
                    ["text"] = result.Subtype == "error_during_execution" ? "Interrupted." : result.Text ?? "The agent reported an error.",
                    ["error"] = result.Subtype != "error_during_execution",
                });
                break;
        }
    }

    static JsonNode? Block(ClaudeBlock block) => block switch
    {
        ClaudeTextBlock text => new JsonObject { ["type"] = "text", ["text"] = text.Text },
        ClaudeThinkingBlock thinking => new JsonObject { ["type"] = "thinking", ["text"] = thinking.Text },
        ClaudeToolUseBlock tool => new JsonObject { ["type"] = "tool_use", ["id"] = tool.Id, ["name"] = tool.Name, ["input"] = JsonNode.Parse(tool.Input.ValueKind == JsonValueKind.Undefined ? "{}" : tool.Input.GetRawText()) },
        _ => null,
    };

    void Write(JsonObject line)
    {
        line["at"] = DateTime.UtcNow.ToString("O");
        try
        {
            lock (_lock)
            {
                using var stream = AgentTranscripts.OpenPrivate(_file, FileMode.Append);
                stream.Write(System.Text.Encoding.UTF8.GetBytes(line.ToJsonString() + "\n"));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A transcript that cannot be written leaves the conversation as it is.
        }
    }
}

/// <summary>
/// The agents' conversations of a folder, from Karvon's transcripts of them. They hold what was said, and what the tools
/// returned: their folders and files are their owner's alone.
/// </summary>
public static partial class AgentTranscripts
{
    const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    const UnixFileMode OwnerOnlyFolder = OwnerOnly | UnixFileMode.UserExecute;

    /// <summary>A folder only its owner opens: made so, or made so again (an older Karvon left others able to read it).</summary>
    public static void PrivateFolder(string folder)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(folder);
            return;
        }
        Directory.CreateDirectory(folder, OwnerOnlyFolder);
        if ((File.GetUnixFileMode(folder) & ~OwnerOnlyFolder) != 0)
            File.SetUnixFileMode(folder, OwnerOnlyFolder);
    }

    /// <summary>A file of theirs to write, only its owner able to read it (made so when it was not).</summary>
    public static FileStream OpenPrivate(string file, FileMode mode)
    {
        var options = new FileStreamOptions { Mode = mode, Access = FileAccess.Write, Share = FileShare.Read };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = OwnerOnly;
        var stream = new FileStream(file, options);
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(stream.SafeFileHandle) & ~OwnerOnly) != 0)
            File.SetUnixFileMode(stream.SafeFileHandle, OwnerOnly);
        return stream;
    }

    /// <summary>The folder of a working directory's transcripts.</summary>
    public static string Folder(string directory, string workingDirectory) =>
        Path.Combine(directory, NonAlphanumeric().Replace(Path.GetFullPath(workingDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), "-"));

    public static string FileName(string sessionId) => NonFileName().Replace(sessionId, "-") + ".jsonl";

    /// <summary>Where an agent keeps what it needs to go on with a conversation (the messages Karvon sends a model).</summary>
    public static string StateFile(string transcript) => Path.ChangeExtension(transcript, ".state.json");

    /// <summary>The newest first; a transcript with no prompt yet is left out.</summary>
    public static IReadOnlyList<ClaudeConversation> List(string directory, string workingDirectory, int max = 50)
    {
        var folder = Folder(directory, workingDirectory);
        if (!Directory.Exists(folder))
            return [];
        return [.. Directory.EnumerateFiles(folder, "*.jsonl")
            .Select(f => (File: f, Modified: File.GetLastWriteTime(f)))
            .OrderByDescending(f => f.Modified)
            .Select(f => Read(f.File, f.Modified))
            .OfType<ClaudeConversation>()
            .Take(max)];
    }

    /// <summary>The conversation with this id in the folder's transcripts; null when there is none.</summary>
    public static ClaudeConversation? Find(string directory, string workingDirectory, string sessionId)
    {
        var file = Path.Combine(Folder(directory, workingDirectory), FileName(sessionId));
        return File.Exists(file) ? Read(file, File.GetLastWriteTime(file)) : null;
    }

    static ClaudeConversation? Read(string file, DateTime modified)
    {
        string? agent = null, name = null, session = null, title = null;
        try
        {
            foreach (var line in File.ReadLines(file))
            {
                if (Parse(line) is not { } e)
                    continue;
                switch (ClaudeJson.Str(e, "kind"))
                {
                    case "start":
                        (agent, name, session) = (ClaudeJson.Str(e, "agent"), ClaudeJson.Str(e, "name"), ClaudeJson.Str(e, "session"));
                        break;
                    case "user" when ClaudeHistory.StripContext(ClaudeJson.Str(e, "text") ?? "") is { Length: > 0 } text:
                        title = text.Split('\n')[0];
                        break;
                }
                if (title is not null)
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        return agent is null || session is null || title is null
            ? null
            : new ClaudeConversation(session, title.Length > 120 ? title[..117] + "..." : title, modified, file) { AgentId = agent, Agent = name };
    }

    static JsonElement? Parse(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>What the panel showed, as events it shows again.</summary>
    public static IReadOnlyList<ClaudeEvent> Load(string file)
    {
        var events = new List<ClaudeEvent>();
        foreach (var line in File.ReadLines(file))
        {
            if (Parse(line) is not { } e)
                continue;
            switch (ClaudeJson.Str(e, "kind"))
            {
                case "user":
                    var images = e.TryGetProperty("images", out var list) && list.ValueKind == JsonValueKind.Array
                        ? list.EnumerateArray().Select(i => new ClaudeImage(ClaudeJson.Str(i, "mediaType") ?? "image/png", ClaudeJson.Str(i, "data") ?? "")).ToList()
                        : [];
                    events.Add(new ClaudeUserText(ClaudeJson.Str(e, "text") ?? "", images));
                    break;
                case "assistant" when e.TryGetProperty("blocks", out var blocks) && blocks.ValueKind == JsonValueKind.Array:
                    events.Add(new ClaudeAssistant(ClaudeJson.Str(e, "id") ?? Guid.NewGuid().ToString("N"),
                        [.. blocks.EnumerateArray().Select(Block).OfType<ClaudeBlock>()], null));
                    break;
                case "results" when e.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array:
                    events.Add(new ClaudeToolResults([.. results.EnumerateArray().Select(r =>
                        new ClaudeToolResult(ClaudeJson.Str(r, "id") ?? "", ClaudeJson.Str(r, "content") ?? "", ClaudeJson.Bool(r, "error")))], null));
                    break;
                case "notice":
                    events.Add(new ClaudeNotice(ClaudeJson.Str(e, "text") ?? "", ClaudeJson.Bool(e, "error")));
                    break;
            }
        }
        return events;
    }

    static ClaudeBlock? Block(JsonElement block) => ClaudeJson.Str(block, "type") switch
    {
        "text" => new ClaudeTextBlock(ClaudeJson.Str(block, "text") ?? ""),
        "thinking" => new ClaudeThinkingBlock(ClaudeJson.Str(block, "text") ?? ""),
        "tool_use" => new ClaudeToolUseBlock(ClaudeJson.Str(block, "id") ?? "", ClaudeJson.Str(block, "name") ?? "",
            block.TryGetProperty("input", out var input) ? input.Clone() : JsonDocument.Parse("{}").RootElement.Clone()),
        _ => null,
    };

    /// <summary>Deletes Karvon's transcript and what it kept for the agent (not the agent's own record of it).</summary>
    public static void Delete(ClaudeConversation conversation)
    {
        File.Delete(conversation.FilePath);
        var state = StateFile(conversation.FilePath);
        if (File.Exists(state))
            File.Delete(state);
    }

    [GeneratedRegex("[^A-Za-z0-9]")]
    private static partial Regex NonAlphanumeric();

    [GeneratedRegex("[^A-Za-z0-9_.-]")]
    private static partial Regex NonFileName();
}
