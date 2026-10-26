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
| Kotlin | kotlin-lsp (JetBrains') | kotlin-lsp's | Gradle (its import, and `gradle run`) | Gradle's `test` |

Working on solutions needs the .NET 10 SDK; each language needs its own toolchain (Node.js, Python, Go, Rust, a C and
C++ compiler, a JDK). The language servers and debuggers download the first time they are needed (or when their module
is turned on in Tools > Get Tools and Features, which lists them): `roslyn-language-server` and `netcoredbg` (both MIT); TypeScript 7 (Apache-2.0, its own language server; no
Node.js needed); js-debug, VS Code's HTML, CSS and JSON servers and its ESLint server (MIT; they run on Node.js, and
ESLint runs a folder's own ESLint only where you let it); Pyright and debugpy (MIT); rust-analyzer, clangd and CodeLLDB;
jdtls, java-debug and JUnit's console launcher; JetBrains' kotlin-lsp (about 350 MB, with its own Java runtime; partly
closed source, and it sends nothing). gopls and Delve are built with your Go (`go install`).

## Editor

IntelliSense in each language by its language server (C# and Razor by Roslyn), Quick Info, parameter info, squiggles,
Ctrl+. quick actions and refactorings (a linter's fixes too: ESLint's), Go To Definition / Implementation, Find All
References, Rename, Format Document, outlining (Ctrl+M), Find in Files, zoom.
Markdown files open with a live preview to the right or below (VS 2022's Markdown editor), following the editor's scrolling.
**Multiple carets** (Visual Studio's): Ctrl+Alt+click adds one, Shift+Alt+. a selection at the next match, Shift+Alt+;
at every match; typing, Backspace, Delete, Enter, Left, Right, Home and End act at each; Escape leaves one.
**Inlay hints** (a parameter's name before its argument, a type after a var; Roslyn's and any language server's) and
**CodeLens** (a member's references after its line, which finds them); **Fix All** in a quick action's menu (Fix all
occurrences in Document, Project or Solution); **Sticky Scroll** (the lines that open the scopes the view is in stay at
its top; one clicked goes there); a **Minimap** in place of the vertical scroll bar (VS's map mode). Each is in the
View menu, kept; the minimap is off at first.

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

A **console app** runs in a console of its own, as in Visual Studio's console window: Ctrl+F5 and F5 start it in the
Terminal window (in place of the shell, which the window's switch button brings back), its output there and the
keyboard its input; after it exits, Enter closes the console. Under F5 the program waits before `Main` until the
debugger has attached (a startup hook of Karvon's), so a breakpoint on its first line is hit. A web app, a worker or
a window's app keeps its output in Output. A launch.json's `"console"` says otherwise: `internalConsole` for Output,
`integratedTerminal` or `externalTerminal` for the console.

## Debug

F5 debugs with the language's debugger (see Languages; TypeScript through its source maps): breakpoints that follow
edits, Step Over / Into / Out, Locals, Watch, Call Stack, data tips, debugging tests, several programs at once
(launch.json's compounds).
A breakpoint's **Conditions** and **Actions** (right-click it, or the editor's Breakpoint > Insert Conditional
Breakpoint / Insert Tracepoint): break only when a condition is true or at a hit count, log a message instead of
breaking (`{x}` puts in a value), or keep it off; its glyph says which, and the settings go with it as lines move.
The **Breakpoints** window (Ctrl+Alt+B) lists them all, to turn on and off, edit, go to or delete. **Exception
Settings** (Ctrl+Alt+E): the exceptions the debugger says it can break on, checked as you like for each debugger
(kept, and told to a running session at once). The **Immediate** window (Ctrl+Alt+I): an expression evaluated where
the program stopped (an assignment too: Locals and Watch follow), Up for the ones before.
**Diagnostic Tools** (Ctrl+Alt+F2): while the program runs (F5 or Ctrl+F5), its processes' CPU (a share of all the
processors) and memory, each second, as graphs. For a .NET process (dotnet run's app, not the SDK's own) **Take Memory
Snapshot** lists the GC heap's types by size and count (dotnet-gcdump), and **Record CPU Profile** lists the functions
by the CPU spent in them for the seconds chosen, Total and Self (dotnet-trace; the managed code's time on the CPU).
Both tools are Microsoft's, installed once you say so (dotnet tool install, into Karvon's data folder); the dump and
the trace are deleted as soon as they are read.

## Test Explorer

Discover, Run All / Failed / Selected, theory rows, durations, failures with stack traces; .NET's
tests (xUnit, NUnit, MSTest), Vitest's and Jest's (by the package's own copy), pytest's (by the workspace's .venv),
go test's, cargo test's, CTest's and JUnit's, run or debugged.
**Code coverage** (Test > Analyze Code Coverage for All Tests): .NET's tests run with coverlet's collector (the test
project needs the coverlet.collector package), then every file's lines are colored beside the text (covered green,
not covered red, some branches only amber) and **Code Coverage Results** lists the files, the least covered first.
Another tool's report (Cobertura's XML, LCOV: Vitest's, Jest's, pytest-cov's, cargo llvm-cov's) is loaded with Test >
Load Coverage Report. Claude reads it with `test_coverage` (a file's lines not covered too).

## Git

Every repository of the solution in one Changes window (trees of staged and unstaged changes), stage,
commit (a message Claude writes from the diff, if you like), fetch, pull, push, sync, local and remote branches, diff
with word highlights, change bars in the editor margin, badges in Solution Explorer; follows commits and checkouts
made in the terminal.
**History** (Git > View Branch History, or a file's View History in Solution Explorer and on its tab): the commits
with their graph (HEAD's, or All Branches), branches and tags, author, date and hash, a filter; a commit's message,
author, committer, parents and files, each opening as a diff against its first parent; and, from a commit's menu,
Create Branch / Tag, Checkout (detached), Cherry-Pick, Revert and Reset (soft, mixed, hard), the ones that change
files asking first. A file's history follows its renames; the histories open follow new commits and checkouts.
**Blame (Annotate)** (Git menu, or the editor's menu): left of the line numbers, the commit each line last changed in
(date, author, subject; a bar as warm as the commit is new), unsaved lines as not committed yet; it follows edits
and commits, hovering tells the commit, clicking opens it in the history. **Show Line Blame** (on by default): the
caret line's commit after its text, as GitLens has it (who, when, why).
**Stashes** (Git Changes' Stash: All, All and Keep Staged, Staged; the message box's text the stash's message): each
applied, popped or dropped, its files each a diff. **Manage Worktrees** (Git menu): a branch's worktree beside the
repository (a new branch's too), opened here, removed, the gone ones pruned. **Interactive Rebase from Here** (a
commit's menu in the history): the commits after it, oldest first, each picked, reworded, stopped at to edit,
squashed, fixed up or dropped, in the order they are put in; git's editor never opens.
**Merge conflicts**: a conflicted file opens in the **merge editor** (Visual Studio's): the incoming and current sides
above, their conflicts shaded, the result below, editable; each conflict taken from one side, the other or both (or all
at once), the result red where a conflict is left; **Ask Claude to Resolve** (its edit of the file comes back here to
review); **Accept Merge** writes it and stages it. Git Changes says when a merge, a cherry-pick, a revert or a rebase
is under way and how many files conflict, with **Abort** (asked first) and **Continue**.

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
**Remote folders** (a machine's Open Remote Folder, or Open in Karvon on its Files tab), as VS Code's Remote SSH without
a server of its own there: the folder is copied here (only what changed, when it was before; not .git, node_modules,
bin, obj, target, .venv and the like, nor files over 5 MB) and opened as the workspace, `SSH: machine` in the status
bar. What changes in the copy (saved in the editor, written by Claude, a tool or a checkout) goes there half a second
after; the tasks, the build and the terminal run there (your ssh, in the folder there, a login shell), the paths they
print read as the copy's; what changed there comes when it opens and when the status bar item is clicked. A file
changed on both sides is asked about. The language servers and the debugger run here, on the copy.

## Jira (View > Jira)

Jira Cloud sites (an email and an API token) and Server or Data Center sites (a personal access token, or a name and
password); Test connection says as whom it signs in. The token or password stays in the system keyring (without one,
Karvon asks for it once a session); `jira.json` in Karvon's settings has only the sites. Under each site: the issues
assigned to you and those you reported, your favourite filters, the boards. An issue opens as a page: its fields,
description and comments (as the site renders them), Move to (its workflow's transitions now), a comment, Assign to
me, Open in Jira, and Ask Claude. A board opens as its columns, of its active sprint; a card dragged to another column
moves by its workflow's transition. JQL search, and New Issue (its project's types, by their ids; yours; its dates).

Claude Code's Jira tools work through Karvon's sign-in, so Claude never sees a token: `jira_sites`, `jira_search`
(JQL), `jira_issue`, `jira_projects`, and, each asking you first, `jira_create` (assigned to the signed-in user, its
type by the project's id, its start and end in the site's date fields), `jira_transition` (by the transition's or the
status's name, in the workflow's own language), `jira_comment` and `jira_assign`.

## Ports (View > Ports)

As Visual Studio Code's port forwarding: a local port at a public address through a jprq tunnel service (tulki.uz by
default; another one by its config page or its event port, host:4321). HTTP is `https://name.domain` (the service's
TLS in front of the plain local port; the name is your key's when you give none), TCP a host and port (SSH, a database,
an HTTPS port as it is; a fixed public port where the service allows it). The window lists the forwarded ports (their
address, protocol, visitors and state, with copy, open, stop and remove) and the ports the running program listens on
(its "Now listening on" or a task's local URLs), one click to forward; nothing is forwarded by itself. A lost connection
is made again, waiting longer each time. The service's API key stays in the system keyring (`ports.json` has the
service only); jprq sends it to the service's event port as its own client does, without TLS. The solution keeps its
forwarded ports, stopped; they stop when it closes.

Claude Code's tools: `tunnel_list`, `tunnel_open` (asking you first: it makes the port public) and `tunnel_close`,
through Karvon's own client, so Claude never sees the key. A `jprq`, `ngrok` or `cloudflared` tunnel in Bash is sent to
them.

## Pipelines (View > Pipelines)

The open repository's CI, as it goes: GitHub Actions' workflow runs (read through your own `gh`, signed in; without
it, a token with the repo and workflow scopes) and GitLab CI's pipelines (gitlab.com's or your company's, found by the
remote; a personal access token, read_api to watch, api to retry and cancel). Tokens stay in the system keyring. The
window lists the checked-out branch's runs (or every branch's), read again every 10 s while one runs and every minute
else, and right after a push; the branch's latest run is in the status bar, and a run that finishes says so. A run
opens as a page: its state, commit, branch, who and how long; its jobs (by stage on GitLab), a job's steps (GitHub's)
and log, colored (GitLab's as it runs, GitHub's once the job is done); Re-run failed jobs, Re-run all, Cancel, a job
again or a manual one started, Open in browser, and Ask Claude to fix a failed run. Nothing is read before a sign-in.

Claude Code's tools: `pipeline_list`, `pipeline_jobs`, `pipeline_log` (the end of a job's log, the failed one's by
default), and, each asking you first, `pipeline_retry` and `pipeline_cancel`. `gh run`, `glab ci` and GitLab's
pipelines API by curl in Bash are sent to them.

## Pull Requests (View > Pull Requests)

The repository's GitHub pull requests and GitLab merge requests (any GitLab host, found by the remote): open, created
by you, waiting for your review, closed or all, read again every two minutes and after a push; the checked-out
branch's own in the status bar. One opens as a page: what it asks, its description, the conversation (comments on
lines with their file and line), the reviewers' say, its checks, its files, each a diff against what the change
started from; Comment, Approve, Request Changes, Merge (a merge commit, squash or rebase; the branch deleted, if you
like), Close, Check Out (a fork's as `pr/N`), Open in Browser, Ask Claude to Review. **New Pull Request** pushes the
branch and proposes it into the default branch. Signed in as the Pipelines are: your own `gh` for GitHub, a token in
the keyring for a GitLab (the same one).
Claude gets `pr_list`, `pr_view` and `pr_diff`, and, asking you, `pr_create`, `pr_comment`, `pr_review` and
`pr_merge`; `gh pr` and `glab mr` in Bash are sent to them.

## HTTP Client (.http files, View > Endpoints Explorer)

Visual Studio's HTTP editor: a `.http` file's requests (between `###` lines: a method and URL, `?` and `&` query lines,
headers, a body) sent from the editor, ▶ beside each or **Send Request** for the one at the caret; the response beside
the file (status, time, size; the body highlighted, JSON indented; its headers; the request as it went out, its
variables put in) and the file's earlier responses. Variables: `@name = value`, `http-client.env.json`'s environments
(`$shared`, and the `.user` file's own values over them; one chosen in the pane), `# @name login` and
`{{login.response.body.$.token}}`, `{{$guid}}`, `{{$randomInt}}`, `{{$timestamp}}`, `{{$datetime}}`,
`{{$processEnv}}`, `{{$dotenv}}`. A development server's own certificate is taken (https://localhost).
The **Endpoints Explorer** lists the solution's ASP.NET Core endpoints by project (controllers' actions with their
routes, minimal APIs' `MapGet`... after their `MapGroup`s, `@page`s), read again when a source is saved; one opens where
it is written or, with **Generate Request**, goes into its project's `.http` file (its host address from
`launchSettings.json`). Claude gets `http_endpoints` and `http_send` (a file's request with its environment, or one of
its own; to localhost at once, elsewhere asking you).

## Kubernetes (View > Kubernetes)

Your clusters through your own `kubectl` and kubeconfig (its auth plugins and all); Karvon names the context in every
command, so kubectl's current one never changes. A context's namespaces (or every one), and in them the deployments,
stateful and daemon sets, jobs, cron jobs, pods, services, ingresses, config maps and secrets (their keys, never their
values), read again every ten seconds, the failing first and counted; the cluster's Argo CD applications and Flux
kustomizations and Helm releases, when it has them. An object opens as a page: its facts, containers, conditions,
events and pods; its logs, following (a container's or all, the run before a crash); its YAML; a shell in a pod.
Restart, Scale, Roll Back, Delete, Port Forward (listed in the window, to open or stop), a cron job run now or
suspended, an app synced or refreshed, Ask Claude; each asks first, in red on a production cluster (by its name).
Nothing is asked of a cluster before the window shows. Claude gets `k8s_contexts`, `k8s_get`, `k8s_describe`,
`k8s_logs` and `k8s_events`, and, asking you, `k8s_restart`, `k8s_scale` and `k8s_delete_pod`; `kubectl get`,
`describe`, `logs`, `rollout restart`, `scale` and `delete pod` in Bash are sent to them.

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

Your `$SHELL` in a real pty, in the solution folder; a console app's own console while it runs (see Build and run).

## Packages

Each language's packages in one Package Manager, as Visual Studio's NuGet one: Browse its registry, the Installed
packages and their Updates, a package's versions and details, Install, Update and Uninstall by the project's own
package manager, from Solution Explorer's menu of its folder or its manifest:

- NuGet: your configured feeds, `dotnet add package` (Manage NuGet Packages...).
- npm: npmjs.com, a `package.json` folder's dependencies or devDependencies, by its npm, pnpm, yarn or bun.
- Cargo: crates.io, a Cargo package's dependencies, dev- or build-dependencies (`cargo add`, `cargo remove`).
- Python: PyPI (a package by its name: pypi.org's search is for browsers), a uv project's dependencies and dependency
  groups (`uv add`, `uv remove`), else `pip` into the folder's virtual environment for its requirements.txt.
- Go: pkg.go.dev's search and the module proxy's versions, go.mod's requirements (`go get module@version`, `@none`).
- Java: Maven Central, a pom.xml's dependencies by their scopes or a build.gradle's (.kts) by their configurations;
  Maven and Gradle have no command that adds one, so Karvon edits the file as it is written.

**A folder whose packages are not installed** (its language server cannot resolve their imports): the first time its
server starts in a session, Karvon asks to install them, and Ctrl+. on such an import offers it too. It runs in Output
(Package Manager), then IntelliSense starts again with the packages. A no is kept with the folder.

- Python: a uv project is synced (`uv sync`). A requirements.txt folder gets a virtual environment (`python3 -m venv`,
  named as its .gitignore leaves one out, else `.venv`) with `pip install -r requirements.txt` into it. A folder that
  already has one gets requirements.txt installed into it (Pyright's `reportMissingImports`).
- JavaScript and TypeScript: a package.json with dependencies and no node_modules is installed by its npm, pnpm, yarn
  or bun (`install`; TypeScript's "Cannot find module", TS2307).
- .NET, Rust, Go, Java and Kotlin need nothing: Roslyn asks for its restore, and cargo, go, Gradle and Maven download
  what the build needs.

## Claude Code (Ctrl+Esc)

Drives your installed `claude` CLI, so your login, settings and MCP servers apply.
Streamed answers, tool calls with inline diffs, approvals, questions and plans, modes (Bypass permissions among
them) and effort, usage, MCP servers, images, `/` commands and `@` files, past conversations, conversations that
keep working in the background, Remote Control (go on from claude.ai or the Claude app), Ask Claude from Solution
Explorer; English or O‘zbekcha. Old conversations open at their last message, compaction summaries and long
prompts fold.
Claude gets Karvon's own tools (`mcp__karvon__*`, an MCP server inside Karvon): the build and the tests (`run_build`,
`build_errors`, `run_tests`, `test_failures`, `test_coverage`), running the app as Ctrl+F5 does (`app_control`: start, stop, restart,
its URLs and output), the debugger where it stopped (`debug_state`, `debug_locals`, `debug_evaluate`,
`debug_control`), the editor (`editor_selection`, `open_document`, `code_diagnostics`,
`find_in_files`), the Database Explorer's data sources (`db_connections`, `db_schema`, `db_query`, `db_explain`),
the solution's containers (`container_list`, `container_logs`, `container_inspect`, `container_restart`,
`compose_up`, `container_remove`) and the Servers (`server_list`, `server_logs`, `server_exec`), with your own
connections and passwords, which it never sees. Reading, building,
testing and stepping run at once; a change asks first, one call at a time (destructive SQL says so), read-only data
sources refuse changes, and secrets in logs and variables are hidden.
Every session Karvon starts (new or resumed) uses them **before the shell**: its system prompt says so, and a Bash
command one of them does (`dotnet build` or `test`, `npm run test`, `psql`, `docker ps`, `ssh` to one of the Servers,
`curl` to Jira's API) is sent to the tool instead; the same command again goes through, for what the tools cannot do.
Turned off with **Before the shell** on the karvon page of the MCP Servers window, for the sessions started after.

## Karvon Studio Installer and updates (Tools > Get Tools and Features)

As Visual Studio Installer: the installed Karvon Studio (its version, how it was installed, its update and the
release's notes), and **Modify**: the modules as **workloads** (languages and tools; one turned on downloads its
components now, one turned off goes when Karvon Studio starts again), their language servers and debuggers as
**individual components** (installed at once, removed when Karvon Studio starts again), **installation locations**,
and the space it all takes. It opens on its own too: `karvon --installer` (Karvon Studio Installer in the menu).

**Updates** come from the GitHub releases (a private repository's through your own `gh`, signed in): checked once a
day as Karvon Studio starts, downloaded, checked against the release's `SHA256SUMS`, and installed as Karvon Studio
closes (or at once with Restart and update; the status bar says when one is there). Each package updates in its own
way: the `.deb` by apt (pkexec asks for your password, so only when you ask), the AppImage and the `.tar.gz` by their
file and folder, the macOS app by its bundle, the Windows installer silently, the `.zip` by its files once Karvon has
exited. Help > Check for Updates checks now; the settings are on the installer's page. A build of the sources does not
update itself.

## Settings Sync (Tools > Settings Sync)

The preferences go with you to your other machines, as VS Code's Settings Sync takes them: the editor's (inlay hints,
CodeLens, sticky scroll, the minimap, line blame, zoom, Markdown preview), Claude's, the updates', the modules on and
off, the Skills' sources and the exceptions to break on; not this machine's paths, window, recent lists, installs or
account. They are kept in a folder another tool syncs (Dropbox, Syncthing, OneDrive, a share) or in a secret gist of
your GitHub account (through gh, signed in as it is). Synced when Karvon starts, a moment after one of them changes,
and with Sync Now; two machines' changes merge by setting (what changed here since the last sync stays here's). The
first time, when both sides have settings, you choose whose.

## Layout

Tool windows at the sides as VS Code's activity bar (an icon each, Git Changes with a badge; click the
icon in front to fold the side), drag them to another place on the bar, to dock or to float them; tab groups (Window > New Vertical / Horizontal Tab Group),
Window > Split, floating documents, tabs dragged to reorder; documents, groups, layout and startup project are
restored per solution.
Keyboard shortcuts follow Visual Studio (Help > Keyboard Shortcuts).

## Not yet

Debugging Blazor WebAssembly in the browser,
WinForms / WPF designers.
