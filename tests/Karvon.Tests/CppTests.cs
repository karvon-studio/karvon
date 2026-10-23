using Avalonia.Headless.XUnit;
using Karvon.Cpp;
using Karvon.Lsp;
using Karvon.Views;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>The C and C++ module: clangd, CodeLLDB, and CMake's and Make's builds, with the user's compilers.</summary>
public class CppTests
{
    static string NewCMakeProject()
    {
        var dir = Path.Combine(Path.GetTempPath(), "karvon-cpp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "CMakeLists.txt"), "cmake_minimum_required(VERSION 3.16)\nproject(shapes C)\nadd_executable(shapes main.c)\n");
        File.WriteAllText(Path.Combine(dir, "main.c"), "#include <stdio.h>\n\n/* A rectangle's area. */\nint area(int w, int h) {\n    int result = w * h;\n    return result;\n}\n\nint main(void) {\n    printf(\"area %d\\n\", area(3, 4));\n    return 0;\n}\n");
        return dir;
    }

    [Fact]
    public void A_cmake_or_make_folder_has_its_builds_and_their_errors_read()
    {
        var dir = NewCMakeProject();
        var tasks = CppTasks.Read(dir);
        Assert.Equal(["cmake: build", "cmake: configure", "ctest", "cmake: run shapes"], tasks.Select(t => t.Name));
        var run = tasks[^1].Debug!;
        Assert.Equal(("lldb", Path.Combine(dir, "build", OperatingSystem.IsWindows() ? "shapes.exe" : "shapes"), "cmake: build"), (run.Type, run.Configuration["program"]!.GetValue<string>(), run.PreLaunchTask));

        // GCC's and Clang's form, and MSVC's (MSBuild's).
        var error = CppTasks.Problem("/w/main.c:10:5: error: use of undeclared identifier 'sizes'")!;
        Assert.Equal(("/w/main.c", 10, 5, DiagnosticSeverity.Error, "use of undeclared identifier 'sizes'"), (error.File, error.Line, error.Column, error.Severity, error.Message));
        var warning = CppTasks.Problem("src/a.cpp:3:9: warning: unused variable 'x' [-Wunused-variable]")!;
        Assert.Equal((DiagnosticSeverity.Warning, "-Wunused-variable", "unused variable 'x'"), (warning.Severity, warning.Code, warning.Message));
        Assert.Equal("C2065", CppTasks.Problem(@"C:\w\main.cpp(10,5): error C2065: 'sizes': undeclared identifier [C:\w\build\shapes.vcxproj]")!.Code);
        Assert.Null(CppTasks.Problem("[ 50%] Building C object CMakeFiles/shapes.dir/main.c.o"));

        // A Makefile: make, its test target, clean.
        var make = Path.Combine(Path.GetTempPath(), "karvon-make-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(make);
        File.WriteAllText(Path.Combine(make, "Makefile"), "CC := cc\nall: app\napp: main.c\n\t$(CC) -o app main.c\ntest: app\n\t./app\nclean:\n\trm -f app\n");
        Assert.Equal(["make", "make test", "make clean"], CppTasks.Read(make).Select(t => t.Name));
        Directory.Delete(make, recursive: true);
        Directory.Delete(dir, recursive: true);
    }

    [AvaloniaFact]
    public async Task Clangd_checks_cmake_builds_and_codelldb_debugs()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("KARVON_CPP_LIVE") == "1"
            && Workspace.TaskVariables.Find("cmake", Environment.GetEnvironmentVariable("PATH") ?? "", "/") is not null,
            "Set KARVON_CPP_LIVE=1, with CMake and a C compiler installed, to download clangd and CodeLLDB and try them.");
        var dir = NewCMakeProject();
        try
        {
            var vm = NewViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            await vm.OpenSolutionAsync(dir);

            // Build: configured, with the compile commands clangd reads; then an error typed shows in the Error List.
            await vm.BuildSolutionCommand.ExecuteAsync(null);
            Assert.True(File.Exists(Path.Combine(dir, "build", "compile_commands.json")), vm.BuildOutput.Document.Text);
            Assert.Empty(vm.Errors);
            var main = Path.Combine(dir, "main.c");
            var doc = vm.OpenDocument(main)!;
            var line = doc.Host.Document.GetLineByNumber(10);
            doc.Host.Document.Replace(line.Offset, line.Length, "    printf(\"area %d\\n\", sizes);");
            await WaitUntil(() => doc.Host.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error), TimeSpan.FromMinutes(4));
            var typed = doc.Host.Diagnostics.First(d => d.Severity == DiagnosticSeverity.Error);
            Assert.Equal(9, typed.Range.Start.Line);
            Assert.IsType<ClangdServer>(Assert.Single(vm.ModuleLanguageServers));
            doc.Host.Save();
            await vm.BuildSolutionCommand.ExecuteAsync(null);
            var error = Assert.Single(vm.Errors, e => e.Line == 10 && e.Source == "Build");
            Assert.Equal(main, error.FilePath);
            doc.Host.Document.Replace(line.Offset, doc.Host.Document.GetLineByNumber(10).Length, "    printf(\"area %d\\n\", area(3, 4));");
            doc.Host.Save();

            // F5 on cmake: run shapes: built first (its preLaunchTask), CodeLLDB stops in the function.
            Assert.Equal("cmake: run shapes", vm.StartupItem?.Name);
            vm.Breakpoints.Toggle(main, 5);
            var tools = ClaudeOf(vm).ToolServer();
            var started = await ClaudeIdeToolsTests.Text(tools, "debug_control", new { action = "start" });
            Assert.True(System.Text.RegularExpressions.Regex.IsMatch(started, @"area.* at main\.c:5\.$"), started + "\n" + vm.DebugOutput.Document.Text);
            var locals = await ClaudeIdeToolsTests.Text(tools, "debug_locals", new { depth = 1 });
            Assert.True(locals.Contains("w: ") && locals.Contains("h: "), locals);
            for (var i = 0; i < 5 && vm.Mode != ViewModels.DebugMode.Design; i++)
            {
                if (vm.Mode == ViewModels.DebugMode.Break)
                    await vm.StartDebuggingCommand.ExecuteAsync(null);
                await WaitUntil(() => vm.Mode != ViewModels.DebugMode.Running, TimeSpan.FromSeconds(20));
            }
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
    public async Task Ctest_lists_runs_and_debugs_in_Test_Explorer()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("KARVON_CPP_LIVE") == "1"
            && Workspace.TaskVariables.Find("ctest", Environment.GetEnvironmentVariable("PATH") ?? "", "/") is not null,
            "Set KARVON_CPP_LIVE=1, with CMake and a C compiler installed, to build and test with them.");
        var dir = Path.Combine(Path.GetTempPath(), "karvon-ctest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var cmake = Path.Combine(dir, "CMakeLists.txt");
            File.WriteAllText(cmake, "cmake_minimum_required(VERSION 3.16)\nproject(shapes C)\nenable_testing()\nadd_executable(check check.c)\nadd_test(NAME multiplies COMMAND check 12)\nadd_test(NAME zero COMMAND check 1)\nadd_test(NAME later COMMAND check 12)\nset_tests_properties(later PROPERTIES DISABLED TRUE)\n");
            var check = Path.Combine(dir, "check.c");
            File.WriteAllText(check, "#include <stdio.h>\n#include <stdlib.h>\n\nint main(int argc, char **argv) {\n    int want = atoi(argv[1]);\n    int got = 3 * 4;\n    if (got != want) {\n        printf(\"got %d, want %d\\n\", got, want);\n        return 1;\n    }\n    return 0;\n}\n");
            var vm = NewViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            await vm.OpenSolutionAsync(dir);

            // Run All Tests: built, listed by ctest (each where its add_test is), run with its JUnit report.
            await vm.RunAllTestsCommand.ExecuteAsync(null);
            Assert.True(vm.TestSummary.StartsWith("Test run finished: 3 test(s) (1 Passed, 1 Failed, 1 Skipped)", StringComparison.Ordinal), vm.TestSummary + "\n" + vm.TestsOutput.Document.Text);
            var project = Assert.Single(vm.TestRoots);
            Assert.Equal("shapes", project.Name);
            var multiplies = project.Children.Single(n => n.Name == "multiplies");
            Assert.Equal((Testing.TestState.Passed, cmake, 5), (multiplies.State, multiplies.SourcePath, multiplies.Line));
            var zero = project.Children.Single(n => n.Name == "zero");
            Assert.Equal((Testing.TestState.Failed, "got 12, want 1"), (zero.State, zero.ErrorMessage));
            Assert.Equal(Testing.TestState.Skipped, project.Children.Single(n => n.Name == "later").State);

            // Run Failed Tests: that one, by -R.
            await vm.RunFailedTestsCommand.ExecuteAsync(null);
            Assert.Equal((Testing.TestState.Passed, Testing.TestState.Failed), (multiplies.State, zero.State));
            Assert.Contains("-R '^(zero)$'", vm.TestsOutput.Document.Text);

            // Debug Selected Tests: its command under CodeLLDB, stopped in the program.
            vm.SelectedTest = multiplies;
            vm.Breakpoints.Toggle(check, 6);
            var debugging = vm.DebugSelectedTestsCommand.ExecuteAsync(null);
            await WaitUntil(() => vm.Mode == ViewModels.DebugMode.Break && vm.CallStack.Count > 0, TimeSpan.FromMinutes(2));
            Assert.True(vm.Mode == ViewModels.DebugMode.Break, vm.DebugOutput.Document.Text + "\n" + vm.TestsOutput.Document.Text);
            Assert.Equal((check, 6), (vm.CallStack[0].SourcePath, vm.CallStack[0].Line));
            for (var i = 0; i < 5 && !debugging.IsCompleted; i++)
            {
                if (vm.Mode == ViewModels.DebugMode.Break)
                    await vm.StartDebuggingCommand.ExecuteAsync(null);
                await WaitUntil(() => debugging.IsCompleted || vm.Mode == ViewModels.DebugMode.Break, TimeSpan.FromSeconds(20));
            }
            Assert.True(debugging.IsCompleted, vm.DebugOutput.Document.Text);
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
