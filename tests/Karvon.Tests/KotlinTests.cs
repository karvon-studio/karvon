using Avalonia.Headless.XUnit;
using Karvon.Kotlin;
using Karvon.Lsp;
using Karvon.Modules;
using Karvon.Templates;
using Karvon.Testing;
using Karvon.ViewModels;
using Karvon.Views;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>The Kotlin module: JetBrains' kotlin-lsp in the editor of .kt files, its debugger for F5 of a Gradle application.</summary>
public class KotlinTests
{
    static string NewFolder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "karvon-kotlin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    static async Task<string> FromTemplateAsync()
    {
        var template = KotlinTemplates.All.Single();
        var request = new NewProjectRequest(template, "Shop App", Directory.CreateTempSubdirectory("karvon-kotlin-").FullName, "Shop App", true, []);
        Directory.CreateDirectory(request.ProjectDirectory);
        return await template.Create!(request, _ => { }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Kotlin_files_its_gradle_applications_run_and_its_template()
    {
        Assert.True(KotlinServer.Serves("/w/src/Main.kt") && KotlinServer.Serves("/w/build.gradle.kts"));
        Assert.False(KotlinServer.Serves("/w/Main.java"));
        Assert.Contains(Karvon.BuiltInModules.Create(), m => m.Id == "kotlin");
        Assert.True(KotlinLspLocator.AllBuilds.ContainsKey(KotlinLspLocator.Platform));

        // A Gradle application's main class, as its build file names it; a Java build's is not Kotlin's.
        var dir = NewFolder();
        File.WriteAllText(Path.Combine(dir, "build.gradle.kts"), "plugins { kotlin(\"jvm\") version \"2.2.20\"\n application }\napplication { mainClass.set(\"shop.MainKt\") }\n");
        Assert.Equal("shop.MainKt", KotlinTasks.MainClassOf(dir));
        var run = Assert.Single(KotlinTasks.Read(dir));
        Assert.Equal(("kotlin: run shop.MainKt", WorkspaceTaskKind.Run, "gradle", true), (run.Name, run.Kind, run.Program, run.IsDefault));
        Assert.Equal(("intellij_gradle", "shop.MainKt"), (run.Debug!.Configuration["type"]!.GetValue<string>(), run.Debug.Configuration["mainClass"]!.GetValue<string>()));
        File.WriteAllText(Path.Combine(dir, "build.gradle.kts"), "plugins { java\n application }\napplication { mainClass = \"shop.Main\" }\n");
        Assert.Empty(KotlinTasks.Read(dir));
        File.Delete(Path.Combine(dir, "build.gradle.kts"));
        File.WriteAllText(Path.Combine(dir, "build.gradle"), "apply plugin: 'kotlin'\nmainClassName = 'AppKt'\n");
        Assert.Equal("AppKt", KotlinTasks.MainClassOf(dir));
        Directory.Delete(dir, recursive: true);

        // The template: a Gradle application whose wrapper names its distribution, and its test.
        var project = await FromTemplateAsync();
        Assert.Equal("MainKt", KotlinTasks.MainClassOf(project));
        Assert.Equal(Path.Combine(project, "src", "main", "kotlin", "Main.kt"), KotlinTasks.MainFileOf(project, "MainKt"));
        Assert.Equal(Path.Combine(project, "src", "main", "kotlin", "Main.kt"), KotlinTasks.Read(project).Single().Debug!.Configuration["file"]!.GetValue<string>());
        Assert.Contains($"gradle-{KotlinTemplates.GradleVersion}-bin.zip", File.ReadAllText(Path.Combine(project, "gradle", "wrapper", "gradle-wrapper.properties")));
        Assert.Contains("rootProject.name = \"shop-app\"", File.ReadAllText(Path.Combine(project, "settings.gradle.kts")));
        Assert.True(File.Exists(Path.Combine(project, "src", "test", "kotlin", "MainTest.kt")));
        // Gradle's wrapper, as Gradle publishes it: kotlin-lsp runs the application with it.
        Assert.Equal(KotlinTemplates.WrapperJarSha256,
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(project, "gradle", "wrapper", "gradle-wrapper.jar")))));
        Assert.StartsWith("#!/bin/sh", File.ReadAllText(Path.Combine(project, "gradlew")));
        Assert.True(File.Exists(Path.Combine(project, "gradlew.bat")));
        if (!OperatingSystem.IsWindows())
            Assert.True(File.GetUnixFileMode(Path.Combine(project, "gradlew")).HasFlag(UnixFileMode.UserExecute));
        Assert.Equal(Path.Combine(project, OperatingSystem.IsWindows() ? "gradlew.bat" : "gradlew"), KotlinTasks.Read(project).Single().Program);
        Directory.Delete(Path.GetDirectoryName(project)!, recursive: true);
    }

    [Fact]
    public void Kotlin_tests_are_found_as_written_and_run_by_gradle()
    {
        // A class's @Test functions (by kotlin.test's name or JUnit's, in backticks too), a nested class's; not a function
        // outside a class, nor one without the annotation.
        var tests = KotlinTestFramework.TestsIn("""
            package shop.cart

            import kotlin.test.Test
            import kotlin.test.assertEquals

            @Test
            fun outside() {}

            class CartTest {
                @Test
                fun adds() {
                    val total = listOf(1, 2).sum()
                    assertEquals(3, total, "sum ${total}")
                }

                @kotlin.test.Test fun `is empty at first`() {}

                fun helper() {}

                @Nested
                inner class Empty {
                    @org.junit.jupiter.api.Test
                    fun totalsZero() {}
                }

                companion object {
                    fun make() = CartTest()
                }
            }

            object Later {
                @Test fun runs() {}
            }
            """);
        Assert.Equal([("shop.cart.CartTest", "adds", 11), ("shop.cart.CartTest", "is empty at first", 16), ("shop.cart.CartTest$Empty", "totalsZero", 23), ("shop.cart.Later", "runs", 32)],
            tests);

        // Gradle runs them again each time (cleanTest), the chosen ones by their filters; the test JVM waits when debugging.
        Assert.Equal(["cleanTest", "test", "--console=plain"], KotlinTestFramework.Arguments(null, debug: false));
        Assert.Equal(["cleanTest", "test", "--console=plain", "--tests", "shop.cart.CartTest.adds", "--debug-jvm"], KotlinTestFramework.Arguments(["shop.cart.CartTest.adds"], debug: true));

        // A Kotlin build with a test has them; a Java build's are not Kotlin's.
        var dir = NewFolder();
        File.WriteAllText(Path.Combine(dir, "build.gradle.kts"), "plugins { kotlin(\"jvm\") version \"2.2.20\" }\n");
        Directory.CreateDirectory(Path.Combine(dir, "src", "test", "kotlin"));
        File.WriteAllText(Path.Combine(dir, "src", "test", "kotlin", "CartTest.kt"), "class CartTest {\n    @Test fun adds() {}\n}\n");
        Assert.True(KotlinTestFramework.IsUsedIn(dir));
        Assert.Single(Karvon.BuiltInModules.Create().Single(m => m.Id == "kotlin").TestFrameworks(new TestFrameworkStart(dir, "", _ => { })));
        File.WriteAllText(Path.Combine(dir, "build.gradle.kts"), "plugins { java }\n");
        Assert.False(KotlinTestFramework.IsUsedIn(dir));

        // Gradle's report: a test's result by its class and function; a test the files did not show joins the tree.
        var reports = Directory.CreateDirectory(Path.Combine(dir, "build", "test-results", "test")).FullName;
        File.WriteAllText(Path.Combine(reports, "TEST-CartTest.xml"), """
            <?xml version="1.0" encoding="UTF-8"?>
            <testsuite name="CartTest" tests="2" skipped="0" failures="1" errors="0">
              <testcase name="adds()" classname="CartTest" time="0.012"/>
              <testcase name="is empty at first()" classname="CartTest" time="0.003">
                <failure message="org.opentest4j.AssertionFailedError: expected: &lt;0&gt; but was: &lt;1&gt;" type="org.opentest4j.AssertionFailedError">org.opentest4j.AssertionFailedError: expected: &lt;0&gt; but was: &lt;1&gt;
            	at CartTest.is empty at first(CartTest.kt:3)</failure>
              </testcase>
              <system-out><![CDATA[]]></system-out>
            </testsuite>
            """);
        var framework = new KotlinTestFramework(new TestFrameworkStart(dir, "", _ => { }));
        var results = framework.Results(Directory.EnumerateFiles(reports)).ToDictionary(r => r.Id);
        Assert.Equal(TestState.Passed, results["CartTest#adds"].State);
        Assert.Equal((TestState.Failed, "org.opentest4j.AssertionFailedError: expected: <0> but was: <1>"), (results["CartTest#is empty at first"].State, results["CartTest#is empty at first"].ErrorMessage));
        Assert.Equal("is empty at first", results["CartTest#is empty at first"].Item!.Name);
        Directory.Delete(dir, recursive: true);
    }

    [AvaloniaFact]
    public async Task Kotlin_lsp_checks_the_code_and_its_debugger_stops_at_a_breakpoint()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("KARVON_KOTLIN_LIVE") == "1",
            "Set KARVON_KOTLIN_LIVE=1 to download kotlin-lsp (about 350 MB) and Gradle, and try them.");
        var project = await FromTemplateAsync();
        var main = Path.Combine(project, "src", "main", "kotlin", "Main.kt");
        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        await vm.OpenSolutionAsync(project);
        try
        {
            // The first .kt file installs kotlin-lsp and starts it; it imports the Gradle build (Gradle downloaded by its
            // wrapper's distribution), then an error shows in the editor.
            var doc = vm.OpenDocument(main)!;
            await WaitUntil(() => doc.Host.HasLanguageServer, TimeSpan.FromMinutes(10));
            Assert.True(doc.Host.HasLanguageServer, vm.ServerOutput.Document.Text);
            doc.Host.Document.Insert(0, "val size: Int = \"large\"\n");
            await WaitUntil(() => doc.Host.Diagnostics.Any(d => d.Code == "INITIALIZER_TYPE_MISMATCH"), TimeSpan.FromMinutes(10));
            var error = Assert.Single(doc.Host.Diagnostics, d => d.Code == "INITIALIZER_TYPE_MISMATCH");
            Assert.Equal((DiagnosticSeverity.Error, 0), (error.Severity, error.Range.Start.Line));
            doc.Host.Document.Replace(0, doc.Host.Document.GetLineByNumber(1).TotalLength, "");
            doc.Host.Save();
            await WaitUntil(() => !doc.Host.Diagnostics.Any(d => d.Code == "INITIALIZER_TYPE_MISMATCH"), TimeSpan.FromSeconds(60));

            // F5: the application under kotlin-lsp's debugger (it builds and runs it itself), stopped in main.
            Assert.Equal("kotlin: run MainKt", vm.StartupItem?.Name);
            vm.Breakpoints.Toggle(main, 4);
            await vm.StartDebuggingCommand.ExecuteAsync(null);
            await WaitUntil(() => vm.Mode == DebugMode.Break && vm.CallStack.Count > 0, TimeSpan.FromMinutes(5));
            Assert.True(vm.Mode == DebugMode.Break, vm.DebugOutput.Document.Text);
            Assert.Equal((main, 4), (vm.CallStack[0].SourcePath, vm.CallStack[0].Line));
            await vm.StartDebuggingCommand.ExecuteAsync(null);
            await WaitUntil(() => vm.Mode == DebugMode.Design, TimeSpan.FromSeconds(60));
            await WaitUntil(() => vm.DebugOutput.Document.Text.Contains("Hello, world!"), TimeSpan.FromSeconds(10));
            Assert.True(vm.DebugOutput.Document.Text.Contains("Hello, world!"), vm.DebugOutput.Document.Text);

            // Test Explorer: the template's test, run by its Gradle; then debugged, stopped in the test.
            var test = Path.Combine(project, "src", "test", "kotlin", "MainTest.kt");
            await vm.RunAllTestsCommand.ExecuteAsync(null);
            Assert.True(vm.TestSummary.StartsWith("Test run finished: 1 test(s) (1 Passed, 0 Failed, 0 Skipped)", StringComparison.Ordinal),
                vm.TestSummary + "\n" + vm.TestsOutput.Document.Text);
            var greets = Assert.Single(vm.TestRoots).Children.Single(n => n.Name == "<default>").Children.Single(n => n.Name == "MainTest").Children.Single();
            Assert.Equal(("greets", TestState.Passed, test, 6), (greets.Name, greets.State, greets.SourcePath, greets.Line));
            vm.SelectedTest = greets;
            vm.Breakpoints.Toggle(test, 7);
            var debugging = vm.DebugSelectedTestsCommand.ExecuteAsync(null);
            await WaitUntil(() => vm.Mode == DebugMode.Break && vm.CallStack.Count > 0, TimeSpan.FromMinutes(3));
            Assert.True(vm.Mode == DebugMode.Break, vm.DebugOutput.Document.Text + "\n" + vm.TestsOutput.Document.Text);
            Assert.Equal((test, 7), (vm.CallStack[0].SourcePath, vm.CallStack[0].Line));
            for (var i = 0; i < 5 && !debugging.IsCompleted; i++)
            {
                if (vm.Mode == DebugMode.Break)
                    await vm.StartDebuggingCommand.ExecuteAsync(null);
                await WaitUntil(() => debugging.IsCompleted || vm.Mode == DebugMode.Break, TimeSpan.FromSeconds(60));
            }
            Assert.True(debugging.IsCompleted, vm.DebugOutput.Document.Text);
            Assert.Equal(TestState.Passed, greets.State);
        }
        finally
        {
            Assert.True(await vm.CloseSolutionCoreAsync());
            window.Close();
            Directory.Delete(Path.GetDirectoryName(project)!, recursive: true);
        }
    }
}
