using Karvon.Claude;
using Karvon.Modules;

namespace Karvon.Documents;

/// <summary>
/// Documents: PDF, Word, Excel and PowerPoint files as Claude Code's tools (<see cref="DocumentClaudeTools"/>), opened,
/// read, changed and saved by document engines in Karvon's own process: PdfCraft, WordCraft, GridCraft and DeckCraft
/// (open source, MIT or Apache-2.0), headless, in a library downloaded the first time a document is opened.
/// </summary>
public sealed class DocumentsModule : KarvonModule
{
    readonly SemaphoreSlim _loading = new(1, 1);
    DocumentLibrary? _library;
    DocumentSessions? _sessions;

    /// <summary>How documents are opened (the tests' fake engines); the library's otherwise.</summary>
    internal Func<CancellationToken, Task<IDocumentEngines>>? Engines { get; set; }

    internal DocumentSessions Sessions => _sessions ??= new(ct => Engines is { } engines ? engines(ct) : LoadAsync(ct));

    /// <summary>The previews' documents, apart from Claude's.</summary>
    DocumentSessions Previews => _previews ??= new(ct => Engines is { } engines ? engines(ct) : LoadAsync(ct));

    DocumentSessions? _previews;

    /// <summary>The files a preview shows: those the engines open that are not text (a .csv stays in the editor).</summary>
    static readonly HashSet<string> Shown = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".docx", ".doc", ".odt", ".rtf", ".xlsx", ".xlsm", ".xls", ".xlsb", ".ods", ".pptx",
    };

    public override IReadOnlyList<Avalonia.Controls.Templates.IDataTemplate> DocumentTemplates =>
        [Document<DocumentPreviewTab>(_ => new Views.DocumentPreviewView())];

    public override bool OpensFile(string path) => Shown.Contains(Path.GetExtension(path));

    public override ViewModels.DocumentTab OpenFile(string path) => new DocumentPreviewTab(path, Previews);

    /// <summary>The previews Claude's documents opened, by file, each with the documents that show it: it closes with the last of them.</summary>
    readonly Dictionary<string, HashSet<int>> _claudePreviews = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    DocumentPreviewTab? Preview(string file) => Workbench.Documents.OfType<DocumentPreviewTab>().FirstOrDefault(d => d.Key == file);

    static int? DocumentIn(System.Text.Json.JsonElement input) =>
        input.TryGetProperty("document", out var d) && d.TryGetInt32(out var id) ? id : null;

    /// <summary>
    /// A document Claude opened or saved: shown in Karvon (drawn again when it is open already), a text format in the editor.
    /// Claude closing it closes the preview that opened for it; one the user had open, or pinned, stays.
    /// </summary>
    public override Task ShowClaudeToolAsync(string tool, System.Text.Json.JsonElement input, ClaudeToolOutput output)
    {
        if (tool == "document_close")
        {
            if (DocumentIn(input) is { } closed)
                CloseClaudePreviews(closed);
            return Task.CompletedTask;
        }
        int? id = null;
        string? file = null;
        // "Document 3: /path/Report.docx (Word)."
        if (tool == "document_open" && output.Text.StartsWith("Document ", StringComparison.Ordinal) && int.TryParse(output.Text[9..output.Text.IndexOf(':')], out var opened))
            (id, file) = (opened, Sessions.Find(opened)?.Path);
        else if (tool == "document_save" && output.Text.StartsWith("Saved ", StringComparison.Ordinal))
            (id, file) = (DocumentIn(input), output.Text[6..^1]);
        if (file is null || !File.Exists(file))
            return Task.CompletedTask;
        var open = Preview(file);
        if (open is null)
            _claudePreviews[file] = [];
        if (id is { } document && _claudePreviews.TryGetValue(file, out var documents))
            documents.Add(document);
        if (open is not null)
        {
            Workbench.ShowDocument(open);
            return open.LoadAsync();
        }
        Workbench.OpenFile(file);
        return Task.CompletedTask;
    }

    void CloseClaudePreviews(int document)
    {
        foreach (var (file, documents) in _claudePreviews.ToList())
        {
            if (!documents.Remove(document) || documents.Count > 0)
                continue;
            _claudePreviews.Remove(file);
            if (Preview(file) is { IsPinned: false } tab)
                Workbench.CloseDocument(tab);
        }
    }

    public override string Id => "documents";

    public override string Name => "Documents";

    public override string Icon => "folder-docs";

    public override string Description =>
        "PDF, Word, Excel and PowerPoint files as Claude Code's tools: opened, read, changed and saved by document engines in "
        + "Karvon's own process (PdfCraft, WordCraft, GridCraft and DeckCraft, open source), with fewer tokens than scripts; "
        + "saving asks first. The engines download the first time a document is opened.";

    public override IReadOnlyList<ModuleComponent> Components =>
    [
        new("karvon-documents", "Document engines (PdfCraft, WordCraft, GridCraft, DeckCraft)", DocumentEnginesLocator.Version,
            DocumentEnginesLocator.InstallRoot, () => DocumentEnginesLocator.Find() is not null,
            async (log, ct) => await DocumentEnginesLocator.EnsureInstalledAsync(log, ct))
        {
            ApproximateSize = 84L << 20,
            Description = "The ArtCraft team's PdfCraft, WordCraft, GridCraft and DeckCraft engines (MIT or Apache-2.0), headless, "
                + "in one library built from Karvon's sources, from Karvon's GitHub releases.",
        },
    ];

    public override IEnumerable<IClaudeTool> ClaudeTools() => DocumentClaudeTools.Create(Sessions, () => Workbench.Workspace);

    /// <summary>A script that reads or writes such a file with Python's libraries in the command itself, their install, an office suite's conversion.</summary>
    static readonly System.Text.RegularExpressions.Regex Scripted = new(
        @"\b(?:import|from)\s+(?:docx|openpyxl|pptx|pypdf|PyPDF2|pdfplumber|fitz|xlsxwriter)\b|\bpip3?\s+install\b[^\n]*\b(?:python-docx|openpyxl|python-pptx|pypdf|PyPDF2|pdfplumber|xlsxwriter)\b"
        + @"|\b(?:soffice|libreoffice)\b[^\n]*--convert-to\b", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    public override IEnumerable<ClaudeShellRule> ClaudeShellRules() =>
    [
        new("document scripts", command =>
        {
            try
            {
                return Scripted.IsMatch(command);
            }
            catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
            {
                return false;
            }
        }, "Karvon reads and changes PDF, Word, Excel and PowerPoint files itself: mcp__karvon__document_open opens one (or a new "
            + "one), document_call runs its engine's tools on it (its text, its cells, a page drawn, a change), document_save writes it, "
            + "to another format too (a .docx as .pdf). Use them, not a script."),
    ];

    async Task<IDocumentEngines> LoadAsync(CancellationToken ct)
    {
        await _loading.WaitAsync(ct);
        try
        {
            if (_library is null)
            {
                var output = Workbench.Output("Documents");
                var path = DocumentEnginesLocator.Find() ?? await DocumentEnginesLocator.EnsureInstalledAsync(line => output.Append(line), ct);
                _library = new DocumentLibrary(path);
            }
            return _library;
        }
        finally
        {
            _loading.Release();
        }
    }

    public override void Dispose()
    {
        _sessions?.Dispose();
        _previews?.Dispose();
        _library?.Dispose();
        base.Dispose();
    }
}
