using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Karvon.Claude;
using Karvon.Workspace;

namespace Karvon.Modules;

/// <summary>Where a tool window is docked: a side group (an activity bar) or the bottom tabs.</summary>
public enum DockSide
{
    Left,
    Right,
    Bottom,
}

/// <summary>
/// A tool window a module adds: an icon on an activity bar and an item in the View menu. Its view is made the first time
/// it shows, so a module never opened does nothing.
/// </summary>
/// <param name="Id">Its id in the layout Karvon remembers ("Mcp").</param>
/// <param name="MenuHeader">Its View menu item, with its access key ("_MCP Servers").</param>
/// <param name="Icon">A <c>VsIcon</c> kind.</param>
/// <param name="CreateView">Its view; null when there is none to show yet (the module says when with <see cref="KarvonModule.ResetToolWindow"/>).</param>
public sealed record ToolWindowInfo(string Id, string Title, string MenuHeader, string Icon, DockSide Side, Func<Control?> CreateView)
{
    /// <summary>The key that shows it from anywhere ("Ctrl+Alt+S"), or null.</summary>
    public string? Gesture { get; init; }

    /// <summary>What its menu item and key do, when more than putting it in front (Claude Code: the prompt gets the focus).</summary>
    public Action? Show { get; init; }

    /// <summary>The set it is a tab of (<see cref="ViewModels.ToolWindows.Git"/>), rather than a tool window of its own; null for none.</summary>
    public string? Set { get; init; }
}

/// <summary>A workload's language server: the files it serves, and how it starts for a folder.</summary>
/// <param name="Id">Its id (one runs per workspace).</param>
/// <param name="Serves">Whether it serves a file (by its path).</param>
public sealed record LanguageServerInfo(string Id, string Name, Func<string, bool> Serves, Func<LanguageServerStart, Task<Lsp.LspClient>> StartAsync)
{
    /// <summary>
    /// For a server that starts with a workspace it is for, as that opens, rather than with the first file it serves
    /// (Roslyn: a solution or a project, whose projects it loads first): whether a workspace is one. It does not start
    /// for another; its progress is the status bar's IntelliSense.
    /// </summary>
    public Func<SolutionInfo, bool>? ForWorkspace { get; init; }

    /// <summary>
    /// A linter's: it only adds its diagnostics to the files it serves, beside their language's server (ESLint beside
    /// TypeScript's), rather than being their server.
    /// </summary>
    public bool Lints { get; init; }

    /// <summary>
    /// Asked before it starts for a workspace, with the file it would start for (ESLint's: whether the user lets it run
    /// the workspace's own ESLint and configuration): false, and it does not start for that workspace while it is open.
    /// </summary>
    public Func<string, string, Task<bool>>? MayStartAsync { get; init; }
}

/// <summary>What a language server starts with.</summary>
/// <param name="Workspace">The folder it serves: the solution's, or the folder opened.</param>
/// <param name="Log">Its Output pane (Language Server): what installing or starting it says.</param>
public sealed record LanguageServerStart(string Workspace, Action<string> Log, CancellationToken Cancellation);

/// <summary>A workload's debugger: the launch.json types it debugs ("node", "coreclr"...), and how it starts for one.</summary>
/// <param name="Types">The configurations' types it debugs.</param>
/// <param name="StartAsync">Its adapter (installed the first time); it may complete the configuration (its own name for the type...).</param>
public sealed record DebuggerInfo(string Id, string Name, IReadOnlyList<string> Types, Func<DebuggerStart, Task<Debugging.DebugAdapter>> StartAsync)
{
    /// <summary>
    /// What it asks before one of its configurations starts, after its preLaunchTask (.NET's: whether to trust the
    /// development certificate for an https launch profile): false, and it does not start.
    /// </summary>
    public Func<DebugLaunch, Task<bool>>? PrepareAsync { get; init; }

    /// <summary>
    /// Ctrl+F5 of one of its configurations (its variables put in), when it runs that without a debugger: the program it
    /// runs (.NET's "dotnet": dotnet run with the launch profile). Null, and the debugger runs it with noDebug.
    /// </summary>
    public Func<JsonObject, Task<WorkspaceTask?>>? RunWithoutDebuggingAsync { get; init; }
}

