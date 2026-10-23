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
        var wrapper = OperatingSystem.IsWindows() ? "gradlew.bat" : "gradlew";
        File.WriteAllText(Path.Combine(dir, wrapper), "");
        var gradle = JavaTasks.Read(dir);
        Assert.Equal(["gradle: build", "gradle: test", "gradle: run", "java: run main"], gradle.Select(t => t.Name));
        Assert.Equal(Path.Combine(dir, wrapper), gradle[0].Program);

        var maven = JavaTasks.Problem("[ERROR] /w/src/main/java/App.java:[12,5] cannot find symbol")!;
        Assert.Equal(("/w/src/main/java/App.java", 12, 5, DiagnosticSeverity.Error, "cannot find symbol"), (maven.File, maven.Line, maven.Column, maven.Severity, maven.Message));
        Assert.Equal(12, JavaTasks.Problem("/w/src/App.java:12: error: cannot find symbol")!.Line);
        Assert.Null(JavaTasks.Problem("[INFO] BUILD SUCCESS"));

        // A JDK's version, from its release file (through the links the PATH's java is).
        var jdk = Path.Combine(dir, "jdk-21", "bin");
        Directory.CreateDirectory(jdk);
        File.WriteAllText(Path.Combine(jdk, "java"), "");
        File.WriteAllText(Path.Combine(dir, "jdk-21", "release"), "IMPLEMENTOR=\"Eclipse Adoptium\"\nJAVA_VERSION=\"21.0.5\"\n");
        Assert.Equal(21, JavaTools.MajorOf(Path.Combine(jdk, "java")));
        if (!OperatingSystem.IsWindows())
        {
            File.CreateSymbolicLink(Path.Combine(dir, "java"), Path.Combine(jdk, "java"));
            Assert.Equal(21, JavaTools.MajorOf(Path.Combine(dir, "java")));
        }
        File.WriteAllText(Path.Combine(dir, "jdk-21", "release"), "JAVA_VERSION=\"1.8.0_412\"\n");
        Assert.Equal(8, JavaTools.MajorOf(Path.Combine(jdk, "java")));
        Directory.Delete(dir, recursive: true);
    }

    const string CalcTest = """
        package shop;

        import org.junit.jupiter.api.Nested;
        import org.junit.jupiter.api.Test;
        import org.junit.jupiter.params.ParameterizedTest;
        import org.junit.jupiter.params.provider.ValueSource;
        import static org.junit.jupiter.api.Assertions.assertEquals;

        class CalcTest {
            @Test
            void adds() {
                int sum = 2 + 2;
                assertEquals(4, sum);
            }

            @Test
            void fails() {
                assertEquals(5, 2 + 2, "two and two");
            }

            @ParameterizedTest(name = "{0} is positive")
            @ValueSource(ints = {1, 2, -3})
            void positive(int n) {
                assertEquals(true, n > 0);
            }

            // @Test void commented() {}
            void helper() {
            }

            @Nested
            class Inner {
                @Test void works() { }
            }
        }
        """;

    [Fact]
    public void JUnit_tests_are_found_in_the_sources_and_read_from_the_reports()
    {
        // The test methods as written: their class's binary name, their line; a helper and a comment are none.
        Assert.Equal([("shop.CalcTest", "adds", 11), ("shop.CalcTest", "fails", 17), ("shop.CalcTest", "positive", 23), ("shop.CalcTest$Inner", "works", 33)],
            JUnitFramework.TestsIn(CalcTest));
        // JUnit 4's, in a class of no package.
        Assert.Equal([("Legacy", "old", 3)], JUnitFramework.TestsIn("public class Legacy {\n    @org.junit.Test\n    public void old() throws Exception {\n    }\n}\n"));

        // The XML report: a parameterized method failed when a row did; a test the sources did not show comes with it.
        var reports = Directory.CreateTempSubdirectory("karvon-junit-reports-").FullName;
        File.WriteAllText(Path.Combine(reports, "TEST-junit-jupiter.xml"), """
            <testsuite name="JUnit Jupiter">
              <testcase name="adds()" classname="shop.CalcTest" time="0.002"><system-out><![CDATA[
            unique-id: [engine:junit-jupiter]/[class:shop.CalcTest]/[method:adds()]
            display-name: JUnit Jupiter > CalcTest > adds()
            ]]></system-out></testcase>
              <testcase name="positive(int)[1]" classname="shop.CalcTest" time="0.001"/>
              <testcase name="positive(int)[3]" classname="shop.CalcTest" time="0.004">
                <failure message="expected: &lt;true&gt; but was: &lt;false&gt;" type="org.opentest4j.AssertionFailedError">org.opentest4j.AssertionFailedError: expected
            	at shop.CalcTest.positive(CalcTest.java:25)</failure>
              </testcase>
              <testcase name="later()" classname="shop.CalcTest" time="0"><skipped/></testcase>
            </testsuite>
            """);
        var framework = new JUnitFramework(new Karvon.Modules.TestFrameworkStart(reports, "", _ => { }), _ => throw new InvalidOperationException());
        var results = framework.Results(reports).ToDictionary(r => r.Id);
        Assert.Equal((Testing.TestState.Passed, (string?)null), (results["shop.CalcTest#adds"].State, results["shop.CalcTest#adds"].Output));
        Assert.Equal((Testing.TestState.Failed, "expected: <true> but was: <false>"), (results["shop.CalcTest#positive"].State, results["shop.CalcTest#positive"].ErrorMessage));
        Assert.Contains("CalcTest.java:25", results["shop.CalcTest#positive"].StackTrace);
        Assert.Equal(TimeSpan.FromSeconds(0.005), results["shop.CalcTest#positive"].Duration);
        Assert.Equal(Testing.TestState.Skipped, results["shop.CalcTest#later"].State);
        Assert.Equal(("later", "CalcTest"), (results["shop.CalcTest#later"].Item!.Name, results["shop.CalcTest#later"].Item!.Groups[^1].Name));
        Directory.Delete(reports, recursive: true);
    }

    [AvaloniaFact]
    public async Task JUnit_runs_and_debugs_in_Test_Explorer()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("KARVON_JAVA_LIVE") == "1" && JavaTools.FindJava(Environment.GetEnvironmentVariable("PATH") ?? "", 21) is not null,
            "Set KARVON_JAVA_LIVE=1, with a JDK 21 installed, to download jdtls and JUnit's launcher and test with them.");
        var dir = Path.Combine(Path.GetTempPath(), "karvon-junit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "src", "shop"));
        Directory.CreateDirectory(Path.Combine(dir, "lib"));
        try
        {
            // Java files with no build of their own: jdtls's project of them takes lib's jars (JUnit's, in its launcher).
            var source = Path.Combine(dir, "src", "shop", "CalcTest.java");
            File.WriteAllText(source, CalcTest);
            File.Copy(await JavaTools.EnsureJUnitConsoleAsync(_ => { }, TestContext.Current.CancellationToken), Path.Combine(dir, "lib", "junit.jar"));
            var vm = NewViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            await vm.OpenSolutionAsync(dir);

            // Run All Tests: found in the source, built by jdtls, run by JUnit's launcher on the project's class path.
            await vm.RunAllTestsCommand.ExecuteAsync(null);
            Assert.True(vm.TestSummary.StartsWith("Test run finished: 4 test(s) (2 Passed, 2 Failed, 0 Skipped)", StringComparison.Ordinal),
                vm.TestSummary + "\n" + vm.TestsOutput.Document.Text + "\n" + vm.ServerOutput.Document.Text);
            var calc = Assert.Single(vm.TestRoots).Children.Single(n => n.Name == "shop").Children.Single(n => n.Name == "CalcTest");
            var adds = calc.Children.Single(n => n.Name == "adds");
            Assert.Equal((Testing.TestState.Passed, source, 11), (adds.State, adds.SourcePath, adds.Line));
            Assert.Equal((Testing.TestState.Failed, "two and two ==> expected: <5> but was: <4>"), (calc.Children.Single(n => n.Name == "fails").State, calc.Children.Single(n => n.Name == "fails").ErrorMessage));
            Assert.Equal(Testing.TestState.Failed, calc.Children.Single(n => n.Name == "positive").State);
            Assert.Equal(Testing.TestState.Passed, Assert.Single(vm.TestRoots).Children.Single(n => n.Name == "shop").Children.Single(n => n.Name == "CalcTest.Inner").Children.Single().State);

            // Debug Selected Tests: the launcher's JVM waits for java-debug, which stops in the test.
            vm.SelectedTest = adds;
            vm.Breakpoints.Toggle(source, 13);
            var debugging = vm.DebugSelectedTestsCommand.ExecuteAsync(null);
            await WaitUntil(() => vm.Mode == ViewModels.DebugMode.Break && vm.CallStack.Count > 0, TimeSpan.FromMinutes(2));
            Assert.True(vm.Mode == ViewModels.DebugMode.Break, vm.DebugOutput.Document.Text + "\n" + vm.TestsOutput.Document.Text);
            Assert.Equal((source, 13), (vm.CallStack[0].SourcePath, vm.CallStack[0].Line));
            for (var i = 0; i < 5 && !debugging.IsCompleted; i++)
            {
                if (vm.Mode == ViewModels.DebugMode.Break)
                    await vm.StartDebuggingCommand.ExecuteAsync(null);
                await WaitUntil(() => debugging.IsCompleted || vm.Mode == ViewModels.DebugMode.Break, TimeSpan.FromSeconds(20));
            }
            Assert.True(debugging.IsCompleted, vm.DebugOutput.Document.Text);
            Assert.Equal(Testing.TestState.Passed, adds.State);
            Assert.True(await vm.CloseSolutionCoreAsync());
            window.Close();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Jdtls_checks_and_java_debug_debugs()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("KARVON_JAVA_LIVE") == "1" && JavaTools.FindJava(Environment.GetEnvironmentVariable("PATH") ?? "", 21) is not null,
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
