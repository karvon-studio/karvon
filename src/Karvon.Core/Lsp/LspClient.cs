using System.Diagnostics;
using System.Text.Json;
using Karvon.Build;

namespace Karvon.Lsp;

/// <summary>
/// A language server over stdio (JSON-RPC): starting it, document sync, the editor's requests, file watching on its behalf,
/// its progress and refreshes. A server's own requests and start come in a subclass (<see cref="RoslynClient"/>).
/// </summary>
public class LspClient : IAsyncDisposable
{
    readonly FileWatchBridge _watch;
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyList<LspDiagnostic>> _published = new(StringComparer.Ordinal);
    int _exited;

    protected LspClient(Process process, JsonRpcConnection rpc, string root)
    {
        Process = process;
        Rpc = rpc;
        _watch = new FileWatchBridge(root, changes => Rpc.NotifyAsync("workspace/didChangeWatchedFiles", new
        {
            changes = changes.Select(c => new { uri = c.Uri, type = c.Type }).ToArray(),
        }));
    }

    protected Process Process { get; }

    protected JsonRpcConnection Rpc { get; }

    public event Action<string>? Log;
    public event Action? DiagnosticsRefreshRequested;

    /// <summary>A server that pushes its diagnostics (textDocument/publishDiagnostics) sent a file's: its path.</summary>
    public event Action<string>? DiagnosticsPublished;

    /// <summary>What the server said it can do (its initialize result's capabilities).</summary>
    public JsonElement Capabilities { get; private set; }

    /// <summary>The server's classifications changed (a project loaded, a referenced file was edited...).</summary>
    public event Action? SemanticTokensRefreshRequested;

    /// <summary>The server's semantic token types, by index (its legend); empty when it has none.</summary>
    public IReadOnlyList<string> SemanticTokenTypes { get; private set; } = [];

    /// <summary>Progress text from the server (e.g. project loading); null when the operation ends.</summary>
    public event Action<string?>? Progress;

    public event Action<Exception?>? Exited;

    /// <summary>The server answers for the workspace (a server that loads projects first says when).</summary>
    public bool IsReady { get; protected set; }

    public int WatcherCount => _watch.WatcherCount;

    /// <summary>The LSP language id of a file the server serves; "plaintext" for one it does not.</summary>
    public virtual string LanguageIdFor(string path) => "plaintext";

    public bool Handles(string path) => LanguageIdFor(path) != "plaintext";

    protected void OnLog(string text) => Log?.Invoke(text);

    protected static string ToUri(string path) => new Uri(path).AbsoluteUri;

    /// <summary>Starts the server's process and the connection to it (stdio).</summary>
    protected static (Process Process, JsonRpcConnection Rpc) Launch(ProcessStartInfo psi)
    {
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        var process = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {Path.GetFileName(psi.FileName)}.");
        return (process, new JsonRpcConnection(process.StandardOutput.BaseStream, process.StandardInput.BaseStream));
    }

    /// <summary>The handlers in place, then the connection open, then initialize and initialized.</summary>
    protected async Task InitializeAsync(string root, object? initializationOptions, CancellationToken ct)
    {
        Wire();
        WireServer();
        Rpc.Start();
        _ = Task.Run(async () =>
        {
            while (await Process.StandardError.ReadLineAsync() is { } line)
                Log?.Invoke("[stderr] " + line);
        });
        var folders = new[] { new { uri = ToUri(root), name = Path.GetFileName(root) } };
        var init = await Rpc.RequestAsync("initialize", initializationOptions is null
            ? new { processId = Environment.ProcessId, clientInfo = new { name = "Karvon", version = "0.1" }, rootUri = ToUri(root), workspaceFolders = folders, capabilities = ClientCapabilities }
            : new { processId = Environment.ProcessId, clientInfo = new { name = "Karvon", version = "0.1" }, rootUri = ToUri(root), workspaceFolders = folders, capabilities = ClientCapabilities, initializationOptions }, ct);
        SemanticTokenTypes = SemanticTokenLegend(init);
        Capabilities = init.ValueKind == JsonValueKind.Object && init.TryGetProperty("capabilities", out var capabilities) ? capabilities.Clone() : default;
        await Rpc.NotifyAsync("initialized", new { });
        if (ReadyOnceInitialized)
            IsReady = true;
    }

