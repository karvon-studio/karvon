using Karvon.Claude;
using Karvon.Claude.Tools;
using Karvon.Modules;
using Karvon.Services;

namespace Karvon.ViewModels;

/// <summary>
/// The modules (see <see cref="KarvonModule"/>): every one Karvon has, those that are on started with Karvon as their
/// workbench, and what Karvon offers them (Claude Code through <see cref="IClaudeService"/>).
/// </summary>
public partial class MainViewModel : IWorkbench, IClaudeService
{
    /// <summary>Every module, on or off, in the order they show (Tools > Modules lists them).</summary>
    public IReadOnlyList<KarvonModule> AllModules { get; private set; } = [];

    /// <summary>The modules that are on, started.</summary>
    public IReadOnlyList<KarvonModule> Modules { get; private set; } = [];

    /// <summary>A module that is on.</summary>
    public T? Module<T>() where T : KarvonModule => Modules.OfType<T>().FirstOrDefault();

    void StartModules()
    {
        AllModules = ModuleCatalog.Create();
        Modules = [.. AllModules.Where(m => !Settings.DisabledModules.Contains(m.Id))];
        foreach (var module in Modules)
            module.Start(this);
    }

    /// <summary>
    /// Karvon's tools for a Claude Code session: the IDE's, the databases' and those of the modules that are on (each module
    /// shows what its tools did).
    /// </summary>
    public ClaudeToolServer KarvonToolServer()
    {
        var tools = Modules.SelectMany(m => m.ClaudeTools().Select(t => (Module: m, Tool: t))).ToList();
        foreach (var (module, tool) in tools)
            _claudeToolModules[tool.Name] = module;
        return KarvonTools.Create(this, tools.Select(t => t.Tool));
    }

    /// <summary>Karvon closes: what the modules started stops.</summary>
    public void ShutdownModules()
    {
        foreach (var module in Modules)
            module.Dispose();
    }

    // ---- IWorkbench ---------------------------------------------------------------------------------------------

    public event Action? WorkspaceChanged;

    void IWorkbench.SaveSettings() => Settings.Save();

    string? IWorkbench.Workspace => Solution?.Directory;

    IEnumerable<DocumentTab> IWorkbench.Documents => Documents;

    /// <summary>True from closing the previous solution until the next one is open (what is the solution's is nobody's then).</summary>
    public bool IsOpeningSolution { get; private set; }

    bool IWorkbench.IsOpeningWorkspace => IsOpeningSolution;

    string? IWorkbench.WorkspaceStateDirectory => Solution is { } solution ? Path.GetDirectoryName(SolutionState.FileFor(solution.FilePath)) : null;

    public event Action<DocumentTab?>? ActiveDocumentChanged;

    void IWorkbench.AddDocument(DocumentTab document)
    {
        if (!Documents.Contains(document))
            Documents.Add(document);
    }

    void IWorkbench.RemoveDocument(DocumentTab document) => Documents.Remove(document);

    void IWorkbench.ShowDocument(DocumentTab document)
    {
        if (!Documents.Contains(document))
            Documents.Add(document);
        ActiveDocument = document;
    }

    void IWorkbench.CloseDocument(DocumentTab document)
    {
        if (Documents.Contains(document))
            _ = CloseDocument(document);
    }

    void IWorkbench.OpenFile(string path, int line)
    {
        if (line > 0)
            NavigateTo(path, new Lsp.LspRange(new Lsp.LspPosition(line - 1, 0), new Lsp.LspPosition(line - 1, 0)));
        else
            OpenDocument(path);
    }

    void IWorkbench.ShowToolWindow(string id) => ShowToolWindow(id);

    void IWorkbench.ShowToolWindowBesideClaude(string id) => ShowToolWindowBeside(id);

    public event Action<string>? FileSaved;

    Task<IReadOnlyList<string>> IWorkbench.PickFilesAsync(string title) => Dialogs?.PickFilesAsync(title) ?? Task.FromResult<IReadOnlyList<string>>([]);

    OutputChannel IWorkbench.Output(string name)
    {
        if (OutputChannels.FirstOrDefault(c => c.Name == name) is { } channel)
            return channel;
        channel = new OutputChannel(name);
        OutputChannels.Add(channel);
        return channel;
    }

    void IWorkbench.ShowOutput(OutputChannel channel)
    {
        SelectedOutput = channel;
        ShowToolWindow(ToolWindows.Output);
    }

    void IWorkbench.OpenUrl(string url) => OpenBrowser(url);

    void IWorkbench.Status(string text) => SetStatus(text);

    T? IWorkbench.Service<T>() where T : class => typeof(T) == typeof(IClaudeService) ? this as T : Modules.OfType<T>().FirstOrDefault();

    // ---- IClaudeService -----------------------------------------------------------------------------------------

    event Action? ClaudeChanged;

    event Action? IClaudeService.Changed
    {
        add => ClaudeChanged += value;
        remove => ClaudeChanged -= value;
    }

    /// <summary>The chosen account's folder (CLAUDE_CONFIG_DIR or ~/.claude otherwise); always the home's in a home of the tests'.</summary>
    string IClaudeService.ConfigDirectory => Environment.GetEnvironmentVariable("KARVON_USER_HOME") is not null ? Path.Combine(UserHome.Path, ".claude")
        : _claude?.SelectedAccount?.ConfigDirectory ?? ClaudeCli.ConfigDirectory;

    /// <summary>The chosen account's settings (~/.claude.json for the default one); always the home's in a home of the tests'.</summary>
    string? IClaudeService.SettingsFile =>
        Environment.GetEnvironmentVariable("KARVON_USER_HOME") is null && _claude?.SelectedAccount is { IsDefault: false } account
            ? Path.Combine(account.ConfigDirectory, ".claude.json") : null;

    IReadOnlyList<IClaudeTool> IClaudeService.BuiltInTools => Settings.ClaudeKarvonTools ? KarvonToolServer().Tools : [];

    void IClaudeService.Ask(string prompt)
    {
        ShowToolWindow(ToolWindows.Claude);
        Claude.Prompt = prompt;
        Claude.RequestFocus();
    }

    Task<string> IClaudeService.AuthenticateMcpAsync(string name) => Claude.AuthenticateMcpAsync(name);

    async Task<string?> IClaudeService.AddMcpServerAsync(string name, string transport, string target, string scope)
    {
        var problem = await Claude.AddMcpServerAsync(name, transport, target, scope);
        ClaudeChanged?.Invoke();
        return problem;
    }

    public Func<string, string?>? ExtraMcpServers { get; set; }
}
