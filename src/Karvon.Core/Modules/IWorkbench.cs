using System.Text.Json;
using Bengine.Introspection.Servers;
using Karvon.Claude;
using Karvon.Services;
using Karvon.ViewModels;
using Karvon.Workspace;

namespace Karvon.Modules;

/// <summary>
/// What a module may use of Karvon Studio: the settings, the open folder, the document well, the tool windows, the
/// status bar, and what other parts of Karvon offer (<see cref="Service{T}"/>). Called on the UI thread.
/// </summary>
public interface IWorkbench
{
    Settings Settings { get; }

    void SaveSettings();

    /// <summary>The open solution's folder, or null.</summary>
    string? Workspace { get; }

    /// <summary>The open solution (or folder) and its projects, or null.</summary>
    SolutionInfo? Solution { get; }

    /// <summary>The solution read again (its projects changed: a package added), Solution Explorer keeping what is expanded.</summary>
    Task ReloadSolutionAsync();

    /// <summary>The solution was read again: a project added, removed or renamed is in <see cref="Solution"/> now.</summary>
    event Action? SolutionReloaded;

    /// <summary>The project a Solution Explorer node is in (its Dependencies, a package of it), or null.</summary>
    ProjectInfo? ProjectOf(ExplorerNode node);

    /// <summary>A question with Yes and No (and the dialog closed: null), as Karvon asks its own.</summary>
    Task<bool?> ConfirmAsync(string title, string message);

    /// <summary>
    /// Visual Studio's Reference Manager for a project: the solution's other projects, those it references checked; the
    /// ones checked when it closes with OK, or null when it was cancelled.
    /// </summary>
    Task<IReadOnlyList<string>?> ChooseProjectReferencesAsync(string project, IReadOnlyList<ProjectReferenceChoice> projects);

    /// <summary>Tools > Restart Language Server: the workspace's language servers stop, and start again for their files.</summary>
    Task RestartLanguageServersAsync();

    /// <summary>A command of the module's run as the tasks run (in the tasks' PATH: Node.js's...), its lines to output: its exit code.</summary>
    Task<int> RunAsync(WorkspaceTask task, Action<string> output, CancellationToken ct);

    /// <summary>
    /// The Package Manager of one of the module's projects (npm's of a package.json folder), as Visual Studio's NuGet
    /// Package Manager is: the one open, else a new one.
    /// </summary>
    Task ManagePackagesAsync(Packages.PackageSource source);

    /// <summary>The build configuration chosen on the toolbar ("Debug", "Release").</summary>
    string Configuration { get; }

    /// <summary>Build Solution, as the menu builds it, its output in Output (Build): whether it built.</summary>
    Task<bool> BuildAsync();

    /// <summary>
    /// A verb of the build ("build", "pack", "publish") on one of the solution's projects, as its module's BuildSolution
    /// does it, in Output (Build) and the Error List: whether it succeeded.
    /// </summary>
    Task<bool> BuildAsync(string verb, ProjectInfo project);

    /// <summary>Add New Item in a node's folder, the template chosen first; the new file's path, or null.</summary>
    Task<string?> AddNewItemAsync(ExplorerNode node, ItemTemplate template);

    /// <summary>A solution was opened or closed.</summary>
    event Action? WorkspaceChanged;

    /// <summary>From closing one solution until the next is open: what is the solution's belongs to none.</summary>
    bool IsOpeningWorkspace { get; }

    /// <summary>The folder where Karvon keeps the open solution's own things (.vs/&lt;Solution&gt;/Karvon), or null.</summary>
    string? WorkspaceStateDirectory { get; }

    /// <summary>What the modules kept of the open solution (see <see cref="KarvonModule.SaveWorkspaceState"/>); empty with none.</summary>
    IReadOnlyDictionary<string, string> WorkspaceState { get; }

    /// <summary>The modules that are on.</summary>
    IReadOnlyList<KarvonModule> Modules { get; }

    /// <summary>The editor in front: its file and what is selected in it, or null.</summary>
    EditorSelection? ActiveEditorSelection();

    /// <summary>Two texts of a file side by side in a document (Claude's proposal against the file).</summary>
    void ShowDiff(string path, string before, string after, string beforeTitle, string afterTitle, string key);

    /// <summary>Files may have changed on disk (Claude edited them): open documents and Solution Explorer follow.</summary>
    void CheckExternalChanges();

    /// <summary>The core's tools for Claude Code: the build, the tests, the debugger, the app, the editor.</summary>
    IEnumerable<IClaudeTool> ClaudeTools();

    /// <summary>A call of one of the core's tools ran: what it did, shown where the user would have done it.</summary>
    Task ShowClaudeToolAsync(string tool, JsonElement input, ClaudeToolOutput output);