    /// <summary>Whether the server answers as soon as it has started (one that loads projects first says when itself).</summary>
    protected virtual bool ReadyOnceInitialized => true;

    /// <summary>What the server asks of its settings (workspace/configuration), by section: null, its defaults.</summary>
    protected virtual object? ConfigurationFor(string? section) => null;

    /// <summary>The server's own requests and notifications (Razor's, Roslyn's project loading).</summary>
    protected virtual void WireServer()
    {
    }

    static readonly object ClientCapabilities = new
    {
        workspace = new
        {
            // Lets the server delegate file watching to us (see FileWatchBridge).
            didChangeWatchedFiles = new { dynamicRegistration = true, relativePatternSupport = true },
            configuration = true,
            workspaceFolders = true,
            applyEdit = true,
            workspaceEdit = new { documentChanges = true },
            diagnostics = new { refreshSupport = true },
            semanticTokens = new { refreshSupport = true },
        },
        textDocument = new
        {
            synchronization = new { didSave = true },
            completion = new
            {
                contextSupport = true,
                completionItem = new
                {
                    snippetSupport = true,
                    labelDetailsSupport = true,
                    resolveSupport = new { properties = new[] { "documentation", "detail", "additionalTextEdits", "textEdit" } },
                },
                completionList = new { itemDefaults = new[] { "editRange", "insertTextFormat", "data", "commitCharacters" } },
            },
            hover = new { contentFormat = new[] { "markdown", "plaintext" } },
            signatureHelp = new
            {
                signatureInformation = new
                {
                    documentationFormat = new[] { "plaintext", "markdown" },
                    parameterInformation = new { labelOffsetSupport = true },
                },
            },
            publishDiagnostics = new { },
            diagnostic = new { dynamicRegistration = true },
            definition = new { },
            references = new { },
            implementation = new { },
            formatting = new { },
            foldingRange = new { lineFoldingOnly = false, foldingRange = new { collapsedText = true } },
            rename = new { prepareSupport = false },
            // Roslyn's classifications (locals, fields, methods, structs...), colored as Visual Studio does.
            semanticTokens = new
            {
                requests = new { range = false, full = new { delta = false } },
                tokenTypes = new[]
                {
                    "namespace", "type", "class", "enum", "interface", "struct", "typeParameter", "parameter", "variable", "property",
                    "enumMember", "event", "function", "method", "macro", "keyword", "modifier", "comment", "string", "number",
                    "regexp", "operator",
                },
                tokenModifiers = new[] { "declaration", "definition", "readonly", "static", "deprecated", "abstract", "async", "modification", "documentation", "defaultLibrary" },
                formats = new[] { "relative" },
                multilineTokenSupport = true,
                overlappingTokenSupport = false,
            },
            codeAction = new
            {
                dataSupport = true,
                isPreferredSupport = true,
                resolveSupport = new { properties = new[] { "edit" } },
                codeActionLiteralSupport = new
                {
                    codeActionKind = new
                    {
                        valueSet = new[] { "", "quickfix", "refactor", "refactor.extract", "refactor.inline", "refactor.rewrite", "source", "source.organizeImports" },
                    },
                },
            },
        },
        window = new { workDoneProgress = true },
    };

