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
            window.Close();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
