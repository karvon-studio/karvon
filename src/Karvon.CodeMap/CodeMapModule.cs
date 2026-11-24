using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Avalonia.Threading;
using Karvon.Claude;
using Karvon.CodeMap.ViewModels;
using Karvon.CodeMap.Views;
using Karvon.Lsp;
using Karvon.Modules;

namespace Karvon.CodeMap;

/// <summary>
/// Code Map: the workspace's structure (its folders, files and declarations, with their doc comments) in an index Karvon
/// keeps as the workspace is, and summaries of what each file and folder is for, written by the model the user chooses
/// (none, Claude through their Claude Code sign-in, or a model of Ollama's). Claude reads it with code_map and code_search
/// instead of listing and reading files to learn a project; the user, in its tool window, searches it as they type.
/// </summary>
public sealed class CodeMapModule : KarvonModule
{
    public const string ToolWindowId = "CodeMap";

    const string Use =
        "Karvon keeps a map of the workspace, up to date: use mcp__karvon__code_map (its tree, with what each folder and file is for, "
        + "or a file's declarations) and mcp__karvon__code_search (by name or by what it does) instead of listing files.";

    CodeMapWorkspace? _map;
    CodeMapViewModel? _view;
    CodeMapSettings? _settings;

    public override string Id => "codemap";

    public override string Name => "Code Map";

    public override string Icon => "tree";

    public override string Description =>
        "The workspace's map: its folders, files and declarations in an index kept up to date as the code changes, with a summary of "
        + "what each file and folder is for (written by Claude, by a local model through Ollama, or by no one). Claude finds its way "
        + "with code_map and code_search instead of listing and reading files; the Code Map window searches it as you type.";

    public override IReadOnlyList<ToolWindowInfo> ToolWindows =>
        [new(ToolWindowId, "Code Map", "Code _Map", "Namespace", DockSide.Right, () => new CodeMapView { DataContext = EnsureView() })];

    /// <summary>Where the settings are kept (the tests' own).</summary>
    internal string SettingsFile { get; set; } = Path.Combine(Services.Settings.ConfigDirectory, "codemap.json");

    /// <summary>What reaches Ollama (the tests' fake).</summary>
    internal HttpClient Http { get; set; } = new() { Timeout = TimeSpan.FromMinutes(5) };

    /// <summary>The map of the workspace open, or null.</summary>
    public CodeMapWorkspace? Map => _map;

    bool _indexes = true;

    /// <summary>Whether workspaces are mapped (the tests' other windows map none, so no language server is asked by the map).</summary>
    internal bool Indexes
    {
        get => _indexes;
        set
        {
            if (_indexes == value)
                return;
            _indexes = value;
            if (!IsStarted)
                return;
            if (value)
                Open();
            else
                Close();
        }
    }

    public CodeMapSettings Settings => _settings ??= CodeMapSettings.Load(SettingsFile);

    public CodeMapViewModel EnsureView() => _view ??= new CodeMapViewModel(this);

    public override IEnumerable<IClaudeTool> ClaudeTools() => CodeMapTools.Create(() => _map);

    /// <summary>Listing the workspace's files (find with a test, tree, ls -R): the map instead.</summary>
    public override IEnumerable<ClaudeShellRule> ClaudeShellRules() =>
    [
        ClaudeShellRule.Command("listing files", @"tree\b|find\b[^;&|\n]*\s-(i?name|i?path|type)\b|ls\s+-[A-Za-z]*R", Use) with { InWorkspace = true },
    ];

    protected override void Started()
    {
        Workbench.FileSaved += OnFileSaved;
        Workbench.GitChanged += OnGitChanged;
        Workbench.ActiveDocumentChanged += OnActiveDocumentChanged;
        Open();
    }

    /// <summary>A file opened in the editor starts its language's server: the files waiting for it are read.</summary>
    void OnActiveDocumentChanged(Karvon.ViewModels.DocumentTab? document)
    {
        if (_map is not { } map)
            return;
        map.Indexer.RetryWaiting();
        _ = map.Indexer.ScanAsync();
    }

    void OnFileSaved(string path) => _ = _map?.Indexer.ScanAsync();

    void OnGitChanged() => _ = _map?.Indexer.ScanAsync();

    public override Task WorkspaceChangedAsync()
    {
        Open();
        return Task.CompletedTask;
    }

    public override Task<bool> CloseWorkspaceAsync()
    {
        Close();
        return Task.FromResult(true);
    }

