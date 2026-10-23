using Avalonia.Headless.XUnit;
using Karvon.Lsp;
using Karvon.Python;
using Karvon.Views;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>The Python module: Pyright's language server in the editor of .py files, with the workspace's interpreter.</summary>
public class PythonTests
{
    [Fact]
    public void It_serves_python_with_the_workspaces_environment()
    {
        Assert.True(PyrightServer.Serves("/w/app.py"));
        Assert.True(PyrightServer.Serves("/w/stubs.pyi"));
        Assert.False(PyrightServer.Serves("/w/app.ts"));
        Assert.Contains(Karvon.BuiltInModules.Create(), m => m.Id == "python" && m.LanguageServers.Single().Serves("/w/app.py"));

        // Its .venv first, as when it is activated; else the PATH's python3.
        var dir = Path.Combine(Path.GetTempPath(), "karvon-py-" + Guid.NewGuid().ToString("N"));
        var bin = Path.Combine(dir, ".venv", OperatingSystem.IsWindows() ? "Scripts" : "bin");
        Directory.CreateDirectory(bin);
        Assert.Null(PythonEnvironment.VirtualEnvironmentBin(dir));
        File.WriteAllText(Path.Combine(bin, OperatingSystem.IsWindows() ? "python.exe" : "python"), "");
        Assert.Equal(bin, PythonEnvironment.VirtualEnvironmentBin(dir));
        Assert.Equal(Path.Combine(bin, OperatingSystem.IsWindows() ? "python.exe" : "python"), PythonEnvironment.Interpreter(dir, ""));
        Assert.StartsWith(bin + Path.PathSeparator, PythonEnvironment.PathFor(dir, "/usr/bin"));

        // Its entry point and the file in the editor run with that interpreter, and debug with debugpy.
        Assert.Empty(PythonTasks.Read(dir));
        File.WriteAllText(Path.Combine(dir, "manage.py"), "");
        var tasks = PythonTasks.Read(dir);
        Assert.Equal(["python: manage.py", "python: current file"], tasks.Select(t => t.Name));
        Assert.Equal([true, false], tasks.Select(t => t.IsDefault));
        Assert.Equal(Path.Combine(bin, OperatingSystem.IsWindows() ? "python.exe" : "python"), tasks[0].Program);
        Assert.Equal(["-u", Path.Combine(dir, "manage.py"), "runserver", "--noreload"], tasks[0].Arguments);
        Assert.Equal(("debugpy", "${file}"), (tasks[1].Debug!.Type, tasks[1].Debug!.Configuration["program"]!.GetValue<string>()));
        Directory.Delete(dir, recursive: true);
    }

