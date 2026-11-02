using System.Text.Json;
using System.Text.Json.Nodes;
using Karvon.Services;

namespace Karvon.Documents;

/// <summary>One of an engine's tools, as its server lists it.</summary>
public sealed record EngineTool(string Name, string Title, string Description, JsonObject InputSchema, bool ReadOnly)
{
    /// <summary>Whether its arguments take a document id (PdfCraft's, which opens several in one session).</summary>
    public bool TakesDocument => InputSchema["properties"] is JsonObject properties && properties.ContainsKey("doc");
}

/// <summary>An engine's answer to a tool: its text (JSON written compactly) and its pictures (PNG, base64).</summary>
public sealed record EngineResult(string Text, IReadOnlyList<string> Images, bool IsError);

/// <summary>A document open in an engine: its own server, what it was opened from, the engine's tools.</summary>
public sealed class OpenDocument(int id, DocumentKind kind, string? path, IDocumentServer server, IReadOnlyList<EngineTool> tools)
{
    public int Id => id;

    public DocumentKind Kind => kind;

    /// <summary>The file it was opened from, or last saved to; null for a new document not saved yet.</summary>
    public string? Path { get; internal set; } = path;

    public IReadOnlyList<EngineTool> Tools => tools;

    internal IDocumentServer Server => server;

    /// <summary>PdfCraft's id of the document in its session.</summary>
    internal int? EngineDocument { get; set; }

    int _next;

    internal int NextRequest() => ++_next;

    public string Title => Path is { } file ? System.IO.Path.GetFileName(file) : $"a new {kind.Name} document";
}

/// <summary>
/// The documents open in the engines, by Karvon's id for each: opened from a file (or new), their engine's tools called,
/// saved, closed. Each has its engine's server of its own, which keeps it in memory: nothing is written until it is saved.
/// At most <see cref="Limit"/> are open; opening another closes the one used least lately.
/// </summary>
public sealed class DocumentSessions(Func<CancellationToken, Task<IDocumentEngines>> engines) : IDisposable
{
    public const int Limit = 16;

    readonly Dictionary<int, OpenDocument> _open = [];
    readonly List<int> _used = [];
    readonly Lock _gate = new();
    int _nextId;

