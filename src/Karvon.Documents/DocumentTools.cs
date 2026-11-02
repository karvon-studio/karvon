using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Karvon.Claude;
using static Karvon.Claude.Tools.ToolInput;

namespace Karvon.Documents;

/// <summary>
/// Claude Code's tools for documents: a PDF, Word, Excel or PowerPoint file opened in Karvon's document engines (in its
/// own process), their tools called on it in memory, saved where the user says yes to. A few tools with the engines' own
/// behind them, their arguments asked for when needed: fewer tokens than scripts that read and write the files.
/// </summary>
public static class DocumentClaudeTools
{
    public static IEnumerable<IClaudeTool> Create(DocumentSessions sessions, Func<string?> workspace) =>
    [
        new DocumentOpenTool(sessions, workspace), new DocumentToolsTool(sessions, workspace), new DocumentCallTool(sessions, workspace),
        new DocumentSaveTool(sessions, workspace), new DocumentCloseTool(sessions, workspace),
    ];

    /// <summary>The engine's tools, each on its line: its name, title and whether it changes the document.</summary>
    internal static string ToolList(OpenDocument document) =>
        string.Join("\n", document.Tools.Where(t => !document.Kind.Hidden.Contains(t.Name))
            .Select(t => $"- {t.Name}: {(t.Title.Length > 0 ? t.Title : OneLine(t.Description, 60))}{(t.ReadOnly ? " (reads)" : "")}"));
}

abstract class DocumentTool(DocumentSessions sessions, Func<string?> workspace) : IClaudeTool
{
    protected DocumentSessions Sessions => sessions;

    public abstract string Name { get; }

    public abstract string Description { get; }

    public abstract JsonObject InputSchema { get; }

    public abstract ClaudeToolPlan Plan(JsonElement input);

    public virtual string? Target(JsonElement input) => null;

    public virtual string? Refusal(JsonElement input) => null;

    public async Task<ClaudeToolOutput> CallAsync(JsonElement input, CancellationToken cancellationToken)
    {
        try
        {
            return await RunAsync(input, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return ClaudeToolOutput.Error(ex.Message);
        }
    }

    protected abstract Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken);

    /// <summary>A path Claude gives: ~ the home folder, relative to the solution's folder.</summary>
    protected string FullPath(string path) =>
        Path.GetFullPath(path.StartsWith('~') ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..] : path,
            workspace() ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    protected static int? Id(JsonElement input)
    {
        if (!input.TryGetProperty("document", out var d))
            return null;
        if (d.ValueKind == JsonValueKind.Number && d.TryGetInt32(out var n))
            return n;
        return d.ValueKind == JsonValueKind.String && int.TryParse(d.GetString(), out var s) ? s : null;
    }

    protected OpenDocument? DocumentOf(JsonElement input) => Id(input) is { } id ? sessions.Find(id) : null;

    protected OpenDocument Document(JsonElement input) =>
        DocumentOf(input) ?? throw new InvalidOperationException(Id(input) is { } id
            ? $"No document {id} is open (open documents: {Opened()}). Open it with document_open."
            : "'document' is required: the id document_open gave.");

    string Opened() => sessions.Open is { Count: > 0 } open ? string.Join(", ", open.Select(d => $"{d.Id} {d.Title}")) : "none";

    protected static JsonObject DocumentProperty => new() { ["type"] = "integer", ["description"] = "The document's id from document_open." };

    /// <summary>What the engine said, as Claude gets it: its text, its pictures.</summary>
    protected static ClaudeToolOutput Output(EngineResult result, string? before = null) =>
        new((before is null ? "" : before + "\n") + (result.Text.Length > 0 ? result.Text : result.IsError ? "The engine failed." : "Done."), result.IsError)
        {
            Images = [.. result.Images.Select(data => new ClaudeToolImage("image/png", data))],
        };
}

/// <summary><c>document_open</c>: a file opened in its engine (or a new document), in memory.</summary>
sealed class DocumentOpenTool(DocumentSessions sessions, Func<string?> workspace) : DocumentTool(sessions, workspace)
{
    public override string Name => "document_open";

