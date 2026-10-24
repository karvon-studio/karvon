using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Karvon.Claude;
using Karvon.Claude.Tools;
using Karvon.ViewModels;
using Karvon.Views;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>Claude's IDE tools on the sample solution: the build, the tests, the debugger and the editor, as Karvon has them.</summary>
// With the sample's other tests, one at a time: Windows locks the Web.dll a running test holds, which another's build would write.
[Collection(SampleSolutionCollection.Name)]
public class ClaudeIdeToolsTests
{
    internal static async Task<(string Text, bool IsError)> Call(ClaudeToolServer server, string tool, object arguments)
    {
        var message = JsonSerializer.SerializeToElement(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = tool, arguments } });
        var result = (await server.HandleAsync(message, CancellationToken.None))["result"]!;
        return (result["content"]![0]!["text"]!.GetValue<string>(), result["isError"]!.GetValue<bool>());
    }

    internal static async Task<string> Text(ClaudeToolServer server, string tool, object arguments)
    {
        var (text, isError) = await Call(server, tool, arguments);
        Assert.False(isError, text);
        return text;
    }

    [AvaloniaFact]
    public async Task Claude_builds_tests_reads_the_editor_and_debugs_through_Karvon()
    {
        var sln = await SampleSolution.GetAsync();
        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        window.Show();
        await vm.OpenSolutionAsync(sln);
        var server = ClaudeOf(vm).ToolServer();
        Assert.Equal(42, server.Tools.Count);
        var program = SampleSolution.PathOf(sln, "Web/Program.cs");

        // The build, as Build > Build Solution, and its errors.
        Assert.Equal("No build has run since Karvon opened the solution; call run_build.", await Text(server, "build_errors", new { }));
        Assert.Matches(@"^Build of BlazorSample\.sln succeeded", await Text(server, "run_build", new { }));
        Assert.StartsWith("Build of BlazorSample.sln succeeded", await Text(server, "build_errors", new { warnings = true }));
        var (unknown, unknownFailed) = await Call(server, "run_build", new { project = "Nope" });
        Assert.True(unknownFailed);
        Assert.Contains("Nope is not a project of the solution", unknown);

        // The tests, as Test Explorer runs them: counts, then each failure with its message and place.
        var tests = await Text(server, "run_tests", new { filter = "GreetingTests" });
        Assert.StartsWith("5 test(s): 3 passed, 1 failed, 1 skipped", tests);
        Assert.Contains("Tests.GreetingTests.Deliberately_failing: failed (Tests/GreetingTests.cs:", tests);
        Assert.Contains("Assert.Equal() Failure", tests);
        Assert.StartsWith("5 test(s): 3 passed, 1 failed", await Text(server, "test_failures", new { }));
        Assert.StartsWith("1 test(s): 0 passed, 1 failed", await Text(server, "run_tests", new { failedOnly = true }));
        Assert.Equal("No test's name contains nothing-like-this.", await Text(server, "run_tests", new { filter = "nothing-like-this" }));

        // The editor: what the user looks at, a file with line numbers, a search.
        var doc = vm.OpenDocument(program)!;
        var line2 = doc.Host.Document.GetLineByNumber(2);
        var start = doc.Host.Document.GetText(line2).IndexOf("builder.Build()", StringComparison.Ordinal);
        doc.Host.Editor.Select(line2.Offset + start, "builder.Build()".Length);
        var selection = await Text(server, "editor_selection", new { });
        Assert.Contains("File: Web/Program.cs", selection);
        Assert.Contains("Language: C#", selection);
        Assert.EndsWith("Selection from line 2:\nbuilder.Build()", selection);
        Assert.StartsWith("Web/Program.cs, lines 1-2 of ", await Text(server, "open_document", new { path = "Web/Program.cs", startLine = 1, endLine = 2 }));
        Assert.Contains("1\tvar builder = WebApplication.CreateBuilder(args);", await Text(server, "open_document", new { path = "Web/Program.cs", endLine = 1 }));
        var (outside, outsideFailed) = await Call(server, "open_document", new { path = "../../etc/passwd" });
        Assert.True(outsideFailed);
        Assert.Contains("is not in the solution's folder", outside);
        Assert.StartsWith("Web/Program.cs:4:5: app.MapGet(\"/\", () => \"Hello from the Karvon sample\");", await Text(server, "find_in_files", new { query = "mapget" }));
        Assert.Equal("No matches.", await Text(server, "find_in_files", new { query = "mapget", matchCase = true }));
        Assert.Contains("Web/Program.cs:1:", await Text(server, "find_in_files", new { query = @"Create\w+\(", regex = true }));
        Assert.StartsWith("Not a regular expression", (await Call(server, "find_in_files", new { query = "(", regex = true })).Text);

        // Roslyn's diagnostics, for a file as the editor has it (an unsaved mistake), and for one that is not open.
        doc.Host.Document.Insert(doc.Host.Document.TextLength, "\nint broken = \"text\";\n");
        var diagnostics = "";
        for (var tries = 0; tries < 60 && !diagnostics.Contains("[CS0029]", StringComparison.Ordinal); tries++)
        {
            if (tries > 0)
                await WaitUntil(() => false, TimeSpan.FromSeconds(1));
            diagnostics = await Text(server, "code_diagnostics", new { path = "Web/Program.cs" });
        }
        Assert.Matches(@"Web/Program\.cs:\d+:\d+: error: .*\[CS0029\]", diagnostics);
        doc.Host.Editor.Undo();
        var greeting = await Text(server, "code_diagnostics", new { path = "Components/Greeting.razor.cs" });
        Assert.DoesNotContain("error", greeting);

        // The debugger: started on a breakpoint, its state and variables, an expression, a step, then stopped.
        Assert.StartsWith("No debug session is running.", await Text(server, "debug_state", new { }));
        doc.Host.ToggleBreakpoint(2);
        Assert.Equal(ToolAccess.Write, server.Plan(ClaudeToolServer.Prefix + "debug_control", JsonSerializer.SerializeToElement(new { action = "start" }))!.Access);
        Assert.Equal(ToolAccess.Run, server.Plan(ClaudeToolServer.Prefix + "debug_control", JsonSerializer.SerializeToElement(new { action = "stepOver" }))!.Access);
        var started = await Text(server, "debug_control", new { action = "start" });
        Assert.Matches(@"^Stopped \(breakpoint\) in .* at Web/Program\.cs:2\.$", started);
        var state = await Text(server, "debug_state", new { });
        Assert.Contains("Call stack:\n> 0: ", state);
        Assert.Contains("Web/Program.cs:2", state);
        var locals = await Text(server, "debug_locals", new { depth = 1 });
        Assert.Contains("  builder: ", locals);
        Assert.Equal("builder.Environment.EnvironmentName = \"Development\" (string)", await Text(server, "debug_evaluate", new { expression = "builder.Environment.EnvironmentName" }));
        Assert.Matches(@"at Web/Program\.cs:4\.$", await Text(server, "debug_control", new { action = "stepOver" }));
        Assert.Equal("The session has ended.", await Text(server, "debug_control", new { action = "stop" }));
        Assert.True((await Call(server, "debug_locals", new { })).IsError);
        doc.Host.ToggleBreakpoint(2);

        // The app, as Ctrl+F5 runs it: where it listens and what it printed, then stopped; never twice.
        Assert.Equal(ToolAccess.Write, server.Plan(ClaudeToolServer.Prefix + "app_control", JsonSerializer.SerializeToElement(new { action = "start" }))!.Access);
        Assert.Equal(ToolAccess.Read, server.Plan(ClaudeToolServer.Prefix + "app_control", JsonSerializer.SerializeToElement(new { action = "status" }))!.Access);
        Assert.StartsWith("Web is not running", await Text(server, "app_control", new { action = "status" }));
        Assert.Contains("is not a runnable project", (await Call(server, "app_control", new { action = "start", project = "Tests" })).Text);
        var running = await Text(server, "app_control", new { action = "start", project = "Web" });
        Assert.Contains("Web is running", running);
        Assert.Contains("Listening on http://localhost:5280.", running);
        Assert.Contains("Now listening on: http://localhost:5280", running);
        Assert.True(vm.IsRunning);
        Assert.True((await Call(server, "app_control", new { action = "start" })).IsError);
        Assert.Contains("Listening on http://localhost:5280.", await Text(server, "app_control", new { action = "restart" }));
        var ended = await Text(server, "app_control", new { action = "stop", lines = 2 });
        Assert.StartsWith("Web is not running: it exited with code", ended);
        Assert.Contains("has exited with code", ended);
        Assert.False(vm.IsRunning);
        Assert.Equal("Nothing is running.", (await Call(server, "app_control", new { action = "stop" })).Text);
        Assert.True(await vm.CloseSolutionCoreAsync());
        window.Close();
    }
}
