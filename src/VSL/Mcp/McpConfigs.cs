using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Tomlyn;
using Tomlyn.Model;
using VSL.Claude;

namespace VSL.Mcp;

/// <summary>Where an MCP server is defined: an agent's own configuration, the folder's, or VSL's. In display order.</summary>
public enum McpOrigin
{
    Project,
    ClaudeUser,
    ClaudeLocal,
    Codex,
    GeminiUser,
    GeminiProject,
    Vsl,
}

/// <summary>An MCP server as a configuration has it: a program to start (stdio) or a URL (HTTP, or the older SSE).</summary>
public sealed record McpServerDefinition(McpOrigin Origin, string Name)
{
    static readonly IReadOnlyDictionary<string, string> None = new Dictionary<string, string>();

    public string? Command { get; init; }

    public IReadOnlyList<string> Args { get; init; } = [];

    public IReadOnlyDictionary<string, string> Env { get; init; } = None;

    public string? Url { get; init; }

    public IReadOnlyDictionary<string, string> Headers { get; init; } = None;

    /// <summary>A URL that speaks only the older SSE transport (type "sse", or Gemini CLI's <c>url</c>).</summary>
    public bool IsSse { get; init; }

    /// <summary>The same name can be in several files: each is a server of its own.</summary>
    public string Key => $"{Origin}:{Name}";

    public bool IsHttp => Url is not null;

    public string CommandLine => McpConfigs.JoinCommandLine([Command ?? "", .. Args]);

    /// <summary>"stdio npx -y server" or "HTTP https://...": never its environment or headers, which may hold secrets.</summary>
    public string Transport => IsHttp ? (IsSse ? "SSE " : "HTTP ") + Url : "stdio " + CommandLine;
}

/// <summary>The servers of every configuration, and the files that could not be read.</summary>
public sealed record McpRead(IReadOnlyList<McpServerDefinition> Servers, IReadOnlyList<string> Problems);

