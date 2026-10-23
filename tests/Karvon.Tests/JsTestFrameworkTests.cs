using Avalonia.Headless.XUnit;
using Karvon.Testing;
using Karvon.ViewModels;
using Karvon.TypeScript;
using Karvon.Views;
using Karvon.Workspace;
using static Karvon.Tests.ClaudeIdeToolsTests;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>Vitest's and Jest's tests in Test Explorer, found and run by the package's own copy.</summary>
public class JsTestFrameworkTests
{
    static string NewPackage(string framework = "vitest")
    {
        var dir = Path.Combine(Path.GetTempPath(), "karvon-vitest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "src"));
        File.WriteAllText(Path.Combine(dir, "package.json"), """
            { "name": "shapes", "private": true, "type": "module", "scripts": { "test": "vitest run" }, "devDependencies": { "vitest": "^3.2.0" } }
            """.Replace("\"vitest\": \"^3.2.0\"", framework == "jest" ? "\"jest\": \"^30.0.0\"" : "\"vitest\": \"^3.2.0\"").Replace("\"type\": \"module\", ", framework == "jest" ? "" : "\"type\": \"module\", "));
        File.WriteAllText(Path.Combine(dir, "src", "math.test.ts"), """
            import { describe, it, expect } from "vitest";
            describe("area", () => {
              it("multiplies", () => { expect(3 * 4).toBe(12); });
              describe("of nothing", () => {
                it("is zero", () => { expect(0 * 4).toBe(1); });
              });
            });
            it.skip("later", () => {});
            """);
        return dir;
    }

    [AvaloniaFact]
    public async Task A_package_that_depends_on_vitest_tests_in_Test_Explorer()
    {
        var dir = NewPackage();
        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        await vm.OpenSolutionAsync(dir);

        // Its tests are Test Explorer's (not the test script's), found when the user runs them: until then vitest runs not.
        Assert.True(vm.RunAllTestsCommand.CanExecute(null));
        Assert.Equal("Run All Tests to discover the tests.", vm.TestSummary);
        await vm.RunAllTestsCommand.ExecuteAsync(null);
        Assert.Equal("Vitest is not installed in " + Path.GetFileName(dir) + ": install its packages (npm install).", vm.TestSummary);
        Assert.Empty(vm.TestRoots);
        Assert.DoesNotContain("> npm run test", vm.TestsOutput.Document.Text);
        Assert.True(await vm.CloseSolutionCoreAsync());
        window.Close();
        Directory.Delete(dir, recursive: true);
    }

    [AvaloniaFact]
    public async Task Vitest_finds_runs_and_reports_the_tests()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("KARVON_TYPESCRIPT_LIVE") == "1", "Set KARVON_TYPESCRIPT_LIVE=1 to install Vitest from npm and run it.");
        var dir = NewPackage();
        Assert.SkipUnless(await InstallAsync(dir), "Node.js is not installed.");
        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        await vm.OpenSolutionAsync(dir);

        // Run All Tests: found by vitest list, run by vitest run; the skipped test joins the tree as it is reported.
        await vm.RunAllTestsCommand.ExecuteAsync(null);
        Assert.True(vm.TestSummary.StartsWith("Test run finished: 3 test(s) (1 Passed, 1 Failed, 1 Skipped)", StringComparison.Ordinal), vm.TestSummary + "\n" + vm.TestsOutput.Document.Text);
        var project = Assert.Single(vm.TestRoots);
        Assert.Equal((TestNodeKind.Project, Path.GetFileName(dir)), (project.Kind, project.Name));
        var file = Assert.Single(project.Children);
        Assert.Equal((TestNodeKind.Namespace, "src/math.test.ts", TestState.Failed), (file.Kind, file.Name, file.State));
        var area = file.Children.Single(n => n.Name == "area");
        var multiplies = area.Children.Single(n => n.Name == "multiplies");
        Assert.Equal((TestState.Passed, Path.Combine(dir, "src", "math.test.ts"), 3), (multiplies.State, multiplies.SourcePath, multiplies.Line));
        var zero = area.Children.Single(n => n.Name == "of nothing").Children.Single();
        Assert.Equal(("is zero", TestState.Failed), (zero.Name, zero.State));
        Assert.Equal("AssertionError: expected +0 to be 1 // Object.is equality", zero.ErrorMessage);
        Assert.Contains("math.test.ts:5", zero.StackTrace);
        Assert.Equal(TestState.Skipped, file.Children.Single(n => n.Name == "later").State);

        // Run Failed Tests: only that one runs (-t); the others keep what they had.
        await vm.RunFailedTestsCommand.ExecuteAsync(null);
        Assert.Equal((TestState.Passed, TestState.Failed), (multiplies.State, zero.State));
        Assert.Contains("-t", vm.TestsOutput.Document.Text);

        // Claude's run_tests: Test Explorer's run, its failures with where they are.
        var server = ClaudeOf(vm).ToolServer();
        var report = await Text(server, "run_tests", new { });
        Assert.Contains("area > of nothing > is zero", report);
        Assert.Contains("expected +0 to be 1", report);