/// <summary>What a debugger starts with.</summary>
/// <param name="Configuration">The launch configuration, its variables put in: launch.json's, or a module's for its task.</param>
/// <param name="Path">The tasks' PATH (the modules' tool folders first): the adapter's, and so the program's.</param>
/// <param name="Log">Output (Debug): what installing or starting it says.</param>
public sealed record DebuggerStart(string Workspace, JsonObject Configuration, string Path, Action<string> Log, CancellationToken Cancellation);

/// <summary>
/// An item a module puts in one of Karvon's menus: its header (with its access key, "Manage NuGet Packages for
/// _Solution..."), what it runs, or the items of its submenu; one with neither is a separator.
/// </summary>
public sealed record ModuleMenuItem(string Header, Func<Task>? Run)
{
    public static ModuleMenuItem Separator { get; } = new("-", null);

    public bool IsSeparator => Run is null && Items is null;

    /// <summary>Its submenu's items: it opens a submenu (Tools > NuGet Package Manager) and runs nothing.</summary>
    public IReadOnlyList<ModuleMenuItem>? Items { get; init; }

    public bool IsEnabled { get; init; } = true;

    /// <summary>The item of Karvon's it goes after, by its header ("Run Tests"; in the main menu, its access key aside).</summary>
    public string? After { get; init; }

    /// <summary>In a Solution Explorer menu: the submenu of Karvon's it goes in, at its end, by its header ("Add").</summary>
    public string? In { get; init; }
}

/// <summary>
/// A module's item of the main menu: in a top menu (Tools), after the item of Karvon's its After names, else first in
/// it, a separator after the modules' first items.
/// </summary>
public sealed record ModuleMainMenuItem(string Menu, ModuleMenuItem Item);

/// <summary>What a module's test frameworks start with.</summary>
/// <param name="Workspace">The open folder, or the solution's.</param>
/// <param name="Path">The tasks' PATH (the modules' tool folders first), for the programs the tests run with.</param>
/// <param name="Log">Output (Tests).</param>
public sealed record TestFrameworkStart(string Workspace, string Path, Action<string> Log)
{
    /// <summary>Runs a launch configuration under the debugger of its type, as F5 does, until its session ends: what it printed.</summary>
    public Func<DebugLaunch, Task<string>>? DebugAsync { get; init; }

    /// <summary>
    /// Starts a launch configuration under the debugger of its type (an attach to the test host vstest started), from any
    /// thread: done once the session runs, its breakpoints set; it throws when the session did not start.
    /// </summary>
    public Func<DebugLaunch, Task>? StartDebuggingAsync { get; init; }
}

/// <summary>
/// A control whose keys come before Karvon's (Bengine Data Studio's keymap in the Database Explorer): while the focus is
/// in it, the window leaves its keys to it.
/// </summary>
public interface IKeymapOwner;

/// <summary>
/// A part of Karvon Studio that can be turned off in Tools > Get Tools and Features (and, later, installed on its own). Karvon knows a
/// module only through this: what it adds to the window (tool windows; how its documents and dialogs look) and what it
/// does when Karvon starts and closes. A module uses Karvon through the <see cref="IWorkbench"/> it is started with.
/// </summary>
public abstract partial class KarvonModule : ObservableObject, IDisposable, IAsyncDisposable
{
    /// <summary>For the settings ("mcp"): which modules are off.</summary>
    public abstract string Id { get; }

    public abstract string Name { get; }

    /// <summary>What it does, for Tools > Get Tools and Features.</summary>
    public abstract string Description { get; }

    /// <summary>The Material icon that stands for it (its workload's card in the installer: "csharp", "docker"); none by default.</summary>
    public virtual string? Icon => null;

    /// <summary>Its item in the status bar, or none (see <see cref="ModuleStatusItem"/>).</summary>
    public virtual ModuleStatusItem? StatusItem => null;

