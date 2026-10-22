# VSL — Visual Studio for Linux

A native Linux IDE for C#, ASP.NET Core and Blazor with Visual Studio's look and workflow. Built with Avalonia;
IntelliSense comes from Microsoft's Roslyn language server (the engine Visual Studio uses), Razor included.

![Editing C# with IntelliSense, Solution Explorer and the build output](docs/screenshots/editor.png)

## Install

Download the [latest release](https://github.com/nbkabdulakhadov/VSL/releases/latest) (Linux x64; VSL itself needs no .NET runtime):

```bash
sudo apt install ./vsl_*_amd64.deb                          # Debian/Ubuntu: `vsl` command and menu entry
chmod +x VSL-*-x86_64.AppImage && ./VSL-*-x86_64.AppImage   # any distribution (or unpack the .tar.gz)
```

From source, `./scripts/install.sh` publishes to `~/.local/share/VSL/app` and adds `vsl`. Then `vsl path/to/App.sln`
(a `.slnx` or `.csproj` works too), or just `vsl` for the start window.

Working on solutions needs the .NET 10 SDK. VSL downloads `roslyn-language-server` and `netcoredbg` (both MIT) on first use.

## Features

| | |
|---|---|
| ![Start window](docs/screenshots/start-window.png) | ![New project](docs/screenshots/new-project.png) |
| **Start window**: recent solutions, clone, open a folder or a project | **New project** from every installed `dotnet new` template, with VS's options |
| ![Debugging](docs/screenshots/debugging.png) | ![Git](docs/screenshots/git.png) |
| **Debugging**: breakpoints, stepping, Locals, Watch, Call Stack | **Git**: Changes window, side-by-side diff, branches |
| ![Test Explorer](docs/screenshots/test-explorer.png) | ![Claude Code](docs/screenshots/claude-code.png) |
| **Test Explorer**: run and debug xUnit, NUnit and MSTest tests | **Claude Code** in a tool window, with diffs and approvals |
| ![Docker Explorer](docs/screenshots/docker-explorer.png) | ![A container's page](docs/screenshots/docker-container.png) |
| **Docker Explorer**: Docker Desktop's dashboard, the solution's compose stack first | **A container's page**: logs, inspect, mounts, a shell, live stats |

- **Editor**: C# and Razor IntelliSense, Quick Info, parameter info, squiggles, Ctrl+. quick actions and refactorings,
  Go To Definition / Implementation, Find All References, Rename, Format Document, outlining (Ctrl+M), Find in Files, zoom.
  Markdown files open with a live preview to the right or below (VS 2022's Markdown editor), following the editor's scrolling.
- **Solutions**: `.sln`, `.slnx` or a lone project file; Solution Explorer with VS file nesting, Dependencies and
  [Material Icon Theme](https://github.com/material-extensions/vscode-material-icon-theme) icons. Add New Item
  (Ctrl+Shift+A: class, interface, record, Razor component...), Existing Item, New Folder, Rename in place (F2) and
  Delete; it follows files changed on disk.
- **Build and run**: Build / Rebuild / Clean, Error List, launch profiles, Ctrl+F5; offers to trust the ASP.NET Core
  HTTPS development certificate.
- **Debug** (F5, netcoredbg): breakpoints that follow edits, Step Over / Into / Out, Locals, Watch, Call Stack, data tips,
  debugging tests.
- **Test Explorer**: discover, Run All / Failed / Selected, theory rows, durations, failures with stack traces.
- **Git**: every repository of the solution in one Changes window (trees of staged and unstaged changes), stage,
  commit (a message Claude writes from the diff, if you like), fetch, pull, push, sync, local and remote branches, diff
  with word highlights, change bars in the editor margin, badges in Solution Explorer; follows commits and checkouts
  made in the terminal.
- **Database Explorer** (Ctrl+Alt+S): [Bengine Data Studio](https://github.com/nbkabdulakhadov/bengine) inside VSL —
  PostgreSQL, MySQL/MariaDB, SQL Server, SQLite, Oracle and MongoDB: object tree, table data with staged edits, SQL
  consoles with completion and explain plans, DDL, table designer, ER diagrams, schema and data compare, backup and
  restore, import and export, server activity. Each solution has its own: data sources, consoles, open tabs, tree
  and history are kept in its `.vs` folder and come back only with it; with no solution open it shows Studio's list.
- **Docker Explorer** (View > Docker Explorer): Docker Desktop's dashboard in a document, through your `docker` (or
  `podman`) command line. Containers with compose projects as stacks, CPU and memory, search, only running, start,
  stop, restart, pause and delete (one or ticked together); a container's page with its logs as they come (colors,
  timestamps, search), inspect, bind mounts, a shell in it and live stats charts. Images (pull, run with ports,
  volumes and variables, clean up), volumes and networks. It follows the engine's events, says why when the engine
  is not running, and knows the solution: its stack first, Compose up / down for its compose file, database
  containers opened in the Database Explorer, Ask Claude why a container failed.
- **Terminal**: your `$SHELL` in a real pty, in the solution folder.
- **NuGet**: browse, install, update and uninstall across your configured feeds.
- **Claude Code** (Ctrl+Esc): drives your installed `claude` CLI, so your login, settings and MCP servers apply.
  Streamed answers, tool calls with inline diffs, approvals, questions and plans, modes (Bypass permissions among
  them) and effort, usage, MCP servers, images, `/` commands and `@` files, past conversations, conversations that
  keep working in the background, Remote Control (go on from claude.ai or the Claude app), Ask Claude from Solution
  Explorer; English or O‘zbekcha.
- **Layout**: drag tool windows to dock or float them; tab groups (Window > New Vertical / Horizontal Tab Group),
  Window > Split, floating documents, tabs dragged to reorder; documents, groups, layout and startup project are
  restored per solution.
  Keyboard shortcuts follow Visual Studio (Help > Keyboard Shortcuts).

Not yet: conditional breakpoints, exception settings, Immediate window, debugging Blazor WebAssembly in the browser,
Fix All, history / blame, merge conflict editor, WinForms / WPF designers.

## Claude Code for Visual Studio (Windows)

`src/VSL.VisualStudio` is VSL's Claude Code panel as a Visual Studio 2022 (17.14+) / 2026 extension: the same CLI
session, protocol and view model (shared with VSL, not copied), with a WPF view that follows the Visual Studio theme.
What it does and how to use it is in [its README](src/VSL.VisualStudio/README.md), which is also its Marketplace page.

The `vsix` workflow builds `VSL.ClaudeCode.VisualStudio.vsix` on Windows for every change to the extension or the shared
Claude code (download it from the run's artifacts; open it to install). Running the workflow by hand with **publish**
ticked uploads it to the Visual Studio Marketplace; raise the version in `source.extension.vsixmanifest` first. Locally
it builds with Visual Studio's MSBuild (not `dotnet build`); the project is not in `VSL.slnx`, so the Linux build is
unchanged:

```powershell
msbuild src/VSL.VisualStudio/VSL.VisualStudio.csproj -restore -p:Configuration=Release
```

## Development

The Database Explorer's engine is the [bengine](https://github.com/nbkabdulakhadov/bengine) repository, a submodule:
clone with `--recursive` (or run `git submodule update --init` in a clone).

```bash
dotnet build VSL.slnx
dotnet tests/VSL.Tests/bin/Debug/net10.0/VSL.Tests.dll      # unit, Roslyn integration and headless UI tests
VSL_SCREENSHOTS=$PWD/docs/screenshots dotnet tests/VSL.Tests/bin/Debug/net10.0/VSL.Tests.dll -class VSL.Tests.Screenshots
```

The last command renders the screenshots above. CI runs the tests on every push. A `vX.Y.Z` tag publishes a release with
the `.deb`, `.AppImage` and `.tar.gz`, which `deploy/linux/package.sh` also builds locally.

```
src/VSL/
  Lsp/         JSON-RPC, Roslyn client, file watching, HTML service for Razor cohosting
  Workspace/   solution and project loading, Solution Explorer tree
  Build/       dotnet runner, MSBuild output parser, dev certificates
  Debugging/   DAP client and netcoredbg
  Testing/     vstest host
  Git/         git CLI wrapper and line diff
  Packages/    NuGet through the dotnet CLI
  Terminal/    integrated terminal
  Claude/      Claude Code CLI session (stream-json) and transcripts
  Templates/   SDK project templates and project creation
  Editor/      AvaloniaEdit host, completion, outlining, diff view
  ViewModels/  Views/   main window, tool windows, dialogs
  Database/    the Database Explorer: Bengine Data Studio's views and view models, on Avalonia 12
external/bengine/       Bengine (submodule): the schema AST and the live-database layer the Database Explorer uses
src/VSL.VisualStudio/   the Claude Code panel as a Visual Studio extension (Windows), sharing src/VSL/Claude and its view model
```

## License

MIT, see [LICENSE](LICENSE). Third-party: Cascadia Mono font (SIL OFL 1.1); Avalonia, AvaloniaEdit, TextMateSharp,
Exclr8.Terminal and Porta.Pty (MIT); Markdig (BSD 2-Clause); roslyn-language-server and netcoredbg (MIT, downloaded at runtime);
Material Icon Theme icons (MIT, Copyright (c) 2025 Material Extensions; `src/VSL/Assets/MaterialIcons`, refreshed with
`scripts/update-material-icons.sh`, and `src/VSL/Database/Assets/Icons`). The Database Explorer uses Bengine (same
author) and its drivers: Npgsql (PostgreSQL License), Microsoft.Data.SqlClient, MySqlConnector, SSH.NET and
Avalonia.Controls.DataGrid (MIT), MongoDB.Driver and SQLitePCLRaw (Apache 2.0), Oracle.ManagedDataAccess.Core (Oracle
Free Use Terms and Conditions).
