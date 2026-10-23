using Karvon.Claude;
using Karvon.Claude.Tools;
using Karvon.Modules;
using Karvon.Services;

namespace Karvon.ViewModels;

/// <summary>
/// The modules (see <see cref="KarvonModule"/>): every one Karvon has, those that are on started with Karvon as their
/// workbench, and what Karvon offers them (Claude Code through <see cref="IClaudeService"/>).
/// </summary>
public partial class MainViewModel : IWorkbench
{
    /// <summary>Every module, on or off, in the order they show (Tools > Modules lists them).</summary>
    public IReadOnlyList<KarvonModule> AllModules { get; private set; } = [];

    /// <summary>The modules that are on, started.</summary>
    public IReadOnlyList<KarvonModule> Modules { get; private set; } = [];

    /// <summary>A module that is on.</summary>
    public T? Module<T>() where T : KarvonModule => Modules.OfType<T>().FirstOrDefault();

    readonly IReadOnlyList<KarvonModule>? _givenModules;

    void StartModules()
    {
        AllModules = _givenModules ?? ModuleCatalog.Create();
        Modules = [.. AllModules.Where(m => !Settings.DisabledModules.Contains(m.Id))];
        foreach (var module in Modules)
            module.Start(this);
        RegisterModuleLanguageServers();
    }

    /// <summary>Every component Tools > Modules shows, by the module it belongs to (on or off).</summary>
    public IReadOnlyList<(string Owner, ModuleComponent Component)> Components =>
        [.. AllModules.SelectMany(m => m.Components.Select(c => (m.Name, c)))];

    /// <summary>Whether a component goes when Karvon Studio starts again.</summary>
    public bool IsRemovalPending(ModuleComponent component) => Settings.RemovedComponents.Contains(component.InstallRoot);

    /// <summary>Tools > Modules > Remove: the component goes when Karvon Studio starts again (it may be running now).</summary>
    public void RemoveComponent(ModuleComponent component)
    {
        if (IsRemovalPending(component))
            return;
        Settings.RemovedComponents.Add(component.InstallRoot);
        Settings.Save();
    }

    /// <summary>A removal taken back: the component stays.</summary>
    public void KeepComponent(ModuleComponent component)
    {
        if (Settings.RemovedComponents.Remove(component.InstallRoot))
            Settings.Save();
    }

    /// <summary>Tools > Modules > Install: the component now, rather than the first time it is needed; its log in Output (Modules).</summary>
    public async Task<bool> InstallComponentAsync(ModuleComponent component, CancellationToken ct = default)
    {
        KeepComponent(component);
        var output = ((IWorkbench)this).Output("Modules");
        output.Append($"Installing {component.Name} {component.Version}...");
        try
        {
            await component.InstallAsync(output.Append, ct);
            output.Append($"{component.Name} {component.Version} is installed.");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            output.Append($"{component.Name} could not be installed: {ex.Message}");
            return false;
        }
    }

    /// <summary>New Project's templates: each module's (.NET's, a Go module...), in the modules' order.</summary>
    public IReadOnlyList<Templates.ProjectTemplateSource> ProjectTemplateSources =>
        [.. Modules.Select(m => new Templates.ProjectTemplateSource(m.Name, m.LoadProjectTemplatesAsync))];

    /// <summary>Karvon closes: what the modules started stops.</summary>
    public async Task ShutdownModulesAsync()
    {
        foreach (var module in Modules)
            await module.DisposeAsync();
    }

    /// <summary>The solution changed: each module is told, one after the other.</summary>
    async Task OnModulesWorkspaceChangedAsync()
    {
        foreach (var module in Modules)
            await module.WorkspaceChangedAsync();
    }

    /// <summary>Claude Code, when its module is on.</summary>
    IClaudeService? ClaudeService => Modules.OfType<IClaudeService>().FirstOrDefault();

    /// <summary>Whether Claude Code is there: its toolbar button and Ask Claude show then.</summary>
    public bool HasClaude => ClaudeService is not null;

    /// <summary>The Claude Code window, the prompt focused (the toolbar, Ask Claude in the editor).</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    public void ShowClaude() => ClaudeService?.Show();

    /// <summary>Solution Explorer > Ask Claude: the files, folders or projects go into the prompt as @mentions.</summary>
    public void AskClaudeAbout(IReadOnlyList<string> paths) => ClaudeService?.AskAbout(paths);

    // ---- IWorkbench ---------------------------------------------------------------------------------------------

    public event Action? WorkspaceChanged;

    void IWorkbench.SaveSettings() => Settings.Save();

    string? IWorkbench.Workspace => Solution?.Directory;

    Task IWorkbench.ReloadSolutionAsync() => ReloadSolutionAsync();

    Task<string?> IWorkbench.AddNewItemAsync(Workspace.ExplorerNode node, Workspace.ItemTemplate template) => AddNewItemAsync(node, template);

    /// <summary>Add New Item's templates: the modules' (C#'s), then those that are no language's.</summary>
    public IReadOnlyList<Workspace.ItemTemplate> NewItemTemplates => [.. Modules.SelectMany(m => m.ItemTemplates), .. Workspace.ItemTemplates.General];

    /// <summary>The project a Solution Explorer node is (a project's) or is under (its Dependencies, a package), in a solution folder or not.</summary>
    public Workspace.ProjectInfo? ProjectOf(Workspace.ExplorerNode node)
    {
        if (node.Project is { } project)
            return project;
        return ExplorerRoots.FirstOrDefault() is { } root ? OwnerOf(root, node) : null;

        static bool Contains(Workspace.ExplorerNode parent, Workspace.ExplorerNode target) =>
            parent.Children.Any(c => c == target || Contains(c, target));

        static Workspace.ProjectInfo? OwnerOf(Workspace.ExplorerNode at, Workspace.ExplorerNode target)
        {
            foreach (var child in at.Children)
            {
                if (child.Kind == Workspace.NodeKind.Project && Contains(child, target))
                    return child.Project;
                if (child.Kind == Workspace.NodeKind.SolutionFolder && OwnerOf(child, target) is { } found)
                    return found;
            }
            return null;
        }
    }

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

    T? IWorkbench.Service<T>() where T : class => Modules.OfType<T>().FirstOrDefault();

    IReadOnlyDictionary<string, string> IWorkbench.WorkspaceState => _state.Modules;

    EditorSelection? IWorkbench.ActiveEditorSelection()
    {
        if (ActiveEditor is not { } doc)
            return null;
        var editor = doc.Host.Editor;
        if (editor.SelectionLength == 0)
            return new EditorSelection(doc.FilePath, null, null, null);
        var document = doc.Host.Document;
        var start = document.GetLineByOffset(editor.SelectionStart).LineNumber;
        var end = document.GetLineByOffset(editor.SelectionStart + editor.SelectionLength).LineNumber;
        var text = editor.SelectedText;
        return new EditorSelection(doc.FilePath, start, end, text.Length > 20000 ? text[..20000] : text);
    }

    void IWorkbench.ShowDiff(string path, string before, string after, string beforeTitle, string afterTitle, string key) =>
        ShowDiff(new DiffViewModel(path, before, after, beforeTitle, afterTitle, key));

    IEnumerable<IClaudeTool> IWorkbench.ClaudeTools() => IdeClaudeTools.Create(this);
}