        // Debug Selected Tests: vitest under js-debug, stopped on the breakpoint in the test's TypeScript.
        var debugged = Path.Combine(dir, "src", "debug.test.ts");
        File.WriteAllText(debugged, """
            import { it, expect } from "vitest";
            it("adds", () => {
              const sum = 2 + 3;
              expect(sum).toBe(5);
            });
            """);
        // Found again, the tree is made again, with the results it had.
        await vm.RefreshTestsCommand.ExecuteAsync(null);
        project = Assert.Single(vm.TestRoots);
        var adds = project.Children.Single(n => n.Name == "src/debug.test.ts").Children.Single();
        multiplies = project.Children.Single(n => n.Name == "src/math.test.ts").Children.Single(n => n.Name == "area").Children.Single(n => n.Name == "multiplies");
        Assert.Equal(TestState.Passed, multiplies.State);
        vm.SelectedTest = adds;
        vm.Breakpoints.Toggle(debugged, 4);
        Assert.True(vm.DebugSelectedTestsCommand.CanExecute(null));
        var debugging = vm.DebugSelectedTestsCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.Mode == DebugMode.Break && vm.CallStack.Count > 0, TimeSpan.FromSeconds(60));
        Assert.True(vm.Mode == DebugMode.Break, vm.DebugOutput.Document.Text + "\n" + vm.TestsOutput.Document.Text);
        Assert.Equal((debugged, 4), (vm.CallStack[0].SourcePath, vm.CallStack[0].Line));
        Assert.Equal("sum = 5 (number)", await Text(server, "debug_evaluate", new { expression = "sum" }));
        await vm.StartDebuggingCommand.ExecuteAsync(null);
        await debugging.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        Assert.Equal(DebugMode.Design, vm.Mode);
        Assert.Equal(TestState.Passed, adds.State);
        Assert.True(await vm.CloseSolutionCoreAsync());
        window.Close();
        Directory.Delete(dir, recursive: true);
    }

    /// <summary>npm install in a package (from the registry), as its user would; false when Node.js is not here.</summary>
    static async Task<bool> InstallAsync(string dir)
    {
        var path = string.Join(Path.PathSeparator, NodeLocator.DirectoryOffPath() ?? "", Environment.GetEnvironmentVariable("PATH") ?? "");
        if (TaskVariables.Find("npm", path, "/") is not { } npm)
            return false;
        var installed = new List<string>();
        using var runner = Karvon.Build.ProcessRunner.Start(npm, ["install", "--ignore-scripts", "--no-audit", "--no-fund"], dir, installed.Add, new Dictionary<string, string> { ["PATH"] = path });
        Assert.True(await runner.WaitAsync(TestContext.Current.CancellationToken) == 0, string.Join("\n", installed));
        return true;
    }

    [AvaloniaFact]
    public async Task Jest_names_its_tests_as_it_runs_them()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("KARVON_TYPESCRIPT_LIVE") == "1", "Set KARVON_TYPESCRIPT_LIVE=1 to install Jest from npm and run it.");
        var dir = NewPackage("jest");
        var test = Path.Combine(dir, "src", "math.test.js");
        File.Move(Path.Combine(dir, "src", "math.test.ts"), test);
        // Jest takes the file as CommonJS, its globals as they are.
        File.WriteAllText(test, File.ReadAllText(test).Replace("import { describe, it, expect } from \"vitest\";\n", ""));
        Assert.SkipUnless(await InstallAsync(dir), "Node.js is not installed.");
        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        await vm.OpenSolutionAsync(dir);

        // Nothing before the first run; then each test where it is, by its describe blocks.
        await vm.RunAllTestsCommand.ExecuteAsync(null);
        Assert.True(vm.TestSummary.StartsWith("Test run finished: 3 test(s) (1 Passed, 1 Failed, 1 Skipped)", StringComparison.Ordinal), vm.TestSummary + "\n" + vm.TestsOutput.Document.Text);
        var file = Assert.Single(Assert.Single(vm.TestRoots).Children);
        Assert.Equal("src/math.test.js", file.Name);
        var area = file.Children.Single(n => n.Name == "area");
        // Where it is (the file has no import line: line 2).
        Assert.Equal((TestState.Passed, 2), (area.Children.Single(n => n.Name == "multiplies").State, area.Children.Single(n => n.Name == "multiplies").Line));
        var zero = area.Children.Single(n => n.Name == "of nothing").Children.Single();
        Assert.Equal(TestState.Failed, zero.State);
        Assert.StartsWith("Error: expect(received).toBe(expected)", zero.ErrorMessage);
        Assert.Equal(TestState.Skipped, file.Children.Single(n => n.Name == "later").State);

        // Run Failed Tests: that one only.
        await vm.RunFailedTestsCommand.ExecuteAsync(null);
        Assert.Equal(TestState.Failed, zero.State);
        Assert.Equal(TestState.Passed, area.Children.Single(n => n.Name == "multiplies").State);
        Assert.True(await vm.CloseSolutionCoreAsync());
        window.Close();
        Directory.Delete(dir, recursive: true);
    }
}
