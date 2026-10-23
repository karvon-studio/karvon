# Features

What Karvon Studio does, in detail; the [README](../README.md) has the short version.

## Languages

| Language | Language server | Debugger | Build and run | Tests |
|---|---|---|---|---|
| C#, ASP.NET Core, Blazor | Roslyn, Razor included | netcoredbg | `dotnet build`, launch profiles | xUnit, NUnit, MSTest |
| TypeScript, JavaScript | TypeScript 7, ESLint | js-debug (Node.js, Chrome, Edge) | `package.json` scripts | Vitest, Jest |
| HTML, CSS, SCSS, Less, JSON | VS Code's | | | |
| Python | Pyright | debugpy | the folder's `.venv`, uv | pytest |
| Go | gopls | Delve | `go build`, `vet`, `run` | `go test` |
| Rust | rust-analyzer | CodeLLDB | `cargo build`, `check`, `clippy`, `run` | `cargo test` |
| C, C++ | clangd | CodeLLDB | CMake, Make | CTest |
| Java | jdtls | java-debug | Maven, Gradle | JUnit 5 and 4 |

Working on solutions needs the .NET 10 SDK; each language needs its own toolchain (Node.js, Python, Go, Rust, a C and
C++ compiler, a JDK). The language servers and debuggers download the first time they are needed, and Tools > Modules
lists them: `roslyn-language-server` and `netcoredbg` (both MIT); TypeScript 7 (Apache-2.0, its own language server; no
Node.js needed); js-debug, VS Code's HTML, CSS and JSON servers and its ESLint server (MIT; they run on Node.js, and
ESLint runs a folder's own ESLint only where you let it); Pyright and debugpy (MIT); rust-analyzer, clangd and CodeLLDB;
jdtls, java-debug and JUnit's console launcher. gopls and Delve are built with your Go (`go install`).

## Editor