    public override string Description =>
        "Opens a PDF, Word (.docx, .doc, .odt, .rtf), Excel (.xlsx, .xls, .ods, .csv) or PowerPoint (.pptx) file in Karvon's document "
        + "engines, or starts a new Word, Excel or PowerPoint document. Use these document tools instead of scripts (python-docx, "
        + "openpyxl, python-pptx, pypdf) to read or change such files. Returns the document's id and its engine's tools, which "
        + "document_call runs on it in memory; nothing is written until document_save.";

    public override JsonObject InputSchema => Schema("""
        {"type":"object","properties":{
          "path":{"type":"string","description":"The file to open; relative to the solution's folder."},
          "kind":{"type":"string","enum":["word","excel","powerpoint"],"description":"For a new document (no path): its kind."},
          "template":{"type":"string","description":"For a new document: Word's template (blank, letter, resume, report), Excel's sample (budget, sales, grades) or PowerPoint's theme."}}}
        """);

    public override ClaudeToolPlan Plan(JsonElement input) =>
        new(ToolAccess.Read, "Open document", Str(input, "path") is { Length: > 0 } path ? FullPath(path) : $"a new {Str(input, "kind")} document", "");

    public override string? Target(JsonElement input) => Str(input, "path") is { Length: > 0 } path ? FullPath(path) : null;

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        string? path = null;
        DocumentKind kind;
        JsonObject? arguments = null;
        if (Str(input, "path") is { Length: > 0 } given)
        {
            path = FullPath(given);
            kind = DocumentKind.Of(path) ?? throw new InvalidOperationException(
                $"{Path.GetFileName(path)} is no document Karvon opens: it opens {string.Join(", ", DocumentKind.All.SelectMany(k => k.Extensions))}.");
            if (!File.Exists(path))
                throw new InvalidOperationException($"{path} does not exist. For a new document give its kind (word, excel, powerpoint) and no path.");
        }
        else
        {
            kind = DocumentKind.Named(Str(input, "kind")) is { NewTool: not null } named ? named
                : throw new InvalidOperationException("Give the file's path, or for a new document its kind: word, excel or powerpoint.");
            if (Str(input, "template") is { Length: > 0 } template && kind.NewArgument is { } argument)
                arguments = new JsonObject { [argument] = template };
        }
        var (document, opened) = await Sessions.OpenAsync(kind, path, arguments, cancellationToken);
        var text = new StringBuilder($"Document {document.Id}: {path ?? $"a new {kind.Name} document"} ({kind.Name}).\n");
        if (opened.Text.Length > 0 && opened.Text != "Done.")
            text.Append(opened.Text).Append('\n');
        text.Append($"Its tools (document_call; document_tools gives one's arguments):\n{DocumentClaudeTools.ToolList(document)}");
        return new ClaudeToolOutput(text.ToString());
    }
}

/// <summary><c>document_tools</c>: the engine's tools for a document, or one's description and arguments.</summary>
sealed class DocumentToolsTool(DocumentSessions sessions, Func<string?> workspace) : DocumentTool(sessions, workspace)
{
    public override string Name => "document_tools";

    public override string Description =>
        "The tools of an open document's engine, or with 'tool' that tool's description and arguments (its JSON Schema), to call with document_call.";

    public override JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["document"] = DocumentProperty,
            ["tool"] = new JsonObject { ["type"] = "string", ["description"] = "A tool's name, for its description and arguments." },
        },
        ["required"] = new JsonArray("document"),
    };

    public override ClaudeToolPlan Plan(JsonElement input) =>
        new(ToolAccess.Read, "Document tools", DocumentOf(input) is { } document ? $"{document.Title}{(Str(input, "tool") is { } tool ? ": " + tool : "")}" : null, "");

    protected override Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var document = Document(input);
        if (Str(input, "tool") is not { Length: > 0 } name)
            return Task.FromResult(new ClaudeToolOutput($"{document.Title} ({document.Kind.Name}):\n{DocumentClaudeTools.ToolList(document)}"));
        var tool = document.Tools.FirstOrDefault(t => t.Name == name && !document.Kind.Hidden.Contains(t.Name))
            ?? throw new InvalidOperationException($"The {document.Kind.Name} engine has no tool {name} here. Its tools:\n{DocumentClaudeTools.ToolList(document)}");
        var schema = tool.InputSchema.DeepClone().AsObject();
        // The document's own id goes in by itself.
        if (document.EngineDocument is not null && schema["properties"] is JsonObject properties)
        {
            properties.Remove("doc");
            if (schema["required"] is JsonArray required && required.FirstOrDefault(r => r?.GetValue<string>() == "doc") is { } doc)
                required.Remove(doc);
        }
        return Task.FromResult(new ClaudeToolOutput($"{tool.Name}: {tool.Description}{(tool.ReadOnly ? " (reads only)" : "")}\nArguments: {schema.ToJsonString()}"));
    }
}

