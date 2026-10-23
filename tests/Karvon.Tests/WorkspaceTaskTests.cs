using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Karvon.Lsp;
using Karvon.Modules;
using Karvon.TypeScript;
using Karvon.Views;
using Karvon.Workspace;
using static Karvon.Tests.ClaudeIdeToolsTests;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>A module that brings a run task and a watch (the tests' shell commands).</summary>
sealed class FakeTaskModule : KarvonModule
{
    public override string Id => "fake-tasks";

    public override string Name => "Fake tasks";

    public override string Description => "A run task and a watch for the tests.";

    public override IReadOnlyList<WorkspaceTask> WorkspaceTasks(string workspace) =>
    [
        new("serve", WorkspaceTaskKind.Run, "sh", ["-c", "echo 'Local:   http://localhost:5173/'; sleep 30"], workspace) { Source = "fake" },
        new("watch", WorkspaceTaskKind.Other, "sh", ["-c", "echo watching; sleep 30"], workspace) { Source = "fake", IsBackground = true },
    ];
}

/// <summary>A module whose project is a frontend beside the solution's: its folder, and its dev server (a shell command).</summary>
sealed class FakeFrontendModule : KarvonModule
{
    public override string Id => "fake-frontend";

    public override string Name => "Fake frontend";

    public override string Description => "A frontend beside the solution for the tests.";

    static string FolderOf(string workspace) => Path.Combine(workspace, "frontend");

    public override IReadOnlyList<WorkspaceProject> WorkspaceProjects(string workspace) =>
        Directory.Exists(FolderOf(workspace)) ? [new("frontend", FolderOf(workspace), Path.Combine(FolderOf(workspace), "package.json")) { Kind = "fake" }] : [];

    public override IReadOnlyList<WorkspaceTask> WorkspaceTasks(string workspace) =>
        Directory.Exists(FolderOf(workspace)) ? [new("fake: dev - frontend", WorkspaceTaskKind.Run, "sh", ["-c", "echo 'frontend ready'; sleep 30"], FolderOf(workspace)) { Source = "fake" }] : [];
}

/// <summary>A folder's tasks: .vscode/tasks.json's and the modules' (package.json's scripts), and what Build, Start and Run All Tests do with them.</summary>
public class WorkspaceTaskTests
{
    static string NewFolder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "karvon-tasks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, ".vscode"));
        return dir;
    }

    [Fact]
    public void Tasks_json_is_read_as_VS_Code_reads_it()
    {
        var dir = NewFolder();
        File.WriteAllText(VsCodeTasks.FileOf(dir), """
            // Comments and trailing commas, as VS Code allows them.
            {
              "version": "2.0.0",
              "options": { "env": { "SHARED": "1" } },
              "tasks": [
                { "label": "compile", "type": "shell", "command": "tsc", "args": ["-p", "src dir"], "group": { "kind": "build", "isDefault": true },
                  "options": { "cwd": "${workspaceFolder}/web", "env": { "MODE": "${workspaceFolderBasename}" } } },
                { "label": "lint", "type": "process", "command": "eslint", "args": ["${file}"], "linux": { "command": "eslint-linux" }, "osx": { "command": "eslint-osx" }, "windows": { "command": "eslint-windows" } },
                { "type": "npm", "script": "test", "path": "web/", "group": "test" },
                { "label": "watch", "type": "shell", "command": "tsc -w", "isBackground": true, },
                { "label": "gulp", "type": "gulp", "task": "default" },
                { "label": "lint", "type": "process", "command": "again" },
              ],
            }
            """);
        var tasks = VsCodeTasks.Read(dir);
        Assert.Equal(["compile", "lint", "npm: test - web", "watch"], tasks.Select(t => t.Name));

        var compile = tasks[0];
        Assert.Equal((WorkspaceTaskKind.Build, true, VsCodeTasks.Source), (compile.Kind, compile.IsDefault, compile.Source));
        Assert.Equal(Path.Combine(dir, "web"), compile.WorkingDirectory);
        Assert.Equal(["-c", "tsc -p 'src dir'"], compile.Arguments);
        Assert.Equal(new Dictionary<string, string> { ["SHARED"] = "1", ["MODE"] = Path.GetFileName(dir) }, compile.Environment);

        // The platform's section wins; ${file} waits for the editor's file, when the task runs.
        var lint = tasks[1];
        Assert.Equal(("eslint-" + TestTools.PlatformKey, WorkspaceTaskKind.Other), (lint.Program, lint.Kind));
        Assert.Equal(["${file}"], lint.Arguments);
        var file = Path.Combine(dir, "src", "app.ts");
        Assert.Equal([file], TaskVariables.ForFile(lint, dir, file).Arguments);
        Assert.Equal("src/app.ts", TaskVariables.Expand("${relativeFile}", dir, file));

        var test = tasks[2];
        Assert.Equal((WorkspaceTaskKind.Test, "npm", Path.Combine(dir, "web")), (test.Kind, test.Program, test.WorkingDirectory));
        Assert.Equal(["run", "test"], test.Arguments);
        Assert.True(tasks[3].IsBackground);
        Assert.Equal("npm run test", test.CommandLine);
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void Problem_matchers_read_a_tasks_errors_as_VS_Code_does()
    {
        static Func<string, Karvon.Build.BuildDiagnostic?> Matcher(string json, string workspace = "/w") =>
            ProblemMatchers.Of(System.Text.Json.JsonDocument.Parse(json).RootElement, text => TaskVariables.Expand(text, workspace))!();

        // VS Code's own, by their names.
        Assert.Equal(new Karvon.Build.BuildDiagnostic(Karvon.Lsp.DiagnosticSeverity.Warning, "", "unused variable 'x'", "src/main.c", 4, 7, null),
            Matcher("\"$gcc\"")("src/main.c:4:7: warning: unused variable 'x'"));
        Assert.Equal(("./main.go", 12, 3, "undefined: y"), Matcher("\"$go\"")("./main.go:12:3: undefined: y") is { } go ? (go.File, go.Line, go.Column, go.Message) : default);
        Assert.Equal("TS2322", Matcher("\"$tsc\"")("src/app.ts(3,5): error TS2322: Type 'string' is not assignable.")!.Code);
        Assert.Null(Matcher("\"$gcc\"")("make: Nothing to be done."));

        // A task's own: its groups, its severity, where its files are; a list tries each.
        var own = Matcher("""
            [ "$gcc", { "owner": "lint", "severity": "info", "fileLocation": ["relative", "${workspaceFolder}/src"],
                "pattern": { "regexp": "^(.*)@(\\d+),(\\d+) \\[([\\w-]+)\\] (.*)$", "file": 1, "line": 2, "column": 3, "code": 4, "message": 5 } } ]
            """);
        Assert.Equal(new Karvon.Build.BuildDiagnostic(Karvon.Lsp.DiagnosticSeverity.Information, "no-var", "use let", "/w/src/app.js", 8, 2, null),
            own("app.js@8,2 [no-var] use let"));
        Assert.Equal("src/x.c", own("src/x.c:1:1: error: boom")!.File);
        var located = Matcher("""{ "pattern": { "regexp": "^(.+):(\\d+:\\d+): (\\w+): (.*)$", "location": 2, "severity": 3 } }""");
        Assert.Equal((Karvon.Lsp.DiagnosticSeverity.Warning, 10, 4, "deprecated"), located("a.py:10:4: warning: deprecated") is { } l ? (l.Severity, l.Line, l.Column, l.Message) : default);

        // Patterns of lines one after another: ESLint's stylish output, a file and then a line for each of its problems.
        var stylish = Matcher("\"$eslint-stylish\"");
        Assert.Null(stylish("/w/src/app.js"));
        Assert.Equal(new Karvon.Build.BuildDiagnostic(Karvon.Lsp.DiagnosticSeverity.Warning, "no-unused-vars", "'x' is assigned a value but never used", "/w/src/app.js", 3, 7, null),
            stylish("   3:7   warning  'x' is assigned a value but never used  no-unused-vars"));
        Assert.Equal(("/w/src/app.js", 9, Karvon.Lsp.DiagnosticSeverity.Error, "semi"), stylish("  9:12  error    Missing semicolon  semi") is { } next ? (next.File, next.Line, next.Severity, next.Code) : default);
        Assert.Null(stylish(""));
        Assert.Null(stylish("  1:1  error  Not after its file  semi"));
        Assert.Equal("/w/b.js", (stylish("/w/b.js"), stylish("  1:1  error  Unexpected var  no-var")).Item2!.File);
        Assert.Null(stylish("✖ 3 problems (2 errors, 1 warning)"));
        // A task's own: no loop, so each problem has its two lines; and each run reads afresh.
        var own2 = """
            { "fileLocation": ["relative", "${workspaceFolder}"],
              "pattern": [ { "regexp": "^In (.*):$", "file": 1 }, { "regexp": "^  line (\\d+): (\\w+) (.*)$", "line": 1, "severity": 2, "message": 3 } ] }
            """;
        var two = Matcher(own2);
        Assert.Null(two("In lib/a.py:"));
        Assert.Equal(("/w/lib/a.py", 4, Karvon.Lsp.DiagnosticSeverity.Warning, "unused import"), two("  line 4: warning unused import") is { } t ? (t.File, t.Line, t.Severity, t.Message) : default);
        Assert.Null(two("  line 5: error not after its file"));
        Assert.Null(two("In lib/b.py:"));
        Assert.Null(Matcher(own2)("  line 1: error a new run, so not after its file"));
        Assert.Null(two("something between"));
        Assert.Null(two("  line 1: error not right after its file"));
        // A list: each matcher reads every line.
        var both = Matcher("""["$gcc", "$eslint-stylish"]""");
        Assert.Null(both("/w/c.js"));
        Assert.Equal("src/x.c", both("src/x.c:1:1: error: boom")!.File);
        Assert.Null(both("/w/c.js"));
        Assert.Equal(("/w/c.js", "semi"), both("  2:1  error  Missing semicolon  semi") is { } c ? (c.File, c.Code) : default);

        // One that Karvon does not know: none (a build then reads file(line,col): error lines).
        Assert.Null(ProblemMatchers.Of(System.Text.Json.JsonDocument.Parse("""{ "pattern": [{ "regexp": "^(.*)$" }, { "regexp": "(" }] }""").RootElement, s => s));
        Assert.Null(ProblemMatchers.Of(System.Text.Json.JsonDocument.Parse("\"$lessCompile\"").RootElement, s => s));
    }

    [AvaloniaFact]
    public async Task A_build_tasks_problems_of_several_lines_are_in_the_Error_List()
    {
        var dir = NewFolder();
        var app = Path.Combine(dir, "app.js");
        var lint = Path.Combine(dir, "lint.txt");
        File.WriteAllText(lint, $"{app}\n  3:7  warning  'x' is assigned a value but never used  no-unused-vars\n  9:1  error  Missing semicolon  semi\n");
        File.WriteAllText(VsCodeTasks.FileOf(dir), """
            { "version": "2.0.0", "tasks": [
              { "label": "lint", "type": "process", "command": "cat", "args": ["lint.txt"], "group": "build", "problemMatcher": "$eslint-stylish" }
            ] }
            """);
        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        await vm.OpenSolutionAsync(dir);

        await vm.BuildSolutionCommand.ExecuteAsync(null);
        Assert.Equal([(app, 9, 1, "semi"), (app, 3, 7, "no-unused-vars")], vm.Errors.Select(e => (e.FilePath, e.Line, e.Column, e.Code)));

        // The next build reads afresh: problems' lines first have no file to be in.
        File.WriteAllText(lint, "  1:1  error  Unexpected var  no-var\n");
        await vm.BuildSolutionCommand.ExecuteAsync(null);
        Assert.Empty(vm.Errors);
        Assert.True(await vm.CloseSolutionCoreAsync());
        Directory.Delete(dir, recursive: true);
    }

    [AvaloniaFact]
    public async Task Depends_on_runs_first_in_sequence_or_together()
    {
        var dir = NewFolder();
        File.WriteAllText(VsCodeTasks.FileOf(dir), """
            { "version": "2.0.0", "tasks": [
              { "label": "a", "type": "shell", "command": "echo a >> order.txt" },
              { "label": "b", "type": "shell", "command": "echo b >> order.txt" },
              { "label": "fail", "type": "shell", "command": "exit 3" },
              { "label": "both", "dependsOn": ["a", "b"], "dependsOrder": "sequence" },
              { "label": "build", "type": "shell", "command": "echo built >> order.txt", "dependsOn": "both", "group": { "kind": "build", "isDefault": true } },
              { "label": "broken", "type": "shell", "command": "echo never >> order.txt", "dependsOn": ["fail"] },
              { "label": "ping", "type": "shell", "command": "echo ping", "dependsOn": "pong" },
              { "label": "pong", "type": "shell", "command": "echo pong", "dependsOn": "ping" },
              { "label": "lost", "type": "shell", "command": "echo lost", "dependsOn": "nowhere" }
            ] }
            """);
        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        await vm.OpenSolutionAsync(dir);
        var both = vm.WorkspaceTasks.Single(t => t.Name == "both");
        Assert.True(both.IsCompound);
        Assert.Equal("dependsOn: a, b", both.CommandLine);

        // Build: its dependencies first, one after another, then the build.
        await vm.BuildSolutionCommand.ExecuteAsync(null);
        Assert.Equal("a\nb\nbuilt\n", File.ReadAllText(Path.Combine(dir, "order.txt")));

        // A dependency that fails, one in a loop, one that is not there: the task does not run, and Output says why.
        WorkspaceTask Named(string name) => vm.WorkspaceTasks.Single(t => t.Name == name);
        await vm.RunTaskCommand.ExecuteAsync(Named("broken"));
        Assert.DoesNotContain("never", File.ReadAllText(Path.Combine(dir, "order.txt")));
        Assert.Contains("'fail', which 'broken' depends on, did not succeed, so 'broken' does not run.", vm.TasksOutput.Document.Text);
        await vm.RunTaskCommand.ExecuteAsync(Named("ping"));
        Assert.Contains("The task 'pong' depends on 'ping', which depends on it: neither runs.", vm.TasksOutput.Document.Text);
        await vm.RunTaskCommand.ExecuteAsync(Named("lost"));
        Assert.Contains("The task 'lost' depends on 'nowhere', which is not a task of the folder.", vm.TasksOutput.Document.Text);
        Assert.DoesNotContain("> ping:", vm.TasksOutput.Document.Text);
        Assert.True(await vm.CloseSolutionCoreAsync());
        Directory.Delete(dir, recursive: true);
    }

    /// <summary>Answers to the inputs a task asks for, in turn; null as a cancelled dialog.</summary>
    sealed class InputDialogs : Karvon.ViewModels.IDialogService
    {
        public Queue<string?> Answers { get; } = [];

        public List<string> Asked { get; } = [];

        public Task<string?> PickSolutionAsync() => Task.FromResult<string?>(null);

        public Task<string?> PromptAsync(string title, string label, string initial) => PromptAsync(title, label, initial, false);

        public Task<string?> PromptAsync(string title, string label, string initial, bool secret)
        {
            Asked.Add($"prompt {title}: {label} [{initial}]{(secret ? " secret" : "")}");
            return Task.FromResult(Answers.Dequeue());
        }

        public Task<string?> PickAsync(string title, string label, IReadOnlyList<string> options, string? initial)
        {
            Asked.Add($"pick {title}: {label} {string.Join('|', options)} [{initial}]");
            return Task.FromResult(Answers.Dequeue());
        }

        public Task<bool?> ConfirmSaveAsync(string fileName) => Task.FromResult<bool?>(false);

        public Task<bool?> ConfirmAsync(string title, string message) => Task.FromResult<bool?>(false);

        public Task<Karvon.ViewModels.NewItemChoice?> ChooseNewItemAsync(string title, string folder, ItemTemplate initial) => Task.FromResult<Karvon.ViewModels.NewItemChoice?>(null);

        public Task<IReadOnlyList<string>> PickFilesAsync(string title) => Task.FromResult<IReadOnlyList<string>>([]);
    }

    [AvaloniaFact]
    public async Task Inputs_are_asked_when_a_task_runs()
    {
        var dir = NewFolder();
        File.WriteAllText(VsCodeTasks.FileOf(dir), """
            { "version": "2.0.0",
              "inputs": [
                { "id": "name", "type": "promptString", "description": "Who to greet", "default": "World" },
                { "id": "color", "type": "pickString", "description": "A color", "options": ["red", { "label": "Blue", "value": "blue" }], "default": "blue" },
                { "id": "token", "type": "promptString", "description": "Token", "password": true },
                { "id": "picked", "type": "command", "command": "extension.pick" },
                { "id": "branch", "type": "command", "command": "shellCommand.execute",
                  "args": { "command": "printf 'main|the main one\\nfeature/login|a feature\\n'", "fieldSeparator": "|", "description": "A branch" } },
                { "id": "first", "type": "command", "command": "shellCommand.execute",
                  "args": { "command": "echo $GREETING; echo later", "env": { "GREETING": "hello" }, "useFirstResult": true } }
              ],
              "tasks": [
                { "label": "greet", "type": "shell", "command": "echo ${input:name} ${input:color} ${input:name} > out.txt" },
                { "label": "login", "type": "shell", "command": "echo ${input:token} > token.txt" },
                { "label": "by command", "type": "shell", "command": "echo ${input:picked}" },
                { "label": "checkout", "type": "shell", "command": "echo ${input:branch} > branch.txt" },
                { "label": "greet first", "type": "shell", "command": "echo ${input:first} > first.txt" },
                { "label": "undefined", "type": "shell", "command": "echo ${input:nope}" }
              ] }
            """);
        File.WriteAllText(VsCodeLaunch.FileOf(dir), """
            { "configurations": [ { "name": "App", "type": "node", "request": "launch", "args": ["${input:port}"] } ],
              "inputs": [ { "id": "port", "type": "promptString", "default": "3000" } ] }
            """);
        Assert.Equal("3000", Assert.Single(VsCodeLaunch.Read(dir)).Inputs["port"].Default);

        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        await vm.OpenSolutionAsync(dir);
        var dialogs = new InputDialogs();
        vm.Dialogs = dialogs;
        WorkspaceTask Named(string name) => vm.WorkspaceTasks.Single(t => t.Name == name);

        // Each input once, as VS Code asks it: a text with its default, one of the options (their values).
        dialogs.Answers.Enqueue("Ada");
        dialogs.Answers.Enqueue("red");
        await vm.RunTaskCommand.ExecuteAsync(Named("greet"));
        await WaitUntil(() => File.Exists(Path.Combine(dir, "out.txt")) && !vm.IsTaskRunning(Named("greet")), TimeSpan.FromSeconds(10));
        Assert.Equal("Ada red Ada\n", File.ReadAllText(Path.Combine(dir, "out.txt")));
        Assert.Equal(["prompt name: Who to greet [World]", "pick color: A color red|blue [blue]"], dialogs.Asked);

        // A password is asked as one; cancelled, the task does not run.
        dialogs.Answers.Enqueue(null);
        await vm.RunTaskCommand.ExecuteAsync(Named("login"));
        Assert.Equal("prompt token: Token [] secret", dialogs.Asked[^1]);
        Assert.False(File.Exists(Path.Combine(dir, "token.txt")));
        Assert.Contains("${input:token} was not answered, so it does not start.", vm.TasksOutput.Document.Text);

        // The Tasks Shell Input extension's: a shell command's lines to pick from (their first fields), or its first line.
        dialogs.Answers.Enqueue("feature/login");
        await vm.RunTaskCommand.ExecuteAsync(Named("checkout"));
        await WaitUntil(() => File.Exists(Path.Combine(dir, "branch.txt")) && !vm.IsTaskRunning(Named("checkout")), TimeSpan.FromSeconds(10));
        Assert.Equal("feature/login\n", File.ReadAllText(Path.Combine(dir, "branch.txt")));
        Assert.Equal("pick branch: A branch main|feature/login [main]", dialogs.Asked[^1]);
        var asked = dialogs.Asked.Count;
        await vm.RunTaskCommand.ExecuteAsync(Named("greet first"));
        await WaitUntil(() => File.Exists(Path.Combine(dir, "first.txt")) && !vm.IsTaskRunning(Named("greet first")), TimeSpan.FromSeconds(10));
        Assert.Equal("hello\n", File.ReadAllText(Path.Combine(dir, "first.txt")));
        Assert.Equal(asked, dialogs.Asked.Count);

        // Another extension's command input, and one that is not there: said, nothing runs.
        await vm.RunTaskCommand.ExecuteAsync(Named("by command"));
        Assert.Contains("${input:picked} is the command extension.pick of a VS Code extension, which Karvon does not run (it runs shellCommand.execute's).", vm.TasksOutput.Document.Text);
        await vm.RunTaskCommand.ExecuteAsync(Named("undefined"));
        Assert.Contains("${input:nope} is not an input of the folder's tasks.json or launch.json.", vm.TasksOutput.Document.Text);
        Assert.True(await vm.CloseSolutionCoreAsync());
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void Package_json_scripts_are_tasks_of_its_package_manager()
    {
        var dir = NewFolder();
        File.WriteAllText(Path.Combine(dir, "package.json"), """
            { "name": "shop", "scripts": { "build": "tsc", "test": "vitest run", "start": "node dist/server.js", "dev": "vite", "build:watch": "tsc -w", "lint": "eslint ." } }
            """);
        File.WriteAllText(Path.Combine(dir, "pnpm-lock.yaml"), "");
        Directory.CreateDirectory(Path.Combine(dir, "apps", "admin"));
        File.WriteAllText(Path.Combine(dir, "apps", "admin", "package.json"), """{ "packageManager": "yarn@4.1.0", "scripts": { "build": "vite build" } }""");
        Directory.CreateDirectory(Path.Combine(dir, "node_modules", "left-pad"));
        File.WriteAllText(Path.Combine(dir, "node_modules", "left-pad", "package.json"), """{ "scripts": { "build": "x" } }""");

        var tasks = PackageScripts.Read(dir);
        Assert.Equal(["pnpm: build", "pnpm: test", "pnpm: start", "pnpm: dev", "pnpm: build:watch", "pnpm: lint", "yarn: build - apps/admin"], tasks.Select(t => t.Name));
        Assert.Equal(
            [WorkspaceTaskKind.Build, WorkspaceTaskKind.Test, WorkspaceTaskKind.Run, WorkspaceTaskKind.Run, WorkspaceTaskKind.Other, WorkspaceTaskKind.Other, WorkspaceTaskKind.Build],
            tasks.Select(t => t.Kind));
        // The folder's own build and test, and its dev script over start.
        Assert.Equal(["pnpm: build", "pnpm: test", "pnpm: dev"], tasks.Where(t => t.IsDefault).Select(t => t.Name));
        Assert.True(tasks.Single(t => t.Name == "pnpm: build:watch").IsBackground);
        var admin = tasks[^1];
        Assert.Equal(("yarn", Path.Combine(dir, "apps", "admin")), (admin.Program, admin.WorkingDirectory));
        Assert.Equal(["run", "build"], admin.Arguments);
        Assert.All(tasks, t => Assert.Equal(PackageScripts.Source, t.Source));

        // The app and the tests are debugged as they run: js-debug launches their package manager, and the node it starts.
        Assert.Equal(["pnpm: test", "pnpm: start", "pnpm: dev"], tasks.Where(t => t.Debug is not null).Select(t => t.Name));
        var dev = tasks.Single(t => t.Name == "pnpm: dev").Debug!;
        Assert.Equal(("pwa-node", "launch", PackageScripts.Source), (dev.Type, dev.Request, dev.Source));
        Assert.Equal("""{"type":"pwa-node","request":"launch","name":"pnpm: dev","runtimeExecutable":"pnpm","runtimeArgs":["run","dev"],"cwd":"DIR","outputCapture":"std","skipFiles":["<node_internals>/**","**/node_modules/**"],"resolveSourceMapLocations":["${workspaceFolder}/**","!**/node_modules/**"]}""",
            dev.Configuration.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }).Replace(System.Text.Json.JsonEncodedText.Encode(dir).ToString(), "DIR"));
        File.WriteAllText(Path.Combine(dir, "bun.lock"), "");
        File.Delete(Path.Combine(dir, "pnpm-lock.yaml"));
        Assert.All(PackageScripts.Read(dir).Where(t => t.Program == "bun"), t => Assert.Null(t.Debug));
        Directory.Delete(dir, recursive: true);
    }

    [AvaloniaFact]
    public async Task A_folder_builds_runs_and_tests_by_its_tasks()
    {
        var dir = NewFolder();
        File.WriteAllText(VsCodeTasks.FileOf(dir), """
            { "version": "2.0.0", "tasks": [
              { "label": "compile", "type": "shell", "command": "echo 'src/app.ts(3,7): error TS2322: Type string is not assignable to type number.'; exit 2", "group": "build" },
              { "label": "unit", "type": "process", "command": "sh", "args": ["-c", "echo '2 passing'"], "group": "test" }
            ] }
            """);
        File.WriteAllText(Path.Combine(dir, "package.json"), """{ "scripts": { "dev": "vite" } }""");
        var vm = NewViewModel(modules: [.. Karvon.BuiltInModules.Create(), new FakeTaskModule()]);
        // The startup item comes back with the folder (kept in its .vs folder).
        vm.RestoresSolutionState = true;
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        await vm.OpenSolutionAsync(dir);

        // tasks.json's, then the modules': the TypeScript module's package.json scripts and the test module's.
        Assert.True(vm.IsFolderWorkspace);
        Assert.Equal(["compile", "unit", "npm: dev", "serve", "watch"], vm.WorkspaceTasks.Select(t => t.Name));
        Assert.Equal(["npm: dev", "serve"], vm.StartupItems.Select(i => i.Name));
        Assert.Equal("npm: dev", vm.StartupItem?.Name);
        Assert.Equal(["compile   (tasks.json)", "unit   (tasks.json)", "npm: dev   (package.json)", "serve   (fake)", "watch   (fake)"],
            MainWindow.RunTaskMenuItems(vm).OfType<MenuItem>().Select(i => i.Header as string));
        Assert.True(vm.BuildSolutionCommand.CanExecute(null));
        Assert.False(vm.CleanSolutionCommand.CanExecute(null));

        // Build: the build task, its errors in the Error List where they are.
        await vm.BuildSolutionCommand.ExecuteAsync(null);
        var error = Assert.Single(vm.Errors);
        Assert.Equal((Path.Combine(dir, "src", "app.ts"), 3, 7, "TS2322"), (error.FilePath, error.Line, error.Column, error.Code));
        Assert.Contains("========== Build: failed, 1 error(s), 0 warning(s) ==========", vm.BuildOutput.Document.Text);
        var server = ClaudeOf(vm).ToolServer();
        var (build, failed) = await Call(server, "run_build", new { });
        Assert.True(failed);
        Assert.Contains("Build of compile failed: 1 error(s)", build);
        Assert.Contains("src/app.ts(3,7): error TS2322", build);
        Assert.Contains("builds by its build task (compile) only", (await Call(server, "run_build", new { verb = "clean" })).Text);

        // Run All Tests: the test task, in Output (Tests).
        Assert.True(vm.RunAllTestsCommand.CanExecute(null));
        await vm.RunAllTestsCommand.ExecuteAsync(null);
        Assert.Contains("2 passing", vm.TestsOutput.Document.Text);
        Assert.Contains("========== unit: passed ==========", vm.TestsOutput.Document.Text);
        Assert.StartsWith("unit passed.", await Text(server, "run_tests", new { }));

        // Start: the run task chosen, as the program, until Stop; Claude starts another by its name.
        Assert.Contains("nope is not a startup item of the folder. Startup items: npm: dev, serve.", (await Call(server, "app_control", new { action = "start", project = "nope" })).Text);
        var started = await Text(server, "app_control", new { action = "start", project = "serve" });
        Assert.StartsWith("serve is running. Listening on http://localhost:5173.", started);
        Assert.True(vm.IsRunning);
        Assert.Equal("serve", vm.StartupItem?.Name);
        await vm.StopCommand.ExecuteAsync(null);
        await WaitUntil(() => !vm.IsRunning, TimeSpan.FromSeconds(10));
        Assert.Contains("The program 'serve' has exited", vm.DebugOutput.Document.Text);

        // A background task runs beside the rest, and stops when run again.
        var watch = vm.WorkspaceTasks.Single(t => t.Name == "watch");
        _ = vm.RunTaskCommand.ExecuteAsync(watch);
        await WaitUntil(() => vm.TasksOutput.Document.Text.Contains("watching"), TimeSpan.FromSeconds(10));
        Assert.True(vm.IsTaskRunning(watch));
        Assert.Contains("Stop watch   (fake)", MainWindow.RunTaskMenuItems(vm).OfType<MenuItem>().Select(i => i.Header as string));
        await vm.RunTaskCommand.ExecuteAsync(watch);
        await WaitUntil(() => vm.TasksOutput.Document.Text.Contains("watch exited"), TimeSpan.FromSeconds(10));
        Assert.False(vm.IsTaskRunning(watch));

        // tasks.json saved in the editor: its tasks are read again; the startup item stays.
        var tasksFile = vm.OpenDocument(VsCodeTasks.FileOf(dir))!;
        tasksFile.Host.Document.Text = """{ "version": "2.0.0", "tasks": [ { "label": "bundle", "type": "shell", "command": "true", "group": { "kind": "build", "isDefault": true } } ] }""";
        tasksFile.Host.Save();
        Assert.Equal(["bundle", "npm: dev", "serve", "watch"], vm.WorkspaceTasks.Select(t => t.Name));
        Assert.Equal("bundle", vm.BuildTask?.Name);
        Assert.Equal("serve", vm.StartupItem?.Name);
        Assert.False(vm.RunAllTestsCommand.CanExecute(null));

        // The folder opens again with the startup item it had; closed, its tasks go.
        Assert.True(await vm.CloseSolutionCoreAsync());
        Assert.Empty(vm.WorkspaceTasks);
        Assert.False(vm.IsFolderWorkspace);
        await vm.OpenSolutionAsync(dir);
        Assert.Equal("serve", vm.StartupItem?.Name);
        Assert.True(await vm.CloseSolutionCoreAsync());
        window.Close();
        Directory.Delete(dir, recursive: true);
    }

    [AvaloniaFact]
    public async Task Npm_builds_by_the_package_json_build_script()
    {
        // Node.js where the user has it (nvm's when Karvon's PATH has none), as the tasks get it.
        var path = string.Join(Path.PathSeparator, Karvon.Build.NodeLocator.DirectoryOffPath() ?? "", Environment.GetEnvironmentVariable("PATH") ?? "");
        Assert.SkipUnless(TaskVariables.Find("npm", path, "/") is not null, "Node.js is not installed.");
        var dir = NewFolder();
        File.WriteAllText(Path.Combine(dir, "package.json"), """
            { "name": "probe", "private": true, "scripts": { "build": "node -e \"console.log('src/a.ts(1,2): error TS1005: ; expected.'); process.exit(1)\"" } }
            """);
        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        await vm.OpenSolutionAsync(dir);
        Assert.Equal("npm: build", vm.BuildTask?.Name);
        await vm.BuildSolutionCommand.ExecuteAsync(null);
        var error = Assert.Single(vm.Errors);
        Assert.Equal((Path.Combine(dir, "src", "a.ts"), 1, "TS1005"), (error.FilePath, error.Line, error.Code));
        Assert.Contains("> npm run build", vm.BuildOutput.Document.Text);
        Assert.True(await vm.CloseSolutionCoreAsync());
        window.Close();
        Directory.Delete(dir, recursive: true);
    }

    [AvaloniaFact]
    public async Task A_solution_keeps_its_own_build_and_has_its_tasks_in_Run_Task()
    {
        var sln = SolutionExplorerTests.CopySample();
        var dir = Path.GetDirectoryName(sln)!;
        Directory.CreateDirectory(Path.Combine(dir, ".vscode"));
        File.WriteAllText(VsCodeTasks.FileOf(dir), """{ "version": "2.0.0", "tasks": [ { "label": "hello", "type": "shell", "command": "echo hello from the task" } ] }""");
        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        await vm.OpenSolutionAsync(sln);
        Assert.False(vm.IsFolderWorkspace);
        Assert.Equal(["hello"], vm.WorkspaceTasks.Select(t => t.Name));
        Assert.Empty(vm.StartupItems);
        Assert.NotNull(vm.StartupProject);
        Assert.True(vm.CleanSolutionCommand.CanExecute(null));
        await vm.RunTaskCommand.ExecuteAsync(vm.WorkspaceTasks[0]);
        Assert.Contains("hello from the task", vm.TasksOutput.Document.Text);
        Assert.True(await vm.CloseSolutionCoreAsync());
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_solution_shows_its_frontend_beside_its_projects()
    {
        var sln = SolutionExplorerTests.CopySample();
        var dir = Path.GetDirectoryName(sln)!;
        // A frontend beside the projects (a package of its own in it), and one in a project's folder, which shows there.
        Directory.CreateDirectory(Path.Combine(dir, "frontend", "src"));
        File.WriteAllText(Path.Combine(dir, "frontend", "package.json"), """{ "name": "@shop/web", "scripts": { "dev": "vite" } }""");
        File.WriteAllText(Path.Combine(dir, "frontend", "pnpm-lock.yaml"), "");
        File.WriteAllText(Path.Combine(dir, "frontend", "src", "main.ts"), "export {};");
        Directory.CreateDirectory(Path.Combine(dir, "frontend", "node_modules", "vite"));
        Directory.CreateDirectory(Path.Combine(dir, "frontend", "packages", "ui"));
        File.WriteAllText(Path.Combine(dir, "frontend", "packages", "ui", "package.json"), "{}");
        Directory.CreateDirectory(Path.Combine(dir, "Web", "ClientApp"));
        File.WriteAllText(Path.Combine(dir, "Web", "ClientApp", "package.json"), "{}");
        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        await vm.OpenSolutionAsync(sln);

        var root = vm.ExplorerRoots.Single();
        var frontend = root.Children.Single(n => n.WorkspaceProject is not null);
        Assert.Equal(("frontend (pnpm)", NodeKind.Folder, Path.Combine(dir, "frontend")), (frontend.DisplayName, frontend.Kind, frontend.Path));
        Assert.Equal("material:nodejs", frontend.ThemeIcon);
        frontend.IsExpanded = true;
        Assert.Equal(["Dependencies", "packages", "src", "package.json", "pnpm-lock.yaml"], frontend.Children.Select(n => n.Name));
        // Its scripts are tasks of the solution too.
        Assert.Contains("pnpm: dev - frontend", vm.WorkspaceTasks.Select(t => t.Name));

        // The solution read again keeps it, once.
        await vm.ReloadSolutionAsync();
        Assert.Single(vm.ExplorerRoots.Single().Children, n => n.WorkspaceProject is not null);
        Assert.True(await vm.CloseSolutionCoreAsync());
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_project_in_a_repository_shows_the_frontend_beside_it()
    {
        // A repository with the backend's lone project and its frontend side by side, and a package.json of the root's
        // own (no package of its own, only what was installed there).
        var repo = NewFolder();
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        var backend = Path.Combine(repo, "Shop.Backend");
        Directory.CreateDirectory(backend);
        var project = Path.Combine(backend, "Shop.Backend.csproj");
        File.WriteAllText(project, """<Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        var frontend = Path.Combine(repo, "Shop.Frontend");
        Directory.CreateDirectory(frontend);
        File.WriteAllText(Path.Combine(frontend, "package.json"), """{ "name": "shop-frontend", "scripts": { "dev": "vite", "build": "vite build" } }""");
        File.WriteAllText(Path.Combine(repo, "package.json"), """{ "dependencies": { "axios": "^1.0.0" } }""");
        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        await vm.OpenSolutionAsync(project);

        // The frontend shows beside the project, its scripts tasks of the solution, its dev script what it runs.
        var node = vm.ExplorerRoots.Single().Children.Single(n => n.WorkspaceProject is not null);
        Assert.Equal(("Shop.Frontend (npm)", frontend), (node.DisplayName, node.Path));
        Assert.Equal("npm: dev - Shop.Frontend", vm.RunTaskOf(node.WorkspaceProject!)?.Name);
        Assert.Contains("npm: build - Shop.Frontend", vm.WorkspaceTasks.Select(t => t.Name));
        Assert.Equal(frontend, vm.WorkspaceTasks.Single(t => t.Name == "npm: build - Shop.Frontend").WorkingDirectory);
        Assert.True(await vm.CloseSolutionCoreAsync());

        // Out of a repository, the project's own folder only.
        Directory.Delete(Path.Combine(repo, ".git"));
        await vm.OpenSolutionAsync(project);
        Assert.DoesNotContain(vm.ExplorerRoots.Single().Children, n => n.WorkspaceProject is not null);
        Assert.DoesNotContain(vm.WorkspaceTasks, t => t.Name.Contains("Shop.Frontend"));
        Assert.True(await vm.CloseSolutionCoreAsync());
        window.Close();
        Directory.Delete(repo, recursive: true);
    }

    [AvaloniaFact]
    public async Task A_frontend_starts_with_the_solution()
    {
        var sln = SolutionExplorerTests.CopySample();
        var dir = Path.GetDirectoryName(sln)!;
        Directory.CreateDirectory(Path.Combine(dir, "frontend"));
        var vm = NewViewModel(modules: [.. Karvon.BuiltInModules.Create(), new FakeFrontendModule()]);
        vm.RestoresSolutionState = true;
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        await vm.OpenSolutionAsync(sln);
        var node = vm.ExplorerRoots.Single().Children.Single(n => n.WorkspaceProject is { Kind: "fake" });
        var frontend = node.WorkspaceProject!;
        Assert.Equal("fake: dev - frontend", vm.RunTaskOf(frontend)?.Name);

        // Solution Explorer's Start with the Solution, off until it is turned on; it comes back with the solution.
        MenuItem StartWith() => window.ExplorerMenuItems(node).OfType<MenuItem>().Single(i => i.Header as string == "Start with the Solution");
        Assert.False(StartWith().IsChecked);
        vm.ToggleStartWithSolutionCommand.Execute(frontend);
        Assert.True(StartWith().IsChecked);
        Assert.True(await vm.CloseSolutionCoreAsync());
        await vm.OpenSolutionAsync(sln);
        node = vm.ExplorerRoots.Single().Children.Single(n => n.WorkspaceProject is { Kind: "fake" });
        Assert.True(StartWith().IsChecked);

        // The startup project starts: the frontend's dev server runs beside it, in Output (Tasks), until it ends.
        vm.StartWithSolution();
        var dev = vm.RunTaskOf(node.WorkspaceProject!)!;
        await WaitUntil(() => vm.TasksOutput.Document.Text.Contains("frontend ready"), TimeSpan.FromSeconds(10));
        Assert.True(vm.IsTaskRunning(dev));
        Assert.Contains("frontend starts with the solution: fake: dev - frontend", vm.DebugOutput.Document.Text);
        vm.StopStartedWithSolution();
        await WaitUntil(() => vm.TasksOutput.Document.Text.Contains("exited with code"), TimeSpan.FromSeconds(10));
        Assert.False(vm.IsTaskRunning(dev));

        // Off again: nothing starts with it.
        vm.ToggleStartWithSolutionCommand.Execute(node.WorkspaceProject);
        vm.StartWithSolution();
        Assert.False(vm.IsTaskRunning(dev));
        Assert.True(await vm.CloseSolutionCoreAsync());
        window.Close();
    }
}