    public virtual IReadOnlyList<ToolWindowInfo> ToolWindows => [];

    /// <summary>How its documents look in the document well: a template for each of its document types.</summary>
    public virtual IReadOnlyList<IDataTemplate> DocumentTemplates => [];

    /// <summary>What it shows over the whole window (its dialogs and messages), once it has something to show.</summary>
    [ObservableProperty]
    public partial object? Overlay { get; protected set; }

    /// <summary>How <see cref="Overlay"/> looks.</summary>
    public virtual IDataTemplate? OverlayTemplate => null;

    /// <summary>The language servers it brings: one starts when a file it serves opens in the editor.</summary>
    public virtual IReadOnlyList<LanguageServerInfo> LanguageServers => [];

    /// <summary>The TextMate grammars of the languages it brings that the editor has none for (Kotlin's).</summary>
    public virtual IReadOnlyList<Editor.SyntaxGrammar> Grammars => [];

    /// <summary>
    /// How a folder's packages of its language get installed, or null: none of its there, or its tools install them
    /// themselves (Roslyn asks for a restore, cargo and Gradle download what the build needs).
    /// </summary>
    public virtual PackageRestore? PackageRestoreIn(string folder) => null;

    /// <summary>Whether a problem its language server reports is an import it cannot resolve (Ctrl+. offers its package restore).</summary>
    public virtual bool IsUnresolvedImport(Lsp.LspDiagnostic diagnostic) => false;

    /// <summary>
    /// The tasks it finds in a folder (its package.json scripts...): read when the folder opens and when one of
    /// <see cref="WorkspaceTaskFiles"/> is saved. Quick and without side effects: it reads files, it runs nothing.
    /// </summary>
    public virtual IReadOnlyList<WorkspaceTask> WorkspaceTasks(string workspace) => [];

    /// <summary>
    /// The projects of its workload it finds in a folder (its package.json folders): in a solution, Solution Explorer shows
    /// those that no .NET project holds beside the solution's. Quick and without side effects, as the tasks are.
    /// </summary>
    public virtual IReadOnlyList<WorkspaceProject> WorkspaceProjects(string workspace) => [];

    /// <summary>
    /// Create Git Repository's .gitignore, when the workspace has none, for what is its own in it (.NET's: the SDK's
    /// dotnet new gitignore): whether it wrote one.
    /// </summary>
    public virtual Task<bool> AddGitIgnoreAsync(SolutionInfo solution) => Task.FromResult(false);

    /// <summary>
    /// How F5 and Ctrl+F5 start a solution's startup project of its kind: a launch configuration of one of its debuggers
    /// (.NET's: netcoredbg's "dotnet", the project with the launch profile and build configuration chosen on the toolbar).
    /// Null for another's project.
    /// </summary>
    public virtual DebugLaunch? StartupLaunch(ProjectInfo project, string? launchProfile, string configuration) => null;

    /// <summary>
    /// The packages of a project of its language in a folder (npm's of a package.json folder), or null: Solution
    /// Explorer shows them in the folder's Dependencies. Quick: it looks for the project's manifest.
    /// </summary>
    public virtual Packages.PackageSource? PackagesIn(string folder) => null;

    /// <summary>The file names its tasks come from ("package.json"): saving one reads the tasks again.</summary>
    public virtual IReadOnlyList<string> WorkspaceTaskFiles => [];

    /// <summary>The folders of the tools its tasks use (Node.js's bin...): first on the PATH of every task.</summary>
    public virtual IEnumerable<string> ToolDirectories => [];

    /// <summary>What it downloads when it is first needed (its language server, its debugger): Tools > Get Tools and Features installs and removes them.</summary>
    public virtual IReadOnlyList<ModuleComponent> Components => [];

    /// <summary>
    /// The test frameworks of a folder it knows (Vitest in a package.json's dependencies): Test Explorer finds and runs
    /// their tests. Read when the folder's tasks are; quick and without side effects, as they are: a framework runs
    /// nothing until Test Explorer asks it to (Run All Tests, Refresh).
    /// </summary>
    public virtual IReadOnlyList<Testing.TestFramework> TestFrameworks(TestFrameworkStart start) => [];

