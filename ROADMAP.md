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
- .NET becomes the first workload: Roslyn, netcoredbg, Test Explorer, NuGet and the templates move behind it.
- Modules can be turned on and off; everything still ships in one package.

## 0b. Languages

- Open Folder: any folder, not only a solution or a project.
- A generic LSP client and DAP session, out of today's Roslyn and netcoredbg ones.
- A workload describes a language: how its projects are found (`package.json`, `pyproject.toml`, `go.mod`…), its
  language server, debugger, build, run and test commands, package manager and templates.
- `.vscode/launch.json` and `tasks.json` are read, so existing projects run as they are.
- Claude's IDE tools (`run_build`, `run_tests`, `app_control`, `debug_*`) work for any workload.
- Mixed repositories: a frontend's `package.json` folder shows as a project beside the solution's.

## 0c. Every platform

- No Linux-only code in Core: paths, process trees, the trash, opening files and URLs, the default shell.
- Cmd on macOS where Ctrl is on Linux and Windows.
- CI builds and tests on Linux, macOS and Windows.

## 1. Modify Karvon Studio

- An installer window as Visual Studio's: workloads and individual components.
- Heavy parts (language servers, debuggers, database drivers) download when their module is installed.
- Later, modules ship from their own repositories and a registry lists them.

## 2. TypeScript and JavaScript

vtsls, ESLint, vscode-js-debug, Vitest and Jest, npm, HTML and CSS.

## 3. macOS release

A signed and notarized `.dmg`, Homebrew, a debugger for Apple Silicon.

## 4. More languages and Windows

Python (basedpyright, debugpy, pytest, uv), Go (gopls, Delve, `go test`), Rust and C/C++ (rust-analyzer, clangd,
lldb-dap), Java and Kotlin (jdtls, java-debug, Maven, Gradle); a Windows release.

## What a language needs to be done

Projects and their dependencies in Solution Explorer; completion, diagnostics, navigation, rename and formatting;
build errors in the Error List; run and debug with breakpoints; tests in Test Explorer; a package manager window;
new project templates; Claude's IDE tools.
