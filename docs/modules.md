# Modules

A module is a part of Karvon Studio that can be turned off in **Tools > Get Tools and Features** (Karvon Studio Installer,
where it is a workload, its components individual components). Later, modules will also install
on their own (see the [roadmap](../ROADMAP.md)). Karvon knows a module only through `Karvon.Modules.KarvonModule`,
and a module uses Karvon only through `IWorkbench`.

Each module is a project of its own, referencing `Karvon.Core`. The core references no module: the application
(`src/Karvon`) gives it the modules it comes with in `BuiltInModules.Create()`. The modules so far are:
- Database Explorer (`src/Karvon.Database`);
- Docker Explorer (`src/Karvon.Docker`);
- Servers (`src/Karvon.Servers`);
- Jira (`src/Karvon.Jira`): Jira's REST API (Cloud's and Server's), its sites' secrets in the keyring, and Claude's Jira tools;
- Ports (`src/Karvon.Ports`): port forwarding through a jprq tunnel service (its multiplexing protocol, gob and all, in C#),
  a bottom tool window, the workbench's `ListeningUrls`, and Claude's tunnel tools;
- Pipelines (`src/Karvon.Pipelines`): GitHub Actions (through gh) and GitLab CI, a run's page, a status bar item, and
  Claude's pipeline tools;
- Pull Requests (`src/Karvon.PullRequests`, which uses the Pipelines' sign-in and APIs): GitHub's pull requests and
  GitLab's merge requests, a page each, a status bar item, and Claude's pull request tools;
- MCP Servers (`src/Karvon.Mcp`);
- Skills (`src/Karvon.Skills`, which uses MCP Servers);
- TypeScript and JavaScript (`src/Karvon.TypeScript`): a workload, so the example of one: a language server, its
  tasks (`package.json` scripts), a debugger (js-debug) and test frameworks (Vitest, Jest); and VS Code's HTML, CSS
  and JSON language servers;
- Python (`src/Karvon.Python`): Pyright's language server, debugpy and pytest, with the workspace's interpreter;
- Go (`src/Karvon.Go`): gopls, Delve, a module's go tasks (with Go's own problem matcher) and go test;
- Rust (`src/Karvon.Rust`): rust-analyzer, CodeLLDB (which builds a launch configuration's "cargo" first), cargo's tasks;
- C and C++ (`src/Karvon.Cpp`): clangd, CodeLLDB, CMake's and Make's builds; it and Rust share CodeLLDB (`src/Karvon.Lldb`);
- Java (`src/Karvon.Java`): jdtls, with java-debug in it (one jdtls for the editor and F5), Maven's and Gradle's builds,
  JUnit in Test Explorer (on jdtls's class path, by JUnit's console launcher);
- .NET (`src/Karvon.DotNet`): Roslyn (C# and Razor), the SDK's project templates, C#'s and Razor's Add New Item
  templates, Test Explorer's xUnit, NUnit and MSTest tests (vstest), netcoredbg (F5), dotnet run (Ctrl+F5), dotnet
  build, the development certificate, user secrets, the NuGet Package Manager and Solution Explorer's NuGet commands
  so far, and Roslyn's and netcoredbg's downloads as its components;
- Claude Code (`src/Karvon.Claude`).

The rest of .NET (the model of a solution and its projects, the startup project and its launch profiles) still lives
in the core and
moves to the .NET module one part at a time.

A module's resources are its assembly's (`avares://Karvon.Database/Assets/...`); the core's are
`avares://Karvon.Core/...` (its themes, `Themes/ExplorerStyles.axaml` for the explorers, fonts, the icon).

## Writing one

```csharp
public sealed partial class NotesModule : KarvonModule
{
    public override string Id => "notes";
    public override string Name => "Notes";
    public override string Description => "Notes kept beside the solution.";

    public override IReadOnlyList<ToolWindowInfo> ToolWindows =>
        [new("Notes", "Notes", "_Notes", "ToolExplorer", DockSide.Right, () => new NotesView { DataContext = EnsureExplorer() })];

    public override IReadOnlyList<IDataTemplate> DocumentTemplates =>
        [Document<NoteDocument>(document => new NoteView { DataContext = document })];

    protected override void Started() => Workbench.WorkspaceChanged += () => Explorer?.Refresh();
}
```

Put it in a project of its own that references `Karvon.Core`, then add it to `BuiltInModules.Create()` and to the
application's project references.

## What a module adds

| Member | What Karvon does with it |
|---|---|
| `ToolWindows` | Each tool window becomes a tab in its group (before Claude Code at the sides) and an item in the View menu. Its view is made the first time it shows: a module that is never opened reads nothing and starts nothing. |
| `DocumentTemplates` | How the module's documents look in the document well, in every window. `Document<T>` puts the view under the well's blue line. |
| `Overlay` and `OverlayTemplate` | The module's dialogs and messages over the whole window, once it has something to show. |
| `LanguageServers` | The language servers it brings (an `LspClient` each, for the files it serves). One starts for the workspace when the first of its files opens in the editor, serves every editor of its files, and stops with the workspace; one with `ForWorkspace` (.NET's Roslyn, for a solution) starts as a workspace it is for opens, and its progress is the status bar's IntelliSense (`LspClient.IsReady`, `Ready`). Servers that push diagnostics and servers that are asked for them both work; `RefreshDiagnostics` and `RefreshSemanticTokens` ask the editors of its files again. Tools > Restart Language Server restarts them all. A file has one server, the first that serves it; one with `Lints` (ESLint) is a linter, which starts beside it and only adds its diagnostics. `MayStartAsync` is asked before one starts for a workspace (ESLint's: whether the user lets it run the folder's code); no, and it stays off while the workspace is open. |
| `AddGitIgnoreAsync(solution)` | Create Git Repository's .gitignore, when the workspace has none, for what is its own in it (.NET's: the SDK's). |
| `StartupLaunch(project, launchProfile, configuration)` | How F5 and Ctrl+F5 start a solution's startup project of its kind: a launch configuration of one of its debuggers (.NET's: netcoredbg's `dotnet`, the project with the launch profile and the build configuration chosen on the toolbar). |
| `PackagesIn(folder)` | The packages of a project of its language in a folder (a `PackageSource`, or null): Solution Explorer shows them in the folder's Dependencies, by their groups, with Manage Packages... and Remove, as a .NET project's. |
| `WorkspaceTasks(workspace)` | The tasks it finds in a folder (`WorkspaceTask`: a program, its arguments, where it runs, and its kind). In a folder opened as it is, Build runs the build task, Start Without Debugging the run task chosen on the toolbar, Run All Tests the test task; Build > Run Task runs any of them, in a solution too. `.vscode/tasks.json`'s come first; a module's of the same name is left out. |
| `WorkspaceProjects(workspace)` | The projects of its workload it finds in a folder (`WorkspaceProject`: a name, its folder, the file that makes it one). In a solution, Solution Explorer shows those that no .NET project holds beside the solution's projects, with that file's icon (a frontend's `package.json` folder). |
| `WorkspaceTaskFiles` | The file names its tasks come from (`package.json`): saving one reads the tasks again. |
| `ToolDirectories` | The folders of the tools its tasks use (Node.js's bin when Karvon's PATH has none): first on every task's PATH. |
| `Components` | What it downloads the first time it is needed (`ModuleComponent`: a name, a version, its folder in Karvon's data folder, whether it is there, how it installs, about how much disk it takes). The installer lists them as individual components: installed at once, removed when Karvon Studio starts again (it may be running), downloaded when the module is turned on. |
| `Icon` | The Material icon of its workload's card in the installer (`"csharp"`, `"docker"`). |
| `TestFrameworks(start)` | The test frameworks of a folder it knows (`TestFramework`: Vitest in a package that depends on it, .NET's vstest in a solution). Test Explorer finds their tests (`TestItem`: an id, the rows above it, where it is), runs them and shows the results of all of them together; a framework runs nothing until Test Explorer asks it to, and one that is not `IsAvailable` (a solution with no test project yet) is left out. To debug, `start.DebugAsync` runs a launch configuration to its end, and `start.StartDebuggingAsync` starts one (an attach to a test host) and returns once it runs. |
| `ProjectTemplates`, `LoadProjectTemplatesAsync(ct)` | Its new projects, in New Project in the modules' order (`LoadProjectTemplatesAsync` when they take a while to list: .NET's are the SDK's). A folder template (`ProjectTemplate.OfModule`: a Go module, a Cargo package...) makes its project folder with its tools or its files (`TemplateSteps`) in `Create`, and that folder opens. One that `MakesSolution` (dotnet new's) makes the solution around its project, which opens; one with `AddToSolution` is in Add > New Project too, and returns the project files the solution adds. |
| `ExplorerMenu(node)` | What it adds to Solution Explorer's context menu of a node (`ModuleMenuItem`: a header, what it runs or the `Items` of its submenu, whether it is enabled; one with neither is a separator). An item whose `After` names one of Karvon's items ("Run Tests") goes after it, with the others of the same `After` in the order given; one whose `In` names a submenu of Karvon's ("Add") goes at its end; the rest go after Karvon's own items, before Open in Terminal and Copy Full Path. `Workbench.ProjectOf(node)` is the project a node is under. |
| `MainMenus` | Its items of the main menu (`ModuleMainMenuItem`: the top menu, and the item: Tools > NuGet Package Manager, a submenu): after the item of Karvon's its `After` names (its access key aside), else first in their top menu, a separator after them. |
| `OpensWorkspace(path)`, `OpenWorkspaceAsync(path, ct)` | The workspace files it opens (.NET's: a .sln, a .slnx, a project file), and how it reads one: the `SolutionInfo` (its projects, Solution Explorer's tree of them, and the `ISolutionEditor` that changes its file: Add > Existing Project, solution folders, solution items, Remove, Rename). File > Open, the recent list and the command line open those; the core opens folders. |
| `BuildSolution(solution, request)` | How it builds a solution that is its (`BuildCommand`: a program and its arguments, run in the solution's folder; how a line of its output says an error or a warning, MSBuild's format by default, and that a project was built). Build, Rebuild, Clean, Pack and Publish ask it, for the solution or one of its projects, with the toolbar's configuration; its output goes to Output (Build) and its errors to the Error List. .NET's is dotnet build. |
| `ItemTemplates` | Its Add New Item templates (`ItemTemplate`: C#'s class, interface...), before those that are no language's (a text file, JSON, Markdown). Its first is what Add New Item suggests in a project. |
| `Debuggers` | The debuggers it brings (`DebuggerInfo`: the launch configuration types it debugs, and how its `DebugAdapter` starts: a program on stdin and stdout, or a DAP server whose child sessions are connections of their own). F5 starts the one of the chosen launch configuration's type, with the configuration (its variables put in), which it may complete; `PrepareAsync` asks first what a start needs (a certificate trusted), and `RunWithoutDebuggingAsync` is the program Ctrl+F5 runs for a configuration, when that is no debugger's noDebug (dotnet run). A task it debugs carries its `DebugLaunch` in `WorkspaceTask.Debug`. A launch configuration's `serverReadyAction` opens the browser once the program listens. netcoredbg is the .NET module's, for `coreclr` and `dotnet` (a project and its launch profile, as F5 starts a solution's startup project). |
| `ClaudeTools()` | The tools it gives the Claude Code sessions Karvon starts (`mcp__karvon__*`). A module that is off gives none. |
| `StatusItem` | Its item in the status bar, before Git's (`ModuleStatusItem`: an icon, a few words, a tooltip, what a click does; hidden until `IsVisible`). |
| `ClaudeShellRules()` | The shell commands its tools do (`ClaudeShellRule.Command(name, pattern, use)`: a command at the start of the line or after `;`, `&&`, `\|`, sudo or `VAR=value`; or a test of its own, as the Servers' ssh to a known machine). A session's Bash call of one is denied once, `use` its reason: which tool to use. |
| `ShowClaudeToolAsync` | One of its tools ran: show what it did where the user would have done it (a container's logs on its page). |
| `Started()` | Runs once Karvon has started with the module on: subscribe to the workbench here. |
| `WorkspaceChangedAsync()` | The solution changed (opened, closed, another): awaited, once what is the solution's has been read. |
| `SaveWorkspaceState(state)` | The solution's state is written: keep what is to come back with it, by your own keys. Read it from `Workbench.WorkspaceState`. |
| `CloseWorkspaceAsync()` | The solution closes: close what is the solution's. Return false to keep it open (the user cancelled). |
| `ResetToolWindow(id)` | The tool window's view is stale (another solution's): the window drops it and asks `CreateView` again when it shows. `CreateView` may return null while there is nothing to show yet. |
| `SaveState()` | The window closes: keep what is to come back next time. |
| `Dispose()`, `DisposeAsync()` | Run when Karvon closes: stop what the module started. |

A tool window can have a `Gesture` that shows it from anywhere ("Ctrl+Alt+S"). A control that implements
`IKeymapOwner` gets its keys before the window's while the focus is in it. A document whose owner closes it overrides
`DocumentTab.CloseByOwner`.

Use `Template<T>` (or `Document<T>`) for templates, never `FuncDataTemplate<T>`. Avalonia's `FuncDataTemplate<T>`
also matches null, so one added to the application would take over every empty presenter.

## What Karvon offers a module

`IWorkbench` gives a module:
- the settings;
- the open folder, its changes, and the folder where Karvon keeps the solution's own things;
- the open solution and its projects, read again on request (a package added) and when that happened, the project
  of a Solution Explorer node, and Add New Item in a node's folder;
- the build configuration, and Build Solution;
- the document well: add, show, close and remove documents, the one in front, open a file at a line;
- the files the editor saves, and a file picker;
- the tool windows (and one beside Claude Code, so the conversation stays in view);
- a pane of its own in the Output window;
- the browser, the status bar, and a question with Yes and No;
- where the program Karvon runs listens (`ListeningUrls`, from its output; `ListeningChanged` when it starts, stops or
  says another);
- the solution's Git repositories and their branches (`Repositories`), `GitChanged` when Git's state is read again, and
  `GitPushed` after a push of Karvon's; a branch pushed as Git Changes pushes it (`PushAsync`), and a git command of
  its own run as Git Changes runs one (`RunGitAsync`: in Output, the status read again after);
- Restart Language Server (a linter let into the folder, or no longer);
- a command of its own run as the tasks run (`RunAsync`), and the Package Manager of one of its projects
  (`ManagePackagesAsync` with its `PackageSource`: the registry's search, a package's versions and details, the
  project's dependencies, and what adds and removes one: the package manager's command, or an edit of the manifest
  for one with no such command), as the NuGet one looks.

`Workbench.Service<T>()` gives a module what other parts of Karvon offer:
- `IClaudeService` (the Claude Code module):
  - its window, a prompt or @mentions in it;
  - the chosen account's folders, Karvon's tools, `claude mcp add`, the extra MCP servers the sessions get, and its
    changes;
  - one prompt answered without tools (`PrintAsync`, for Git Changes' commit message);
- `IDatabaseService` (the Database Explorer module): a data source added to it;
- another module, by its type.

It returns null for a part that is off, and a module keeps working without it.
