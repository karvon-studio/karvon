namespace Karvon.Documents;

/// <summary>
/// A kind of document and its engine's tools for what Karvon does itself (open, start, save, export to another format),
/// which Claude is not given, as it is not the desktop app's (a window to click or shoot).
/// </summary>
/// <param name="Id">The engine's kind: pdf, word, excel, slides.</param>
/// <param name="Name">As the user says it: PDF, Word, Excel, PowerPoint.</param>
/// <param name="Extensions">The files it opens, lowercase with their dot; its own format first.</param>
/// <param name="Saves">The formats it saves to, by extension.</param>
/// <param name="NewArgument">A new document's argument for its template, theme or sample.</param>
/// <param name="ExportTool">What saves to a format of <paramref name="Saves"/> its save tool does not (PowerPoint to PDF).</param>
public sealed record DocumentKind(string Id, string Name, IReadOnlyList<string> Extensions, IReadOnlyList<string> Saves,
    string OpenTool, string SaveTool, string? NewTool, string? NewArgument, string? ExportTool, IReadOnlySet<string> Hidden)
{
    public static readonly DocumentKind Pdf = new("pdf", "PDF", [".pdf"], [".pdf"], "doc_open", "doc_save", null, null, null,
        new HashSet<string>(["doc_open", "doc_save", "doc_close"]));

    public static readonly DocumentKind Word = new("word", "Word", [".docx", ".doc", ".odt", ".rtf"],
        [".docx", ".pdf", ".odt", ".rtf", ".html", ".md", ".tex", ".txt", ".png"], "open_document", "save_document", "new_document", "template", null,
        new HashSet<string>(["open_document", "save_document", "new_document", "screenshot", "click", "key", "ui_inspect", "parity"]));

    public static readonly DocumentKind Excel = new("excel", "Excel", [".xlsx", ".xlsm", ".xls", ".xlsb", ".ods", ".csv", ".tsv"],
        [".xlsx", ".csv", ".tsv", ".json", ".html"], "open_workbook", "save_workbook", "new_workbook", "sample", null,
        new HashSet<string>(["open_workbook", "save_workbook", "new_workbook", "screenshot"]));

    public static readonly DocumentKind Slides = new("slides", "PowerPoint", [".pptx"], [".pptx", ".pdf"],
        "open_presentation", "save_presentation", "new_presentation", "theme", "export",
        new HashSet<string>(["open_presentation", "save_presentation", "new_presentation", "export", "screenshot", "call_app"]));

    public static readonly IReadOnlyList<DocumentKind> All = [Pdf, Word, Excel, Slides];

    /// <summary>The kind that opens <paramref name="path"/>, by its extension; null for none.</summary>
    public static DocumentKind? Of(string path) =>
        All.FirstOrDefault(k => k.Extensions.Contains(Path.GetExtension(path).ToLowerInvariant()));

    /// <summary>A kind by the name Claude gives it (word, docx, excel, xlsx, powerpoint, pptx, slides, pdf); null for none.</summary>
    public static DocumentKind? Named(string? name) => name?.Trim().TrimStart('.').ToLowerInvariant() switch
    {
        "pdf" => Pdf,
        "word" or "docx" or "document" => Word,
        "excel" or "xlsx" or "workbook" or "spreadsheet" => Excel,
        "powerpoint" or "pptx" or "slides" or "presentation" => Slides,
        _ => null,
    };
}