    IEnumerable<DocumentTab> Documents { get; }

    /// <summary>The document in front.</summary>
    DocumentTab? ActiveDocument { get; set; }

    event Action<DocumentTab?>? ActiveDocumentChanged;

    /// <summary>A document in the document well, behind the one in front.</summary>
    void AddDocument(DocumentTab document);

    /// <summary>A document out of the document well, without asking (its owner closed it).</summary>
    void RemoveDocument(DocumentTab document);

    /// <summary>A document in the document well (added when it is not there yet), in front.</summary>
    void ShowDocument(DocumentTab document);

    /// <summary>Closes a document, asking to save it when it has unsaved changes.</summary>
    void CloseDocument(DocumentTab document);

    /// <summary>A file in the editor, at a line when one is given.</summary>
    void OpenFile(string path, int line = 0);

    /// <summary>A file of the editor's was saved.</summary>
    event Action<string>? FileSaved;

    /// <summary>Files to open; empty when cancelled.</summary>
    Task<IReadOnlyList<string>> PickFilesAsync(string title);

    void ShowToolWindow(string id);

    /// <summary>A tool window shown without hiding Claude Code: in Claude Code's own group it is only put in front of the others.</summary>
    void ShowToolWindowBesideClaude(string id);

    /// <summary>The module's pane of the Output window, made the first time.</summary>
    OutputChannel Output(string name);

    /// <summary>The Output window, at this pane.</summary>
    void ShowOutput(OutputChannel channel);

    /// <summary>A page in the desktop's browser.</summary>
    void OpenUrl(string url);

    /// <summary>
    /// Where the program Karvon runs (Ctrl+F5, F5, a folder's task) listens, as its output says it (http://localhost:5000);
    /// empty when nothing runs.
    /// </summary>
    IReadOnlyList<string> ListeningUrls { get; }

    /// <summary>The program started or stopped, or said another place it listens.</summary>
    event Action? ListeningChanged;

    /// <summary>A line in the status bar.</summary>
    void Status(string text);

    /// <summary>What Karvon or another module offers (<see cref="IClaudeService"/>), or null when it is off.</summary>
    T? Service<T>() where T : class;
}

/// <summary>The editor in front: its file and, when something is selected, the lines and the text.</summary>
public sealed record EditorSelection(string FilePath, int? StartLine, int? EndLine, string? Text);

/// <summary>The Database Explorer, for the modules that find databases (a database container).</summary>
public interface IDatabaseService
{
    /// <summary>A data source in the Database Explorer (unless one is there already that <paramref name="isSame"/>), the explorer in view.</summary>
    void AddDataSource(ServerProfile server, Func<ServerProfile, bool> isSame);
}

/// <summary>Claude Code in Karvon, for the modules that work with it.</summary>
public interface IClaudeService
{
    /// <summary>The chosen account's configuration folder: ~/.claude, or its CLAUDE_CONFIG_DIR.</summary>
    string ConfigDirectory { get; }

    /// <summary>The chosen account's settings file; null for ~/.claude.json.</summary>
    string? SettingsFile { get; }

    /// <summary>The tools Karvon gives the Claude Code sessions it starts; empty when they are off.</summary>
    IReadOnlyList<IClaudeTool> BuiltInTools { get; }

    /// <summary>Another account was chosen, or Claude Code's settings changed: what was read from them is to be read again.</summary>
    event Action? Changed;

    /// <summary>Claude Code's tool window, which other tool windows do not hide when they show beside it.</summary>
    string ToolWindowId { get; }

    /// <summary>The Claude Code window in front, the prompt focused (View > Claude Code, Ctrl+Esc, Ask Claude in the editor).</summary>
    void Show();

    /// <summary>The prompt in the Claude Code window, for the user to send.</summary>
    void Ask(string prompt);

    /// <summary>Files, folders or projects as @mentions in the prompt (Solution Explorer > Ask Claude).</summary>
    void AskAbout(IReadOnlyList<string> paths);

    /// <summary>
    /// One prompt answered without tools by a fast model (<c>claude -p</c>) in a folder: the answer. Throws
    /// InvalidOperationException, saying why, when there is none (no CLI, an error).
    /// </summary>
    Task<string> PrintAsync(string directory, string prompt);

    /// <summary>Claude Code signs in to one of its MCP servers; what it said.</summary>
    Task<string> AuthenticateMcpAsync(string name);

    /// <summary><c>claude mcp add</c>: why it failed, or null.</summary>
    Task<string?> AddMcpServerAsync(string name, string transport, string target, string scope);

    /// <summary>The MCP servers a session started in a folder gets beside its own: a file for <c>--mcp-config</c>, or null.</summary>
    Func<string, string?>? ExtraMcpServers { get; set; }
}
