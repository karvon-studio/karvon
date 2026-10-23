using Avalonia.Headless.XUnit;
using Karvon.Java;
using Karvon.Lsp;
using Karvon.Views;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>The Java module: jdtls and java-debug in one, Maven's and Gradle's builds, with the user's JDK.</summary>
public class JavaTests
{
    [Fact]
    public void Maven_and_gradle_folders_have_their_builds_and_errors_read()
    {
        var dir = Path.Combine(Path.GetTempPath(), "karvon-java-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "pom.xml"), "<project/>");
        Assert.Equal(["maven: compile", "maven: test", "maven: package", "java: run main"], JavaTasks.Read(dir).Select(t => t.Name));
        File.Delete(Path.Combine(dir, "pom.xml"));
        File.WriteAllText(Path.Combine(dir, "build.gradle"), "plugins { id 'application' }\n");
        File.WriteAllText(Path.Combine(dir, "gradlew"), "");
        var gradle = JavaTasks.Read(dir);
        Assert.Equal(["gradle: build", "gradle: test", "gradle: run", "java: run main"], gradle.Select(t => t.Name));
        Assert.Equal(Path.Combine(dir, "gradlew"), gradle[0].Program);

        var maven = JavaTasks.Problem("[ERROR] /w/src/main/java/App.java:[12,5] cannot find symbol")!;
        Assert.Equal(("/w/src/main/java/App.java", 12, 5, DiagnosticSeverity.Error, "cannot find symbol"), (maven.File, maven.Line, maven.Column, maven.Severity, maven.Message));
        Assert.Equal(12, JavaTasks.Problem("/w/src/App.java:12: error: cannot find symbol")!.Line);
        Assert.Null(JavaTasks.Problem("[INFO] BUILD SUCCESS"));
        Directory.Delete(dir, recursive: true);
    }

    [AvaloniaFact]
    public async Task Jdtls_checks_and_java_debug_debugs()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("KARVON_JAVA_LIVE") == "1" && JavaTools.FindJava(Environment.GetEnvironmentVariable("PATH") ?? "") is not null,
            "Set KARVON_JAVA_LIVE=1, with a JDK 21 installed, to download jdtls and java-debug and try them.");
        var dir = Path.Combine(Path.GetTempPath(), "karvon-java-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "src"));
        try
        {
            var app = Path.Combine(dir, "src", "App.java");
            File.WriteAllText(app, "public class App {\n    /** A rectangle's area. */\n    static int area(int w, int h) {\n        int result = w * h;\n        return result;\n    }\n\n    public static void main(String[] args) {\n        int size = area(3, 4);\n        System.out.println(\"area \" + size);\n    }\n}\n");
            var vm = NewViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            await vm.OpenSolutionAsync(dir);

            // jdtls: an error as it is typed, then gone; hover with the Javadoc.
            var doc = vm.OpenDocument(app)!;
            var line = doc.Host.Document.GetLineByNumber(10);
            doc.Host.Document.Replace(line.Offset, line.Length, "        System.out.println(\"area \" + sizes);");
            await WaitUntil(() => doc.Host.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error), TimeSpan.FromMinutes(4));
            Assert.True(doc.Host.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error && d.Range.Start.Line == 9 && d.Message.Contains("sizes")),
                string.Join("\n", doc.Host.Diagnostics.Select(d => d.Message)) + "\n" + vm.ServerOutput.Document.Text);
            doc.Host.Document.Replace(line.Offset, doc.Host.Document.GetLineByNumber(10).Length, "        System.out.println(\"area \" + size);");
            await WaitUntil(() => doc.Host.Diagnostics.All(d => d.Severity != DiagnosticSeverity.Error), TimeSpan.FromSeconds(60));
            Assert.DoesNotContain(doc.Host.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
            doc.Host.Save();
            var server = Assert.IsType<JdtlsServer>(Assert.Single(vm.ModuleLanguageServers));
            Assert.Contains("A rectangle's area.", await server.HoverAsync(app, new LspPosition(8, 20), CancellationToken.None) ?? "");

            // F5: jdtls finds the main class (the editor's file), java-debug stops in the method.
            Assert.Equal("java: run main", vm.StartupItem?.Name);
            vm.Breakpoints.Toggle(app, 4);
            var tools = ClaudeOf(vm).ToolServer();
            var started = await ClaudeIdeToolsTests.Text(tools, "debug_control", new { action = "start" });
            Assert.True(System.Text.RegularExpressions.Regex.IsMatch(started, @"area.* at src/App\.java:4\.$"), started + "\n" + vm.DebugOutput.Document.Text);
            Assert.Contains("12", await ClaudeIdeToolsTests.Text(tools, "debug_evaluate", new { expression = "w * h" }));
            await vm.StartDebuggingCommand.ExecuteAsync(null);
            await WaitUntil(() => vm.Mode == ViewModels.DebugMode.Design, TimeSpan.FromSeconds(60));
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