/// <summary>
/// The MCP servers of the agents' configurations, read and written in each one's own format: the folder's .mcp.json,
/// Claude Code's ~/.claude.json (every project's, and this folder's under "projects"), Codex's config.toml, Gemini CLI's
/// settings.json (the user's and the folder's), and VSL's own list. Also which of them the Claude Code sessions VSL starts
/// get beside the ones Claude Code reads itself.
/// </summary>
public sealed partial class McpConfigs(string configDirectory, string? workspace, string? claudeFile = null)
{
    static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>The home the agents keep their files in: VSL_USER_HOME (the tests'), else the user's.</summary>
    public static string Home => Environment.GetEnvironmentVariable("VSL_USER_HOME") is { Length: > 0 } home ? home
        : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Codex's folder: CODEX_HOME, else ~/.codex (always the latter in a home of the tests').</summary>
    static string CodexHome => Environment.GetEnvironmentVariable("VSL_USER_HOME") is null && Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } codex
        ? codex : Path.Combine(Home, ".codex");

    public string ConfigDirectory { get; } = configDirectory;

    /// <summary>The solution's folder, or null when none is open (its files are then not read).</summary>
    public string? Workspace { get; } = workspace is { Length: > 0 } ? workspace : null;

    /// <summary>Claude Code's settings: ~/.claude.json, or the chosen account's folder's.</summary>
    public string ClaudeFile { get; } = claudeFile ?? Path.Combine(Home, ".claude.json");

    string SwitchesFile => Path.Combine(ConfigDirectory, "mcp-claude.json");

    public IEnumerable<McpOrigin> Origins => Enum.GetValues<McpOrigin>().Where(o => Workspace is not null || !IsInWorkspace(o));

    public string FileOf(McpOrigin origin) => origin switch
    {
        McpOrigin.Project => Path.Combine(Workspace ?? "", ".mcp.json"),
        McpOrigin.ClaudeUser or McpOrigin.ClaudeLocal => ClaudeFile,
        McpOrigin.Codex => Path.Combine(CodexHome, "config.toml"),
        McpOrigin.GeminiUser => Path.Combine(Home, ".gemini", "settings.json"),
        McpOrigin.GeminiProject => Path.Combine(Workspace ?? "", ".gemini", "settings.json"),
        _ => Path.Combine(ConfigDirectory, "mcp-servers.json"),
    };

    public static string Describe(McpOrigin origin) => origin switch
    {
        McpOrigin.Project => "This folder (.mcp.json)",
        McpOrigin.ClaudeUser => "Claude Code",
        McpOrigin.ClaudeLocal => "Claude Code, this folder",
        McpOrigin.Codex => "Codex",
        McpOrigin.GeminiUser => "Gemini CLI",
        McpOrigin.GeminiProject => "Gemini CLI, this folder",
        _ => "VSL",
    };

    public static bool IsInWorkspace(McpOrigin origin) => origin is McpOrigin.Project or McpOrigin.ClaudeLocal or McpOrigin.GeminiProject;

    /// <summary>
    /// Claude Code reads these itself (asking once before it starts those of .mcp.json): VSL never hands them to its
    /// sessions again, nor anything under their names.
    /// </summary>
    public static bool ClaudeReadsItself(McpOrigin origin) => origin is McpOrigin.Project or McpOrigin.ClaudeUser or McpOrigin.ClaudeLocal;

    static bool IsGemini(McpOrigin origin) => origin is McpOrigin.GeminiUser or McpOrigin.GeminiProject;

    // ---- Reading -------------------------------------------------------------------------------------------------

    public McpRead Read()
    {
        var servers = new List<McpServerDefinition>();
        var problems = new List<string>();
        foreach (var origin in Origins)
        {
            var file = FileOf(origin);
            if (!File.Exists(file))
                continue;
            try
            {
                if (origin == McpOrigin.Codex)
                    servers.AddRange(FromToml(File.ReadAllText(file)));
                else
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file), Lenient);
                    var holder = origin == McpOrigin.ClaudeLocal ? ProjectOf(doc.RootElement) : doc.RootElement;
                    if (holder is { ValueKind: JsonValueKind.Object } h && h.TryGetProperty("mcpServers", out var map) && map.ValueKind == JsonValueKind.Object)
                        servers.AddRange(map.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Object).Select(p => FromJson(origin, p.Name, p.Value)));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or TomlException)
            {
                problems.Add($"{file}: {ex.Message}");
            }
        }
        return new McpRead(servers, problems);
    }

    /// <summary>~/.claude.json's entry for this folder (Claude Code keys it by the folder's path).</summary>
    JsonElement? ProjectOf(JsonElement root)
    {
        if (Workspace is null || root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("projects", out var projects) || projects.ValueKind != JsonValueKind.Object)
            return null;
        return ProjectKey(projects.EnumerateObject().Select(p => p.Name)) is { } key ? projects.GetProperty(key) : null;
    }

    string? ProjectKey(IEnumerable<string> keys)
    {
        var folder = Workspace!.TrimEnd('/', '\\');
        return keys.FirstOrDefault(k => k == Workspace) ?? keys.FirstOrDefault(k => k.TrimEnd('/', '\\') == folder);
    }

    static McpServerDefinition FromJson(McpOrigin origin, string name, JsonElement entry)
    {
        var httpUrl = Str(entry, "httpUrl");
        var url = httpUrl ?? Str(entry, "url");
        return new McpServerDefinition(origin, name)
        {
            Command = url is null ? Str(entry, "command") : null,
            Args = url is null && entry.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array
                ? args.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.String).Select(a => a.GetString()!).ToList() : [],
            Env = Map(entry, "env"),
            Url = url,
            Headers = Map(entry, "headers"),
            // Gemini CLI's url is SSE (httpUrl is streamable HTTP); the others say "type": "sse".
            IsSse = url is not null && (Str(entry, "type") == "sse" || IsGemini(origin) && httpUrl is null),
        };
    }

    static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static Dictionary<string, string> Map(JsonElement e, string name) =>
        e.TryGetProperty(name, out var map) && map.ValueKind == JsonValueKind.Object
            ? map.EnumerateObject().Where(p => p.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                .ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText())
            : [];

    static IEnumerable<McpServerDefinition> FromToml(string text)
    {
        if (!Toml.ToModel(text).TryGetValue("mcp_servers", out var section) || section is not TomlTable servers)
            yield break;
        foreach (var (name, value) in servers)
        {
            if (value is not TomlTable table)
                continue;
            var url = table.TryGetValue("url", out var u) ? u as string : null;
            yield return new McpServerDefinition(McpOrigin.Codex, name)
            {
                Command = url is null && table.TryGetValue("command", out var c) ? c as string : null,
                Args = url is null && table.TryGetValue("args", out var a) && a is TomlArray array ? array.OfType<string>().ToList() : [],
                Env = TomlMap(table, "env"),
                Url = url,
                Headers = TomlMap(table, "http_headers"),
            };
        }
    }

    static Dictionary<string, string> TomlMap(TomlTable table, string name) =>
        table.TryGetValue(name, out var value) && value is TomlTable map
            ? map.Where(p => p.Value is string or long or double or bool).ToDictionary(p => p.Key, p => Convert.ToString(p.Value, System.Globalization.CultureInfo.InvariantCulture)!)
            : [];

    // ---- Writing -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Adds the server to its origin's file, or changes it there (<paramref name="replacing"/>: its name before); the
    /// rest of the file stays, and the entry's other settings too. Returns the file.
    /// </summary>
    public string Write(McpServerDefinition server, string? replacing = null)
    {
        var file = FileOf(server.Origin);
        if (server.Origin == McpOrigin.Codex)
            EditToml(file, replacing ?? server.Name, server);
        else
            EditJson(file, server.Origin, map =>
            {
                var entry = map[replacing ?? server.Name] as JsonObject;
                if (replacing is not null)
                    map.Remove(replacing);
                map[server.Name] = ToJson(server, IsGemini(server.Origin), entry?.DeepClone() as JsonObject);
            });
        return file;
    }

    public void Remove(McpServerDefinition server)
    {
        var file = FileOf(server.Origin);
        if (server.Origin == McpOrigin.Codex)
            EditToml(file, server.Name, null);
        else
            EditJson(file, server.Origin, map => map.Remove(server.Name));
        var switches = Switches();
        if (switches.Remove(server.Key))
            SaveSwitches(switches);
    }

    void EditJson(string file, McpOrigin origin, Action<JsonObject> edit)
    {
        var root = File.Exists(file) && JsonNode.Parse(File.ReadAllText(file), documentOptions: Lenient) is JsonObject existing ? existing : new JsonObject();
        var holder = root;
        if (origin == McpOrigin.ClaudeLocal)
        {
            var projects = Child(root, "projects");
            holder = Child(projects, ProjectKey(projects.Select(p => p.Key)) ?? Workspace!);
        }
        edit(Child(holder, "mcpServers"));
        Save(file, root.ToJsonString(Indented) + "\n", secret: origin == McpOrigin.Vsl);
    }

    static JsonObject Child(JsonObject parent, string name)
    {
        if (parent[name] is JsonObject child)
            return child;
        var made = new JsonObject();
        parent[name] = made;
        return made;
    }

    /// <summary>The entry in Claude Code's shape ("type", as .mcp.json has it), or Gemini CLI's (httpUrl, no type).</summary>
    static JsonObject ToJson(McpServerDefinition server, bool gemini, JsonObject? entry = null)
    {
        entry ??= new JsonObject();
        foreach (var key in server.IsHttp ? new[] { "command", "args", "env", "url", "httpUrl" } : ["url", "httpUrl", "headers", "command", "args"])
            entry.Remove(key);
        if (!gemini)
            entry["type"] = server.IsHttp ? server.IsSse ? "sse" : "http" : "stdio";
        if (server.IsHttp)
        {
            entry[gemini && !server.IsSse ? "httpUrl" : "url"] = server.Url;
            SetMap(entry, "headers", server.Headers);
        }
        else
        {
            entry["command"] = server.Command;
            entry["args"] = new JsonArray([.. server.Args.Select(a => (JsonNode?)a)]);
            SetMap(entry, "env", server.Env);
        }
        return entry;
    }

    static void SetMap(JsonObject entry, string name, IReadOnlyDictionary<string, string> map)
    {
        if (map.Count == 0)
            entry.Remove(name);
        else
            entry[name] = new JsonObject(map.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value)));
    }

    /// <summary>
    /// config.toml is the user's, comments and all: only the server's own tables ([mcp_servers.name] and its
    /// [mcp_servers.name.*]) are taken out, and the new ones go at the end.
    /// </summary>
    static void EditToml(string file, string name, McpServerDefinition? server)
    {
        var lines = File.Exists(file) ? File.ReadAllLines(file).ToList() : [];
        var kept = new List<string>();
        var inside = false;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('['))
                inside = IsTableOf(trimmed, name) || server is not null && IsTableOf(trimmed, server.Name);
            if (!inside)
                kept.Add(line);
        }
        while (kept.Count > 0 && kept[^1].Trim().Length == 0)
            kept.RemoveAt(kept.Count - 1);
        if (server is not null)
        {
            if (kept.Count > 0)
                kept.Add("");
            kept.Add($"[mcp_servers.{TomlKey(server.Name)}]");
            if (server.IsHttp)
            {
                kept.Add($"url = {TomlString(server.Url!)}");
                if (server.Headers.Count > 0)
                    kept.Add($"http_headers = {TomlTable(server.Headers)}");
            }
            else
            {
                kept.Add($"command = {TomlString(server.Command ?? "")}");
                kept.Add($"args = [{string.Join(", ", server.Args.Select(TomlString))}]");
                if (server.Env.Count > 0)
                    kept.Add($"env = {TomlTable(server.Env)}");
            }
        }
        Save(file, string.Join("\n", kept) + "\n", secret: false);
    }

    static bool IsTableOf(string header, string name)
    {
        var compact = WhiteSpace().Replace(header.TrimStart('[').TrimEnd(']'), "");
        foreach (var key in new[] { name, $"\"{name.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"" })
            if (compact == "mcp_servers." + key || compact.StartsWith("mcp_servers." + key + ".", StringComparison.Ordinal))
                return true;
        return false;
    }

    static string TomlKey(string name) => BareKey().IsMatch(name) ? name : TomlString(name);

    static string TomlTable(IReadOnlyDictionary<string, string> map) => "{ " + string.Join(", ", map.Select(p => $"{TomlString(p.Key)} = {TomlString(p.Value)}")) + " }";

    static string TomlString(string text)
    {
        var builder = new StringBuilder("\"");
        foreach (var c in text)
            builder.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                < ' ' => $"\\u{(int)c:X4}",
                _ => c.ToString(),
            });
        return builder.Append('"').ToString();
    }

    /// <summary>A file of VSL's that may hold a token is the user's alone.</summary>
    static void Save(string file, string text, bool secret)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
        if (secret && !OperatingSystem.IsWindows())
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    // ---- Claude Code sessions -----------------------------------------------------------------------------------

    /// <summary>Whether the Claude Code sessions VSL starts have it: always those Claude reads itself; VSL's own by default.</summary>
    public bool IsForClaude(McpServerDefinition server) =>
        ClaudeReadsItself(server.Origin) || (Switches().TryGetValue(server.Key, out var on) ? on : server.Origin == McpOrigin.Vsl);

    public void SetForClaude(McpServerDefinition server, bool on)
    {
        var switches = Switches();
        switches[server.Key] = on;
        SaveSwitches(switches);
    }

    Dictionary<string, bool> Switches()
    {
        try
        {
            return File.Exists(SwitchesFile) ? JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(SwitchesFile)) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    void SaveSwitches(Dictionary<string, bool> switches) => Save(SwitchesFile, JsonSerializer.Serialize(switches, Indented) + "\n", secret: false);

    /// <summary>
    /// What a Claude Code session VSL starts gets beside its own servers (as <c>mcpServers</c>): the others switched on for
    /// it, under names Claude does not have already (nor "vsl", VSL's tools), the first of a name winning, with ${VAR}
    /// filled in from the environment.
    /// </summary>
    public JsonObject ForClaude()
    {
        var servers = Read().Servers;
        var taken = servers.Where(s => ClaudeReadsItself(s.Origin)).Select(s => s.Name).Append(ClaudeToolServer.Name).ToHashSet(StringComparer.Ordinal);
        var given = new JsonObject();
        foreach (var server in servers.Where(s => !ClaudeReadsItself(s.Origin) && IsForClaude(s)))
            if (!taken.Contains(server.Name) && !given.ContainsKey(server.Name))
                given[server.Name] = ToJson(Expand(server), gemini: false);
        return given;
    }

    // ---- Variables and command lines ----------------------------------------------------------------------------

    /// <summary>${VAR} and ${VAR:-default}, as Claude Code expands them; an unset variable is its default or empty.</summary>
    public static string Expand(string text) =>
        Variable().Replace(text, m => Environment.GetEnvironmentVariable(m.Groups[1].Value) is { Length: > 0 } value ? value : m.Groups[2].Value);

    public static McpServerDefinition Expand(McpServerDefinition server) => server with
    {
        Command = server.Command is null ? null : Expand(server.Command),
        Args = server.Args.Select(Expand).ToList(),
        Env = server.Env.ToDictionary(p => p.Key, p => Expand(p.Value)),
        Url = server.Url is null ? null : Expand(server.Url),
        Headers = server.Headers.ToDictionary(p => p.Key, p => Expand(p.Value)),
    };

    /// <summary>Words split at white space, "quoted words" kept together.</summary>
    public static List<string> SplitCommandLine(string line)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        var quoted = false;
        var any = false;
        foreach (var c in line)
        {
            if (c == '"')
            {
                quoted = !quoted;
                any = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any)
                    words.Add(word.ToString());
                word.Clear();
                any = false;
            }
            else
            {
                word.Append(c);
                any = true;
            }
        }
        if (any)
            words.Add(word.ToString());
        return words;
    }

    public static string JoinCommandLine(IEnumerable<string> words) =>
        string.Join(" ", words.Select(w => w.Length == 0 || w.Any(c => char.IsWhiteSpace(c) || c == '"') ? "\"" + w.Replace("\"", "") + "\"" : w));

    [GeneratedRegex(@"\$\{([A-Za-z_][A-Za-z0-9_]*)(?::-([^}]*))?\}")]
    private static partial Regex Variable();

    [GeneratedRegex(@"^[A-Za-z0-9_-]+$")]
    private static partial Regex BareKey();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();
}