    void Wire()
    {
        Rpc.OnRequest("workspace/configuration", p =>
            Task.FromResult<object?>(p.GetProperty("items").EnumerateArray()
                .Select(item => ConfigurationFor(item.TryGetProperty("section", out var section) ? section.GetString() : null)).ToArray()));
        Rpc.OnRequest("client/registerCapability", p =>
        {
            foreach (var reg in p.GetProperty("registrations").EnumerateArray())
                if (reg.GetProperty("method").GetString() == "workspace/didChangeWatchedFiles")
                    _watch.Register(reg.GetProperty("id").GetString()!, reg.GetProperty("registerOptions"));
            return Task.FromResult<object?>(null);
        });
        Rpc.OnRequest("client/unregisterCapability", p =>
        {
            // The spec really spells it "unregisterations".
            if (p.TryGetProperty("unregisterations", out var list))
                foreach (var u in list.EnumerateArray())
                    _watch.Unregister(u.GetProperty("id").GetString()!);
            return Task.FromResult<object?>(null);
        });
        Rpc.OnRequest("window/workDoneProgress/create", _ => Task.FromResult<object?>(null));
        Rpc.OnRequest("workspace/diagnostic/refresh", _ =>
        {
            DiagnosticsRefreshRequested?.Invoke();
            return Task.FromResult<object?>(null);
        });
        Rpc.OnRequest("workspace/semanticTokens/refresh", _ =>
        {
            SemanticTokensRefreshRequested?.Invoke();
            return Task.FromResult<object?>(null);
        });
        Rpc.OnNotification("textDocument/publishDiagnostics", p =>
        {
            if (!p.TryGetProperty("uri", out var uri) || uri.GetString() is not { } text || !Uri.TryCreate(text, UriKind.Absolute, out var parsed))
                return;
            _published[parsed.AbsoluteUri] = p.TryGetProperty("diagnostics", out var items) ? LspParse.Diagnostics(items) : [];
            DiagnosticsPublished?.Invoke(parsed.LocalPath);
        });
        Rpc.OnNotification("window/logMessage", p =>
        {
            var type = p.GetProperty("type").GetInt32();
            if (type <= 3)
                Log?.Invoke(p.GetProperty("message").GetString() ?? "");
        });
        Rpc.OnNotification("$/progress", p =>
        {
            if (!p.TryGetProperty("value", out var v) || !v.TryGetProperty("kind", out var kind))
                return;
            if (kind.GetString() == "end")
            {
                Progress?.Invoke(null);
                return;
            }
            var title = v.TryGetProperty("title", out var t) ? t.GetString() : null;
            var message = v.TryGetProperty("message", out var m) ? m.GetString() : null;
            Progress?.Invoke(string.Join(": ", new[] { title, message }.Where(s => !string.IsNullOrEmpty(s))));
        });
        Rpc.Unhandled += method => Log?.Invoke($"[client] no handler for '{method}'");
        Rpc.Disconnected += ex =>
        {
            if (Interlocked.Exchange(ref _exited, 1) == 0)
                Exited?.Invoke(ex);
        };
    }

    static IReadOnlyList<string> SemanticTokenLegend(JsonElement initializeResult) =>
        initializeResult.ValueKind == JsonValueKind.Object
        && initializeResult.TryGetProperty("capabilities", out var caps)
        && caps.TryGetProperty("semanticTokensProvider", out var provider) && provider.ValueKind == JsonValueKind.Object
        && provider.TryGetProperty("legend", out var legend)
        && legend.TryGetProperty("tokenTypes", out var types) && types.ValueKind == JsonValueKind.Array
            ? types.EnumerateArray().Select(t => t.GetString() ?? "").ToList()
            : [];

    // ---- Document sync ---------------------------------------------------------------------------------------

    public Task DidOpenAsync(string path, string text) => Rpc.NotifyAsync("textDocument/didOpen", new
    {
        textDocument = new { uri = ToUri(path), languageId = LanguageIdFor(path), version = 1, text },
    });

    public Task DidChangeAsync(string path, int version, IReadOnlyList<LspContentChange> changes) => Rpc.NotifyAsync("textDocument/didChange", new
    {
        textDocument = new { uri = ToUri(path), version },
        contentChanges = changes.Select(c => c.Range is { } r ? (object)new { range = r, text = c.Text } : new { text = c.Text }).ToArray(),
    });

    public Task DidSaveAsync(string path) => Rpc.NotifyAsync("textDocument/didSave", new { textDocument = new { uri = ToUri(path) } });

    public Task DidCloseAsync(string path) => Rpc.NotifyAsync("textDocument/didClose", new { textDocument = new { uri = ToUri(path) } });

    // ---- Language features -----------------------------------------------------------------------------------

    static object DocPos(string path, LspPosition pos) => new { textDocument = new { uri = ToUri(path) }, position = pos };