    [AvaloniaFact]
    public async Task Pyright_checks_the_code_as_it_is_typed()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("KARVON_PYTHON_LIVE") == "1",
            "Set KARVON_PYTHON_LIVE=1 to install Pyright from the npm registry and try its language server.");
        var dir = Path.Combine(Path.GetTempPath(), "karvon-py-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "shapes.py"), "def area(width: int, height: int) -> int:\n    return width * height\n");
            var app = Path.Combine(dir, "app.py");
            File.WriteAllText(app, "from shapes import area\n\nsize: int = \"large\"\nprint(area(2, 3), size)\n");
            var vm = NewViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            await vm.OpenSolutionAsync(dir);

            // The first .py file installs Pyright and starts its server; the type error shows in the editor.
            var doc = vm.OpenDocument(app)!;
            await WaitUntil(() => doc.Host.Diagnostics.Count > 0, TimeSpan.FromMinutes(3));
            var error = Assert.Single(doc.Host.Diagnostics);
            Assert.Equal(DiagnosticSeverity.Error, error.Severity);
            Assert.Equal(2, error.Range.Start.Line);
            Assert.Contains("\"Literal['large']\"", error.Message);
            var server = Assert.IsType<PyrightServer>(Assert.Single(vm.ModuleLanguageServers));

            // The fix, typed: the error goes.
            var line = doc.Host.Document.GetLineByNumber(3);
            doc.Host.Document.Replace(line.Offset, line.Length, "size: int = 42");
            await WaitUntil(() => doc.Host.Diagnostics.Count == 0, TimeSpan.FromSeconds(30));
            Assert.Empty(doc.Host.Diagnostics);

            // Completion, hover and go to definition across the files.
            var members = await server.CompletionAsync(app, new LspPosition(3, 0), null, CancellationToken.None);
            Assert.Contains(members.Items, i => i.Label == "area");
            Assert.Contains("int", await server.HoverAsync(app, new LspPosition(3, 7), CancellationToken.None));
            var definition = Assert.Single(await server.DefinitionAsync(app, new LspPosition(3, 7), CancellationToken.None));
            Assert.EndsWith("shapes.py", new Uri(definition.Uri).LocalPath);

            doc.Host.Save();
            Assert.True(await vm.CloseSolutionCoreAsync());
            Assert.Empty(vm.ModuleLanguageServers);
            window.Close();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task F5_debugs_python_with_debugpy()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("KARVON_PYTHON_LIVE") == "1", "Set KARVON_PYTHON_LIVE=1 to install debugpy from PyPI and debug with it.");
        Assert.SkipUnless(PythonEnvironment.Interpreter("/", Environment.GetEnvironmentVariable("PATH") ?? "") is not null, "Python is not installed.");
        var dir = Path.Combine(Path.GetTempPath(), "karvon-py-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, ".vscode"));
        try
        {
            var main = Path.Combine(dir, "main.py");
            File.WriteAllText(main, "def area(w, h):\n    result = w * h\n    return result\n\nprint(\"area\", area(3, 4))\n");
            File.WriteAllText(Workspace.VsCodeLaunch.FileOf(dir), """
                { "configurations": [ { "name": "Python: main", "type": "debugpy", "request": "launch", "program": "${workspaceFolder}/main.py" } ] }
                """);
            var vm = NewViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            await vm.OpenSolutionAsync(dir);
            Assert.Equal("Python: main", vm.StartupItem?.Name);
            var server = ClaudeOf(vm).ToolServer();

            // F5: stopped on the breakpoint in the function; its variables, an expression, a step, then to its end.
            vm.Breakpoints.Toggle(main, 3);
            var started = await ClaudeIdeToolsTests.Text(server, "debug_control", new { action = "start" });
            Assert.True(vm.Mode == ViewModels.DebugMode.Break, started + "\n" + vm.DebugOutput.Document.Text);
            Assert.Matches(@"^Stopped \(breakpoint\) in area at main\.py:3\.$", started);
            Assert.Equal("w * h = 12 (int)", await ClaudeIdeToolsTests.Text(server, "debug_evaluate", new { expression = "w * h" }));
            Assert.Contains("  result: int = 12", await ClaudeIdeToolsTests.Text(server, "debug_locals", new { depth = 1 }));
            Assert.True(vm.Breakpoints.IsVerified(main, 3));
            await vm.StartDebuggingCommand.ExecuteAsync(null);
            await WaitUntil(() => vm.Mode == ViewModels.DebugMode.Design, TimeSpan.FromSeconds(30));
            Assert.True(vm.Mode == ViewModels.DebugMode.Design, vm.DebugOutput.Document.Text);
            Assert.Contains("area 12", vm.DebugOutput.Document.Text);
            Assert.True(await vm.CloseSolutionCoreAsync());

            // With no launch.json: its main.py, and the file in the editor, run and debug as the Python extension's do.
            File.Delete(Workspace.VsCodeLaunch.FileOf(dir));
            await vm.OpenSolutionAsync(dir);
            Assert.Equal(["python: main.py", "python: current file"], vm.StartupItems.Select(i => i.Name));
            Assert.Equal("python: main.py", vm.StartupItem?.Name);
            // The tests' folders come back without their state: the breakpoint again.
            if (!vm.Breakpoints.Has(main, 3))
                vm.Breakpoints.Toggle(main, 3);
            started = await ClaudeIdeToolsTests.Text(server = ClaudeOf(vm).ToolServer(), "debug_control", new { action = "start" });
            Assert.True(System.Text.RegularExpressions.Regex.IsMatch(started, @"in area at main\.py:3\.$"), started + "\n" + vm.DebugOutput.Document.Text);
            await vm.StartDebuggingCommand.ExecuteAsync(null);
            await WaitUntil(() => vm.Mode == ViewModels.DebugMode.Design, TimeSpan.FromSeconds(30));
            Assert.True(vm.Mode == ViewModels.DebugMode.Design, vm.DebugOutput.Document.Text);
            await vm.StartWithoutDebuggingCommand.ExecuteAsync(null);
            // Its lines come through the UI thread's queue.
            await WaitUntil(() => vm.DebugOutput.Document.Text.Contains("area 12"), TimeSpan.FromSeconds(10));
            Assert.True(vm.DebugOutput.Document.Text.Contains("area 12") && vm.DebugOutput.Document.Text.Contains("main.py' has exited with code 0"), vm.DebugOutput.Document.Text);
            Assert.True(await vm.CloseSolutionCoreAsync());
            window.Close();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Pytest_finds_runs_and_debugs_the_tests()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("KARVON_PYTHON_LIVE") == "1", "Set KARVON_PYTHON_LIVE=1 to install pytest from PyPI and run it.");
        var python = PythonEnvironment.Interpreter("/", Environment.GetEnvironmentVariable("PATH") ?? "");
        Assert.SkipUnless(python is not null, "Python is not installed.");
        var dir = Path.Combine(Path.GetTempPath(), "karvon-pytest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "tests"));
        try
        {
            // The workspace's .venv, with pytest in it, as its user would have it.
            var output = new List<string>();
            using (var venv = Karvon.Build.ProcessRunner.Start(python!, ["-m", "venv", ".venv"], dir, output.Add))
                Assert.SkipUnless(await venv.WaitAsync(TestContext.Current.CancellationToken) == 0, "No venv: " + string.Join("\n", output));
            var pip = Path.Combine(PythonEnvironment.VirtualEnvironmentBin(dir)!, OperatingSystem.IsWindows() ? "python.exe" : "python");
            using (var install = Karvon.Build.ProcessRunner.Start(pip, ["-m", "pip", "install", "--quiet", "--disable-pip-version-check", "pytest"], dir, output.Add))
                Assert.True(await install.WaitAsync(TestContext.Current.CancellationToken) == 0, string.Join("\n", output));
            var test = Path.Combine(dir, "tests", "test_shapes.py");
            File.WriteAllText(test, """
                import pytest

                def area(w, h):
                    return w * h

                class TestArea:
                    def test_multiplies(self):
                        assert area(3, 4) == 12

                    @pytest.mark.parametrize("w", [0, 1])
                    def test_of_zero(self, w):
                        assert area(w, 0) == 1

                @pytest.mark.skip(reason="later")
                def test_later():
                    pass
                """);
            var vm = NewViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            await vm.OpenSolutionAsync(dir);

            // Run All Tests: found (where they are) and run by the .venv's pytest.
            await vm.RunAllTestsCommand.ExecuteAsync(null);
            Assert.True(vm.TestSummary.StartsWith("Test run finished: 4 test(s) (1 Passed, 2 Failed, 1 Skipped)", StringComparison.Ordinal), vm.TestSummary + "\n" + vm.TestsOutput.Document.Text);
            var file = Assert.Single(Assert.Single(vm.TestRoots).Children);
            Assert.Equal((Testing.TestNodeKind.Namespace, "tests/test_shapes.py"), (file.Kind, file.Name));
            var area = file.Children.Single(n => n.Name == "TestArea");
            var multiplies = area.Children.Single(n => n.Name == "test_multiplies");
            Assert.Equal((Testing.TestState.Passed, test, 7), (multiplies.State, multiplies.SourcePath, multiplies.Line));
            var ofZero = area.Children.Single(n => n.Name == "test_of_zero");
            Assert.Equal((Testing.TestNodeKind.Method, 2), (ofZero.Kind, ofZero.Children.Count));
            var zero = ofZero.Children.Single(n => n.Name == "test_of_zero[0]");
            Assert.Equal(Testing.TestState.Failed, zero.State);
            Assert.Equal("assert 0 == 1\n +  where 0 = area(0, 0)", zero.ErrorMessage);
            var later = file.Children.Single(n => n.Name == "test_later");
            Assert.Equal((Testing.TestState.Skipped, "Skipped: later"), (later.State, later.ErrorMessage));

            // Run Failed Tests: those two only, by their node ids.
            await vm.RunFailedTestsCommand.ExecuteAsync(null);
            Assert.Equal((Testing.TestState.Passed, Testing.TestState.Failed), (multiplies.State, zero.State));
            Assert.Contains("tests/test_shapes.py::TestArea::test_of_zero[0]", vm.TestsOutput.Document.Text);

            // Debug Selected Tests: pytest under debugpy, stopped in the test.
            vm.SelectedTest = multiplies;
            vm.Breakpoints.Toggle(test, 8);
            var debugging = vm.DebugSelectedTestsCommand.ExecuteAsync(null);
            await WaitUntil(() => vm.Mode == ViewModels.DebugMode.Break && vm.CallStack.Count > 0, TimeSpan.FromSeconds(60));
            Assert.True(vm.Mode == ViewModels.DebugMode.Break, vm.DebugOutput.Document.Text + "\n" + vm.TestsOutput.Document.Text);
            Assert.Equal((test, 8), (vm.CallStack[0].SourcePath, vm.CallStack[0].Line));
            await vm.StartDebuggingCommand.ExecuteAsync(null);
            await debugging.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
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
