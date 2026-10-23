# Karvon Studio

A native IDE with Visual Studio's look and workflow, made to grow by modules: today C#, ASP.NET Core and Blazor on
Linux, TypeScript and JavaScript, Python and Go; more languages, macOS and Windows next (see the
[roadmap](ROADMAP.md)). Built with Avalonia; IntelliSense comes from Microsoft's Roslyn language server (the engine
Visual Studio uses), Razor included. Karvon Studio grew out of VSL, whose history this repository carries.

![Editing C# with IntelliSense, Solution Explorer and the build output](docs/screenshots/editor.png)

## Install

Download the [latest release](https://github.com/karvon-studio/karvon/releases/latest) (Linux x64; Karvon itself needs no .NET runtime):

```bash
sudo apt install ./karvon-studio_*_amd64.deb                                 # Debian/Ubuntu: `karvon` command and menu entry
chmod +x Karvon-Studio-*-x86_64.AppImage && ./Karvon-Studio-*-x86_64.AppImage   # any distribution (or unpack the .tar.gz)
```

From source, `./scripts/install.sh` publishes to `~/.local/share/karvon/app` and adds `karvon`. Then `karvon path/to/App.sln`
(a `.slnx` or `.csproj` works too), or just `karvon` for the start window.

Working on solutions needs the .NET 10 SDK. Karvon downloads `roslyn-language-server` and `netcoredbg` (both MIT) on first use,
TypeScript 7 (Apache-2.0, its own language server; no Node.js needed) when the first `.ts` or `.js` file opens, js-debug
(MIT, the JavaScript debugger of VS Code; it runs on Node.js) the first time F5 debugs JavaScript, and Pyright (MIT, Python's
language server; it runs on Node.js) when the first `.py` file opens; gopls and Delve are built with the user's Go
(`go install`) the first time they are needed.

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
| **Docker Explorer**: a tool window tree, Docker Desktop's tables as documents | **A container's page**: logs, inspect, mounts, a shell, live stats |

- **Editor**: C# and Razor IntelliSense, Quick Info, parameter info, squiggles, Ctrl+. quick actions and refactorings,
  Go To Definition / Implementation, Find All References, Rename, Format Document, outlining (Ctrl+M), Find in Files, zoom.
  Markdown files open with a live preview to the right or below (VS 2022's Markdown editor), following the editor's scrolling.
- **Solutions**: `.sln`, `.slnx` or a lone project file; Solution Explorer with VS file nesting, Dependencies and
  [Material Icon Theme](https://github.com/material-extensions/vscode-material-icon-theme) icons. Add New Item
  (Ctrl+Shift+A: class, interface, record, Razor component...), Existing Item, New Folder, New EditorConfig, Rename in
  place (F2) and Delete; it follows files changed on disk. The solution's own menu as VS's: Add > New Project (the new
  project wizard), Existing Project, New Solution Folder, solution items (New / Existing Item, listed in Solution Items),
  Remove (Del) and Rename for projects (the project file and the references to it), solution folders, solution items
  and the solution itself; Run Tests, Restore NuGet Packages, Set Startup Project, Create Git Repository, Edit
  Solution File. On projects: Pack, Publish to Folder, Add > Project Reference (Reference Manager), Manage User
  Secrets; in Dependencies, Remove for packages and project references. A solution or project file changed elsewhere
  (an editor, git, `dotnet sln`) is read again, keeping what is expanded.
- **Folders** (File > Open Folder): any folder as it is, built, run and tested by its tasks: `.vscode/tasks.json`'s (shell,
  process and npm tasks, their build and test groups) and its `package.json` scripts (npm, pnpm, yarn or bun). Build
  (Ctrl+Shift+B) runs the build task with its errors in the Error List, Run All Tests the test task; Build > Run Task
  runs any of them, a watch beside the rest. The toolbar chooses what starts: a `.vscode/launch.json` configuration (F5
  debugs it with the debugger of its type, after its `preLaunchTask`; Ctrl+F5 runs it without) or a run task (dev,
  start...: Ctrl+F5 runs it, and F5 debugs a `package.json` script with every node process it starts). A solution shows
  the `package.json` folders beside its projects (a frontend) in Solution Explorer, their scripts in Run Task; Start with
  the Solution on one runs its dev server beside the startup project, as VS's multiple startup projects do.
- **Build and run**: Build / Rebuild / Clean, Error List, launch profiles, Ctrl+F5; offers to trust the ASP.NET Core
  HTTPS development certificate.
- **Debug** (F5; netcoredbg for .NET, js-debug for Node.js and TypeScript through its source maps): breakpoints that
  follow edits, Step Over / Into / Out, Locals, Watch, Call Stack, data tips, debugging tests.
- **Test Explorer**: discover, Run All / Failed / Selected, theory rows, durations, failures with stack traces; .NET's
  tests (xUnit, NUnit, MSTest), Vitest's and Jest's (by the package's own copy) and pytest's (by the workspace's .venv), run
  or debugged.
- **Git**: every repository of the solution in one Changes window (trees of staged and unstaged changes), stage,
  commit (a message Claude writes from the diff, if you like), fetch, pull, push, sync, local and remote branches, diff
  with word highlights, change bars in the editor margin, badges in Solution Explorer; follows commits and checkouts
  made in the terminal.
- **Database Explorer** (Ctrl+Alt+S): [Bengine Data Studio](https://github.com/nbkabdulakhadov/bengine) inside Karvon —
  PostgreSQL, MySQL/MariaDB, SQL Server, SQLite, Oracle and MongoDB: object tree, table data with staged edits, SQL
  consoles with completion and explain plans, DDL, table designer, ER diagrams, schema and data compare, backup and
  restore, import and export, server activity. Each solution has its own: data sources, consoles, open tabs, tree
  and history are kept in its `.vs` folder and come back only with it; with no solution open it shows Studio's list.
- **Docker Explorer** (View > Docker Explorer): a tool window, as the Database Explorer, through your `docker` (or
  `podman`) command line: a tree of containers (compose projects as stacks, the solution's first), images, volumes and
  networks, with a context menu for each. What it opens is a document. Docker Desktop's tables: containers with CPU
  and memory, search, only running, start, stop, restart, pause and delete (one or ticked together); images (pull,
  run with ports, volumes and variables, clean up), volumes and networks. A container's page: its logs as they come
  (colors, timestamps, search), inspect, bind mounts, a shell in it and live stats charts. It follows the engine's
  events, says why when the engine is not running, and knows the solution: its stack first, Compose up / down for
  its compose file and what the file gets wrong (a service that reaches another through `localhost`), database
  containers opened in the Database Explorer, Ask Claude why a container fails (with its last log lines, secrets
  hidden).
- **Servers** (View > Servers): the machines of `~/.ssh/config` (Include, ProxyJump and wildcards as OpenSSH reads
  them) and those you add, in groups, with whether Karvon is connected and how fast each answers. A machine's page: its
  state (system, uptime, CPU, memory, disks), a terminal (your `ssh`), its files (upload by dropping them, open one in
  the editor and saving puts it back, asking when it changed there), its systemd services (start, stop, restart) and
  its logs (the journal, a unit's or a file's, followed as they come, secrets hidden), Ask Claude about them. A new
  host key is shown to compare before it is trusted; passwords and passphrases are asked once and can go to the system
  keyring; a production machine asks before every change, yours and Claude's.
- **MCP Servers** (View > MCP Servers): the MCP servers of every agent's configuration in one tree, this folder's
  `.mcp.json`, Claude Code's (every project's and this folder's), Codex's `config.toml` and Gemini CLI's, with Karvon's own
  tools first. A server connects when you open it (never at start) and shows its tools, resources and prompts; its page
  runs a tool with a form made from its schema, reads a resource, gets a prompt, and logs everything said (its stderr
  too). Add and edit servers (a program, with its environment, or a URL, with its headers) in each file's own format,
  keeping the rest of it; `${VAR}` stays as written. The Claude Code sessions Karvon starts get the servers you switch on
  for them, beside those Claude Code reads itself.
- **Skills** (View > Skills): the skills Claude Code loads (the folder's `.claude/skills`, yours, the plugins'), each
  with its checks (the name matches its folder and the rules, the description says when to use it, linked files exist,
  scripts can run), "Try a prompt" (whether a prompt gives an agent a reason to load it) and its files; New Skill from a
  template. The subagents, commands and hooks of the folder and yours. AGENTS.md and GEMINI.md written from CLAUDE.md
  (with the folder's skills, which those agents do not load themselves) and kept in step, never over a file written by
  hand without asking. A marketplace of skills from GitHub repositories, teams' registries and
  [Agensi](https://www.agensi.io) (chosen by source, searched, free or paid) shows what a skill asks for (the tools it
  allows, its scripts and their text, the network) before it is installed, updated or removed; Agensi's skills are
  licensed to run through Agensi, so "Use in Claude Code" gives Claude Code its MCP server and the skill loads on demand
  (a paid one is bought only when you agree).
- **Terminal**: your `$SHELL` in a real pty, in the solution folder.
- **NuGet**: browse, install, update and uninstall across your configured feeds.
- **Claude Code** (Ctrl+Esc): drives your installed `claude` CLI, so your login, settings and MCP servers apply.
  Streamed answers, tool calls with inline diffs, approvals, questions and plans, modes (Bypass permissions among
  them) and effort, usage, MCP servers, images, `/` commands and `@` files, past conversations, conversations that
  keep working in the background, Remote Control (go on from claude.ai or the Claude app), Ask Claude from Solution
  Explorer; English or O‘zbekcha. Old conversations open at their last message, compaction summaries and long
  prompts fold.
  Claude gets Karvon's own tools (`mcp__karvon__*`, an MCP server inside Karvon): the build and the tests (`run_build`,
  `build_errors`, `run_tests`, `test_failures`), running the app as Ctrl+F5 does (`app_control`: start, stop, restart,
  its URLs and output), the debugger where it stopped (`debug_state`, `debug_locals`, `debug_evaluate`,
  `debug_control`), the editor (`editor_selection`, `open_document`, `code_diagnostics`,
  `find_in_files`), the Database Explorer's data sources (`db_connections`, `db_schema`, `db_query`, `db_explain`),
  the solution's containers (`container_list`, `container_logs`, `container_inspect`, `container_restart`,
  `compose_up`, `container_remove`) and the Servers (`server_list`, `server_logs`, `server_exec`), with your own
  connections and passwords, which it never sees. Reading, building,
  testing and stepping run at once; a change asks first, one call at a time (destructive SQL says so), read-only data
  sources refuse changes, and secrets in logs and variables are hidden.
- **Layout**: tool windows at the sides as VS Code's activity bar (an icon each, Git Changes with a badge; click the
  icon in front to fold the side), drag them to another place on the bar, to dock or to float them; tab groups (Window > New Vertical / Horizontal Tab Group),
  Window > Split, floating documents, tabs dragged to reorder; documents, groups, layout and startup project are
  restored per solution.
  Keyboard shortcuts follow Visual Studio (Help > Keyboard Shortcuts).

Not yet: conditional breakpoints, exception settings, Immediate window, debugging Blazor WebAssembly in the browser,
Fix All, history / blame, merge conflict editor, WinForms / WPF designers.

## Claude Code for Visual Studio (Windows)

`src/Karvon.VisualStudio` is Karvon's Claude Code panel as a Visual Studio 2022 (17.14+) / 2026 extension: the same CLI
session, protocol and view model (shared with Karvon, not copied), with a WPF view that follows the Visual Studio theme.
What it does and how to use it is in [its README](src/Karvon.VisualStudio/README.md), which is also its Marketplace page.

The `vsix` workflow builds `Karvon.ClaudeCode.VisualStudio.vsix` on Windows for every change to the extension or the shared
Claude code (download it from the run's artifacts; open it to install). Running the workflow by hand with **publish**
ticked uploads it to the Visual Studio Marketplace; raise the version in `source.extension.vsixmanifest` first. Locally
it builds with Visual Studio's MSBuild (not `dotnet build`); the project is not in `Karvon.slnx`, so the Linux build is
unchanged:

```powershell
msbuild src/Karvon.VisualStudio/Karvon.VisualStudio.csproj -restore -p:Configuration=Release
```

## Development

The Database Explorer's engine is the [bengine](https://github.com/nbkabdulakhadov/bengine) repository, a submodule:
clone with `--recursive` (or run `git submodule update --init` in a clone).

```bash
dotnet build Karvon.slnx
dotnet tests/Karvon.Tests/bin/Debug/net10.0/Karvon.Tests.dll      # unit, Roslyn integration and headless UI tests
KARVON_SCREENSHOTS=$PWD/docs/screenshots dotnet tests/Karvon.Tests/bin/Debug/net10.0/Karvon.Tests.dll -class Karvon.Tests.Screenshots
```

The last command renders the screenshots above. CI runs the tests on every push. A `vX.Y.Z` tag publishes a release with
the `.deb`, `.AppImage` and `.tar.gz`, which `deploy/linux/package.sh` also builds locally.

```
src/Karvon/             the application: the core and the modules it comes with (BuiltInModules.cs)
src/Karvon.Core/        the workbench; it knows no module
  Modules/     the module contract: KarvonModule, IWorkbench (see docs/modules.md)
  Ui/          what the modules' views share: overlay dialogs, read-only editors
  Lsp/         JSON-RPC, Roslyn client, file watching, HTML service for Razor cohosting
  Workspace/   solution and project loading, Solution Explorer tree
  Build/       dotnet runner, MSBuild output parser, dev certificates
  Debugging/   DAP client and sessions (child sessions too), netcoredbg
  Testing/     vstest host
  Git/         git CLI wrapper and line diff
  Packages/    NuGet through the dotnet CLI
  Terminal/    integrated terminal
  Claude/      the tool contract and Karvon's IDE tools for Claude Code, the panel's language
  Templates/   SDK project templates and project creation
  Editor/      AvaloniaEdit host, completion, outlining, diff view
  ViewModels/  Views/   main window, tool windows, dialogs
src/Karvon.Database/    module: the Database Explorer (Bengine Data Studio's views and view models, on Avalonia 12)
src/Karvon.Docker/      module: the Docker Explorer
src/Karvon.Servers/     module: SSH servers
src/Karvon.Mcp/         module: MCP Servers
src/Karvon.Skills/      module: Skills (uses Karvon.Mcp)
src/Karvon.TypeScript/  module: TypeScript and JavaScript (TypeScript 7's language server, package.json scripts, js-debug, Vitest, Jest)
src/Karvon.Python/      module: Python (Pyright's language server, debugpy, pytest, the workspace's .venv)
src/Karvon.Go/          module: Go (gopls, Delve, go build/test/vet/run, go test in Test Explorer)
src/Karvon.Claude/      module: Claude Code (its session, protocol and view model are shared with the Visual Studio extension)
external/bengine/       Bengine (submodule): the schema AST and the live-database layer the Database Explorer uses
src/Karvon.VisualStudio/   the Claude Code panel as a Visual Studio extension (Windows), sharing src/Karvon.Claude's session and view model
```

## License

MIT, see [LICENSE](LICENSE). Third-party: Cascadia Mono font (SIL OFL 1.1); Avalonia, AvaloniaEdit, TextMateSharp,
Exclr8.Terminal and Porta.Pty (MIT); Markdig (BSD 2-Clause); roslyn-language-server, netcoredbg, js-debug, Pyright and debugpy (MIT, downloaded at runtime);
Material Icon Theme icons (MIT, Copyright (c) 2025 Material Extensions; `src/Karvon.Core/Assets/MaterialIcons`, refreshed with
`scripts/update-material-icons.sh`, and `src/Karvon.Database/Assets/Icons`). The Database Explorer uses Bengine (same
author) and its drivers: Npgsql (PostgreSQL License), Microsoft.Data.SqlClient, MySqlConnector, SSH.NET and
Avalonia.Controls.DataGrid (MIT), MongoDB.Driver and SQLitePCLRaw (Apache 2.0), Oracle.ManagedDataAccess.Core (Oracle
Free Use Terms and Conditions).
