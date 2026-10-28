using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Karvon.Go;
using Karvon.Lsp;
using Karvon.Modules;
using Karvon.Views;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>The Go module: gopls, Delve, a module's go tasks and go test's tests, with the Go the user has.</summary>
public class GoTests
{
    static string NewModule()
    {
        var dir = Path.Combine(Path.GetTempPath(), "karvon-go-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "shapes"));
        File.WriteAllText(Path.Combine(dir, "go.mod"), "module example.com/shop\n\ngo 1.22\n");
        File.WriteAllText(Path.Combine(dir, "main.go"), "package main\n\nimport (\n\t\"fmt\"\n\n\t\"example.com/shop/shapes\"\n)\n\nfunc main() {\n\tsize := shapes.Area(3, 4)\n\tfmt.Println(\"area\", size)\n}\n");
        File.WriteAllText(Path.Combine(dir, "shapes", "shapes.go"), "package shapes\n\n// Area is a rectangle's.\nfunc Area(w, h int) int {\n\treturn w * h\n}\n");
        File.WriteAllText(Path.Combine(dir, "shapes", "shapes_test.go"), """
            package shapes

            import "testing"

            func TestArea(t *testing.T) {
            	if Area(3, 4) != 12 {
            		t.Fatal("wrong")
            	}
            }

            func TestZero(t *testing.T) {
            	if got := Area(0, 4); got != 1 {
            		t.Errorf("Area(0, 4) = %d, want 1", got)
            	}
            }

            func TestLater(t *testing.T) {
            	t.Skip("later")
            }
            """);
        return dir;
    }

    [Fact]
    public void A_go_module_has_its_tasks_and_its_errors_read()
    {
        var dir = NewModule();
        var tasks = GoTasks.Read(dir);
        Assert.Equal(["go: build", "go: test", "go: vet", "go: run"], tasks.Select(t => t.Name));
        // The build compiles the tests too: go vet after go build.
        Assert.Equal("go build ./... && go vet ./...", tasks[0].Arguments[^1]);
        Assert.Equal(("go", "debug", dir), (tasks[3].Debug!.Type, tasks[3].Debug!.Configuration["mode"]!.GetValue<string>(), tasks[3].Debug!.Configuration["program"]!.GetValue<string>()));

        // The compiler's, vet's and a test's form of an error.
        var error = GoTasks.Problem("./main.go:10:2: undefined: sizes")!;
        Assert.Equal(("./main.go", 10, 2, "undefined: sizes"), (error.File, error.Line, error.Column, error.Message));
        Assert.Equal(12, GoTasks.Problem("shapes/shapes_test.go:12: Area(0, 4) = 0, want 1")!.Line);
        var vet = GoTasks.Problem("vet: stock/stock_test.go:73:19: sample(t).LowStock undefined (type *Store has no field or method LowStock)")!;
        Assert.Equal(("stock/stock_test.go", 73, 19), (vet.File, vet.Line, vet.Column));
        Assert.Null(GoTasks.Problem("ok  \texample.com/shop/shapes\t0.002s"));
        Assert.Null(GoTasks.Problem("https://example.com:8080: refused"));

        // launch.json's "auto" is the Go extension's: Delve debugs, or tests a test file; its binary out of the folder.
        var configuration = new JsonObject { ["type"] = "go", ["request"] = "launch", ["mode"] = "auto", ["program"] = Path.Combine(dir, "shapes", "shapes_test.go") };
        GoDebugger.Complete(configuration, dir);
        Assert.Equal("test", configuration["mode"]!.GetValue<string>());
        Assert.StartsWith(Path.GetTempPath(), configuration["output"]!.GetValue<string>());
        Assert.Empty(GoTasks.Read(Path.Combine(dir, "shapes")));
        Directory.Delete(dir, recursive: true);
    }

    static bool HasGo() => Environment.GetEnvironmentVariable("KARVON_GO_LIVE") == "1"
        && Workspace.TaskVariables.Find("go", GoTools.PathWithGo(Environment.GetEnvironmentVariable("PATH") ?? ""), "/") is not null;

    [AvaloniaFact]
    public async Task Gopls_checks_and_delve_debugs_a_module()
    {
        Assert.SkipUnless(HasGo(), "Set KARVON_GO_LIVE=1, with Go installed, to install gopls and Delve and try them.");
        var dir = NewModule();
        try
        {
            var vm = NewViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            await vm.OpenSolutionAsync(dir);

            // gopls: the error as it is typed, then gone; hover and go to definition into the package.
            var main = Path.Combine(dir, "main.go");
            var doc = vm.OpenDocument(main)!;
            var line = doc.Host.Document.GetLineByNumber(11);
            doc.Host.Document.Replace(line.Offset, line.Length, "\tfmt.Println(\"area\", sizes)");
            await WaitUntil(() => doc.Host.Diagnostics.Count > 0, TimeSpan.FromMinutes(4));
            // The compiler's errors, as go build has them: the name undefined, and the variable no longer used.
            await WaitUntil(() => doc.Host.Diagnostics.Any(d => d.Message.Contains("undefined: sizes")), TimeSpan.FromSeconds(30));
            var error = doc.Host.Diagnostics.Single(d => d.Message.Contains("undefined: sizes"));
            Assert.Equal((DiagnosticSeverity.Error, 10), (error.Severity, error.Range.Start.Line));
            Assert.Contains(doc.Host.Diagnostics, d => d.Message == "declared and not used: size");
            doc.Host.Document.Replace(line.Offset, line.Length, "\tfmt.Println(\"area\", size)");
            await WaitUntil(() => doc.Host.Diagnostics.Count == 0, TimeSpan.FromSeconds(30));
            Assert.Empty(doc.Host.Diagnostics);
            var server = Assert.IsType<GoplsServer>(Assert.Single(vm.ModuleLanguageServers));
            Assert.Contains("Area is a rectangle's", await server.HoverAsync(main, new LspPosition(9, 18), CancellationToken.None));
            var definition = Assert.Single(await server.DefinitionAsync(main, new LspPosition(9, 18), CancellationToken.None));
            Assert.EndsWith("shapes.go", new Uri(definition.Uri).LocalPath);
            doc.Host.Save();

            // F5 on go: run: Delve builds it and stops in the package's code.
            Assert.Equal("go: run", vm.StartupItem?.Name);
            var shapes = Path.Combine(dir, "shapes", "shapes.go");
            vm.Breakpoints.Toggle(shapes, 5);
            var server2 = ClaudeOf(vm).ToolServer();
            var started = await ClaudeIdeToolsTests.Text(server2, "debug_control", new { action = "start" });
            Assert.True(vm.Mode == ViewModels.DebugMode.Break, started + "\n" + vm.DebugOutput.Document.Text);
            Assert.Matches(@"in .*Area at shapes/shapes\.go:5\.$", started);
            Assert.Equal("w * h = 12 (int)", await ClaudeIdeToolsTests.Text(server2, "debug_evaluate", new { expression = "w * h" }));
            await vm.StartDebuggingCommand.ExecuteAsync(null);
            await WaitUntil(() => vm.Mode == ViewModels.DebugMode.Design, TimeSpan.FromSeconds(30));
            Assert.True(vm.DebugOutput.Document.Text.Contains("area 12"), vm.DebugOutput.Document.Text);
            vm.Breakpoints.Toggle(shapes, 5);
            Assert.True(await vm.CloseSolutionCoreAsync());
            window.Close();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Go_test_builds_and_tests_in_Test_Explorer()
    {
        Assert.SkipUnless(HasGo(), "Set KARVON_GO_LIVE=1, with Go installed, to build and test with it.");
        var dir = NewModule();
        try
        {
            var vm = NewViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            await vm.OpenSolutionAsync(dir);

            // Build: go build's errors in the Error List, where they are.
            File.WriteAllText(Path.Combine(dir, "broken.go"), "package main\n\nfunc broken() int {\n\treturn missing\n}\n");
            await vm.BuildSolutionCommand.ExecuteAsync(null);
            var error = Assert.Single(vm.Errors);
            Assert.Equal((Path.Combine(dir, "broken.go"), 4, 9, "undefined: missing"), (error.FilePath, error.Line, error.Column, error.Description));
            File.Delete(Path.Combine(dir, "broken.go"));

            // Test Explorer: the test functions, found in the files; run by go test -json.
            await vm.RunAllTestsCommand.ExecuteAsync(null);
            Assert.True(vm.TestSummary.StartsWith("Test run finished: 3 test(s) (1 Passed, 1 Failed, 1 Skipped)", StringComparison.Ordinal), vm.TestSummary + "\n" + vm.TestsOutput.Document.Text);
            var package = Assert.Single(Assert.Single(vm.TestRoots).Children);
            Assert.Equal(("example.com/shop", "example.com/shop/shapes"), (vm.TestRoots[0].Name, package.Name));
            var area = package.Children.Single(n => n.Name == "TestArea");
            Assert.Equal((Testing.TestState.Passed, Path.Combine(dir, "shapes", "shapes_test.go"), 5), (area.State, area.SourcePath, area.Line));
            var zero = package.Children.Single(n => n.Name == "TestZero");
            Assert.Equal((Testing.TestState.Failed, "shapes_test.go:13: Area(0, 4) = 0, want 1"), (zero.State, zero.ErrorMessage));
            Assert.Equal(Testing.TestState.Skipped, package.Children.Single(n => n.Name == "TestLater").State);

            // Run Failed Tests: that one, by -run.
            await vm.RunFailedTestsCommand.ExecuteAsync(null);
            Assert.Equal((Testing.TestState.Passed, Testing.TestState.Failed), (area.State, zero.State));
            Assert.Contains("-run '^(?:TestZero)$'", vm.TestsOutput.Document.Text);

            // Debug Selected Tests: Delve in its test mode, stopped in the test.
            vm.SelectedTest = area;
            var test = Path.Combine(dir, "shapes", "shapes_test.go");
            vm.Breakpoints.Toggle(test, 6);
            var debugging = vm.DebugSelectedTestsCommand.ExecuteAsync(null);
            await WaitUntil(() => vm.Mode == ViewModels.DebugMode.Break && vm.CallStack.Count > 0, TimeSpan.FromMinutes(2));
            Assert.True(vm.Mode == ViewModels.DebugMode.Break, vm.DebugOutput.Document.Text + "\n" + vm.TestsOutput.Document.Text);
            Assert.Equal((test, 6), (vm.CallStack[0].SourcePath, vm.CallStack[0].Line));
            await vm.StartDebuggingCommand.ExecuteAsync(null);
            await debugging.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
            Assert.Equal(Testing.TestState.Passed, area.State);
            Assert.True(await vm.CloseSolutionCoreAsync());
            window.Close();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
