using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Karvon.Build;
using Karvon.Editor;
using Karvon.Lsp;
using Karvon.Modules;
using Karvon.Services;
using Karvon.Workspace;

namespace Karvon.ViewModels;

/// <summary>Ids of the dockable tool windows.</summary>
public static class ToolWindows
{
    public const string SolutionExplorer = "SolutionExplorer";
    public const string TestExplorer = "TestExplorer";
    public const string GitChanges = "GitChanges";
    public const string ErrorList = "ErrorList";
    public const string Output = "Output";
    public const string FindResults = "FindResults";
    public const string Locals = "Locals";
    public const string Watch = "Watch";
    public const string CallStack = "CallStack";
    public const string Terminal = "Terminal";
}

public enum StatusKind
{
    NoSolution,
    Ready,
    Busy,
    Running,
}

/// <summary>Dialogs the view provides to the view model.</summary>
public interface IDialogService
{
    Task<string?> PickSolutionAsync();
    /// <summary>A folder to open; null when cancelled.</summary>
    Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
    Task<string?> PromptAsync(string title, string label, string initial);
    /// <summary>A text that does not show as it is typed (a password) when <paramref name="secret"/>; null when cancelled.</summary>
    Task<string?> PromptAsync(string title, string label, string initial, bool secret) => PromptAsync(title, label, initial);
    /// <summary>One of <paramref name="options"/> (<paramref name="initial"/> chosen first); null when cancelled.</summary>
    Task<string?> PickAsync(string title, string label, IReadOnlyList<string> options, string? initial) => Task.FromResult<string?>(null);
    /// <summary>Returns true = save, false = discard, null = cancel.</summary>
    Task<bool?> ConfirmSaveAsync(string fileName);
    /// <summary>Yes/No question; returns true for Yes.</summary>
    Task<bool?> ConfirmAsync(string title, string message);
    /// <summary>Add New Item: a template and a name for a new file in <paramref name="folder"/>, or null when cancelled.</summary>
    Task<NewItemChoice?> ChooseNewItemAsync(string title, string folder, ItemTemplate initial);
    /// <summary>Files to add (Add Existing Item); empty when cancelled.</summary>
    Task<IReadOnlyList<string>> PickFilesAsync(string title);
    /// <summary>Add > Existing Project: project files; empty when cancelled.</summary>
    Task<IReadOnlyList<string>> PickProjectsAsync() => PickFilesAsync("Add Existing Project");
    /// <summary>Add > New Project: the "Add a new project" wizard for <paramref name="solution"/>; the project files it made, empty when cancelled.</summary>
    Task<IReadOnlyList<string>> CreateProjectAsync(string solution) => Task.FromResult<IReadOnlyList<string>>([]);
    /// <summary>Add > Project Reference: the projects <paramref name="project"/> is to reference, or null when cancelled.</summary>
    Task<IReadOnlyList<string>?> ChooseProjectReferencesAsync(string project, IReadOnlyList<ProjectReferenceChoice> projects) =>
        Task.FromResult<IReadOnlyList<string>?>(null);
}

public partial class MainViewModel : ObservableObject
{
    readonly List<ErrorItem> _buildErrors = [];
    (string Verb, string Target, bool Succeeded, TimeSpan Took)? _lastBuild;
    CancellationTokenSource? _buildCts;
    ProcessRunner? _runProcess;
    int? _lastExitCode;

    public MainViewModel() : this(Settings.Load())
    {
    }

    /// <summary>With these settings (the modules that are on among them), and these modules instead of the application's.</summary>
    public MainViewModel(Settings settings, IReadOnlyList<KarvonModule>? modules = null)
    {
        _givenModules = modules;
        Settings = settings;
        InitializeDocumentGroups();
        if (Settings.UiLanguage is "en" or "uz")
            Karvon.Claude.ClaudeLanguage.Instance.Language = Settings.UiLanguage;
        BuildOutput = new OutputChannel("Build");
        DebugOutput = new OutputChannel("Debug");
        ServerOutput = new OutputChannel("Language Server");
        OutputChannels = [BuildOutput, DebugOutput, ServerOutput, GitOutput];
        SelectedOutput = BuildOutput;
        InitializeDebugging();
        StartModules();
        EditorHost.DefaultZoom = Math.Clamp(Settings.EditorZoom, EditorHost.MinZoom, EditorHost.MaxZoom);
    }

    public IDialogService? Dialogs { get; set; }
    public Settings Settings { get; }

    public ObservableCollection<ExplorerNode> ExplorerRoots { get; } = [];
    public ObservableCollection<DocumentTab> Documents { get; } = [];

    /// <summary>The open editor tabs (the document well also holds diff and NuGet tabs).</summary>
    public IEnumerable<DocumentViewModel> Editors => Documents.OfType<DocumentViewModel>();

    public DocumentViewModel? ActiveEditor => ActiveDocument as DocumentViewModel;
    public ObservableCollection<ErrorItem> Errors { get; } = [];
    public ObservableCollection<FindResultItem> FindResults { get; } = [];
    public ObservableCollection<ProjectInfo> StartupProjects { get; } = [];
    public ObservableCollection<string> LaunchProfiles { get; } = [];
    public ObservableCollection<OutputChannel> OutputChannels { get; }
    public IReadOnlyList<string> Configurations { get; } = ["Debug", "Release"];
    public IReadOnlyList<string> RecentSolutions => Settings.RecentSolutions;