IntelliSense in each language by its language server (C# and Razor by Roslyn), Quick Info, parameter info, squiggles,
Ctrl+. quick actions and refactorings (a linter's fixes too: ESLint's), Go To Definition / Implementation, Find All
References, Rename, Format Document, outlining (Ctrl+M), Find in Files, zoom.
Markdown files open with a live preview to the right or below (VS 2022's Markdown editor), following the editor's scrolling.

## Solutions

`.sln`, `.slnx` or a lone project file; Solution Explorer with VS file nesting, Dependencies and
[Material Icon Theme](https://github.com/material-extensions/vscode-material-icon-theme) icons. Add New Item
(Ctrl+Shift+A: class, interface, record, Razor component...), Existing Item, New Folder, New EditorConfig, Rename in
place (F2) and Delete; it follows files changed on disk. The solution's own menu as VS's: Add > New Project (the new
project wizard), Existing Project, New Solution Folder, solution items (New / Existing Item, listed in Solution Items),
Remove (Del) and Rename for projects (the project file and the references to it), solution folders, solution items
and the solution itself; Run Tests, Restore NuGet Packages, Set Startup Project, Create Git Repository, Edit
Solution File. On projects: Pack, Publish to Folder, Add > Project Reference (Reference Manager), Manage User
Secrets; in Dependencies, Remove for packages and project references. A solution or project file changed elsewhere
(an editor, git, `dotnet sln`) is read again, keeping what is expanded.

## Folders (File > Open Folder)

Any folder as it is, built, run and tested by its tasks: `.vscode/tasks.json`'s (shell,
process and npm tasks, their build and test groups), its `package.json` scripts (npm, pnpm, yarn or bun) and its .NET
solution's or projects' (`dotnet build`, `dotnet test`, and `dotnet run` of each app by its launch profile, which F5
debugs after its build; Roslyn loads them, and Test Explorer has their tests), as each language's module finds them. Build
(Ctrl+Shift+B) runs the build task with its errors in the Error List, Run All Tests the test task; Build > Run Task
runs any of them, a watch beside the rest. The toolbar chooses what starts: a `.vscode/launch.json` configuration (F5
debugs it with the debugger of its type, after its `preLaunchTask`; Ctrl+F5 runs it without) or a run task (dev,
start...: Ctrl+F5 runs it, and F5 debugs a `package.json` script with every node process it starts). A solution shows
the `package.json` folders beside its projects (a frontend) in Solution Explorer, their scripts in Run Task, those of
its repository too (a backend's project opened with its frontend beside it); Start with the Solution on one runs its dev
server beside the startup project, as VS's multiple startup projects do.

## Build and run

Build / Rebuild / Clean, Error List, launch profiles, Ctrl+F5; offers to trust the ASP.NET Core
HTTPS development certificate.

## Debug

F5 debugs with the language's debugger (see Languages; TypeScript through its source maps): breakpoints that follow
edits, Step Over / Into / Out, Locals, Watch, Call Stack, data tips, debugging tests, several programs at once
(launch.json's compounds).

## Test Explorer

Discover, Run All / Failed / Selected, theory rows, durations, failures with stack traces; .NET's
tests (xUnit, NUnit, MSTest), Vitest's and Jest's (by the package's own copy), pytest's (by the workspace's .venv),
go test's, cargo test's, CTest's and JUnit's, run or debugged.

## Git

Every repository of the solution in one Changes window (trees of staged and unstaged changes), stage,
commit (a message Claude writes from the diff, if you like), fetch, pull, push, sync, local and remote branches, diff
with word highlights, change bars in the editor margin, badges in Solution Explorer; follows commits and checkouts
made in the terminal.

## Database Explorer (Ctrl+Alt+S)

[Bengine Data Studio](https://github.com/nbkabdulakhadov/bengine) inside Karvon —
PostgreSQL, MySQL/MariaDB, SQL Server, SQLite, Oracle and MongoDB: object tree, table data with staged edits, SQL
consoles with completion and explain plans, DDL, table designer, ER diagrams, schema and data compare, backup and
restore, import and export, server activity. Each solution has its own: data sources, consoles, open tabs, tree
and history are kept in its `.vs` folder and come back only with it; with no solution open it shows Studio's list.

## Docker Explorer (View > Docker Explorer)

A tool window, as the Database Explorer, through your `docker` (or
`podman`) command line: a tree of containers (compose projects as stacks, the solution's first), images, volumes and
networks, with a context menu for each. What it opens is a document. Docker Desktop's tables: containers with CPU
and memory, search, only running, start, stop, restart, pause and delete (one or ticked together); images (pull,
run with ports, volumes and variables, clean up), volumes and networks. A container's page: its logs as they come
(colors, timestamps, search), inspect, bind mounts, a shell in it and live stats charts. It follows the engine's
events, says why when the engine is not running, and knows the solution: its stack first, Compose up / down for
its compose file and what the file gets wrong (a service that reaches another through `localhost`), database
containers opened in the Database Explorer, Ask Claude why a container fails (with its last log lines, secrets
hidden).

## Servers (View > Servers)

The machines of `~/.ssh/config` (Include, ProxyJump and wildcards as OpenSSH reads
them) and those you add, in groups, with whether Karvon is connected and how fast each answers. A machine's page: its
state (system, uptime, CPU, memory, disks), a terminal (your `ssh`), its files (upload by dropping them, open one in
the editor and saving puts it back, asking when it changed there), its systemd services (start, stop, restart) and
its logs (the journal, a unit's or a file's, followed as they come, secrets hidden), Ask Claude about them. A new
host key is shown to compare before it is trusted; passwords and passphrases are asked once and can go to the system
keyring; a production machine asks before every change, yours and Claude's.

## MCP Servers (View > MCP Servers)

The MCP servers of every agent's configuration in one tree, this folder's
`.mcp.json`, Claude Code's (every project's and this folder's), Codex's `config.toml` and Gemini CLI's, with Karvon's own
tools first. A server connects when you open it (never at start) and shows its tools, resources and prompts; its page
runs a tool with a form made from its schema, reads a resource, gets a prompt, and logs everything said (its stderr
too). Add and edit servers (a program, with its environment, or a URL, with its headers) in each file's own format,
keeping the rest of it; `${VAR}` stays as written. The Claude Code sessions Karvon starts get the servers you switch on
for them, beside those Claude Code reads itself.

## Skills (View > Skills)

The skills Claude Code loads (the folder's `.claude/skills`, yours, the plugins'), each
with its checks (the name matches its folder and the rules, the description says when to use it, linked files exist,
scripts can run), "Try a prompt" (whether a prompt gives an agent a reason to load it) and its files; New Skill from a
template. The subagents, commands and hooks of the folder and yours. AGENTS.md and GEMINI.md written from CLAUDE.md
(with the folder's skills, which those agents do not load themselves) and kept in step, never over a file written by
hand without asking. A marketplace of skills from GitHub repositories, teams' registries and
[Agensi](https://www.agensi.io) (chosen by source, searched, free or paid) shows what a skill asks for (the tools it
allows, its scripts and their text, the network) before it is installed, updated or removed; Agensi's skills are
licensed to run through Agensi, so "Use in Claude Code" gives Claude Code its MCP server and the skill loads on demand
(a paid one is bought only when you agree).

## Terminal

Your `$SHELL` in a real pty, in the solution folder.

## NuGet

Browse, install, update and uninstall across your configured feeds.

## Claude Code (Ctrl+Esc)

Drives your installed `claude` CLI, so your login, settings and MCP servers apply.
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

## Layout

Tool windows at the sides as VS Code's activity bar (an icon each, Git Changes with a badge; click the
icon in front to fold the side), drag them to another place on the bar, to dock or to float them; tab groups (Window > New Vertical / Horizontal Tab Group),
Window > Split, floating documents, tabs dragged to reorder; documents, groups, layout and startup project are
restored per solution.
Keyboard shortcuts follow Visual Studio (Help > Keyboard Shortcuts).

## Not yet

Conditional breakpoints, exception settings, Immediate window, debugging Blazor WebAssembly in the browser,
Fix All, history / blame, merge conflict editor, WinForms / WPF designers.
