using Avalonia.Headless.XUnit;
using Karvon.Lsp;
using Karvon.Rust;
using Karvon.Views;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>The Rust module: rust-analyzer, CodeLLDB and a Cargo package's tasks, with the cargo the user has.</summary>
public class RustTests
{
    static string NewPackage()
    {
        var dir = Path.Combine(Path.GetTempPath(), "karvon-rust-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "src"));
        File.WriteAllText(Path.Combine(dir, "Cargo.toml"), "[package]\nname = \"shapes\"\nversion = \"0.1.0\"\nedition = \"2021\"\n");
        File.WriteAllText(Path.Combine(dir, "src", "main.rs"),
            "/// A rectangle's area.\nfn area(w: i32, h: i32) -> i32 {\n    let result = w * h;\n    result\n}\n\nfn main() {\n    let size = area(3, 4);\n    println!(\"area {size}\");\n}\n");
        return dir;
    }

    [Fact]
    public void A_cargo_package_has_its_tasks_and_its_errors_read()
    {
        var dir = NewPackage();
        var tasks = CargoTasks.Read(dir);
        Assert.Equal(["cargo: build", "cargo: check", "cargo: clippy", "cargo: test", "cargo: run"], tasks.Select(t => t.Name));
        Assert.Equal("lldb", tasks[^1].Debug!.Type);
        var error = CargoTasks.Problem("src/main.rs:8:16: error[E0425]: cannot find value `sizes` in this scope")!;
        Assert.Equal(("src/main.rs", 8, 16, "E0425", "cannot find value `sizes` in this scope", DiagnosticSeverity.Error),
            (error.File, error.Line, error.Column, error.Code, error.Message, error.Severity));
        Assert.Equal(DiagnosticSeverity.Warning, CargoTasks.Problem("src/main.rs:3:9: warning: unused variable: `x`")!.Severity);
        Assert.Null(CargoTasks.Problem("   Compiling shapes v0.1.0 (/tmp/shapes)"));
        Assert.Empty(CargoTasks.Read(Path.Combine(dir, "src")));
        Directory.Delete(dir, recursive: true);
    }

    [AvaloniaFact]
    public async Task Rust_analyzer_checks_codelldb_debugs_and_cargo_builds()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("KARVON_RUST_LIVE") == "1"
            && Workspace.TaskVariables.Find("cargo", RustTools.PathWithCargo(Environment.GetEnvironmentVariable("PATH") ?? ""), "/") is not null,
            "Set KARVON_RUST_LIVE=1, with Rust installed, to download rust-analyzer and CodeLLDB and try them.");
        var dir = NewPackage();
        try
        {
            var vm = NewViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            await vm.OpenSolutionAsync(dir);

            // rust-analyzer: an error as it is typed (once the package is loaded), then gone; hover.
            var main = Path.Combine(dir, "src", "main.rs");
            var doc = vm.OpenDocument(main)!;
            await WaitUntil(() => vm.ModuleLanguageServers.Count() == 1 && doc.Host.HasLanguageServer, TimeSpan.FromMinutes(3));
            var server = Assert.IsType<RustAnalyzerServer>(Assert.Single(vm.ModuleLanguageServers));
            var hover = "";
            for (var i = 0; i < 60 && !hover.Contains("A rectangle's area."); i++)
            {
                hover = await server.HoverAsync(main, new LspPosition(7, 16), CancellationToken.None) ?? "";
                if (!hover.Contains("A rectangle's area."))
                    await WaitUntil(() => false, TimeSpan.FromSeconds(1));
            }
            Assert.Contains("A rectangle's area.", hover);
            var line = doc.Host.Document.GetLineByNumber(9);
            // Saved, cargo check runs (rust-analyzer's flycheck, as in VS Code): its error shows where it is.
            doc.Host.Document.Replace(line.Offset, line.Length, "    println!(\"area {}\", sizes);");
            doc.Host.Save();
            await WaitUntil(() => doc.Host.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error), TimeSpan.FromMinutes(2));
            Assert.Contains(doc.Host.Diagnostics, d => d.Severity == DiagnosticSeverity.Error && d.Range.Start.Line == 8);

            // Build: cargo's errors in the Error List, where they are.
            Assert.Equal("cargo: build", vm.BuildTask?.Name);
            await vm.BuildSolutionCommand.ExecuteAsync(null);
            var error = Assert.Single(vm.Errors, e => e.Code == "E0425");
            Assert.Equal((main, 9), (error.FilePath, error.Line));
            doc.Host.Document.Replace(line.Offset, doc.Host.Document.GetLineByNumber(9).Length, "    println!(\"area {size}\");");
            doc.Host.Save();