    /// <summary>The workspace's map, opened from its file in the solution's own folder, and scanned.</summary>
    void Open()
    {
        Close();
        if (_indexes && Workbench.Workspace is { } root && Directory.Exists(root))
        {
            _map = new CodeMapWorkspace(root, DatabaseFile(root), OutlineAsync);
            _map.SetWriter(Writer(root));
            _map.Start();
        }
        _view?.SetMap(_map);
    }

    void Close()
    {
        var map = _map;
        _map = null;
        _view?.SetMap(null);
        map?.Dispose();
    }

    /// <summary>The map's file: in the solution's own folder (.vs/…/Karvon), else in Karvon's, by the folder's path.</summary>
    string DatabaseFile(string root) =>
        Workbench.WorkspaceStateDirectory is { } state
            ? Path.Combine(state, "codemap.db")
            : Path.Combine(Services.Settings.ConfigDirectory, "codemap", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root)), 0, 8) + ".db");

    /// <summary>
    /// A file's outline by its language server if that runs (the map starts none: it would download one the first time),
    /// asked on the window's thread, where the servers are, after what the user does there (the first scan asks thousands).
    /// </summary>
    Task<IReadOnlyList<LspDocumentSymbol>?> OutlineAsync(string file, CancellationToken cancellationToken) =>
        Dispatcher.UIThread.InvokeAsync(() => Workbench.OutlineAsync(file, start: false, cancellationToken), DispatcherPriority.Background);

    /// <summary>Who writes the summaries, as the settings say: null for no one (or Claude Code off in Karvon).</summary>
    Func<string, CancellationToken, Task<string>>? Writer(string root) => Settings.Summaries switch
    {
        SummarySource.Claude when Workbench.Service<IClaudeService>() is { } claude =>
            (prompt, _) => Dispatcher.UIThread.InvokeAsync(() => claude.PrintAsync(root, prompt)),
        SummarySource.Ollama => OllamaAsync,
        _ => null,
    };

    /// <summary>Whether Claude can write the summaries (Claude Code is on in Karvon).</summary>
    public bool HasClaude => Workbench.Service<IClaudeService>() is not null;

    sealed record OllamaRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("prompt")] string Prompt,
        [property: JsonPropertyName("stream")] bool Stream,
        [property: JsonPropertyName("options")] OllamaOptions Options);

    sealed record OllamaOptions([property: JsonPropertyName("temperature")] double Temperature);

    sealed record OllamaResponse([property: JsonPropertyName("response")] string? Response);

    async Task<string> OllamaAsync(string prompt, CancellationToken cancellationToken)
    {
        var settings = Settings;
        using var reply = await Http.PostAsJsonAsync(settings.OllamaUrl.TrimEnd('/') + "/api/generate",
            new OllamaRequest(settings.OllamaModel, prompt, false, new OllamaOptions(0.2)), cancellationToken);
        if (!reply.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Ollama at {settings.OllamaUrl} answered {(int)reply.StatusCode}: {(await reply.Content.ReadAsStringAsync(cancellationToken)).Trim()}");
        return (await reply.Content.ReadFromJsonAsync<OllamaResponse>(cancellationToken))?.Response ?? "";
    }

    /// <summary>Who writes the summaries from now on (kept in codemap.json): the map starts on them at once.</summary>
    public void SetSummaries(SummarySource source, string? ollamaUrl = null, string? ollamaModel = null)
    {
        var settings = Settings;
        settings.Summaries = source;
        if (ollamaUrl is { Length: > 0 })
            settings.OllamaUrl = ollamaUrl.Trim();
        if (ollamaModel is { Length: > 0 })
            settings.OllamaModel = ollamaModel.Trim();
        try
        {
            settings.Save(SettingsFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Workbench.Status($"The Code Map's settings were not saved: {ex.Message}");
        }
        if (_map is { } map)
            map.SetWriter(Writer(map.Root));
    }

    /// <summary>A file of the map opened in the editor, at a line.</summary>
    public void OpenFile(string path, int line)
    {
        if (_map is { } map)
            Workbench.OpenFile(Path.Combine(map.Root, path), line);
    }

    public bool IsShowing => Workbench.IsToolWindowShowing(ToolWindowId);

    public override void Dispose()
    {
        if (IsStarted)
        {
            Workbench.FileSaved -= OnFileSaved;
            Workbench.GitChanged -= OnGitChanged;
            Workbench.ActiveDocumentChanged -= OnActiveDocumentChanged;
        }
        Close();
        _view?.Dispose();
        Http.Dispose();
    }
}
