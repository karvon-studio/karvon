# Modules

A module is a part of Karvon Studio that can be turned off in **Tools > Modules**. Later, modules will also install
on their own (see the [roadmap](../ROADMAP.md)). Karvon knows a module only through `Karvon.Modules.KarvonModule`,
and a module uses Karvon only through `IWorkbench`.

Each module is a project of its own, referencing `Karvon.Core`. The core references no module: the application
(`src/Karvon`) gives it the modules it comes with in `BuiltInModules.Create()`. The modules so far are:
- Database Explorer (`src/Karvon.Database`);
- Docker Explorer (`src/Karvon.Docker`);
- Servers (`src/Karvon.Servers`);
- MCP Servers (`src/Karvon.Mcp`);
- Skills (`src/Karvon.Skills`, which uses MCP Servers);
- TypeScript and JavaScript (`src/Karvon.TypeScript`): a workload, so the example of one: a language server, its
  tasks (`package.json` scripts) and a debugger (js-debug);
- Claude Code (`src/Karvon.Claude`).

.NET still lives in the core and is to become a workload module.

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
| `LanguageServers` | The language servers it brings (an `LspClient` each, for the files it serves). One starts for the workspace when the first of its files opens in the editor, serves every editor of its files, and stops with the workspace. Servers that push diagnostics and servers that are asked for them both work. |
| `WorkspaceTasks(workspace)` | The tasks it finds in a folder (`WorkspaceTask`: a program, its arguments, where it runs, and its kind). In a folder opened as it is, Build runs the build task, Start Without Debugging the run task chosen on the toolbar, Run All Tests the test task; Build > Run Task runs any of them, in a solution too. `.vscode/tasks.json`'s come first; a module's of the same name is left out. |
| `WorkspaceProjects(workspace)` | The projects of its workload it finds in a folder (`WorkspaceProject`: a name, its folder, the file that makes it one). In a solution, Solution Explorer shows those that no .NET project holds beside the solution's projects, with that file's icon (a frontend's `package.json` folder). |
| `WorkspaceTaskFiles` | The file names its tasks come from (`package.json`): saving one reads the tasks again. |
| `ToolDirectories` | The folders of the tools its tasks use (Node.js's bin when Karvon's PATH has none): first on every task's PATH. |
| `Components` | What it downloads the first time it is needed (`ModuleComponent`: a name, a version, its folder in Karvon's data folder, whether it is there, how it installs). Tools > Modules lists them under the module, with Install (at once) and Remove (when Karvon Studio starts again, since it may be running). |
| `TestFrameworks(start)` | The test frameworks of a folder it knows (`TestFramework`: Vitest in a package that depends on it). Test Explorer finds their tests (`TestItem`: an id, the rows above it, where it is), runs them and shows the results beside .NET's; a framework runs nothing until Test Explorer asks it to. |
| `Debuggers` | The debuggers it brings (`DebuggerInfo`: the launch configuration types it debugs, and how its `DebugAdapter` starts: a program on stdin and stdout, or a DAP server whose child sessions are connections of their own). F5 starts the one of the chosen launch configuration's type, with the configuration (its variables put in), which it may complete. A task it debugs carries its `DebugLaunch` in `WorkspaceTask.Debug`. netcoredbg is the core's, for `coreclr`. |
| `ClaudeTools()` | The tools it gives the Claude Code sessions Karvon starts (`mcp__karvon__*`). A module that is off gives none. |
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
- the document well: add, show, close and remove documents, the one in front, open a file at a line;
- the files the editor saves, and a file picker;
- the tool windows (and one beside Claude Code, so the conversation stays in view);
- a pane of its own in the Output window;
- the browser and the status bar.

`Workbench.Service<T>()` gives a module what other parts of Karvon offer:
- `IClaudeService` (the Claude Code module):
  - its window, a prompt or @mentions in it;
  - the chosen account's folders, Karvon's tools, `claude mcp add`, the extra MCP servers the sessions get, and its
    changes;
  - one prompt answered without tools (`PrintAsync`, for Git Changes' commit message);
- `IDatabaseService` (the Database Explorer module): a data source added to it;
- another module, by its type.

It returns null for a part that is off, and a module keeps working without it.