            // F5 on cargo: run: cargo builds it, CodeLLDB stops in the function; its variables, then to its end.
            Assert.Equal("cargo: run", vm.StartupItem?.Name);
            vm.Breakpoints.Toggle(main, 3);
            var tools = ClaudeOf(vm).ToolServer();
            var started = await ClaudeIdeToolsTests.Text(tools, "debug_control", new { action = "start" });
            Assert.True(vm.Mode == ViewModels.DebugMode.Break, started + "\n" + vm.DebugOutput.Document.Text);
            Assert.True(System.Text.RegularExpressions.Regex.IsMatch(started, @"area.* at src/main\.rs:3\.$"), started);
            var locals = await ClaudeIdeToolsTests.Text(tools, "debug_locals", new { depth = 1 });
            Assert.True(locals.Contains("w: ") && locals.Contains("h: "), locals);
            await vm.StartDebuggingCommand.ExecuteAsync(null);
            await WaitUntil(() => vm.Mode == ViewModels.DebugMode.Design, TimeSpan.FromSeconds(30));
            Assert.True(vm.DebugOutput.Document.Text.Contains("area 12"), vm.DebugOutput.Document.Text);
            Assert.True(await vm.CloseSolutionCoreAsync());
            window.Close();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Cargo_test_lists_runs_and_debugs_in_Test_Explorer()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("KARVON_RUST_LIVE") == "1"
            && Workspace.TaskVariables.Find("cargo", RustTools.PathWithCargo(Environment.GetEnvironmentVariable("PATH") ?? ""), "/") is not null,
            "Set KARVON_RUST_LIVE=1, with Rust installed, to build and test with cargo.");
        var dir = Path.Combine(Path.GetTempPath(), "karvon-rust-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "src"));
        Directory.CreateDirectory(Path.Combine(dir, "tests"));
        try
        {
            File.WriteAllText(Path.Combine(dir, "Cargo.toml"), "[package]\nname = \"shapes\"\nversion = \"0.1.0\"\nedition = \"2021\"\n");
            var lib = Path.Combine(dir, "src", "lib.rs");
            File.WriteAllText(lib, """
                pub fn area(w: i32, h: i32) -> i32 { w * h }

                #[cfg(test)]
                mod tests {
                    use super::*;

                    #[test]
                    fn multiplies() {
                        let size = area(3, 4);
                        assert_eq!(size, 12);
                    }

                    #[test]
                    fn zero() {
                        assert_eq!(area(0, 4), 1);
                    }

                    #[test]
                    #[ignore]
                    fn later() {}
                }
                """);
            File.WriteAllText(Path.Combine(dir, "tests", "outside.rs"), "#[test]\nfn from_outside() {\n    assert_eq!(shapes::area(2, 2), 4);\n}\n");
            var vm = NewViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            await vm.OpenSolutionAsync(dir);

            // Run All Tests: each test binary's tests, by its target and modules, where they are.
            await vm.RunAllTestsCommand.ExecuteAsync(null);
            Assert.True(vm.TestSummary.StartsWith("Test run finished: 4 test(s) (2 Passed, 1 Failed, 1 Skipped)", StringComparison.Ordinal), vm.TestSummary + "\n" + vm.TestsOutput.Document.Text);
            var package = Assert.Single(vm.TestRoots);
            Assert.Equal(["src/lib.rs", "tests/outside.rs"], package.Children.Select(n => n.Name));
            var module = package.Children[0].Children.Single(n => n.Name == "tests");
            var multiplies = module.Children.Single(n => n.Name == "multiplies");
            Assert.Equal((Testing.TestState.Passed, lib, 8), (multiplies.State, multiplies.SourcePath, multiplies.Line));
            var zero = module.Children.Single(n => n.Name == "zero");
            Assert.Equal(Testing.TestState.Failed, zero.State);
            Assert.True(zero.ErrorMessage?.StartsWith("assertion `left == right` failed", StringComparison.Ordinal), zero.ErrorMessage + "\n" + zero.StackTrace);
            Assert.Equal(Testing.TestState.Skipped, module.Children.Single(n => n.Name == "later").State);
            Assert.Equal(Testing.TestState.Passed, package.Children[1].Children.Single().State);

            // Run Failed Tests: that one, by --exact.
            await vm.RunFailedTestsCommand.ExecuteAsync(null);
            Assert.Equal((Testing.TestState.Passed, Testing.TestState.Failed), (multiplies.State, zero.State));
            Assert.Contains("tests::zero --exact", vm.TestsOutput.Document.Text);

            // Debug Selected Tests: the unit test binary under CodeLLDB, stopped in the test.
            vm.SelectedTest = multiplies;
            vm.Breakpoints.Toggle(lib, 10);
            var debugging = vm.DebugSelectedTestsCommand.ExecuteAsync(null);
            await WaitUntil(() => vm.Mode == ViewModels.DebugMode.Break && vm.CallStack.Count > 0, TimeSpan.FromMinutes(2));
            Assert.True(vm.Mode == ViewModels.DebugMode.Break, vm.DebugOutput.Document.Text + "\n" + vm.TestsOutput.Document.Text);
            Assert.Equal((lib, 10), (vm.CallStack[0].SourcePath, vm.CallStack[0].Line));
            // assert_eq! is several places of line 10, where LLDB stops in turn: continue to the end.
            var stops = 0;
            while (!debugging.IsCompleted && stops++ < 10)
            {
                if (vm.Mode == ViewModels.DebugMode.Break)
                    await vm.StartDebuggingCommand.ExecuteAsync(null);
                await WaitUntil(() => debugging.IsCompleted || vm.Mode == ViewModels.DebugMode.Break, TimeSpan.FromSeconds(30));
            }
            Assert.True(debugging.IsCompleted, $"{stops} stops; " + vm.DebugOutput.Document.Text);
            Assert.Equal(Testing.TestState.Passed, multiplies.State);
            Assert.True(await vm.CloseSolutionCoreAsync());
            window.Close();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
