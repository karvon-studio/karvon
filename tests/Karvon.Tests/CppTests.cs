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
        Assert.Equal(("lldb", Path.Combine(dir, "build", "shapes"), "cmake: build"), (run.Type, run.Configuration["program"]!.GetValue<string>(), run.PreLaunchTask));

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
}
