using Avalonia.Headless.XUnit;
using Karvon.Modules;
using Karvon.TypeScript;
using Karvon.ViewModels;
using Karvon.Views;
using Karvon.Workspace;
using static Karvon.Tests.ClaudeIdeToolsTests;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>F5 in a folder: its launch.json configurations and the tasks a module debugs, under the debugger of their type.</summary>
public class DebugLaunchTests
{
    static string NewFolder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "karvon-launch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, ".vscode"));
        return dir;
    }

    [Fact]
    public void Server_ready_action_opens_where_the_program_listens_once()
    {
        var log = new List<string>();
        static System.Text.Json.Nodes.JsonObject Config(string action) => System.Text.Json.Nodes.JsonNode.Parse($$"""{ "serverReadyAction": {{action}} }""")!.AsObject();

        // VS Code's defaults: a URL after "listening on", or a port on localhost; once only, the terminal's colors aside.
        var url = Karvon.Debugging.ServerReadyAction.Of(Config("""{ "action": "openExternally" }"""), log.Add)!;
        Assert.Null(url.Match("Starting...\n"));
        Assert.Equal("http://127.0.0.1:5173/", url.Match("  \u001b[32mlistening on\u001b[0m: http://127.0.0.1:5173/\n"));
        Assert.Null(url.Match("listening on: http://127.0.0.1:5173/\n"));
        Assert.Equal("http://localhost:3000", Karvon.Debugging.ServerReadyAction.Of(Config("{}"), log.Add)!.Match("Server listening on port 3000"));

        // ASP.NET Core's line, with a uriFormat.
        var aspnet = Karvon.Debugging.ServerReadyAction.Of(Config("""{ "pattern": "\\bNow listening on:\\s+(https?://\\S+)", "uriFormat": "%s/swagger" }"""), log.Add)!;
        Assert.Equal("https://localhost:7001/swagger", aspnet.Match("info: Microsoft.Hosting.Lifetime[14]\n      Now listening on: https://localhost:7001\n"));

        // None, one of VS Code's browser debuggers, or a pattern that is no regular expression: nothing opens, and the log says why.
        Assert.Null(Karvon.Debugging.ServerReadyAction.Of(new System.Text.Json.Nodes.JsonObject(), log.Add));
        Assert.Null(Karvon.Debugging.ServerReadyAction.Of(Config("""{ "action": "debugWithChrome" }"""), log.Add));
        Assert.Null(Karvon.Debugging.ServerReadyAction.Of(Config("""{ "pattern": "(" }"""), log.Add));
        Assert.Equal(2, log.Count);
        Assert.Contains("debugWithChrome", log[0]);
    }

    [Fact]
    public void Launch_json_is_read_as_VS_Code_reads_it()
    {
        var dir = NewFolder();
        File.WriteAllText(VsCodeLaunch.FileOf(dir), """
            // Comments and trailing commas, as VS Code allows them.
            {
              "version": "0.2.0",
              "configurations": [
                { "name": "Launch app", "type": "node", "request": "launch", "program": "${workspaceFolder}/src/app.js",
                  "args": ["${relativeFile}", "--port", "${env:KARVON_TEST_PORT}"], "env": { "HOME_DIR": "${userHome}" },
                  "preLaunchTask": "npm: build", "postDebugTask": "cleanup", "linux": { "program": "${workspaceFolder}/src/linux.js" }, },
                { "name": "Attach", "type": "node", "request": "attach", "port": 9229 },
                { "name": "No type" },
                { "name": "Launch app", "type": "node", "request": "launch", "program": "again.js" },
              ],
              "compounds": [ { "name": "Both", "configurations": ["Launch app", "Attach"] } ],
            }
            """);
        var launches = VsCodeLaunch.Read(dir);
        Assert.Equal(["Launch app", "Attach"], launches.Select(l => l.Name));

        var app = launches[0];
        Assert.Equal(("node", "launch", "npm: build", "cleanup", VsCodeLaunch.Source), (app.Type, app.Request, app.PreLaunchTask, app.PostDebugTask, app.Source));
        Assert.Null(app.Configuration["linux"]);
        Assert.Equal(("attach", (string?)null), (launches[1].Request, launches[1].PreLaunchTask));

        // Its variables go in when it starts: the platform's program, the editor's file, the environment's.
        Environment.SetEnvironmentVariable("KARVON_TEST_PORT", "8080");
        var file = Path.Combine(dir, "src", "app.js");
        var started = TaskVariables.ForFile(app.Configuration, dir, file);
        Assert.Equal(Path.Combine(dir, "src", "linux.js"), started["program"]!.GetValue<string>());
        Assert.Equal(["src/app.js", "--port", "8080"], started["args"]!.AsArray().Select(a => a!.GetValue<string>()));
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), started["env"]!["HOME_DIR"]!.GetValue<string>());
        Assert.Equal("${workspaceFolder}/src/linux.js", app.Configuration["program"]!.GetValue<string>());

        // js-debug's own names for the types, the folder for its defaults, and the Output window as the console.
        JsDebugger.Complete(started, dir);
        Assert.Equal(("pwa-node", dir, "internalConsole"),
            (started["type"]!.GetValue<string>(), started["__workspaceFolder"]!.GetValue<string>(), started["console"]!.GetValue<string>()));
        Directory.Delete(dir, recursive: true);
    }

    [AvaloniaFact]
    public async Task A_folder_starts_its_launch_configurations_and_its_run_tasks()
    {
        var dir = NewFolder();
        File.WriteAllText(VsCodeLaunch.FileOf(dir), """
            { "configurations": [
              { "name": "Cobol app", "type": "cobol", "request": "launch", "program": "app.cbl" },
              { "name": "Prepared", "type": "cobol", "request": "launch", "preLaunchTask": "missing" }
            ] }
            """);
        var vm = NewViewModel(modules: [.. Karvon.BuiltInModules.Create(), new FakeTaskModule()]);
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        await vm.OpenSolutionAsync(dir);

        // launch.json's first (the first of them chosen), then the run tasks.
        Assert.Equal(["Cobol app", "Prepared", "serve"], vm.StartupItems.Select(i => i.Name));
        Assert.Equal("Cobol app", vm.StartupItem?.Name);
        Assert.True(vm.StartDebuggingCommand.CanExecute(null));

        // A type no module debugs, and a preLaunchTask the folder does not have: nothing starts, and the Output says why.
        await vm.StartDebuggingCommand.ExecuteAsync(null);
        Assert.Contains("No debugger debugs 'cobol' (Cobol app): its module brings one", vm.DebugOutput.Document.Text);
        Assert.Equal(DebugMode.Design, vm.Mode);
        vm.StartupItem = vm.StartupItems[1];
        await vm.StartDebuggingCommand.ExecuteAsync(null);
        Assert.Contains("The preLaunchTask 'missing' is not a task of the folder", vm.DebugOutput.Document.Text);

        // F5 on a task nothing debugs runs it without the debugger, and says so.
        vm.StartupItem = vm.StartupItems.Single(i => i.Name == "serve");
        var run = vm.StartDebuggingCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.IsRunning, TimeSpan.FromSeconds(10));
        Assert.Contains("serve has no debugger, so it runs without one", vm.DebugOutput.Document.Text);
        await vm.StopCommand.ExecuteAsync(null);
        await run;
        Assert.False(vm.IsRunning);
        Assert.True(await vm.CloseSolutionCoreAsync());
        window.Close();
        Directory.Delete(dir, recursive: true);
    }

    [AvaloniaFact]
    public async Task F5_debugs_node_with_js_debug()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("KARVON_TYPESCRIPT_LIVE") == "1", "Set KARVON_TYPESCRIPT_LIVE=1 to download js-debug and debug with it.");
        var path = string.Join(Path.PathSeparator, Karvon.Build.NodeLocator.DirectoryOffPath() ?? "", Environment.GetEnvironmentVariable("PATH") ?? "");
        Assert.SkipUnless(TaskVariables.Find("npm", path, "/") is not null, "Node.js is not installed.");
        var dir = NewFolder();
        var main = Path.Combine(dir, "main.js");
        File.WriteAllText(main, """
            function area(w, h) {
              const result = w * h;
              return result;
            }
            console.log("area", area(3, 4));
            """);
        File.WriteAllText(Path.Combine(dir, "package.json"), """{ "name": "probe", "private": true, "scripts": { "start": "node main.js", "test": "node check.js" } }""");
        var check = Path.Combine(dir, "check.js");
        File.WriteAllText(check, """
            const expected = 12;
            if (3 * 4 !== expected) process.exit(1);
            console.log("checked");
            """);
        File.WriteAllText(VsCodeTasks.FileOf(dir), """{ "version": "2.0.0", "tasks": [ { "label": "prepare", "type": "shell", "command": "echo prepared" } ] }""");
        File.WriteAllText(VsCodeLaunch.FileOf(dir), """
            { "configurations": [
              { "name": "Launch main", "type": "node", "request": "launch", "program": "${workspaceFolder}/main.js", "preLaunchTask": "prepare" }
            ] }
            """);
        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        await vm.OpenSolutionAsync(dir);
        Assert.Equal(["Launch main", "npm: start"], vm.StartupItems.Select(i => i.Name));
        var server = ClaudeOf(vm).ToolServer();

        // F5: the preLaunchTask, then node under js-debug, stopped on the breakpoint; its variables, an expression, a step.
        vm.Breakpoints.Toggle(main, 3);
        var started = await Text(server, "debug_control", new { action = "start" });
        Assert.True(vm.Mode == DebugMode.Break, started + "\n" + vm.DebugOutput.Document.Text);
        Assert.Contains("prepared", vm.TasksOutput.Document.Text);
        Assert.Matches(@"^Stopped \(breakpoint\) in .*area at main\.js:3\.$", started);
        Assert.Equal("w * h = 12 (number)", await Text(server, "debug_evaluate", new { expression = "w * h" }));
        Assert.Contains("Local: area:\n  h: number = 4\n  result: number = 12", await Text(server, "debug_locals", new { depth = 1 }));
        Assert.True(vm.Breakpoints.IsVerified(main, 3));
        // Over the return: back in the caller.
        Assert.Matches(@"at main\.js:5\.$", await Text(server, "debug_control", new { action = "stepOver" }));
        await Text(server, "debug_control", new { action = "continue" });
        await WaitEnded();
        Assert.Contains("area 12", vm.DebugOutput.Document.Text);

        // F5 on the start script: npm runs under the debugger, and the node it starts stops on the breakpoint too.
        vm.StartupItem = vm.StartupItems.Single(i => i.Name == "npm: start");
        started = await Text(server, "debug_control", new { action = "start" });
        Assert.True(vm.Mode == DebugMode.Break, started + "\n" + vm.DebugOutput.Document.Text);
        Assert.Matches(@"area at main\.js:3\.$", started);
        await vm.StartDebuggingCommand.ExecuteAsync(null);
        await WaitEnded();
        Assert.True(vm.DebugOutput.Document.Text.Contains("area 12"), vm.DebugOutput.Document.Text);
        Assert.Contains("> node main.js", vm.DebugOutput.Document.Text);
        Assert.DoesNotContain("Could not read source map", vm.DebugOutput.Document.Text);

        // Debug All Tests: the test script under the debugger, stopped in the test.
        vm.Breakpoints.Toggle(check, 3);
        Assert.True(vm.DebugAllTestsCommand.CanExecute(null));
        await vm.DebugAllTestsCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.Mode == DebugMode.Break && vm.CallStack.Count > 0, TimeSpan.FromSeconds(30));
        Assert.True(vm.CallStack.FirstOrDefault()?.SourcePath == check, vm.DebugOutput.Document.Text);
        Assert.Equal(3, vm.CallStack[0].Line);
        await vm.StartDebuggingCommand.ExecuteAsync(null);
        await WaitEnded();
        Assert.Contains("checked", vm.DebugOutput.Document.Text);
        Assert.Contains("The program 'npm: test' has exited.", vm.DebugOutput.Document.Text);

        // Ctrl+F5 on the launch configuration: it runs without the debugger, past the breakpoint.
        vm.StartupItem = vm.StartupItems[0];
        await vm.StartWithoutDebuggingCommand.ExecuteAsync(null);
        await WaitEnded();
        Assert.Contains("Running Launch main (without debugging)", vm.DebugOutput.Document.Text);
        Assert.Contains("area 12", vm.DebugOutput.Document.Text);
        Assert.True(await vm.CloseSolutionCoreAsync());

        // TypeScript, compiled with its source maps (by TypeScript 7's tsc): the breakpoint in the .ts file stops the .js.
        var shapes = Path.Combine(dir, "src", "shapes.ts");
        Directory.CreateDirectory(Path.GetDirectoryName(shapes)!);
        File.WriteAllText(shapes, """
            export function perimeter(w: number, h: number): number {
              const sides: number[] = [w, h, w, h];
              return sides.reduce((a, b) => a + b, 0);
            }
            console.log("perimeter", perimeter(3, 4));
            """);
        var tsc = await TypeScriptServerLocator.EnsureInstalledAsync(_ => { }, TestContext.Current.CancellationToken);
        var compiled = new List<string>();
        using (var runner = Karvon.Build.ProcessRunner.Start(tsc, ["--sourceMap", "--outDir", "out", "--module", "commonjs", "src/shapes.ts"], dir, compiled.Add))
            Assert.True(await runner.WaitAsync(TestContext.Current.CancellationToken) == 0, string.Join("\n", compiled));
        File.WriteAllText(VsCodeLaunch.FileOf(dir), """
            { "configurations": [ { "name": "Shapes", "type": "node", "request": "launch", "program": "${workspaceFolder}/out/shapes.js" } ] }
            """);
        await vm.OpenSolutionAsync(dir);
        vm.StartupItem = vm.StartupItems.Single(i => i.Name == "Shapes");
        vm.Breakpoints.Toggle(shapes, 3);
        started = await Text(server, "debug_control", new { action = "start" });
        Assert.True(vm.Mode == DebugMode.Break, started + "\n" + vm.DebugOutput.Document.Text);
        Assert.Matches(@"perimeter at src/shapes\.ts:3\.$", started);
        Assert.Equal("sides.length = 4 (number)", await Text(server, "debug_evaluate", new { expression = "sides.length" }));
        await vm.StartDebuggingCommand.ExecuteAsync(null);
        await WaitEnded();
        Assert.Contains("perimeter 14", vm.DebugOutput.Document.Text);
        Assert.True(await vm.CloseSolutionCoreAsync());
        window.Close();
        Directory.Delete(dir, recursive: true);

        async Task WaitEnded()
        {
            await WaitUntil(() => vm.Mode == DebugMode.Design, TimeSpan.FromSeconds(30));
            Assert.True(vm.Mode == DebugMode.Design, vm.DebugOutput.Document.Text);
        }
    }

    [AvaloniaFact]
    public async Task F5_debugs_a_page_in_chrome_with_js_debug()
    {
        var path = string.Join(Path.PathSeparator, Karvon.Build.NodeLocator.DirectoryOffPath() ?? "", Environment.GetEnvironmentVariable("PATH") ?? "");
        var chrome = TaskVariables.Find("google-chrome", path, "/") ?? TaskVariables.Find("chromium", path, "/");
        Assert.SkipUnless(Environment.GetEnvironmentVariable("KARVON_TYPESCRIPT_LIVE") == "1" && TaskVariables.Find("node", path, "/") is not null && chrome is not null,
            "Set KARVON_TYPESCRIPT_LIVE=1, with Node.js and Chrome installed, to debug a page in Chrome with js-debug.");
        var dir = NewFolder();
        var script = Path.Combine(dir, "app.js");
        File.WriteAllText(script, """
            function total(prices) {
              const sum = prices.reduce((a, b) => a + b, 0);
              return sum;
            }
            document.title = "total " + total([3, 4, 5]);
            """);
        File.WriteAllText(Path.Combine(dir, "index.html"), "<!DOCTYPE html>\n<html><head><title>shop</title></head><body><script src=\"app.js\"></script></body></html>\n");
        // A browser of its own (js-debug's profile, not the user's), and no window: headless; the Chrome found, as a CI
        // machine (no sandbox there) has it.
        File.WriteAllText(VsCodeLaunch.FileOf(dir), $$"""
            { "configurations": [
              { "name": "Page in Chrome", "type": "chrome", "request": "launch", "url": "file://${workspaceFolder}/index.html", "webRoot": "${workspaceFolder}",
                "runtimeExecutable": {{System.Text.Json.JsonSerializer.Serialize(chrome)}},
                "runtimeArgs": ["--headless=new", "--no-first-run", "--no-default-browser-check", "--no-sandbox"] }
            ] }
            """);
        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        await vm.OpenSolutionAsync(dir);
        Assert.Equal("Page in Chrome", vm.StartupItem?.Name);

        // F5: Chrome under js-debug (a child session for the page), stopped in the page's script.
        vm.Breakpoints.Toggle(script, 3);
        var debugging = vm.StartDebuggingCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.Mode == DebugMode.Break && vm.CallStack.Count > 0, TimeSpan.FromMinutes(2));
        Assert.True(vm.Mode == DebugMode.Break, vm.DebugOutput.Document.Text);
        Assert.Equal((script, 3), (vm.CallStack[0].SourcePath, vm.CallStack[0].Line));
        await debugging;

        // Stop: the browser goes with the session.
        await vm.StopCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.Mode == DebugMode.Design, TimeSpan.FromSeconds(30));
        Assert.Equal(DebugMode.Design, vm.Mode);
        Assert.True(await vm.CloseSolutionCoreAsync());
        window.Close();
        Directory.Delete(dir, recursive: true);
    }

    [AvaloniaFact]
    public async Task A_compound_debugs_its_programs_together()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("KARVON_TYPESCRIPT_LIVE") == "1", "Set KARVON_TYPESCRIPT_LIVE=1 to download js-debug and debug with it.");
        var path = string.Join(Path.PathSeparator, Karvon.Build.NodeLocator.DirectoryOffPath() ?? "", Environment.GetEnvironmentVariable("PATH") ?? "");
        Assert.SkipUnless(TaskVariables.Find("node", path, "/") is not null, "Node.js is not installed.");
        var dir = NewFolder();
        // Two programs that each stop at a breakpoint a moment after they start (when both sessions are up), then wait
        // to be told to end.
        var server = Path.Combine(dir, "server.js");
        File.WriteAllText(server, """
            function listen(port) {
              console.log("server on", port);
            }
            setTimeout(() => listen(8080), 1000);
            setTimeout(() => {}, 60000);
            """);
        File.WriteAllText(Path.Combine(dir, "quick.js"), "console.log(\"quick done\");\n");
        var client = Path.Combine(dir, "client.js");
        File.WriteAllText(client, """
            function ask(what) {
              console.log("client asks", what);
            }
            setTimeout(() => ask("hello"), 1000);
            setTimeout(() => {}, 60000);
            """);
        File.WriteAllText(VsCodeLaunch.FileOf(dir), """
            { "configurations": [
                { "name": "Server", "type": "node", "request": "launch", "program": "${workspaceFolder}/server.js" },
                { "name": "Client", "type": "node", "request": "launch", "program": "${workspaceFolder}/client.js" },
                { "name": "Quick", "type": "node", "request": "launch", "program": "${workspaceFolder}/quick.js" }
              ],
              "compounds": [
                { "name": "Both", "configurations": ["Server", "Client"], "stopAll": true },
                { "name": "Server and quick", "configurations": ["Server", "Quick"], "stopAll": true },
                { "name": "Nothing known", "configurations": ["Missing"] }
              ] }
            """);
        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        await vm.OpenSolutionAsync(dir);
        // The compound after the configurations; one of none the file has is left out.
        Assert.Equal(["Server", "Client", "Quick", "Both", "Server and quick"], vm.StartupItems.Select(i => i.Name));
        vm.StartupItem = vm.StartupItems.Single(i => i.Name == "Both");

        // F5: both under js-debug at once; each stops at its breakpoint, and the one that stopped is the one shown.
        vm.Breakpoints.Toggle(server, 2);
        vm.Breakpoints.Toggle(client, 2);
        await vm.StartDebuggingCommand.ExecuteAsync(null);
        var stoppedIn = new HashSet<string>();
        for (var i = 0; i < 4 && stoppedIn.Count < 2; i++)
        {
            await WaitUntil(() => vm.Mode == DebugMode.Break && vm.CallStack.Count > 0, TimeSpan.FromMinutes(2));
            Assert.True(vm.Mode == DebugMode.Break && vm.CallStack.Count > 0, $"{vm.Mode} at {i}, stopped in {string.Join(", ", stoppedIn.Select(Path.GetFileName))}\n{vm.DebugOutput.Document.Text}");
            Assert.Equal(2, vm.CallStack[0].Line);
            stoppedIn.Add(vm.CallStack[0].SourcePath!);
            // Continue: the shown one goes on, and the other, stopped meanwhile or later, is shown.
            await vm.StartDebuggingCommand.ExecuteAsync(null);
        }
        Assert.Equal([client, server], stoppedIn.Order());
        await WaitUntil(() => vm.DebugOutput.Document.Text.Contains("server on 8080") && vm.DebugOutput.Document.Text.Contains("client asks hello"), TimeSpan.FromSeconds(30));
        Assert.Equal(DebugMode.Running, vm.Mode);

        // Stop: both go.
        await vm.StopCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.Mode == DebugMode.Design, TimeSpan.FromSeconds(30));
        Assert.Equal(DebugMode.Design, vm.Mode);

        // stopAll: the program that ends by itself ends the one that would have run on.
        vm.Breakpoints.Toggle(server, 2);
        vm.Breakpoints.Toggle(client, 2);
        vm.StartupItem = vm.StartupItems.Single(i => i.Name == "Server and quick");
        await vm.StartDebuggingCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.DebugOutput.Document.Text.Contains("quick done") && vm.Mode == DebugMode.Design, TimeSpan.FromSeconds(30));
        Assert.True(vm.Mode == DebugMode.Design, vm.DebugOutput.Document.Text);
        Assert.True(await vm.CloseSolutionCoreAsync());
        window.Close();
        Directory.Delete(dir, recursive: true);
    }
}
