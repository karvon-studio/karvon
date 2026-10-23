# Roadmap

Karvon Studio starts as an IDE for .NET on Linux. It is to become an IDE for many languages on Linux, macOS and
Windows, installed by modules as Visual Studio is by workloads. Each step ships on its own; .NET keeps working as
it does today all along.

## 0a. Module boundaries

- The app splits into projects: `Karvon.Core` (editor, Explorer, Git, Terminal, Find, settings) and one project per
  module (`Karvon.DotNet`, `Karvon.Database`, `Karvon.Docker`, `Karvon.Servers`, `Karvon.Claude`, `Karvon.Mcp`,
  `Karvon.Skills`). Core cannot reference a module; the compiler keeps the boundaries.
- A module contributes through one interface: tool windows, documents, menu and context menu items, commands, Claude
  tools, settings, Solution Explorer nodes.
- .NET becomes the first workload: Roslyn, netcoredbg, Test Explorer, NuGet and the templates move behind it. It goes
  feature by feature, each through an extension point the core gets for it: the NuGet window (menu and Solution
  Explorer items from modules; in now: `Karvon.DotNet` has it, with Roslyn's and netcoredbg's downloads), the
  templates (New Project, Add New Item; in now: both are the modules', .NET's too), Test Explorer (a workspace's
  tests; in now: .NET's vstest is the module's), run and debug (startup items; in now: F5 on the startup project is a
  launch configuration of the module's netcoredbg, Ctrl+F5 its dotnet run), the build (in now: the module's dotnet
  build), Roslyn (a solution's language server; in now: the module's), and last the solution itself (a module opens
  .sln, .slnx and .csproj files; the core opens folders; in now: they are the module's to open, read and change;
  next, the solution's model and the startup project).
- Modules can be turned on and off; everything still ships in one package.

## 0b. Languages

- Open Folder: any folder, not only a solution or a project (in now: with .NET in it too, as VS Code's C# Dev Kit has it:
  Roslyn loads its solution or projects, dotnet build, test and run are its tasks, F5 debugs an app).
- A generic LSP client and DAP session, out of today's Roslyn and netcoredbg ones (in now: the modules bring language
  servers and debuggers; a debugger may be a DAP server with child sessions, as js-debug is).
- A workload describes a language: how its projects are found (`package.json`, `pyproject.toml`, `go.mod`…), its
  language server, debugger, build, run and test commands, package manager and templates.
- `.vscode/launch.json` and `tasks.json` are read, so existing projects run as they are (in now: `tasks.json` and the
  modules' tasks build, run and test a folder, a task's `dependsOn` first (together, or in sequence) and its
  `problemMatcher` reading its errors (VS Code's `$msCompile`, `$tsc`, `$gcc`, `$go`, `$eslint-stylish` and
  `$eslint-compact`, or its own patterns, of one line or of several, the last looping); `launch.json`'s
  configurations start under the debugger of their type, with `preLaunchTask` and `postDebugTask`, and
  `serverReadyAction` opens the browser; its compounds debug their programs together (stopAll too); both ask their
  `${input:}` (promptString, pickString); not yet: command inputs).
- Claude's IDE tools (`run_build`, `run_tests`, `app_control`, `debug_*`) work for any workload (in now, for a folder's
  tasks and launch configurations).
- Test Explorer for any workload: a module's test frameworks find and run their tests (in now: .NET's through vstest,
  Vitest's and Jest's under js-debug, pytest, go test, cargo test, CTest and JUnit, run or debugged; Debug All Tests runs a
  folder's `package.json` test script under js-debug when no framework is known).
- Mixed repositories: a frontend's `package.json` folder, in the solution's folder or its repository's, shows as a
  project beside the solution's (in now, with its
  scripts in Run Task, and Start with the Solution: its dev server runs beside the startup project, as VS's multiple
  startup projects do, and under F5 under its own debugger beside the startup project's).

## 0c. Every platform

- No Linux-only code in Core: paths, process trees, the trash, opening files and URLs, the default shell (in now: a
  path in a folder as the platform has it (Windows: either slash, any case); opening files, folders and URLs, and the
  trash, on Linux, macOS and Windows; process trees on Linux and macOS;
  netcoredbg's build for Linux x64 and Arm64, Apple Silicon and Windows x64; the terminal's shell: zsh on macOS,
  PowerShell on Windows).
- Cmd on macOS where Ctrl is on Linux and Windows (in now: the window's keys, its menus, the editor's, and the Database
  Explorer's keymap, which takes Cmd as Ctrl; Ctrl+Tab and Ctrl+` stay Ctrl).
- CI builds and tests on Linux, macOS and Windows (in now: the Platforms workflow builds on macOS and Windows, run by
  hand; next, their tests).

## 1. Modify Karvon Studio

- An installer window as Visual Studio's: workloads and individual components (in now: Tools > Modules lists each
  module's components, the language servers and debuggers it downloads, with their state and size; Install gets one at
  once, Remove takes it away when Karvon Studio starts again).
- Heavy parts (language servers, debuggers, database drivers) download when their module is installed.
- Later, modules ship from their own repositories and a registry lists them.

## 2. TypeScript and JavaScript

TypeScript 7's own language server (`tsc --lsp`), the `package.json` scripts as tasks, js-debug for F5 and Vitest in
Test Explorer, Jest's too, VS Code's HTML, CSS (with SCSS and Less) and JSON language servers, and a page debugged in
Chrome or Edge by a launch.json configuration, and ESLint's diagnostics and fixes (Ctrl+.) beside TypeScript's, where the
user lets it run the folder's own ESLint (in now: the TypeScript and JavaScript module).

## 3. macOS release

A signed and notarized `.dmg`, Homebrew; .NET debugging on Apple Silicon (netcoredbg's build is in; an Intel Mac has none).

## 4. More languages and Windows

Each language is a module (in now unless it says next):

- Python: Pyright's language server, debugpy for F5 (launch.json's, or the folder's main.py, app.py, manage.py or the
  file in the editor), pytest in Test Explorer, all with the workspace's .venv; a uv project's uv sync and scripts as
  tasks, and a uv project's template.
- Go: gopls, Delve, a module's go build, test, vet and run, go test in Test Explorer.
- Rust: rust-analyzer, CodeLLDB, a Cargo package's build, check, clippy, test and run, cargo test in Test Explorer.
- C and C++: clangd, CodeLLDB, CMake's and Make's builds, CTest in Test Explorer.
- Java: jdtls with java-debug in it, Maven's and Gradle's builds, JUnit (5, and 4) in Test Explorer, run and debugged;
  next Kotlin.

Then a Windows release.

## What a language needs to be done

Projects and their dependencies in Solution Explorer (in now: each language's Dependencies, in its project's folder);
completion, diagnostics, navigation, rename and formatting;
build errors in the Error List; run and debug with breakpoints; tests in Test Explorer; a package manager window (in
now: NuGet, npm, Cargo, PyPI, Go and Maven Central's); new project templates; Claude's IDE tools.
