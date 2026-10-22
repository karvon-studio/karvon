# Modules

A module is a part of Karvon Studio that can be turned off in **Tools > Modules**. Later, modules will also install
on their own (see the [roadmap](../ROADMAP.md)). Karvon knows a module only through `Karvon.Modules.KarvonModule`,
and a module uses Karvon only through `IWorkbench`.

The modules so far are MCP Servers (`Mcp/McpModule.cs`) and Skills (`Skills/SkillsModule.cs`). The others still live in
`MainViewModel` and are moving over one by one.

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

Then add it to `ModuleCatalog.Create()`.

## What a module adds

| Member | What Karvon does with it |
|---|---|
| `ToolWindows` | Each tool window becomes a tab in its group (before Claude Code at the sides) and an item in the View menu. Its view is made the first time it shows: a module that is never opened reads nothing and starts nothing. |
| `DocumentTemplates` | How the module's documents look in the document well, in every window. `Document<T>` puts the view under the well's blue line. |
| `Overlay` and `OverlayTemplate` | The module's dialogs and messages over the whole window, once it has something to show. |
| `Started()` | Runs once Karvon has started with the module on: subscribe to the workbench here. |
| `Dispose()` | Runs when Karvon closes: stop what the module started. |

Use `Template<T>` (or `Document<T>`) for templates, never `FuncDataTemplate<T>`. Avalonia's `FuncDataTemplate<T>`
also matches null, so one added to the application would take over every empty presenter.

## What Karvon offers a module

`IWorkbench` gives a module:
- the settings;
- the open folder and its changes;
- the document well (show and close documents, open a file at a line);
- the tool windows, the browser and the status bar.

`Workbench.Service<T>()` gives a module what other parts of Karvon offer:
- `IClaudeService` (Claude Code): the chosen account's folders, Karvon's tools, `claude mcp add`, the extra MCP
  servers the sessions get, and its changes;
- another module, by its type.

It returns null for a part that is off, and a module keeps working without it.