    static readonly JsonSerializerOptions Compact = new() { WriteIndented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public IReadOnlyList<OpenDocument> Open
    {
        get
        {
            lock (_gate)
                return [.. _open.Values.OrderBy(d => d.Id)];
        }
    }

    public OpenDocument? Find(int id)
    {
        lock (_gate)
        {
            if (!_open.TryGetValue(id, out var document))
                return null;
            _used.Remove(id);
            _used.Add(id);
            return document;
        }
    }

    /// <summary><paramref name="path"/> opened (or, with none, a new document of <paramref name="kind"/>): the document and what its engine said.</summary>
    public async Task<(OpenDocument Document, EngineResult Opened)> OpenAsync(DocumentKind kind, string? path, JsonObject? newArguments, CancellationToken ct)
    {
        if (path is null && kind.NewTool is null)
            throw new InvalidOperationException($"The {kind.Name} engine opens files only: give a file's path.");
        var server = await (await engines(ct)).OpenAsync(kind.Id, ct);
        try
        {
            var probe = new OpenDocument(0, kind, path, server, []);
            await RequestAsync(probe, "initialize", new JsonObject
            {
                ["protocolVersion"] = "2025-06-18",
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "karvon", ["version"] = "1" },
            }, ct);
            await server.HandleAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""", ct);
            var listed = await RequestAsync(probe, "tools/list", new JsonObject(), ct);
            var tools = (listed["tools"] as JsonArray ?? []).OfType<JsonObject>().Select(t => new EngineTool(
                t["name"]?.GetValue<string>() ?? "",
                t["title"]?.GetValue<string>() ?? t["annotations"]?["title"]?.GetValue<string>() ?? "",
                t["description"]?.GetValue<string>() ?? "",
                t["inputSchema"] as JsonObject ?? new JsonObject(),
                t["annotations"]?["readOnlyHint"]?.GetValue<bool>() == true)).Where(t => t.Name.Length > 0).ToList();
            var document = new OpenDocument(Interlocked.Increment(ref _nextId), kind, path, server, tools);
            var opened = path is not null
                ? await CallAsync(document, kind.OpenTool, new JsonObject { ["path"] = path }, ct, own: true)
                : await CallAsync(document, kind.NewTool!, newArguments ?? new JsonObject(), ct, own: true);
            if (opened.IsError)
                throw new InvalidOperationException(opened.Text);
            if (kind == DocumentKind.Pdf && TryParse(opened.Text)?["doc"] is JsonValue doc && doc.TryGetValue<int>(out var engineDocument))
                document.EngineDocument = engineDocument;
            OpenDocument? closed = null;
            lock (_gate)
            {
                _open[document.Id] = document;
                _used.Add(document.Id);
                if (_open.Count > Limit)
                {
                    var oldest = _used[0];
                    _used.RemoveAt(0);
                    _open.Remove(oldest, out closed);
                }
            }
            closed?.Server.Dispose();
            return (document, opened);
        }
        catch
        {
            server.Dispose();
            throw;
        }
    }

    /// <summary>One of the engine's tools on the document (its id given for PdfCraft's tools that take one).</summary>
    public Task<EngineResult> CallAsync(OpenDocument document, string tool, JsonObject arguments, CancellationToken ct) =>
        CallAsync(document, tool, arguments, ct, own: false);

    async Task<EngineResult> CallAsync(OpenDocument document, string tool, JsonObject arguments, CancellationToken ct, bool own)
    {
        if (!own && document.Kind.Hidden.Contains(tool))
            throw new InvalidOperationException($"{tool} is not called directly: Karvon opens and saves documents (document_open, document_save).");
        if (document.EngineDocument is { } doc && !arguments.ContainsKey("doc")
            && (document.Tools.FirstOrDefault(t => t.Name == tool)?.TakesDocument ?? own))
            arguments["doc"] = doc;
        var result = await RequestAsync(document, "tools/call", new JsonObject { ["name"] = tool, ["arguments"] = arguments }, ct);
        var texts = new List<string>();
        var images = new List<string>();
        foreach (var block in (result["content"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (block["type"]?.GetValue<string>() == "image" && block["data"]?.GetValue<string>() is { Length: > 0 } data)
                images.Add(data);
            else if (block["text"]?.GetValue<string>() is { } text)
                texts.Add(TryParse(text) is { } json ? json.ToJsonString(Compact) : text);
        }
        return new EngineResult(string.Join("\n", texts), images, result["isError"]?.GetValue<bool>() == true);
    }

    /// <summary>
    /// The document saved to <paramref name="path"/>, its format by the extension (exported, when its save tool does not
    /// write that one); it is the document's file then when that is its own format. A PDF saved to its own file is an
    /// incremental update, which keeps its signatures valid.
    /// </summary>
    public async Task<EngineResult> SaveAsync(OpenDocument document, string path, CancellationToken ct)
    {
        var kind = document.Kind;
        var own = Path.GetExtension(path).Equals(kind.Extensions[0], StringComparison.OrdinalIgnoreCase);
        var saved = !own && kind.ExportTool is { } export
            ? await CallAsync(document, export, new JsonObject { ["path"] = path }, ct, own: true)
            : await CallAsync(document, kind.SaveTool, kind == DocumentKind.Pdf && string.Equals(path, document.Path, FilePaths.Comparison) ? new JsonObject() : new JsonObject { ["path"] = path }, ct, own: true);
        if (!saved.IsError && own)
            document.Path = path;
        return saved;
    }

    /// <summary>The document closed: its unsaved changes go.</summary>
    public bool Close(int id)
    {
        OpenDocument? document;
        lock (_gate)
        {
            if (!_open.Remove(id, out document))
                return false;
            _used.Remove(id);
        }
        document.Server.Dispose();
        return true;
    }

    static async Task<JsonObject> RequestAsync(OpenDocument document, string method, JsonObject parameters, CancellationToken ct)
    {
        var id = document.NextRequest();
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters }.ToJsonString();
        var answer = await document.Server.HandleAsync(message, ct)
            ?? throw new InvalidOperationException($"The {document.Kind.Name} engine did not answer {method}.");
        var reply = JsonNode.Parse(answer) as JsonObject ?? throw new InvalidOperationException($"The {document.Kind.Name} engine's answer is not JSON-RPC.");
        if (reply["error"] is JsonObject error)
            throw new InvalidOperationException($"The {document.Kind.Name} engine: {error["message"]?.GetValue<string>() ?? error.ToJsonString()}");
        return reply["result"] as JsonObject ?? new JsonObject();
    }

    static JsonNode? TryParse(string text)
    {
        var trimmed = text.AsSpan().TrimStart();
        if (trimmed.Length == 0 || trimmed[0] is not ('{' or '['))
            return null;
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        OpenDocument[] all;
        lock (_gate)
        {
            all = [.. _open.Values];
            _open.Clear();
            _used.Clear();
        }
        foreach (var document in all)
            document.Server.Dispose();
    }
}