    /// <summary>
    /// What it adds to Solution Explorer's menu of a node (a project's Manage NuGet Packages...): each item after the
    /// item of Karvon's its After names, or at the end of the submenu its In names, the rest at the end of Karvon's own
    /// (before Open in Terminal, Copy Full Path...).
    /// </summary>
    public virtual IReadOnlyList<ModuleMenuItem> ExplorerMenu(ExplorerNode node) => [];

    /// <summary>Its items of the main menu (Tools > NuGet Package Manager, Tools > Trust ASP.NET Core Development Certificate).</summary>
    public virtual IReadOnlyList<ModuleMainMenuItem> MainMenus => [];

    /// <summary>Whether it opens a workspace file (.NET's: a .sln, a .slnx, a project file); the core opens folders.</summary>
    public virtual bool OpensWorkspace(string path) => false;

    /// <summary>A workspace file it opens, read: the solution, its projects, and Solution Explorer's tree of them.</summary>
    public virtual Task<SolutionInfo> OpenWorkspaceAsync(string path, CancellationToken ct) =>
        Task.FromException<SolutionInfo>(new NotSupportedException($"{Name} opens no {Path.GetExtension(path)} file."));

    /// <summary>
    /// How it builds a solution that is its (.NET's: dotnet build of a .sln, a .slnx or a project): Build, Rebuild, Clean,
    /// Pack and Publish run it, its output in Output (Build), its errors in the Error List. Null for a solution, or a
    /// verb, that is not its.
    /// </summary>
    public virtual BuildCommand? BuildSolution(SolutionInfo solution, BuildRequest request) => null;

    /// <summary>Its Add New Item templates (C#'s class, interface...), before those that are no language's.</summary>
    public virtual IReadOnlyList<ItemTemplate> ItemTemplates => [];

    /// <summary>
    /// Its project templates listed later, the first time New Project shows its templates (.NET's come from the SDK,
    /// which takes a while): <see cref="ProjectTemplates"/> unless it overrides it.
    /// </summary>
    public virtual Task<IReadOnlyList<Templates.ProjectTemplate>> LoadProjectTemplatesAsync(CancellationToken ct) => Task.FromResult(ProjectTemplates);

    /// <summary>
    /// Its project templates (a Go module, a Cargo package...), in New Project; those that add to a solution
    /// (<see cref="Templates.ProjectTemplate.AddToSolution"/>) in Add > New Project too.
    /// </summary>
    public virtual IReadOnlyList<Templates.ProjectTemplate> ProjectTemplates => [];

    /// <summary>The debuggers it brings: F5 starts the one of a launch configuration's type.</summary>
    public virtual IReadOnlyList<DebuggerInfo> Debuggers => [];

    /// <summary>Where Settings Sync can keep the settings besides a folder (the Pipelines module's: a secret GitHub gist).</summary>
    public virtual IReadOnlyList<Services.SettingsSyncTarget> SettingsSyncTargets => [];

    /// <summary>The workspace's machine when it is a copy of a folder there (its tasks, builds and terminal run there); null when it is here.</summary>
    public virtual RemoteWorkspace? RemoteWorkspaceOf(string workspace) => null;

    /// <summary>What the module finds out about a process of the program running (Diagnostic Tools: a memory snapshot, a CPU profile).</summary>
    public virtual IReadOnlyList<Diagnostics.ProcessDiagnostic> ProcessDiagnostics => [];

    /// <summary>The tools it gives the Claude Code sessions Karvon starts (mcp__karvon__*): new ones for each session.</summary>
    /// <summary>
    /// A pane of its own beside the editor of a file it takes (null for the others), as the HTTP client's response is
    /// beside a .http file; it may add margins to the editor too (<see cref="Editor.EditorHost.AddMargin"/>).
    /// </summary>
    public virtual Editor.EditorPane? EditorPane(Editor.EditorHost host) => null;

