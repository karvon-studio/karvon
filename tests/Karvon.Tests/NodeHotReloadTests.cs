using Avalonia.Headless.XUnit;
using Karvon.Debugging;
using Karvon.Modules;
using Karvon.TypeScript;
using Karvon.Views;
using Karvon.Workspace;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>Hot Reload of a Node.js program: the agent in it applies saved changes in place (V8's LiveEdit), or asks for a restart.</summary>
public class NodeHotReloadTests
{
    [Fact]
    public void The_agents_messages_are_read_and_left_out_of_the_output()
    {
        List<RunNews> news = [];
        Assert.Equal("says one\n", HotReloadMessages.Take("says one\n", news));
        Assert.Empty(news);
        var text = HotReloadMessages.Take("\u001b]7700;{\"live\":\"/w/server.js\"}\u0007\nsays one\n\u001b]7700;{\"applied\":\"/w/server.js\",\"ms\":12}\u0007\n", news);
        Assert.Equal("says one\n", text);
        Assert.Equal([new RunNews(null) { Live = "/w/server.js" }, new RunNews("Hot Reload: server.js applied in 12 ms")], news);
        news.Clear();
        Assert.Equal("", HotReloadMessages.Take("\u001b]7700;{\"error\":\"/w/server.js\",\"line\":2,\"message\":\"Uncaught SyntaxError: Unexpected token ';'\"}\u0007", news));
        Assert.Equal("Hot Reload: server.js(2): Uncaught SyntaxError: Unexpected token ';'", news.Single().Status);
        news.Clear();
        HotReloadMessages.Take("\u001b]7700;{\"restart\":\"/w/server.js\",\"reason\":\"a change outside its functions\"}\u0007\n", news);
        Assert.Equal("/w/server.js", news.Single().Restart);
        Assert.Equal("Hot Reload: server.js needs a restart (a change outside its functions)", news.Single().Status);
        // Not its JSON: left out, and nothing said.
        news.Clear();
        Assert.Equal("x", HotReloadMessages.Take("\u001b]7700;not json\u0007x", news));
        Assert.Empty(news);
    }

    [Fact]
    public void A_dev_server_or_a_watcher_reloads_by_itself()
    {
        foreach (var command in new[] { "vite", "vite --port 3000", "next dev", "nuxt dev", "nodemon server.js", "tsx watch src/index.ts", "node --watch server.js",
            "ng serve", "react-scripts start", "webpack serve --mode development", "cross-env NODE_ENV=dev vite", "bun --hot index.ts", "astro dev" })
            Assert.True(NodeHotReload.ReloadsItself(command), command);
        foreach (var command in new[] { "node server.js", "node dist/index.js", "tsx src/index.ts", "ts-node app.ts", "nest start", "node --inspect server.js", "next start", "serve-static ." })
            Assert.False(NodeHotReload.ReloadsItself(command), command);
    }

    [Fact]
    public void A_node_launch_gets_the_agent_in_its_NODE_OPTIONS()
    {
        var launch = new System.Text.Json.Nodes.JsonObject { ["type"] = "pwa-node", ["request"] = "launch", ["env"] = new System.Text.Json.Nodes.JsonObject { ["NODE_OPTIONS"] = "--max-old-space-size=4096" } };
        JsDebugger.AddHotReload(launch, "/w");
        Assert.Equal($"--max-old-space-size=4096 --require \"{NodeHotReload.Agent.Replace('\\', '/')}\"", launch["env"]!["NODE_OPTIONS"]!.GetValue<string>());
        Assert.Equal("/w", launch["env"]!["KARVON_HOT_RELOAD_ROOT"]!.GetValue<string>());
        // Not an attach, nor a browser's page.
        var attach = new System.Text.Json.Nodes.JsonObject { ["type"] = "pwa-node", ["request"] = "attach" };
        JsDebugger.AddHotReload(attach, "/w");
        Assert.Null(attach["env"]);
        var chrome = new System.Text.Json.Nodes.JsonObject { ["type"] = "pwa-chrome", ["request"] = "launch" };
        JsDebugger.AddHotReload(chrome, "/w");
        Assert.Null(chrome["env"]);
    }

    [AvaloniaFact]
    public async Task A_saved_function_applies_in_place_and_a_change_outside_one_restarts_the_run()
    {
        var path = string.Join(Path.PathSeparator, Karvon.Build.NodeLocator.DirectoryOffPath() ?? "", Environment.GetEnvironmentVariable("PATH") ?? "");
        Assert.SkipUnless(TaskVariables.Find("npm", path, "/") is not null, "Node.js is not installed.");
        var dir = Directory.CreateTempSubdirectory("karvon-node-hot-reload-").FullName;
        File.WriteAllText(Path.Combine(dir, "package.json"), """{ "name": "app", "scripts": { "start": "node server.js" } }""");
        var server = Path.Combine(dir, "server.js");
        File.WriteAllText(server, """
            const fs = require('node:fs');
            console.log('started with ' + fs.readFileSync(__dirname + '/greeting.json', 'utf8').trim());
            const handler = () => 'one';
            setInterval(() => console.log('says ' + handler()), 200);

            """);
        File.WriteAllText(Path.Combine(dir, "greeting.json"), "\"hi\"\n");
        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        int Started() => vm.DebugOutput.Document.Text.Split('\n').Count(l => l.StartsWith("started with", StringComparison.Ordinal));
        try
        {
            window.Show();
            await vm.OpenSolutionAsync(dir);
            vm.StartupItem = vm.StartupItems.Single(i => i.Name == "npm: start");
            var run = vm.StartWithoutDebuggingCommand.ExecuteAsync(null);
            await WaitUntil(() => vm.DebugOutput.Document.Text.Contains("says one", StringComparison.Ordinal), TimeSpan.FromSeconds(30));
            Assert.True(vm.DebugOutput.Document.Text.Contains("says one", StringComparison.Ordinal), vm.DebugOutput.Document.Text);
            // The agent's messages are not in Output.
            Assert.DoesNotContain("7700", vm.DebugOutput.Document.Text);

            // The handler changed in the editor and saved: applied in place, the program going on (it started once).
            var doc = vm.OpenDocument(server)!;
            doc.Host.Editor.Document.Text = doc.Host.Editor.Document.Text.Replace("'one'", "'two'", StringComparison.Ordinal);
            doc.Host.Save();
            await WaitUntil(() => vm.DebugOutput.Document.Text.Contains("says two", StringComparison.Ordinal), TimeSpan.FromSeconds(15));
            Assert.Contains("says two", vm.DebugOutput.Document.Text);
            Assert.Equal(1, Started());
            await WaitUntil(() => vm.StatusText.StartsWith("Hot Reload: server.js applied in", StringComparison.Ordinal), TimeSpan.FromSeconds(5));
            Assert.StartsWith("Hot Reload: server.js applied in", vm.StatusText);

            // Code that does not compile: said, nothing applied nor restarted.
            doc.Host.Editor.Document.Text = doc.Host.Editor.Document.Text.Replace("'two'", "'two' +", StringComparison.Ordinal);
            doc.Host.Save();
            await WaitUntil(() => vm.StatusText.Contains("SyntaxError", StringComparison.Ordinal), TimeSpan.FromSeconds(10));
            Assert.StartsWith("Hot Reload: server.js(3): Uncaught SyntaxError", vm.StatusText);
            Assert.Equal(1, Started());

            // A change outside its functions (the code that ran as it loaded): the run restarts with it (Output anew).
            doc.Host.Editor.Document.Text = doc.Host.Editor.Document.Text.Replace("'two' +", "'three'", StringComparison.Ordinal).Replace("200);", "150);", StringComparison.Ordinal);
            doc.Host.Save();
            await WaitUntil(() => vm.StatusText == "Restart on Save: npm: start restarted with server.js", TimeSpan.FromSeconds(30));
            Assert.Equal("Restart on Save: npm: start restarted with server.js", vm.StatusText);
            await WaitUntil(() => vm.DebugOutput.Document.Text.Contains("says three", StringComparison.Ordinal), TimeSpan.FromSeconds(15));
            Assert.Contains("says three", vm.DebugOutput.Document.Text);
            Assert.Equal(1, Started());

            // A file the program reads, not one it loaded: Restart on Save.
            var greeting = vm.OpenDocument(Path.Combine(dir, "greeting.json"))!;
            greeting.Host.Editor.Document.Text = "\"salom\"\n";
            greeting.Host.Save();
            await WaitUntil(() => vm.DebugOutput.Document.Text.Contains("started with \"salom\"", StringComparison.Ordinal), TimeSpan.FromSeconds(30));
            Assert.Contains("started with \"salom\"", vm.DebugOutput.Document.Text);

            await vm.StopCommand.ExecuteAsync(null);
            await WaitUntil(() => !vm.IsRunning, TimeSpan.FromSeconds(10));
            Assert.False(vm.IsRunning);
        }
        finally
        {
            await vm.CloseSolutionCoreAsync();
            window.Close();
            Directory.Delete(dir, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Under_the_debugger_a_saved_function_applies_in_place_too()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("KARVON_TYPESCRIPT_LIVE") == "1", "Set KARVON_TYPESCRIPT_LIVE=1 to download js-debug and debug with it.");
        var path = string.Join(Path.PathSeparator, Karvon.Build.NodeLocator.DirectoryOffPath() ?? "", Environment.GetEnvironmentVariable("PATH") ?? "");
        Assert.SkipUnless(TaskVariables.Find("node", path, "/") is not null, "Node.js is not installed.");
        var dir = Directory.CreateTempSubdirectory("karvon-node-hot-reload-").FullName;
        Directory.CreateDirectory(Path.Combine(dir, ".vscode"));
        File.WriteAllText(VsCodeLaunch.FileOf(dir), """
            { "version": "0.2.0", "configurations": [ { "type": "node", "request": "launch", "name": "Server", "program": "${workspaceFolder}/server.js" } ] }
            """);
        var server = Path.Combine(dir, "server.js");
        File.WriteAllText(server, "const handler = () => 'one';\nsetInterval(() => console.log('says ' + handler()), 200);\n");
        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        try
        {
            window.Show();
            await vm.OpenSolutionAsync(dir);
            vm.StartupItem = vm.StartupItems.Single(i => i.Name == "Server");
            await vm.StartDebuggingCommand.ExecuteAsync(null);
            await WaitUntil(() => vm.DebugOutput.Document.Text.Contains("says one", StringComparison.Ordinal), TimeSpan.FromSeconds(60));
            Assert.True(vm.DebugOutput.Document.Text.Contains("says one", StringComparison.Ordinal), vm.DebugOutput.Document.Text);
            var doc = vm.OpenDocument(server)!;
            doc.Host.Editor.Document.Text = doc.Host.Editor.Document.Text.Replace("'one'", "'two'", StringComparison.Ordinal);
            doc.Host.Save();
            await WaitUntil(() => vm.DebugOutput.Document.Text.Contains("says two", StringComparison.Ordinal), TimeSpan.FromSeconds(15));
            Assert.True(vm.DebugOutput.Document.Text.Contains("says two", StringComparison.Ordinal), vm.StatusText + "\n" + vm.DebugOutput.Document.Text);
            Assert.StartsWith("Hot Reload: server.js applied in", vm.StatusText);
            Assert.DoesNotContain("7700", vm.DebugOutput.Document.Text);
            Assert.Equal(Karvon.ViewModels.DebugMode.Running, vm.Mode);
        }
        finally
        {
            await vm.CloseSolutionCoreAsync();
            window.Close();
            Directory.Delete(dir, recursive: true);
        }
    }
}
