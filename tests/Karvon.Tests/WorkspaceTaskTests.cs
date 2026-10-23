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
                { "label": "lint", "type": "process", "command": "eslint", "args": ["${file}"], "linux": { "command": "eslint-linux" } },
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
        Assert.Equal(("eslint-linux", WorkspaceTaskKind.Other), (lint.Program, lint.Kind));
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
        var path = string.Join(Path.PathSeparator, NodeLocator.DirectoryOffPath() ?? "", Environment.GetEnvironmentVariable("PATH") ?? "");
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
        Assert.Equal(["packages", "src", "package.json", "pnpm-lock.yaml"], frontend.Children.Select(n => n.Name));
        // Its scripts are tasks of the solution too.
        Assert.Contains("pnpm: dev - frontend", vm.WorkspaceTasks.Select(t => t.Name));

        // The solution read again keeps it, once.
        await vm.ReloadSolutionAsync();
        Assert.Single(vm.ExplorerRoots.Single().Children, n => n.WorkspaceProject is not null);
        Assert.True(await vm.CloseSolutionCoreAsync());
        window.Close();
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