    /// <summary>
    /// Inline completions (an AI's suggestion in gray after the caret as the user types; Tab puts it in): the text to put in
    /// at the caret of a request, or null for none. The first module's that gives them serves every editor; null for none.
    /// </summary>
    public virtual Func<Editor.InlineCompletionRequest, CancellationToken, Task<string?>>? InlineCompletions => null;

    public virtual IEnumerable<IClaudeTool> ClaudeTools() => [];

    /// <summary>The shell commands its tools do: Claude is sent to the tools instead (see <see cref="ClaudeShellRule"/>).</summary>
    public virtual IEnumerable<ClaudeShellRule> ClaudeShellRules() => [];

    /// <summary>
    /// A call of one of its tools ran (on the UI thread): what it did, shown where the user would have done it (a
    /// container's logs on its page). It runs unless the user turned that off.
    /// </summary>
    public virtual Task ShowClaudeToolAsync(string tool, JsonElement input, ClaudeToolOutput output) => Task.CompletedTask;

    /// <summary>Karvon, from <see cref="Start"/> on.</summary>
    protected IWorkbench Workbench { get; private set; } = null!;

    public bool IsStarted { get; private set; }

    /// <summary>Karvon has started with the module on (on the UI thread).</summary>
    public void Start(IWorkbench workbench)
    {
        Workbench = workbench;
        IsStarted = true;
        Started();
    }

    protected virtual void Started()
    {
    }

    /// <summary>Karvon closes: what the module started stops.</summary>
    public virtual void Dispose()
    {
    }

    /// <summary>Karvon closes, for a module that stops something that takes a while (Claude Code's CLI).</summary>
    public virtual ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>The solution changed (opened, closed, another): awaited, after what is the solution's has been read.</summary>
    public virtual Task WorkspaceChangedAsync() => Task.CompletedTask;

    /// <summary>The solution's state is written: what the module keeps of it, by its own keys ("claude.conversation").</summary>
    public virtual void SaveWorkspaceState(IDictionary<string, string> state)
    {
    }

    /// <summary>
    /// The solution closes (another opens, or none): what the module keeps of it closes. False keeps the solution open
    /// (the user cancelled, say over a transaction still open).
    /// </summary>
    public virtual Task<bool> CloseWorkspaceAsync() => Task.FromResult(true);

    /// <summary>The window closes: the module keeps what is to come back next time (a tree, tabs).</summary>
    public virtual void SaveState()
    {
    }

    /// <summary>A tool window's view is to be made again: the window drops it, and makes it again when it shows.</summary>
    public event Action<string>? ToolWindowReset;

    protected void ResetToolWindow(string id) => ToolWindowReset?.Invoke(id);

    /// <summary>
    /// The folder a Solution Explorer node stands for that has a manifest in it (package.json, Cargo.toml): a folder's,
    /// the folder opened's, or the manifest's own; null for another node.
    /// </summary>
    protected static string? FolderWith(Workspace.ExplorerNode node, string manifest)
    {
        var dir = node.Kind switch
        {
            Workspace.NodeKind.Folder => node.Path,
            Workspace.NodeKind.Solution when node.IsFolderRoot => node.Path,
            Workspace.NodeKind.File when node.Name == manifest => Path.GetDirectoryName(node.Path),
            _ => null,
        };
        return dir is not null && File.Exists(Path.Combine(dir, manifest)) ? dir : null;
    }

    /// <summary>A view of <typeparamref name="T"/>, and of nothing else (FuncDataTemplate&lt;T&gt; takes null for any T: a template of the application's would show in every empty presenter).</summary>
    protected static IDataTemplate Template<T>(Func<T, Control> view) where T : class =>
        new FuncDataTemplate(data => data is T, (data, _) => view((T)data!));

    /// <summary>A document of the module as Karvon's documents look: under the document well's blue line.</summary>
    protected static IDataTemplate Document<T>(Func<T, Control> view) where T : class => Template<T>(document => new Border
    {
        Classes = { "docframe" },
        BorderBrush = new SolidColorBrush(Color.Parse("#007ACC")),
        BorderThickness = new Thickness(0, 2, 0, 0),
        Child = view(document),
    });
}