/// <summary><c>document_call</c>: one of the engine's tools on an open document, in memory.</summary>
sealed class DocumentCallTool(DocumentSessions sessions, Func<string?> workspace) : DocumentTool(sessions, workspace)
{
    /// <summary>The arguments that name a file (one a tool writes, or reads into the document), at any depth.</summary>
    static readonly HashSet<string> FileArguments = ["path", "out", "out_dir", "output", "file", "folder", "dir"];

    public override string Name => "document_call";

    public override string Description =>
        "Runs one of an open document's engine tools on it (document_open lists them; document_tools gives one's arguments), e.g. "
        + "Word's get_text, inspect_document or execute {command, params}; Excel's read_range or write_range; PowerPoint's inspect_slide "
        + "or add_slide; PDF's text_extract or page_render. Changes stay in memory until document_save. A tool that draws a page "
        + "returns the picture.";

    public override JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["document"] = DocumentProperty,
            ["tool"] = new JsonObject { ["type"] = "string", ["description"] = "The engine's tool." },
            ["arguments"] = new JsonObject { ["type"] = "object", ["description"] = "Its arguments, as document_tools gives them." },
        },
        ["required"] = new JsonArray("document", "tool"),
    };

    /// <summary>
    /// The files a call of a tool that changes things names (a picture it writes or puts in, the pages it takes out, a
    /// command's file): Karvon cannot tell which it writes, so it asks. None for a tool that only reads.
    /// </summary>
    List<string> Files(JsonElement input, EngineTool? tool)
    {
        List<string> files = [];
        if (tool is { ReadOnly: true } || !input.TryGetProperty("arguments", out var arguments))
            return files;
        void Walk(JsonElement e, int depth)
        {
            if (depth > 8)
                return;
            if (e.ValueKind == JsonValueKind.Object)
                foreach (var p in e.EnumerateObject())
                {
                    if (FileArguments.Contains(p.Name) && p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { Length: > 0 } file)
                        files.Add(FullPath(file));
                    else
                        Walk(p.Value, depth + 1);
                }
            else if (e.ValueKind == JsonValueKind.Array)
                foreach (var item in e.EnumerateArray())
                    Walk(item, depth + 1);
        }
        Walk(arguments, 0);
        return files;
    }

    public override ClaudeToolPlan Plan(JsonElement input)
    {
        var document = DocumentOf(input);
        var name = Str(input, "tool") ?? "";
        var tool = document?.Tools.FirstOrDefault(t => t.Name == name);
        var subject = document is null ? name : $"{document.Title}: {name}";
        var code = input.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Object ? a.GetRawText() : null;
        if (Files(input, tool) is [var file, ..] files)
            return new(ToolAccess.Write, "Document", $"{subject} → {file}", $"Allow Claude's {name} on {document?.Title} to use {Path.GetFileName(file)}?")
            {
                Code = code,
                Warning = $"It may write {string.Join(", ", files)}" + (files.Any(File.Exists) ? ", which exists." : "."),
            };
        // A change of the document in memory: nothing on disk until document_save, which asks.
        return new(tool is { ReadOnly: true } ? ToolAccess.Read : ToolAccess.Run, "Document", subject, "") { Code = code };
    }

    public override string? Target(JsonElement input) => DocumentOf(input)?.Path;

    public override string? Refusal(JsonElement input)
    {
        if (DocumentOf(input) is not { } document || Str(input, "tool") is not { } tool)
            return null;
        // PdfCraft's tool_call runs another of its tools by name.
        if (tool == "tool_call" && input.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Object && Str(a, "name") is { } inner)
            tool = inner;
        return document.Kind.Hidden.Contains(tool)
            ? $"{tool} is not called directly: document_open opens a file, document_save saves it (as another format too, by its extension)."
            : null;
    }

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var document = Document(input);
        var tool = Str(input, "tool") is { Length: > 0 } name ? name : throw new InvalidOperationException("'tool' is required: one of the document's engine tools.");
        var arguments = input.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Object ? JsonNode.Parse(a.GetRawText())!.AsObject() : new JsonObject();
        Absolute(arguments, 0);
        if (document.Tools.All(t => t.Name != tool))
            throw new InvalidOperationException($"The {document.Kind.Name} engine has no tool {tool}. Its tools:\n{DocumentClaudeTools.ToolList(document)}");
        return Output(await Sessions.CallAsync(document, tool, arguments, cancellationToken));
    }

    /// <summary>The files the arguments name, made full (relative to the solution's folder, as Claude's are), as the plan asked about them.</summary>
    void Absolute(JsonNode? node, int depth)
    {
        if (depth > 8)
            return;
        if (node is JsonObject o)
            foreach (var (key, value) in o.ToList())
            {
                if (FileArguments.Contains(key) && value is JsonValue v && v.TryGetValue<string>(out var file) && file.Length > 0)
                    o[key] = FullPath(file);
                else
                    Absolute(value, depth + 1);
            }
        else if (node is JsonArray a)
            foreach (var item in a)
                Absolute(item, depth + 1);
    }
}

