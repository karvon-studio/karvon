using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Karvon.Claude;
using Karvon.Documents;
using Karvon.Views;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>Documents: PDF, Word, Excel and PowerPoint files as Claude's tools, through the document engines in Karvon's process.</summary>
public class DocumentsTests
{
    /// <summary>An engine's MCP server, as the library's answers it: its tools listed, each call kept and answered.</summary>
    sealed class FakeServer(string kind, JsonArray tools) : IDocumentServer
    {
        public List<(string Tool, JsonObject Arguments)> Calls { get; } = [];

        public bool Disposed { get; private set; }

        public Task<string?> HandleAsync(string message, CancellationToken cancellationToken)
        {
            var request = JsonNode.Parse(message)!.AsObject();
            if (request["id"] is null)
                return Task.FromResult<string?>(null);
            JsonObject result;
            switch (request["method"]!.GetValue<string>())
            {
                case "tools/list":
                    result = new JsonObject { ["tools"] = tools.DeepClone() };
                    break;
                case "tools/call":
                    var name = request["params"]!["name"]!.GetValue<string>();
                    var arguments = request["params"]!["arguments"]!.AsObject();
                    Calls.Add((name, arguments.DeepClone().AsObject()));
                    var content = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = Answer(name, arguments) });
                    if (name is "render_page")
                        content.Add(new JsonObject { ["type"] = "image", ["data"] = "iVBORw0KGgo=", ["mimeType"] = "image/png" });
                    result = new JsonObject { ["content"] = content, ["isError"] = name == "fail" };
                    break;
                default:
                    result = new JsonObject { ["protocolVersion"] = "2025-06-18" };
                    break;
            }
            return Task.FromResult<string?>(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = request["id"]!.DeepClone(), ["result"] = result }.ToJsonString());
        }

        string Answer(string name, JsonObject arguments) => (kind, name) switch
        {
            ("pdf", "doc_open") => """{"doc": 3, "pages": 2}""",
            ("word", "get_text") => "{\n  \"text\": \"Hello\",\n  \"words\": 1\n}",
            _ => "Done.",
        };

        public void Dispose() => Disposed = true;
    }

    sealed class FakeEngines : IDocumentEngines
    {
        public List<FakeServer> Servers { get; } = [];

        static JsonObject Tool(string name, string title, bool readOnly, JsonObject? properties = null) => new()
        {
            ["name"] = name,
            ["title"] = title,
            ["description"] = $"{title}, as the engine does it.",
            ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = properties ?? new JsonObject() },
            ["annotations"] = new JsonObject { ["readOnlyHint"] = readOnly },
        };

        public Task<IDocumentServer> OpenAsync(string kind, CancellationToken cancellationToken)
        {
            var doc = () => new JsonObject { ["doc"] = new JsonObject { ["type"] = "integer" } };
            JsonArray tools = kind == "pdf"
                ?
                [
                    Tool("doc_open", "Open a PDF", false), Tool("doc_save", "Save a document", false, doc()),
                    Tool("text_extract", "Extract text", true, doc()), Tool("tool_call", "Run any PdfCraft tool", false),
                    Tool("doc_split", "Split a document", false, new JsonObject { ["doc"] = new JsonObject(), ["out_dir"] = new JsonObject() }),
                ]
                :
                [
                    Tool("open_document", "Open", false), Tool("save_document", "Save", false), Tool("new_document", "New document", false),
                    Tool("new_workbook", "New workbook", false), Tool("get_text", "Document text", true), Tool("execute", "Run a command", false),
                    Tool("render_page", "Render page", true), Tool("screenshot", "Screenshot app", true), Tool("fail", "Fails", false),
                ];
            var server = new FakeServer(kind, tools);
            Servers.Add(server);
            return Task.FromResult<IDocumentServer>(server);
        }
    }

    static JsonElement Input(object value) => JsonSerializer.SerializeToElement(value);

    static (Dictionary<string, IClaudeTool> Tools, FakeEngines Engines, DocumentSessions Sessions, string Folder) Setup()
    {
        var folder = Directory.CreateTempSubdirectory("karvon-documents-").FullName;
        var engines = new FakeEngines();
        var sessions = new DocumentSessions(_ => Task.FromResult<IDocumentEngines>(engines));
        return (DocumentClaudeTools.Create(sessions, () => folder).ToDictionary(t => t.Name), engines, sessions, folder);
    }

    [Fact]
    public async Task A_document_is_opened_its_engine_tools_called_in_memory_and_saved_where_the_user_says_yes_to()
    {
        var (tools, engines, sessions, folder) = Setup();
        File.WriteAllText(Path.Combine(folder, "Report.docx"), "");
        try
        {
            // Opened by its path (relative to the solution's folder): reading, at once; its engine's tools, those Karvon runs itself left out.
            var open = Input(new { path = "Report.docx" });
            Assert.Equal(ToolAccess.Read, tools["document_open"].Plan(open).Access);
            Assert.Equal(Path.Combine(folder, "Report.docx"), tools["document_open"].Target(open));
            var opened = await tools["document_open"].CallAsync(open, default);
            Assert.False(opened.IsError, opened.Text);
            Assert.StartsWith($"Document 1: {Path.Combine(folder, "Report.docx")} (Word).", opened.Text);
            Assert.Contains("- get_text: Document text (reads)", opened.Text);
            Assert.Contains("- execute: Run a command", opened.Text);
            Assert.DoesNotContain("open_document", opened.Text);
            Assert.DoesNotContain("screenshot", opened.Text);
            var server = engines.Servers.Single();
            Assert.Equal(("open_document", Path.Combine(folder, "Report.docx")), (server.Calls[0].Tool, server.Calls[0].Arguments["path"]!.GetValue<string>()));

            // A tool that reads goes at once, its JSON written compactly; a change of the document in memory too.
            var text = Input(new { document = 1, tool = "get_text" });
            Assert.Equal(ToolAccess.Read, tools["document_call"].Plan(text).Access);
            Assert.Equal("""{"text":"Hello","words":1}""", (await tools["document_call"].CallAsync(text, default)).Text);
            Assert.Equal(ToolAccess.Run, tools["document_call"].Plan(Input(new { document = 1, tool = "execute", arguments = new { command = "format.bold" } })).Access);

            // A command that names a file asks first (Karvon cannot tell whether it writes it), the path made full.
            var saveByCommand = Input(new { document = "1", tool = "execute", arguments = new { command = "file.save", @params = new { path = "out/Report.pdf" } } });
            var plan = tools["document_call"].Plan(saveByCommand);
            Assert.Equal(ToolAccess.Write, plan.Access);
            Assert.Equal("Allow Claude's execute on Report.docx to use Report.pdf?", plan.Question);
            await tools["document_call"].CallAsync(saveByCommand, default);
            Assert.Equal(Path.Combine(folder, "out", "Report.pdf"), server.Calls[^1].Arguments["params"]!["path"]!.GetValue<string>());

            // What Karvon opens and saves itself is refused before anyone is asked; an unknown tool says the document's tools.
            Assert.Contains("document_save", tools["document_call"].Refusal(Input(new { document = 1, tool = "save_document" })));
            var unknown = await tools["document_call"].CallAsync(Input(new { document = 1, tool = "nope" }), default);
            Assert.True(unknown.IsError);
            Assert.Contains("- get_text", unknown.Text);

            // A tool's description and arguments; a page drawn comes as a picture.
            Assert.StartsWith("execute: Run a command, as the engine does it.\nArguments: {\"type\":\"object\"", (await tools["document_tools"].CallAsync(Input(new { document = 1, tool = "execute" }), default)).Text);
            var page = await tools["document_call"].CallAsync(Input(new { document = 1, tool = "render_page", arguments = new { page = 1 } }), default);
            Assert.Equal([new ClaudeToolImage("image/png", "iVBORw0KGgo=")], page.Images);
            // Karvon's tool server gives Claude the picture after the text.
            var answer = await KarvonTools.Create(tools.Values).HandleAsync(Input(new
            {
                jsonrpc = "2.0", id = 1, method = "tools/call",
                @params = new { name = "document_call", arguments = new { document = 1, tool = "render_page", arguments = new { page = 1 } } },
            }), default);
            var content = answer["result"]!["content"]!.AsArray();
            Assert.Equal(("text", "image", "iVBORw0KGgo="), (content[0]!["type"]!.GetValue<string>(), content[1]!["type"]!.GetValue<string>(), content[1]!["data"]!.GetValue<string>()));
            Assert.True((await tools["document_call"].CallAsync(Input(new { document = 1, tool = "fail" }), default)).IsError);

            // Saved: to its own file, or another format by the extension, each asked; one it cannot write is refused.
            var save = tools["document_save"].Plan(Input(new { document = 1 }));
            Assert.Equal((ToolAccess.Write, "Allow Claude to save its changes to Report.docx?"), (save.Access, save.Question));
            File.WriteAllText(Path.Combine(folder, "Report.pdf"), "");
            var asPdf = tools["document_save"].Plan(Input(new { document = 1, path = "Report.pdf" }));
            Assert.Equal(("Allow Claude to write Report.pdf (Report.docx)?", $"{Path.Combine(folder, "Report.pdf")} exists: it is written over."), (asPdf.Question, asPdf.Warning));
            Assert.Equal($"Saved {Path.Combine(folder, "Report.pdf")}.", (await tools["document_save"].CallAsync(Input(new { document = 1, path = "Report.pdf" }), default)).Text);
            Assert.Equal(("save_document", Path.Combine(folder, "Report.pdf")), (server.Calls[^1].Tool, server.Calls[^1].Arguments["path"]!.GetValue<string>()));
            // A .pdf is not the document's file: saving again goes to the .docx.
            Assert.Equal(Path.Combine(folder, "Report.docx"), tools["document_save"].Target(Input(new { document = 1 })));
            Assert.Contains("saves .docx", (await tools["document_save"].CallAsync(Input(new { document = 1, path = "Report.xlsx" }), default)).Text);

            // Closed: its server goes, and the document is no more.
            Assert.Equal("Closed Report.docx.", (await tools["document_close"].CallAsync(Input(new { document = 1 }), default)).Text);
            Assert.True(server.Disposed);
            Assert.Equal("No document 1 is open (open documents: none). Open it with document_open.", (await tools["document_call"].CallAsync(text, default)).Text);

            // A new document of a kind, its sample given; a file that is not a document, or not there, is said.
            var created = await tools["document_open"].CallAsync(Input(new { kind = "excel", template = "budget" }), default);
            Assert.StartsWith("Document 2: a new Excel document (Excel).", created.Text);
            Assert.Equal(("new_workbook", "budget"), (engines.Servers[^1].Calls[0].Tool, engines.Servers[^1].Calls[0].Arguments["sample"]!.GetValue<string>()));
            Assert.Contains("give the path to save it to", (await tools["document_save"].CallAsync(Input(new { document = 2 }), default)).Text);
            Assert.Contains("is no document Karvon opens", (await tools["document_open"].CallAsync(Input(new { path = "notes.txt" }), default)).Text);
            Assert.Contains("does not exist", (await tools["document_open"].CallAsync(Input(new { path = "Missing.docx" }), default)).Text);
            Assert.StartsWith("Give the file's path", (await tools["document_open"].CallAsync(Input(new { kind = "pdf" }), default)).Text);
        }
        finally
        {
            sessions.Dispose();
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task A_pdf_gets_its_engine_id_its_files_written_ask_and_at_most_sixteen_documents_stay_open()
    {
        var (tools, engines, sessions, folder) = Setup();
        File.WriteAllText(Path.Combine(folder, "Scan.pdf"), "");
        try
        {
            Assert.False((await tools["document_open"].CallAsync(Input(new { path = "Scan.pdf" }), default)).IsError);
            var server = engines.Servers.Single();

            // PdfCraft's document id goes in by itself, and is not among the arguments Claude is told.
            await tools["document_call"].CallAsync(Input(new { document = 1, tool = "text_extract", arguments = new { pages = new[] { 1 } } }), default);
            Assert.Equal(3, server.Calls[^1].Arguments["doc"]!.GetValue<int>());
            Assert.DoesNotContain("\"doc\"", (await tools["document_tools"].CallAsync(Input(new { document = 1, tool = "text_extract" }), default)).Text);

            // The pages it splits out are written: asked. tool_call does not run what Karvon does itself.
            Assert.Equal(ToolAccess.Write, tools["document_call"].Plan(Input(new { document = 1, tool = "doc_split", arguments = new { out_dir = "parts" } })).Access);
            Assert.NotNull(tools["document_call"].Refusal(Input(new { document = 1, tool = "tool_call", arguments = new { name = "doc_save" } })));
            Assert.Null(tools["document_call"].Refusal(Input(new { document = 1, tool = "tool_call", arguments = new { name = "page_rotate" } })));

            // Saved to its own file: an incremental update (no path), which keeps its signatures valid.
            await tools["document_save"].CallAsync(Input(new { document = 1 }), default);
            Assert.Equal("doc_save", server.Calls[^1].Tool);
            Assert.False(server.Calls[^1].Arguments.ContainsKey("path"));

            // The seventeenth document closes the one used least lately.
            for (var i = 0; i < DocumentSessions.Limit; i++)
                await tools["document_open"].CallAsync(Input(new { kind = "word" }), default);
            Assert.True(server.Disposed);
            Assert.Equal(DocumentSessions.Limit, sessions.Open.Count);
        }
        finally
        {
            sessions.Dispose();
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Scripts_that_read_or_write_documents_are_sent_to_the_document_tools()
    {
        var rule = new DocumentsModule().ClaudeShellRules().Single();
        Assert.True(rule.Matches("python3 - <<'EOF'\nimport openpyxl\nwb = openpyxl.load_workbook('a.xlsx')\nEOF"));
        Assert.True(rule.Matches("python3 -c \"from docx import Document; print(Document('a.docx').paragraphs)\""));
        Assert.True(rule.Matches("pip install python-pptx"));
        Assert.True(rule.Matches("soffice --headless --convert-to pdf Report.docx"));
        Assert.False(rule.Matches("python3 -m pytest tests/test_report.py"));
        Assert.False(rule.Matches("pip install requests"));
    }

    [Fact]
    public void Linux_macOS_and_Windows_have_a_build_of_the_engines()
    {
        Assert.Equal(["linux-x64", "osx-arm64", "osx-x64", "win-x64"], DocumentEnginesLocator.AllBuilds.Keys.Order());
        Assert.All(DocumentEnginesLocator.AllBuilds.Values, sha256 => Assert.Matches("^[0-9a-f]{64}$", sha256));
    }

    [Fact]
    public async Task Every_platforms_build_of_the_engines_is_as_pinned()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("KARVON_DOWNLOAD_LIVE") == "1", "Set KARVON_DOWNLOAD_LIVE=1 to download every platform's document engines.");
        var ct = TestContext.Current.CancellationToken;
        using var http = new HttpClient();
        foreach (var (platform, sha256) in DocumentEnginesLocator.AllBuilds)
        {
            var bytes = await http.GetByteArrayAsync($"https://github.com/{Karvon.Updates.UpdateFeed.Repository}/releases/download/documents-{DocumentEnginesLocator.Version}/karvon-documents-{DocumentEnginesLocator.Version}-{platform}.zip", ct);
            Assert.Equal(sha256, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)));
            using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(bytes));
            var library = platform.StartsWith("win", StringComparison.Ordinal) ? "karvon_documents.dll" : platform.StartsWith("osx", StringComparison.Ordinal) ? "libkarvon_documents.dylib" : "libkarvon_documents.so";
            Assert.NotNull(zip.GetEntry(library));
            Assert.Contains(zip.Entries, entry => entry.FullName.StartsWith("licenses/pdfcraft-", StringComparison.Ordinal));
        }
    }

    /// <summary>The library built from native/documents (cargo build --release), or the one KARVON_DOCUMENTS_LIBRARY names.</summary>
    static string? BuiltLibrary()
    {
        if (Environment.GetEnvironmentVariable("KARVON_DOCUMENTS_LIBRARY") is { Length: > 0 } given)
            return File.Exists(given) ? given : null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Karvon.slnx")))
                return Path.Combine(dir.FullName, "native", "documents", "target", "release", DocumentLibrary.FileName) is var built && File.Exists(built) ? built : null;
        return null;
    }

    [Fact]
    public async Task The_engines_write_Word_Excel_and_PowerPoint_files_and_read_them_and_their_PDFs_again()
    {
        Assert.SkipUnless(BuiltLibrary() is not null, "Build native/documents (cargo build --release) or set KARVON_DOCUMENTS_LIBRARY to try the engines.");
        using var library = new DocumentLibrary(BuiltLibrary()!);
        Assert.Equal("0.1.0", library.Version);
        var folder = Directory.CreateTempSubdirectory("karvon-documents-live-").FullName;
        using var sessions = new DocumentSessions(_ => Task.FromResult<IDocumentEngines>(library));
        var tools = DocumentClaudeTools.Create(sessions, () => folder).ToDictionary(t => t.Name);
        async Task<string> Call(string tool, object input)
        {
            var output = await tools[tool].CallAsync(Input(input), default);
            Assert.False(output.IsError, output.Text);
            return output.Text;
        }
        try
        {
            // Word: a new document, typed into, saved as .docx and as .pdf; opened again, its text is there.
            Assert.StartsWith("Document 1:", await Call("document_open", new { kind = "word" }));
            await Call("document_call", new { document = 1, tool = "type_text", arguments = new { text = "Karvon reads this report." } });
            await Call("document_save", new { document = 1, path = "Report.docx" });
            await Call("document_save", new { document = 1, path = "Report.pdf" });
            await Call("document_open", new { path = "Report.docx" });
            Assert.Contains("Karvon reads this report.", await Call("document_call", new { document = 2, tool = "get_text" }));
            await Call("document_open", new { path = "Report.pdf" });
            Assert.Contains("Karvon reads this report.", await Call("document_call", new { document = 3, tool = "text_extract" }));
            var page = await tools["document_call"].CallAsync(Input(new { document = 3, tool = "page_render", arguments = new { page = 1, dpi = 20 } }), default);
            Assert.False(page.IsError, page.Text);
            Assert.StartsWith("iVBOR", Assert.Single(page.Images).Data);

            // Excel: values written, saved as .xlsx, read again.
            await Call("document_open", new { kind = "excel" });
            await Call("document_call", new { document = 4, tool = "write_range", arguments = new { range = "A1", values = new object[][] { ["Item", "Price"], ["Tea", 3] } } });
            await Call("document_call", new { document = 4, tool = "set_cell", arguments = new { cell = "B3", input = "=B2*2" } });
            await Call("document_save", new { document = 4, path = "Prices.xlsx" });
            await Call("document_open", new { path = "Prices.xlsx" });
            var cells = await Call("document_call", new { document = 5, tool = "read_range", arguments = new { range = "A1:B3" } });
            Assert.Contains("Tea", cells);
            Assert.Contains("6", cells);

            // PowerPoint: a slide's text box, saved as .pptx and exported as .pdf; opened again, the text is there.
            await Call("document_open", new { kind = "powerpoint" });
            await Call("document_call", new { document = 6, tool = "add_text_box", arguments = new { x = 60, y = 60, w = 400, text = "Quarterly review" } });
            await Call("document_save", new { document = 6, path = "Deck.pptx" });
            await Call("document_save", new { document = 6, path = "Deck.pdf" });
            await Call("document_open", new { path = "Deck.pptx" });
            Assert.Contains("Quarterly review", await Call("document_call", new { document = 7, tool = "inspect_slide", arguments = new { index = 0 } }));
            Assert.True(new FileInfo(Path.Combine(folder, "Deck.pdf")).Length > 0);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Word_Excel_PowerPoint_and_PDF_files_open_in_Karvon_their_pages_drawn_and_shown_again_when_Claude_saves_them()
    {
        Assert.SkipUnless(BuiltLibrary() is not null, "Build native/documents (cargo build --release) or set KARVON_DOCUMENTS_LIBRARY to try the engines.");
        using var library = new DocumentLibrary(BuiltLibrary()!);
        var folder = Directory.CreateTempSubdirectory("karvon-preview-").FullName;
        var vm = NewViewModel();
        var module = vm.Module<DocumentsModule>()!;
        module.Engines = _ => Task.FromResult<IDocumentEngines>(library);
        var main = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        main.Show();
        var tools = DocumentClaudeTools.Create(module.Sessions, () => folder).ToDictionary(t => t.Name);
        async Task<string> Call(string tool, object input)
        {
            var output = await tools[tool].CallAsync(Input(input), default);
            Assert.False(output.IsError, output.Text);
            return output.Text;
        }
        async Task<DocumentPreviewTab> Open(string file)
        {
            vm.OpenDocument(Path.Combine(folder, file));
            var tab = Assert.IsType<DocumentPreviewTab>(vm.ActiveDocument);
            await WaitUntil(() => tab.Pages.Count > 0 && tab.Pages[0].Image is not null, TimeSpan.FromSeconds(30));
            Assert.True(tab.Pages[0].Image is not null, tab.Status);
            return tab;
        }
        try
        {
            // The engines' own samples, saved as files of a project.
            await Call("document_open", new { kind = "word", template = "report" });
            await Call("document_save", new { document = 1, path = "Report.docx" });
            await Call("document_save", new { document = 1, path = "Report.pdf" });
            await Call("document_open", new { kind = "excel", template = "budget" });
            await Call("document_save", new { document = 2, path = "Budget.xlsx" });
            await Call("document_open", new { kind = "powerpoint", template = "Harbor" });
            await Call("document_call", new { document = 3, tool = "add_text_box", arguments = new { x = 80, y = 170, w = 800, text = "Quarterly review", size = 54 } });
            await Call("document_call", new { document = 3, tool = "add_text_box", arguments = new { x = 80, y = 280, w = 800, text = "Documents open in Karvon Studio, drawn by its document engines", size = 24, color = "#2F6F8F" } });
            await Call("document_save", new { document = 3, path = "Deck.pptx" });

            // Opened from Solution Explorer or File > Open: a document of their own, its pages drawn by the engines.
            var word = await Open("Report.docx");
            Assert.Equal(("Report.docx", "Word"), (word.Title, word.KindText));
            Assert.Matches(@"^\d+ pages?$", word.Status);
            Snapshot(main, "documents-word");
            var excel = await Open("Budget.xlsx");
            Snapshot(main, "documents-excel");
            var deck = await Open("Deck.pptx");
            Snapshot(main, "documents-powerpoint");
            var pdf = await Open("Report.pdf");
            Assert.Equal(word.Pages.Count, pdf.Pages.Count);

            // As wide as the view as it opens; zoomed, the pages' size follows; opened again, the same tab comes to the front.
            Assert.True(pdf.FitWidth);
            Assert.Equal(pdf.Pages[0].Width * pdf.Zoom, pdf.Pages[0].DisplayWidth, 3);
            pdf.ActualSizeCommand.Execute(null);
            pdf.ZoomInCommand.Execute(null);
            Assert.Equal(("125 %", false), (pdf.ZoomText, pdf.FitWidth));
            Assert.Equal(pdf.Pages[0].Width * 1.25, pdf.Pages[0].DisplayWidth, 3);
            Snapshot(main, "documents-pdf");
            vm.OpenDocument(Path.Combine(folder, "Report.docx"));
            Assert.Same(word, vm.ActiveDocument);
            Assert.Equal(4, vm.Documents.OfType<DocumentPreviewTab>().Count());

            // Claude saves the deck with another slide: the tab in Karvon shows it.
            await Call("document_open", new { path = "Deck.pptx" });
            await Call("document_call", new { document = 4, tool = "add_slide", arguments = new { layout = "title" } });
            var saved = await tools["document_save"].CallAsync(Input(new { document = 4 }), default);
            await module.ShowClaudeToolAsync("document_save", Input(new { document = 4 }), saved);
            Assert.Same(deck, vm.ActiveDocument);
            await WaitUntil(() => deck.Pages.Count == 2, TimeSpan.FromSeconds(30));
            Assert.Equal(2, deck.Pages.Count);

            // A .csv stays in the editor.
            File.WriteAllText(Path.Combine(folder, "rows.csv"), "a,b\n1,2\n");
            Assert.False(module.OpensFile(Path.Combine(folder, "rows.csv")));
        }
        finally
        {
            main.Close();
            Directory.Delete(folder, recursive: true);
        }
    }
}
