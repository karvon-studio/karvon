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
    /// <summary>Every module, on or off, in the order they show (Tools > Get Tools and Features lists them).</summary>
    public IReadOnlyList<KarvonModule> AllModules { get; private set; } = [];

    /// <summary>The modules that are on, started.</summary>
    public IReadOnlyList<KarvonModule> Modules { get; private set; } = [];

    /// <summary>The modules' items in the status bar.</summary>
    public IReadOnlyList<ModuleStatusItem> StatusItems { get; private set; } = [];

    /// <summary>A module that is on.</summary>
    public T? Module<T>() where T : KarvonModule => Modules.OfType<T>().FirstOrDefault();

    readonly IReadOnlyList<KarvonModule>? _givenModules;

    void StartModules()
    {
        AllModules = _givenModules ?? ModuleCatalog.Create();
        Modules = [.. AllModules.Where(m => !Settings.DisabledModules.Contains(m.Id))];
        foreach (var module in Modules)
        {
            module.Start(this);
            foreach (var grammar in module.Grammars)
                Editor.TextMateSupport.Add(grammar);
        }
        StatusItems = [.. Modules.Select(m => m.StatusItem).OfType<ModuleStatusItem>()];
        RegisterModuleLanguageServers();
    }

    /// <summary>Every component Tools > Get Tools and Features shows, by the module it belongs to (on or off).</summary>
    public IReadOnlyList<(string Owner, ModuleComponent Component)> Components =>
        [.. AllModules.SelectMany(m => m.Components.Select(c => (m.Name, c)))];

    /// <summary>Whether a component goes when Karvon Studio starts again.</summary>
    public bool IsRemovalPending(ModuleComponent component) => Settings.RemovedComponents.Contains(component.InstallRoot);

    /// <summary>Tools > Get Tools and Features > Remove: the component goes when Karvon Studio starts again (it may be running now).</summary>
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

    /// <summary>Tools > Get Tools and Features > Install: the component now, rather than the first time it is needed; its log in Output (Modules).</summary>
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

    /// <summary>
    /// Tools > Get Tools and Features > OK: the modules that are off from now on (it takes effect when Karvon Studio starts again), and
    /// what a module turned on downloads, downloaded now, as Visual Studio installs a workload's components (their log in
    /// Output (Modules)). False when one of them could not be installed (it downloads the first time it is needed).
    /// </summary>
    public async Task<bool> ChangeModulesAsync(IReadOnlyList<string> disabled, CancellationToken ct = default)
    {
        var turnedOn = Settings.DisabledModules.Except(disabled).ToHashSet(StringComparer.Ordinal);
        Settings.DisabledModules = [.. disabled];
        Settings.Save();
        var installed = true;
        foreach (var module in AllModules.Where(m => turnedOn.Contains(m.Id)))
            foreach (var component in module.Components.Where(c => !c.IsInstalled()))
            {
                ((IWorkbench)this).Status($"Installing {component.Name} {component.Version} ({module.Name})...");
                installed &= await InstallComponentAsync(component, ct);
            }
        return installed;
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

    /// <summary>Claude's run_tests without a build (noBuild): the tests run as the last build that succeeded left them.</summary>
    bool _testsWithoutBuild;

    Task<bool> IWorkbench.BuildAsync() =>
        _testsWithoutBuild && _lastBuild is { Succeeded: true, Verb: not "Clean" } ? Task.FromResult(true) : RunBuildAsync("build", "Build");

    Task<bool> IWorkbench.BuildAsync(string verb, Workspace.ProjectInfo project) =>
        RunBuildAsync(verb, verb.Length > 0 ? char.ToUpperInvariant(verb[0]) + verb[1..] : "Build", project.FilePath);

    Task<bool?> IWorkbench.ConfirmAsync(string title, string message) => Dialogs?.ConfirmAsync(title, message) ?? Task.FromResult<bool?>(null);

    Task<IReadOnlyList<string>?> IWorkbench.ChooseProjectReferencesAsync(string project, IReadOnlyList<ProjectReferenceChoice> projects) =>
        Dialogs?.ChooseProjectReferencesAsync(project, projects) ?? Task.FromResult<IReadOnlyList<string>?>(null);

    Task IWorkbench.RestartLanguageServersAsync() => RestartLanguageServer();

    Task IWorkbench.OpenWorkspaceAsync(string path) => OpenSolutionAsync(path);

    async Task<ProgramConsole> IWorkbench.StartInConsoleAsync(WorkspaceTask task)
    {
        var (prepared, program, environment) = PrepareTask(task);
        var (console, exited) = await OpenProgramConsoleAsync(task.Name, program, prepared.Arguments, prepared.WorkingDirectory, environment);
        return new ProgramConsole(console.ProcessId!.Value, exited);
    }

    async Task<int> IWorkbench.RunAsync(WorkspaceTask task, Action<string> output, CancellationToken ct)
    {
        using var runner = await StartTaskAsync(task, output);
        return await runner.WaitAsync(ct);
    }

    internal Task ManagePackagesAsync(Packages.PackageSource source) => ((IWorkbench)this).ManagePackagesAsync(source);

    async Task IWorkbench.ManagePackagesAsync(Packages.PackageSource source)
    {
        if (Documents.FirstOrDefault(d => d.Key == Packages.PackagesViewModel.KeyOf(source)) is { } open)
        {
            ((IWorkbench)this).ShowDocument(open);
            return;
        }
        var output = ((IWorkbench)this).Output("Package Manager");
        // After a change, what reads the dependencies reads them again: Solution Explorer's Dependencies, the tasks.
        var manager = new Packages.PackagesViewModel(source, ((IWorkbench)this).RunAsync, output.Append, () =>
        {
            RefreshDependencies(source.Directory);
            RefreshWorkspaceTasks();
            return Task.CompletedTask;
        });
        ((IWorkbench)this).ShowDocument(manager);
        await manager.InitializeAsync();
    }

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

    /// <summary>Whether a tool window shows, as the window has it (set by the window).</summary>
    public Func<string, bool>? ToolWindowShowing { get; set; }

    bool IWorkbench.IsToolWindowShowing(string id) => ToolWindowShowing?.Invoke(id) == true;

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

    IReadOnlyList<GitRepositoryState> IWorkbench.Repositories => [.. GitRepositories.Select(r => new GitRepositoryState(r.Repository, r.Status?.Branch.Branch))];

    public event Action? GitChanged;

    public event Action<Git.GitRepository>? GitPushed;

    Task<bool> IWorkbench.PushAsync(Git.GitRepository repository) =>
        GitRepositories.FirstOrDefault(r => r.Repository.Root == repository.Root) is { Status: { } status } item
            ? GitOperationAsync("Push", async () => Pushed(item, await repository.PushAsync(status.Branch, GitOutput.Append)))
            : Task.FromResult(false);

    Task<bool> IWorkbench.RunGitAsync(string title, Func<Task<Git.GitResult>> operation, bool changesFiles) =>
        HistoryOperationAsync(title, operation, changesFiles);

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