    public async Task<LspCompletionList> CompletionAsync(string path, LspPosition pos, string? triggerCharacter, CancellationToken ct)
    {
        var result = await Rpc.RequestAsync("textDocument/completion", new
        {
            textDocument = new { uri = ToUri(path) },
            position = pos,
            context = triggerCharacter is null ? (object)new { triggerKind = 1 } : new { triggerKind = 2, triggerCharacter },
        }, ct);
        return LspParse.Completion(result);
    }

    public async Task<(LspCompletionItem Item, string? Documentation)> ResolveCompletionAsync(LspCompletionItem item, CancellationToken ct)
    {
        var result = await Rpc.RequestAsync("completionItem/resolve", item.Raw, ct);
        if (result.ValueKind != JsonValueKind.Object)
            return (item, null);
        var resolved = LspParse.CompletionItem(result, default);
        // Resolve may drop the itemDefaults-derived edit; keep the original one in that case.
        if (resolved.TextEdit is null && item.TextEdit is not null)
            resolved = resolved with { TextEdit = item.TextEdit };
        var doc = result.TryGetProperty("documentation", out var d) ? LspParse.Markup(d) : null;
        return (resolved, doc);
    }

    public async Task<string?> HoverAsync(string path, LspPosition pos, CancellationToken ct)
    {
        var result = await Rpc.RequestAsync("textDocument/hover", DocPos(path, pos), ct);
        return result.ValueKind == JsonValueKind.Object && result.TryGetProperty("contents", out var c) ? LspParse.Markup(c) : null;
    }

    public async Task<LspSignatureHelp?> SignatureHelpAsync(string path, LspPosition pos, CancellationToken ct) =>
        LspParse.SignatureHelp(await Rpc.RequestAsync("textDocument/signatureHelp", DocPos(path, pos), ct));

    public async Task<IReadOnlyList<LspLocation>> DefinitionAsync(string path, LspPosition pos, CancellationToken ct) =>
        LspParse.Locations(await Rpc.RequestAsync("textDocument/definition", DocPos(path, pos), ct));

    public async Task<IReadOnlyList<LspLocation>> ImplementationAsync(string path, LspPosition pos, CancellationToken ct) =>
        LspParse.Locations(await Rpc.RequestAsync("textDocument/implementation", DocPos(path, pos), ct));

    public async Task<IReadOnlyList<LspLocation>> ReferencesAsync(string path, LspPosition pos, CancellationToken ct) =>
        LspParse.Locations(await Rpc.RequestAsync("textDocument/references", new
        {
            textDocument = new { uri = ToUri(path) },
            position = pos,
            context = new { includeDeclaration = true },
        }, ct));

    /// <summary>A file's diagnostics: those the server pushed for it, else pulled (textDocument/diagnostic).</summary>
    public async Task<IReadOnlyList<LspDiagnostic>> DiagnosticsAsync(string path, CancellationToken ct)
    {
        if (_published.TryGetValue(ToUri(path), out var pushed))
            return pushed;
        var result = await Rpc.RequestAsync("textDocument/diagnostic", new { textDocument = new { uri = ToUri(path) } }, ct);
        return result.ValueKind == JsonValueKind.Object && result.TryGetProperty("items", out var items)
            ? LspParse.Diagnostics(items)
            : [];
    }

    /// <summary>The file's semantic tokens in LSP's relative encoding (5 integers per token); empty when there are none.</summary>
    public async Task<int[]> SemanticTokensAsync(string path, CancellationToken ct)
    {
        if (SemanticTokenTypes.Count == 0)
            return [];
        var result = await Rpc.RequestAsync("textDocument/semanticTokens/full", new { textDocument = new { uri = ToUri(path) } }, ct);
        return result.ValueKind == JsonValueKind.Object && result.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
            ? data.EnumerateArray().Select(v => v.GetInt32()).ToArray()
            : [];
    }

