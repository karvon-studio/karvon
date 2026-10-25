# Development

```bash
git clone --recursive https://github.com/karvon-studio/karvon   # or: git submodule update --init
dotnet build Karvon.slnx
dotnet tests/Karvon.Tests/bin/Debug/net10.0/Karvon.Tests.dll   # unit, Roslyn integration and headless UI tests
```

The Database Explorer's engine is the [bengine](https://github.com/nbkabdulakhadov/bengine) repository, the
`external/bengine` submodule.

## Tests

The tests that download a language's servers and run its toolchain are off unless their variable is set:
`KARVON_TYPESCRIPT_LIVE=1`, `KARVON_PYTHON_LIVE=1`, `KARVON_GO_LIVE=1`, `KARVON_RUST_LIVE=1`, `KARVON_CPP_LIVE=1` and
`KARVON_JAVA_LIVE=1` (and `KARVON_KOTLIN_LIVE=1`, which downloads about 350 MB). They never touch your own settings, SSH configuration or keyring.

The README's screenshots are rendered by the tests too:

```bash
KARVON_SCREENSHOTS=$PWD/docs/screenshots dotnet tests/Karvon.Tests/bin/Debug/net10.0/Karvon.Tests.dll -class Karvon.Tests.Screenshots
```

## Releases

CI builds and tests every push. A `vX.Y.Z` tag publishes a release with the `.deb`, `.AppImage` and `.tar.gz`, which
`deploy/linux/package.sh` also builds locally. `./scripts/install.sh` installs a build of your own into
`~/.local/share/karvon/app` and adds the `karvon` command.

## The repository

```
src/Karvon/             the application: the core and the modules it comes with (BuiltInModules.cs)
src/Karvon.Core/        the workbench; it knows no module
  Modules/     the module contract: KarvonModule, IWorkbench (see docs/modules.md)
  Ui/          what the modules' views share: overlay dialogs, read-only editors
  Lsp/         JSON-RPC, the language server client, file watching
  Workspace/   a folder opened as it is, Solution Explorer's tree, the solution model
  Build/       process runner, the dotnet CLI, MSBuild's error format
  Debugging/   DAP client and sessions (child sessions too)
  Testing/     what a test framework is (Test Explorer shows the modules')
  Git/         git CLI wrapper and line diff
  Terminal/    integrated terminal
  Claude/      the tool contract and Karvon's IDE tools for Claude Code, the panel's language
  Templates/   what a project template is (the modules' are in New Project)
  Editor/      AvaloniaEdit host, completion, outlining, diff view
  ViewModels/  Views/   main window, tool windows, dialogs
src/Karvon.Database/    module: the Database Explorer (Bengine Data Studio's views and view models, on Avalonia 12)
src/Karvon.Docker/      module: the Docker Explorer
src/Karvon.Servers/     module: SSH servers
src/Karvon.Jira/        module: Jira Cloud, Server and Data Center sites, their issues and boards, Claude's Jira tools
src/Karvon.Ports/       module: Ports (port forwarding through a jprq tunnel service), Claude's tunnel tools
src/Karvon.Pipelines/   module: Pipelines (GitHub Actions and GitLab CI runs, jobs and logs), Claude's pipeline tools
src/Karvon.PullRequests/ module: Pull Requests (GitHub's, GitLab's merge requests; uses Karvon.Pipelines), Claude's pull request tools
src/Karvon.Kubernetes/  module: Kubernetes (kubeconfig's clusters through kubectl), Claude's Kubernetes tools
src/Karvon.Mcp/         module: MCP Servers
src/Karvon.Skills/      module: Skills (uses Karvon.Mcp)
src/Karvon.DotNet/      module: .NET (solutions and projects, Roslyn, netcoredbg, dotnet build, the SDK's templates, NuGet, vstest)
src/Karvon.TypeScript/  module: TypeScript and JavaScript (TypeScript 7's language server, package.json scripts, js-debug, Vitest, Jest; HTML, CSS, JSON; ESLint)
src/Karvon.Python/      module: Python (Pyright's language server, debugpy, pytest, the workspace's .venv)
src/Karvon.Go/          module: Go (gopls, Delve, go build/test/vet/run, go test in Test Explorer)
src/Karvon.Rust/        module: Rust (rust-analyzer, CodeLLDB, cargo build/check/clippy/test/run, cargo test in Test Explorer)
src/Karvon.Cpp/         module: C and C++ (clangd, CodeLLDB, CMake's configure/build/ctest/run, Make, CTest in Test Explorer)
src/Karvon.Lldb/        CodeLLDB, which the Rust and C and C++ modules debug with
src/Karvon.Java/        module: Java (jdtls with java-debug in it, Maven's and Gradle's builds, JUnit in Test Explorer)
src/Karvon.Kotlin/      module: Kotlin (JetBrains' kotlin-lsp and its debugger, Gradle's tests in Test Explorer, a template)
src/Karvon.Claude/      module: Claude Code (its session, protocol and view model, and the panel)
external/bengine/       Bengine (submodule): the schema AST and the live-database layer the Database Explorer uses
```

How a module plugs into the workbench is in [modules.md](modules.md).