/// <summary><c>document_save</c>: an open document written to its file, or to another (its format by the extension).</summary>
sealed class DocumentSaveTool(DocumentSessions sessions, Func<string?> workspace) : DocumentTool(sessions, workspace)
{
    public override string Name => "document_save";

    public override string Description =>
        "Saves an open document: to the file it was opened from, or to 'path', whose extension gives the format (Word: .docx, .pdf, "
        + ".odt, .rtf, .html, .md, .txt; Excel: .xlsx, .csv; PowerPoint: .pptx, .pdf; PDF: .pdf). The user is asked first.";

    public override JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["document"] = DocumentProperty,
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "Where to save it; relative to the solution's folder. Default: the file it was opened from." },
        },
        ["required"] = new JsonArray("document"),
    };

    string? PathOf(JsonElement input) => Str(input, "path") is { Length: > 0 } path ? FullPath(path) : DocumentOf(input)?.Path;

    public override ClaudeToolPlan Plan(JsonElement input)
    {
        var path = PathOf(input);
        var document = DocumentOf(input);
        return new(ToolAccess.Write, "Save document", path, path is null ? "" : document?.Path == path
            ? $"Allow Claude to save its changes to {Path.GetFileName(path)}?"
            : $"Allow Claude to write {Path.GetFileName(path)} ({document?.Title})?")
        {
            Warning = path is not null && File.Exists(path) && document?.Path != path ? $"{path} exists: it is written over." : null,
        };
    }

    public override string? Target(JsonElement input) => PathOf(input);

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var document = Document(input);
        var path = PathOf(input) ?? throw new InvalidOperationException($"{document.Title} has no file yet: give the path to save it to.");
        if (!document.Kind.Saves.Contains(Path.GetExtension(path).ToLowerInvariant()))
            throw new InvalidOperationException($"The {document.Kind.Name} engine saves {string.Join(", ", document.Kind.Saves)}, not {Path.GetExtension(path)}.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var saved = await Sessions.SaveAsync(document, path, cancellationToken);
        return saved.IsError ? Output(saved) : new ClaudeToolOutput($"Saved {path}.");
    }
}

/// <summary><c>document_close</c>: an open document closed, its unsaved changes gone.</summary>
sealed class DocumentCloseTool(DocumentSessions sessions, Func<string?> workspace) : DocumentTool(sessions, workspace)
{
    public override string Name => "document_close";

    public override string Description => "Closes an open document; changes not saved with document_save go.";

    public override JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject { ["document"] = DocumentProperty },
        ["required"] = new JsonArray("document"),
    };

    public override ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Read, "Close document", DocumentOf(input)?.Title, "");

    protected override Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var document = Document(input);
        Sessions.Close(document.Id);
        return Task.FromResult(new ClaudeToolOutput($"Closed {document.Title}."));
    }
}