    public OutputChannel BuildOutput { get; }
    public OutputChannel DebugOutput { get; }
    public OutputChannel ServerOutput { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    [NotifyPropertyChangedFor(nameof(HasSolution))]
    [NotifyCanExecuteChangedFor(nameof(BuildSolutionCommand), nameof(RebuildSolutionCommand), nameof(CleanSolutionCommand), nameof(StartWithoutDebuggingCommand), nameof(StartDebuggingCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunAllTestsCommand), nameof(RunFailedTestsCommand), nameof(RunSelectedTestsCommand), nameof(RepeatLastTestRunCommand), nameof(RefreshTestsCommand), nameof(DebugAllTestsCommand), nameof(DebugSelectedTestsCommand))]
    public partial SolutionInfo? Solution { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveEditor))]
    public partial DocumentTab? ActiveDocument { get; set; }

    [ObservableProperty]
    public partial OutputChannel SelectedOutput { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Ready";

    [ObservableProperty]
    public partial StatusKind Status { get; set; } = StatusKind.NoSolution;

    [ObservableProperty]
    public partial string CaretText { get; set; } = "";

    [ObservableProperty]
    public partial string ServerStatus { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BuildSolutionCommand), nameof(RebuildSolutionCommand), nameof(CleanSolutionCommand), nameof(StartWithoutDebuggingCommand), nameof(StartDebuggingCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunAllTestsCommand), nameof(RunFailedTestsCommand), nameof(RunSelectedTestsCommand), nameof(RepeatLastTestRunCommand), nameof(RefreshTestsCommand), nameof(DebugAllTestsCommand), nameof(DebugSelectedTestsCommand))]
    public partial bool IsBuilding { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopCommand), nameof(StartWithoutDebuggingCommand), nameof(StartDebuggingCommand))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartWithoutDebuggingCommand), nameof(StartDebuggingCommand))]
    public partial ProjectInfo? StartupProject { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartButtonText))]
    public partial string? LaunchProfile { get; set; }

    [ObservableProperty]
    public partial string Configuration { get; set; } = "Debug";

    /// <summary>Raised to bring a tool window forward, whichever dock group it currently lives in.</summary>
    public event Action<string, string[]>? ToolWindowRequested;

    /// <summary>The last tool window the IDE brought forward (for tests and diagnostics).</summary>
    public string? LastRequestedToolWindow { get; private set; }

    /// <summary>Shows a tool window (see <see cref="ToolWindows"/>) unless one of <paramref name="unlessShowing"/> is already in front.</summary>
    public void ShowToolWindow(string id, params string[] unlessShowing)
    {
        LastRequestedToolWindow = id;
        ToolWindowRequested?.Invoke(id, unlessShowing);
    }

    [ObservableProperty]
    public partial string FindResultsTitle { get; set; } = "Find Symbol Results";

    [ObservableProperty]
    public partial bool ShowErrors { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowWarnings { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowMessages { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ErrorCountText))]
    public partial int ErrorCount { get; set; }

    public string ErrorCountText => ErrorCount == 1 ? "1 Error" : $"{ErrorCount} Errors";

    public string WarningCountText => WarningCount == 1 ? "1 Warning" : $"{WarningCount} Warnings";

    public string MessageCountText => MessageCount == 1 ? "1 Message" : $"{MessageCount} Messages";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WarningCountText))]
    public partial int WarningCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MessageCountText))]
    public partial int MessageCount { get; set; }

    [ObservableProperty]
    public partial string ExplorerFilter { get; set; } = "";

    public bool HasSolution => Solution is not null;

    public string WindowTitle => Solution is null ? "Karvon" : $"{Solution.Name} - Karvon";

    partial void OnShowErrorsChanged(bool value) => RefreshErrorList();
    partial void OnShowWarningsChanged(bool value) => RefreshErrorList();
    partial void OnShowMessagesChanged(bool value) => RefreshErrorList();

    partial void OnActiveDocumentChanged(DocumentTab? value)
    {
        SyncGroupWithActiveDocument(value);
        ActiveDocumentChanged?.Invoke(value);
        UpdateCaretText();
        if (value is not DocumentViewModel editor)
            return;
        // Like VS, refresh squiggles for the document the user switches to.
        editor.Host.ScheduleDiagnostics(150);
        if (Solution is not null && !_restoringState)
            _state.ActiveDocument = editor.FilePath;
    }

    partial void OnStartupProjectChanged(ProjectInfo? value)
    {
        LaunchProfiles.Clear();
        foreach (var p in value?.LaunchProfiles ?? [])
            LaunchProfiles.Add(p.Name);
        var saved = Solution is null ? null : _state.LaunchProfile;
        LaunchProfile = LaunchProfiles.Contains(saved ?? "") ? saved : LaunchProfiles.FirstOrDefault();
        if (Solution is not null && value is not null && !_restoringState)
        {
            _state.StartupProject = value.FilePath;
            SaveSession();
        }
    }

    partial void OnLaunchProfileChanged(string? value)
    {
        if (Solution is not null && value is not null && !_restoringState)
        {
            _state.LaunchProfile = value;
            SaveSession();
        }
    }

    partial void OnConfigurationChanged(string value)
    {
        if (Solution is not null && !_restoringState)
        {
            _state.Configuration = value;
            SaveSession();
        }
    }

    void SetStatus(string text, StatusKind? kind = null)
    {
        StatusText = text;
        if (kind is { } k)
            Status = k;
    }

    // ---- Solution lifecycle --------------------------------------------------------------------------------

    [RelayCommand]
    async Task OpenSolution()
    {
        if (Dialogs is null || await Dialogs.PickSolutionAsync() is not { } path)
            return;
        await OpenSolutionAsync(path);
    }

    [RelayCommand]
    Task OpenRecent(string path) => OpenSolutionAsync(path);

    /// <summary>File > Open > Folder: a folder as it is, its files in Solution Explorer (VS's Open Folder).</summary>
    [RelayCommand]
    async Task OpenFolder()
    {
        if (Dialogs is null || await Dialogs.PickFolderAsync("Open Folder") is not { } folder)
            return;
        await OpenSolutionAsync(folder);
    }

    public async Task OpenSolutionAsync(string path)
    {
        SolutionInfo solution;
        IsOpeningSolution = true;
        try
        {
            if (!await CloseSolutionCoreAsync())
                return;
            SetStatus($"Opening {Path.GetFileName(path)}...", StatusKind.Busy);
            try
            {
                solution = await LoadWorkspaceAsync(path);
            }
            catch (Exception ex)
            {
                SetStatus($"Failed to open solution: {ex.Message}", StatusKind.NoSolution);
                return;
            }

            Solution = solution;
            AddWorkspaceProjects(solution);
            NoteStructureRead(solution);
            ExplorerRoots.Add(solution.Root);
            Settings.AddRecent(path);
            OnPropertyChanged(nameof(RecentSolutions));
            _state = RestoresSolutionState ? SolutionState.Load(path, Settings) : new SolutionState();
            _restoringState = true;
            try
            {
                RestoreSolutionState(solution, _state);
            }
            finally
            {
                _restoringState = false;
            }
            Settings.Save();
        }
        finally
        {
            // What shows from now on is this solution's (or no solution's, when it failed to open): its own data sources,
            // its .mcp.json, its skills.
            IsOpeningSolution = false;
            WorkspaceChanged?.Invoke();
        }

        SetStatus("Ready", StatusKind.Ready);
        await OpenGitAsync(solution);
        // New shells start in the solution folder, like VS's Developer terminal.
        if (_terminal is { } terminal)
            terminal.WorkingDirectory = solution.Directory;
        await OnModulesWorkspaceChangedAsync();
        await StartWorkspaceLanguageServersAsync();
    }

    [RelayCommand]
    async Task CloseSolution()
    {
        if (await CloseSolutionCoreAsync())
            WorkspaceChanged?.Invoke();
    }

    /// <summary>Closes everything; returns false if the user cancelled saving.</summary>
    public async Task<bool> CloseSolutionCoreAsync()
    {
        foreach (var doc in Editors.Where(d => d.IsDirty).ToList())
        {
            if (Dialogs is null)
                break;
            var answer = await Dialogs.ConfirmSaveAsync(doc.FileName);
            if (answer is null)
                return false;
            if (answer == true)
                doc.Host.Save();
        }
        SaveSession();
        // What the modules keep of the solution closes with it (the Database Explorer: a console's open transaction asks first).
        foreach (var module in Modules)
            if (!await module.CloseWorkspaceAsync())
                return false;

        _runProcess?.Kill();
        if (_session is { } debugSession)
            await debugSession.TerminateAsync();
        _buildCts?.Cancel();
        StopWorkspaceTasks();
        // Every tab goes; those that follow something (the Docker Explorer's engine streams) stop following it.
        foreach (var doc in Documents.ToList())
            doc.Dispose();
        Documents.Clear();
        ActiveDocument = null;
        await StopModuleLanguageServersAsync();
        ExplorerRoots.Clear();
        StartupProjects.Clear();
        LaunchProfiles.Clear();
        _buildErrors.Clear();
        FindResults.Clear();
        RefreshErrorList();
        Breakpoints.Load(new Dictionary<string, List<int>>());
        Watches.Clear();
        ResetTesting();
        CloseGit();
        Solution = null;
        await OnModulesWorkspaceChangedAsync();
        SetStatus("Ready", StatusKind.NoSolution);
        return true;
    }

    /// <summary>Writes the solution's state to its .vs folder (documents with caret and scroll, explorer, debugging...).</summary>
    public void SaveSession()
    {
        if (Solution is not { } solution || _restoringState)
            return;
        CaptureSolutionState(_state);
        _state.Save(solution.FilePath);
    }

    // ---- Language server -------------------------------------------------------------------------------------

    /// <summary>Tools > Restart Language Server: every language server of the workspace stops, and starts again for its files.</summary>
    [RelayCommand]
    async Task RestartLanguageServer()
    {
        foreach (var d in Editors)
            d.Host.Detach();
        await StopModuleLanguageServersAsync();
        await StartWorkspaceLanguageServersAsync();
        foreach (var d in Editors)
            AttachModuleLanguageServer(d);
    }

    // ---- Documents ---------------------------------------------------------------------------------------------

    public void OpenNode(ExplorerNode node)
    {
        if (node.Kind == NodeKind.File && node.Path is not null)
            OpenDocument(node.Path);
        else if (node.Kind == NodeKind.Project && node.Path is not null)
            OpenDocument(node.Path);
    }

    public DocumentViewModel? OpenDocument(string path, bool activate = true)
    {
        var existing = Editors.FirstOrDefault(d => d.FilePath == path);
        if (existing is null)
        {
            if (!File.Exists(path) || IsBinary(path))
                return null;
            EditorHost host;
            try
            {
                host = new EditorHost(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SetStatus(ex.Message);
                return null;
            }
            existing = new DocumentViewModel(host);
            Wire(existing);
            host.AttachBreakpoints(Breakpoints);
            _ = RefreshGitBaselineAsync(existing);
            if (Mode == DebugMode.Break)
                host.DataTipProvider = EvaluateDataTipAsync;
            Documents.Add(existing);
            AttachModuleLanguageServer(existing);
        }
        if (activate)
            ActiveDocument = existing;
        return existing;
    }

    static bool IsBinary(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".ico" or ".webp" or ".xlsx" or ".docx" or ".pdf" or ".dll" or ".exe"
        or ".ttf" or ".woff" or ".woff2" or ".zip" or ".pyc";

    void Wire(DocumentViewModel doc)
    {
        var host = doc.Host;
        host.CaretMoved += () =>
        {
            if (ActiveDocument == doc)
                UpdateCaretText();
        };
        host.DiagnosticsChanged += RefreshErrorList;
        host.StatusMessage += text => SetStatus(text);
        host.DefinitionsFound += locations =>
        {
            if (locations.Count == 1)
                NavigateTo(locations[0]);
            else
                ShowFindResults("Go To Definition", locations);
        };
        host.ReferencesFound += (symbol, refs) => ShowFindResults($"'{symbol}' references", refs);
        host.WorkspaceEditRequested += ApplyWorkspaceEditAsync;
        host.WorkspaceOperationsRequested += ApplyWorkspaceOperationsAsync;
        host.Saved += () => _ = RefreshGitAsync();
        host.Saved += () =>
        {
            FileSaved?.Invoke(doc.FilePath);
            OnWorkspaceFileSaved(doc.FilePath);
        };
        host.CompareRequested += () => _ = CompareWithUnmodifiedAsync(doc);
        host.AskClaudeRequested += ShowClaude;
        host.ZoomChanged += () => Settings.EditorZoom = host.Zoom;
        host.PromptRequested += initial => Dialogs?.PromptAsync("Rename", "New name:", initial) ?? Task.FromResult<string?>(null);
    }

    void UpdateCaretText()
    {
        if (ActiveEditor is null)
        {
            CaretText = "";
            return;
        }
        var (line, col) = ActiveEditor.Host.CaretPosition;
        CaretText = $"Ln: {line}   Ch: {col}";
    }

    [RelayCommand]
    async Task CloseDocument(DocumentTab? doc)
    {
        doc ??= ActiveDocument;
        if (doc is null)
            return;
        if (doc.CloseByOwner())
            return;
        if (doc is DocumentViewModel { IsDirty: true } editor && Dialogs is not null)
        {
            var answer = await Dialogs.ConfirmSaveAsync(editor.FileName);
            if (answer is null)
                return;
            if (answer == true)
                editor.Host.Save();
        }
        var index = Documents.IndexOf(doc);
        Documents.Remove(doc);
        doc.Dispose();
        if (ActiveDocument == doc || ActiveDocument is null)
            ActiveDocument = Documents.Count == 0 ? null : Documents[Math.Clamp(index, 0, Documents.Count - 1)];
        RefreshErrorList();
        SaveSession();
    }

    // ---- Solution Explorer context menu ----------------------------------------------------------------------

    [RelayCommand]
    Task BuildProject(ProjectInfo? project) => project is null ? Task.CompletedTask : RunBuildAsync("build", "Build", project.FilePath);

    [RelayCommand]
    Task RebuildProject(ProjectInfo? project) => project is null ? Task.CompletedTask : RunBuildAsync("rebuild", "Rebuild", project.FilePath);

    [RelayCommand]
    Task CleanProject(ProjectInfo? project) => project is null ? Task.CompletedTask : RunBuildAsync("clean", "Clean", project.FilePath);

    /// <summary>Pack: the project's NuGet package (dotnet pack), in bin/&lt;configuration&gt;.</summary>
    [RelayCommand]
    Task PackProject(ProjectInfo? project) => project is null ? Task.CompletedTask : RunBuildAsync("pack", "Pack", project.FilePath);

    /// <summary>Publish to a folder (dotnet publish): bin/&lt;configuration&gt;/&lt;framework&gt;/publish, as VS's folder profile.</summary>
    [RelayCommand]
    Task PublishProject(ProjectInfo? project) => project is null ? Task.CompletedTask : RunBuildAsync("publish", "Publish", project.FilePath);

    [RelayCommand]
    void SetStartupProject(ProjectInfo? project)
    {
        if (project is not null && StartupProjects.FirstOrDefault(p => p.FilePath == project.FilePath) is { } match)
            StartupProject = match;
    }

    /// <summary>Starts a new terminal in the given folder (Solution Explorer > Open in Terminal).</summary>
    public async Task OpenTerminalAtAsync(string folder)
    {
        Terminal.WorkingDirectory = folder;
        ShowTerminal();
        await Terminal.RestartAsync();
    }

    // ---- Document tab commands (tab context menu) -----------------------------------------------------------

    async Task CloseDocumentsAsync(IEnumerable<DocumentTab> docs)
    {
        foreach (var doc in docs.ToList())
        {
            var before = Documents.Count;
            await CloseDocument(doc);
            if (Documents.Count == before)
                return; // the user cancelled a save prompt
        }
    }

    [RelayCommand]
    Task CloseAllButThis(DocumentTab? doc) => CloseDocumentsAsync(Documents.Where(d => d != doc));

    [RelayCommand]
    Task CloseAllButPinned() => CloseDocumentsAsync(Documents.Where(d => !d.IsPinned));

    [RelayCommand]
    Task CloseAllDocuments() => CloseDocumentsAsync(Documents);

    /// <summary>The tabs of the document's group, in their order (Close All Tabs to the Left / Right stay in it, as in VS).</summary>
    IReadOnlyList<DocumentTab> TabsOfGroup(DocumentTab doc) => GroupOf(doc)?.Tabs ?? (IReadOnlyList<DocumentTab>)Documents;

    [RelayCommand]
    Task CloseTabsToLeft(DocumentTab? doc) =>
        doc is null ? Task.CompletedTask : CloseDocumentsAsync(TabsOfGroup(doc).TakeWhile(d => d != doc).Where(d => !d.IsPinned));

    [RelayCommand]
    Task CloseTabsToRight(DocumentTab? doc) =>
        doc is null ? Task.CompletedTask : CloseDocumentsAsync(TabsOfGroup(doc).SkipWhile(d => d != doc).Skip(1).Where(d => !d.IsPinned));

    /// <summary>Pinned tabs move to the left edge of their group, in pin order, like VS.</summary>
    [RelayCommand]
    void TogglePin(DocumentTab? doc)
    {
        if (doc is null)
            return;
        doc.IsPinned = !doc.IsPinned;
        var active = ActiveDocument;
        var target = doc.IsPinned ? Documents.Count(d => d.IsPinned && d != doc) : Documents.Count(d => d.IsPinned);
        Documents.Move(Documents.IndexOf(doc), Math.Min(target, Documents.Count - 1));
        if (GroupOf(doc) is { } group)
        {
            var tabs = group.Tabs;
            var at = doc.IsPinned ? tabs.Count(d => d.IsPinned && d != doc) : tabs.Count(d => d.IsPinned);
            _syncingGroups = true;
            try
            {
                tabs.Move(tabs.IndexOf(doc), Math.Min(at, tabs.Count - 1));
            }
            finally
            {
                _syncingGroups = false;
            }
        }
        ActiveDocument = active;
    }

    public void SaveDocument(DocumentViewModel doc)
    {
        try
        {
            doc.Host.Save();
            SetStatus("Item(s) Saved");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus("Save failed: " + ex.Message);
        }
    }

    [RelayCommand]
    void Save()
    {
        if (ActiveEditor is not { } doc)
            return;
        try
        {
            doc.Host.Save();
            SetStatus("Item(s) Saved");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus("Save failed: " + ex.Message);
        }
    }

    [RelayCommand]
    void SaveAll()
    {
        foreach (var doc in Editors.Where(d => d.IsDirty))
            doc.Host.Save();
        SetStatus("Item(s) Saved");
    }

    public void NavigateTo(LspLocation location) => NavigateTo(location.FilePath, location.Range);

    public void NavigateTo(string path, LspRange range)
    {
        var doc = OpenDocument(path);
        if (doc is null)
            return;
        Dispatcher.UIThread.Post(() => doc.Host.NavigateTo(range), DispatcherPriority.Loaded);
    }

    [RelayCommand]
    void NavigateToError(ErrorItem? item)
    {
        if (item?.FilePath is not { } path || !File.Exists(path))
            return;
        var pos = new LspPosition(Math.Max(0, item.Line - 1), Math.Max(0, item.Column - 1));
        NavigateTo(path, new LspRange(pos, pos));
    }

    [RelayCommand]
    void NavigateToFindResult(FindResultItem? item)
    {
        if (item is not null)
            NavigateTo(item.FilePath, item.Range);
    }

    /// <summary>Called when the window regains focus: pick up edits made outside Karvon (git checkout etc.).</summary>
    public void CheckExternalChanges()
    {
        if (HasGit)
        {
            _ = RefreshGitAsync();
            _ = RefreshAllGitBaselinesAsync();
        }
        foreach (var doc in Editors)
            if (doc.Host.ReloadIfChangedOnDisk())
                SetStatus($"{doc.FileName} was reloaded because it changed on disk.");
    }

    async Task ApplyWorkspaceEditAsync(IReadOnlyDictionary<string, IReadOnlyList<LspTextEdit>> edits)
    {
        foreach (var (path, fileEdits) in edits)
        {
            // Open each affected file so the edit is undoable and the server sees it through didChange.
            var doc = OpenDocument(path, activate: false);
            doc?.Host.ApplyEdits(fileEdits);
        }
        SetStatus($"Renamed across {edits.Count} file(s). Use Save All to write them.");
        await Task.CompletedTask;
    }

    /// <summary>Applies a code action's workspace edit: file creates/renames/deletes and text edits, in order.</summary>
    async Task ApplyWorkspaceOperationsAsync(IReadOnlyList<LspFileOperation> ops)
    {
        var filesChanged = false;
        foreach (var op in ops)
        {
            switch (op.Kind)
            {
                case "create":
                    if (!File.Exists(op.Path))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(op.Path)!);
                        await File.WriteAllTextAsync(op.Path, "");
                        filesChanged = true;
                    }
                    break;
                case "rename" when op.NewPath is not null:
                    if (Editors.FirstOrDefault(d => d.FilePath == op.Path) is { } open)
                    {
                        if (open.IsDirty)
                            open.Host.Save();
                        Documents.Remove(open);
                        open.Dispose();
                    }
                    File.Move(op.Path, op.NewPath);
                    filesChanged = true;
                    break;
                case "delete":
                    if (Editors.FirstOrDefault(d => d.FilePath == op.Path) is { } deleted)
                    {
                        Documents.Remove(deleted);
                        deleted.Dispose();
                    }
                    File.Delete(op.Path);
                    filesChanged = true;
                    break;
                case "edit":
                    // Edits land in the editor buffer (undoable, synced to the server); the user saves as usual.
                    OpenDocument(op.Path, activate: false)?.Host.ApplyEdits(op.Edits);
                    break;
            }
        }
        if (filesChanged)
            await RefreshExplorer();
    }

    void ShowFindResults(string title, IReadOnlyList<LspLocation> locations)
    {
        FindResults.Clear();
        var lines = new Dictionary<string, string[]>();
        foreach (var loc in locations.OrderBy(l => l.FilePath, StringComparer.Ordinal).ThenBy(l => l.Range.Start.Line))
        {
            var path = loc.FilePath;
            if (!lines.TryGetValue(path, out var text))
                lines[path] = text = File.Exists(path) ? File.ReadAllLines(path) : [];
            var line = loc.Range.Start.Line < text.Length ? text[loc.Range.Start.Line].Trim() : "";
            FindResults.Add(new FindResultItem(path, loc.Range.Start.Line + 1, loc.Range.Start.Character + 1, line, loc.Range));
        }
        FindResultsTitle = $"{title} ({locations.Count})";
        ShowToolWindow(ToolWindows.FindResults);
        SetStatus($"{locations.Count} result(s) found.");
    }

    // ---- Error List ---------------------------------------------------------------------------------------

    /// <summary>VS's "Build + IntelliSense": the last build's errors and the open files' live ones, each once.</summary>
    internal List<ErrorItem> AllProblems()
    {
        var live = Editors
            .SelectMany(d => d.Host.Diagnostics.Select(x => new ErrorItem(
                x.Severity == DiagnosticSeverity.Hint ? DiagnosticSeverity.Information : x.Severity,
                x.Code, x.Message, ProjectNameFor(d.FilePath), d.FilePath,
                x.Range.Start.Line + 1, x.Range.Start.Character + 1, "IntelliSense")));
        return _buildErrors.Concat(live)
            .DistinctBy(e => (e.FilePath, e.Line, e.Code, e.Description))
            .ToList();
    }

    void RefreshErrorList()
    {
        var all = AllProblems();

        ErrorCount = all.Count(e => e.Severity == DiagnosticSeverity.Error);
        WarningCount = all.Count(e => e.Severity == DiagnosticSeverity.Warning);
        MessageCount = all.Count(e => e.Severity >= DiagnosticSeverity.Information);
        Errors.Clear();
        foreach (var e in all
                     .Where(e => e.Severity switch
                     {
                         DiagnosticSeverity.Error => ShowErrors,
                         DiagnosticSeverity.Warning => ShowWarnings,
                         _ => ShowMessages,
                     })
                     .OrderBy(e => e.Severity)
                     .ThenBy(e => e.File, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(e => e.Line))
            Errors.Add(e);
    }

    string ProjectNameFor(string? path)
    {
        if (path is null || Solution is null)
            return "";
        return Solution.Projects
            .Where(p => FilePaths.IsUnder(path, p.Directory))
            .OrderByDescending(p => p.Directory.Length)
            .FirstOrDefault()?.Name ?? "";
    }

    // ---- Build -------------------------------------------------------------------------------------------------

    /// <summary>A solution or a project to build, or a folder with a build task.</summary>
    bool CanBuild => !IsBuilding && (Solution is { IsFolder: false } || IsFolderWorkspace && BuildTask is not null);

    /// <summary>A solution or a project to clean (a folder's tasks have no clean).</summary>
    bool CanClean => Solution is { IsFolder: false } && !IsBuilding;

    [RelayCommand(CanExecute = nameof(CanBuild))]
    Task BuildSolution() => RunBuildAsync("build", "Build");

    [RelayCommand(CanExecute = nameof(CanBuild))]
    Task RebuildSolution() => RunBuildAsync("rebuild", "Rebuild All");

    [RelayCommand(CanExecute = nameof(CanClean))]
    Task CleanSolution() => RunBuildAsync("clean", "Clean");

    [RelayCommand]
    void CancelBuild() => _buildCts?.Cancel();

    /// <summary>
    /// Builds (or cleans, packs, publishes) the solution or one of its projects as the solution's module does it (.NET's
    /// dotnet build), into the Output window and the Error List: whether it succeeded. A folder builds by its build task
    /// (and has no clean).
    /// </summary>
    async Task<bool> RunBuildAsync(string verb, string title, string? target = null)
    {
        if (Solution is not { } solution || IsBuilding)
            return false;
        if (solution.IsFolder)
        {
            if (verb is "build" or "rebuild" && BuildTask is { } task)
                return await RunBuildTaskAsync(task);
            SetStatus(verb is "build" or "rebuild" ? "The folder has no build task (.vscode/tasks.json, package.json)." : $"A folder has no {verb}.");
            return false;
        }
        var request = new Modules.BuildRequest(verb, target ?? solution.FilePath, Configuration);
        if (Modules.Select(m => m.BuildSolution(solution, request)).FirstOrDefault(c => c is not null) is not { } command)
        {
            SetStatus($"No module builds {Path.GetFileName(solution.FilePath)}: its module is off (Tools > Modules).");
            return false;
        }
        SaveAll();
        IsBuilding = true;
        SelectedOutput = BuildOutput;
        ShowToolWindow(ToolWindows.Output);
        BuildOutput.Clear();
        _buildErrors.Clear();
        RefreshErrorList();
        SetStatus($"{title} started...", StatusKind.Busy);
        var sw = Stopwatch.StartNew();
        BuildOutput.Append($"{title} started at {DateTime.Now:HH:mm}...");

        var succeeded = 0;
        var cts = _buildCts = new CancellationTokenSource();
        int exitCode;
        try
        {
            using var runner = ProcessRunner.Start(command.Program, command.Arguments, solution.Directory, line =>
            {
                BuildOutput.Append(line);
                if (command.ProjectBuilt?.Invoke(line) == true)
                    Interlocked.Increment(ref succeeded);
                if (command.Problems(line) is { } d)
                    Dispatcher.UIThread.Post(() => AddBuildError(d));
            });
            exitCode = await runner.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            BuildOutput.Append("Build was canceled.");
            exitCode = -1;
        }
        finally
        {
            IsBuilding = false;
        }

        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        var verbLabel = verb switch { "clean" => "Clean", "rebuild" => "Rebuild All", "pack" => "Pack", "publish" => "Publish", _ => "Build" };
        return ReportBuild(verbLabel, Path.GetFileName(target ?? solution.FilePath), exitCode == 0, sw.Elapsed, command.ProjectBuilt is null ? null : succeeded);
    }

    /// <summary>A build's last lines in Output, the status bar, and the Error List when it failed with errors.</summary>
    bool ReportBuild(string verbLabel, string target, bool ok, TimeSpan took, int? projectsBuilt)
    {
        var errors = _buildErrors.Count(e => e.Severity == DiagnosticSeverity.Error);
        var warnings = _buildErrors.Count(e => e.Severity == DiagnosticSeverity.Warning);
        var built = projectsBuilt is { } count ? $", {count} project(s) built" : "";
        BuildOutput.Append($"========== {verbLabel}: {(ok ? "succeeded" : "failed")}, {errors} error(s), {warnings} warning(s){built} ==========");
        BuildOutput.Append($"========== {verbLabel} completed at {DateTime.Now:HH:mm} and took {took.TotalSeconds:0.000} seconds ==========");
        SetStatus(ok ? $"{verbLabel} succeeded" : $"{verbLabel} failed", IsRunning || IsDebugging ? StatusKind.Running : StatusKind.Ready);
        _lastBuild = (verbLabel, target, ok, took);
        if (!ok && errors > 0)
            ShowToolWindow(ToolWindows.ErrorList);
        return ok;
    }

    void AddBuildError(BuildDiagnostic d)
    {
        var project = d.Project is null ? "" : Path.GetFileNameWithoutExtension(d.Project);
        var item = new ErrorItem(d.Severity, d.Code, d.Message, project, d.File is null ? null : Path.GetFullPath(d.File, Solution?.Directory ?? "/"), d.Line, d.Column, "Build");
        if (_buildErrors.Any(e => e.FilePath == item.FilePath && e.Line == item.Line && e.Code == item.Code && e.Description == item.Description))
            return;
        _buildErrors.Add(item);
        RefreshErrorList();
    }

    // ---- Run ---------------------------------------------------------------------------------------------------

    bool CanStart => Solution is not null && (IsFolderWorkspace ? StartupItem is not null : StartupProject is not null) && !IsBuilding && !IsRunning
        && Mode == DebugMode.Design;

    [RelayCommand(CanExecute = nameof(CanStart))]
    async Task StartWithoutDebugging()
    {
        // A folder runs what is chosen on the toolbar: a run task, or a launch configuration without the debugger.
        if (IsFolderWorkspace)
        {
            switch (StartupItem)
            {
                case { Task: { } task }:
                    await RunProgramTaskAsync(task);
                    break;
                case { Debug: { } launch }:
                    await LaunchConfigurationAsync(launch, noDebug: true);
                    break;
            }
            return;
        }
        if (Solution is null || StartupProject is not { } project)
            return;
        // Like VS: build first, run only if the build succeeded.
        if (!await RunBuildAsync("build", "Build", project.FilePath))
            return;
        await LaunchConfigurationAsync(StartupLaunch(project), noDebug: true, withSolution: true);
    }

    /// <summary>A page in the desktop's browser; when there is none, the URL is in the Output window anyway.</summary>
    static void OpenBrowser(string url) => DesktopShell.Open(url);

    // ---- Solution Explorer helpers -------------------------------------------------------------------------

    [RelayCommand]
    void CollapseAll()
    {
        foreach (var root in ExplorerRoots)
            Collapse(root);
        if (ExplorerRoots.FirstOrDefault() is { } r)
            r.IsExpanded = true;

        static void Collapse(ExplorerNode node)
        {
            node.IsExpanded = false;
            foreach (var c in node.Children)
                Collapse(c);
        }
    }

    [RelayCommand]
    Task RefreshExplorer() => ReloadSolutionAsync();

    /// <summary>Selects the active document in Solution Explorer (VS: "Sync with Active Document").</summary>
    public ExplorerNode? SyncWithActiveDocument()
    {
        if (ActiveEditor is null || ExplorerRoots.FirstOrDefault() is not { } root)
            return null;
        var node = root.FindByPath(ActiveEditor.FilePath);
        if (node is null)
            return null;
        ExpandAncestors(root, node);
        return node;
    }

    static bool ExpandAncestors(ExplorerNode current, ExplorerNode target)
    {
        if (current == target)
            return true;
        foreach (var child in current.Children)
        {
            if (ExpandAncestors(child, target))
            {
                current.IsExpanded = true;
                return true;
            }
        }
        return false;
    }

    /// <summary>Find in Files (Ctrl+Shift+F): plain case-insensitive search over the solution's source files.</summary>
    public async Task FindInFilesAsync(string text)
    {
        if (Solution is not { } solution)
            return;
        SetStatus($"Find all \"{text}\"...", StatusKind.Busy);
        var hits = await Task.Run(() =>
        {
            var results = new List<FindResultItem>();
            foreach (var file in Directory.EnumerateFiles(solution.Directory, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
            {
                if (file.Contains("/bin/", StringComparison.Ordinal) || file.Contains("/obj/", StringComparison.Ordinal)
                    || file.Contains("/.", StringComparison.Ordinal) || file.Contains("/node_modules/", StringComparison.Ordinal)
                    || IsBinary(file) || new FileInfo(file).Length > 2_000_000)
                    continue;
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length && results.Count < 5000; i++)
                {
                    var col = lines[i].IndexOf(text, StringComparison.OrdinalIgnoreCase);
                    if (col < 0)
                        continue;
                    var start = new LspPosition(i, col);
                    results.Add(new FindResultItem(file, i + 1, col + 1, lines[i].Trim(), new LspRange(start, start with { Character = col + text.Length })));
                }
            }
            return results;
        });
        FindResults.Clear();
        foreach (var hit in hits)
            FindResults.Add(hit);
        FindResultsTitle = $"Find All \"{text}\" ({hits.Count})";
        ShowToolWindow(ToolWindows.FindResults);
        SetStatus($"Matching lines: {hits.Count}", StatusKind.Ready);
    }

    /// <summary>Files whose name contains the filter, for the Solution Explorer search box.</summary>
    public IEnumerable<ExplorerNode> SearchFiles(string filter)
    {
        if (Solution is null || filter.Length < 2)
            yield break;
        foreach (var project in Solution.Projects)
        {
            if (!Directory.Exists(project.Directory))
                continue;
            foreach (var file in Directory.EnumerateFiles(project.Directory, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
            {
                if (file.Contains("/bin/", StringComparison.Ordinal) || file.Contains("/obj/", StringComparison.Ordinal) || file.Contains("/.", StringComparison.Ordinal))
                    continue;
                var name = Path.GetFileName(file);
                if (name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    yield return new ExplorerNode(NodeKind.File, name, file) { Detail = Path.GetRelativePath(Solution.Directory, Path.GetDirectoryName(file)!) };
            }
        }
    }
}