    public async Task<IReadOnlyList<LspFoldingRange>> FoldingRangesAsync(string path, CancellationToken ct)
    {
        var result = await Rpc.RequestAsync("textDocument/foldingRange", new { textDocument = new { uri = ToUri(path) } }, ct);
        if (result.ValueKind != JsonValueKind.Array)
            return [];
        static int? Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
        static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return result.EnumerateArray()
            .Select(r => new LspFoldingRange(Int(r, "startLine") ?? 0, Int(r, "startCharacter"), Int(r, "endLine") ?? 0, Int(r, "endCharacter"),
                Str(r, "kind"), Str(r, "collapsedText")))
            .ToList();
    }

    public async Task<IReadOnlyList<LspTextEdit>> FormatAsync(string path, int tabSize, bool insertSpaces, CancellationToken ct) =>
        LspParse.TextEdits(await Rpc.RequestAsync("textDocument/formatting", new
        {
            textDocument = new { uri = ToUri(path) },
            options = new { tabSize, insertSpaces },
        }, ct));

    /// <summary>Returns per-file edits for a rename, or null when the symbol cannot be renamed.</summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<LspTextEdit>>?> RenameAsync(string path, LspPosition pos, string newName, CancellationToken ct)
    {
        var result = await Rpc.RequestAsync("textDocument/rename", new
        {
            textDocument = new { uri = ToUri(path) },
            position = pos,
            newName,
        }, ct);
        if (result.ValueKind != JsonValueKind.Object)
            return null;
        var edits = new Dictionary<string, IReadOnlyList<LspTextEdit>>();
        if (result.TryGetProperty("documentChanges", out var dc))
        {
            foreach (var change in dc.EnumerateArray())
                if (change.TryGetProperty("textDocument", out var td))
                    edits[new Uri(td.GetProperty("uri").GetString()!).LocalPath] = LspParse.TextEdits(change.GetProperty("edits"));
        }
        else if (result.TryGetProperty("changes", out var changes))
        {
            foreach (var file in changes.EnumerateObject())
                edits[new Uri(file.Name).LocalPath] = LspParse.TextEdits(file.Value);
        }
        return edits;
    }

    /// <summary>Quick Actions and Refactorings (Ctrl+.) for a range, given the diagnostics it overlaps.</summary>
    public async Task<IReadOnlyList<LspCodeAction>> CodeActionsAsync(string path, LspRange range, IReadOnlyList<LspDiagnostic> diagnostics, CancellationToken ct) =>
        LspParse.CodeActions(await Rpc.RequestAsync("textDocument/codeAction", new
        {
            textDocument = new { uri = ToUri(path) },
            range,
            context = new
            {
                // The server matches fixes to diagnostics by their original JSON, so send them back verbatim.
                diagnostics = diagnostics.Where(d => d.Raw.ValueKind == JsonValueKind.Object).Select(d => d.Raw).ToArray(),
                triggerKind = 1,
            },
        }, ct));

    /// <summary>Computes the edit for a code action (codeAction/resolve unless the server already sent it).</summary>
    public async Task<IReadOnlyList<LspFileOperation>> ResolveCodeActionAsync(LspCodeAction action, CancellationToken ct)
    {
        if (action.Raw.TryGetProperty("edit", out var inline))
            return LspParse.WorkspaceEdit(inline);
        var resolved = await Rpc.RequestAsync("codeAction/resolve", action.Raw, ct);
        return resolved.ValueKind == JsonValueKind.Object && resolved.TryGetProperty("edit", out var edit)
            ? LspParse.WorkspaceEdit(edit)
            : [];
    }

    public virtual async ValueTask DisposeAsync()
    {
        _watch.Dispose();
        // Its children, before it goes: they may outlive a server that exits by itself (Roslyn's build hosts).
        var tree = Services.ProcessTree.Descendants(Process.Id);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await Rpc.RequestAsync("shutdown", null, cts.Token);
            await Rpc.NotifyAsync("exit", null);
        }
        catch
        {
            // Server already gone or unresponsive; fall through to kill.
        }
        await Rpc.DisposeAsync();
        try
        {
            if (!Process.HasExited)
                Process.Kill(entireProcessTree: true);
            using var exited = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await Process.WaitForExitAsync(exited.Token);
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        {
        }
        Services.ProcessTree.Kill(tree);
        Process.Dispose();
    }
}
