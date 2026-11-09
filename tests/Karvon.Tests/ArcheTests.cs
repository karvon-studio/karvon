using Avalonia.Headless.XUnit;
using Karvon.Arche;
using Karvon.Editor;
using Karvon.Lsp;
using Karvon.Modules;
using Karvon.Templates;
using Karvon.Testing;
using Karvon.Views;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>The Arche module: arche-lsp, a package's arche-driver tasks and its test blocks, with the Arche the user has.</summary>
public class ArcheTests
{
    const string Manifest = """
        import std.build;

        const PACKAGE: build.Package = build.Package {
            name: "shop",
            version: "0.1.0",
            edition: 2027,
            needs: [],
        };

        """;

    static string NewPackage(params (string Name, string Text)[] files)
    {
        var dir = Directory.CreateTempSubdirectory("karvon-arche-").FullName;
        File.WriteAllText(Path.Combine(dir, "package.arche"), Manifest);
        foreach (var (name, text) in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(dir, name))!);
            File.WriteAllText(Path.Combine(dir, name), text);
        }
        return dir;
    }

    /// <summary>A build of arche-lang's layout with the tools named, for ARCHE_HOME, and a folder for a PATH with others.</summary>
    static string FakeHome(params string[] tools)
    {
        var home = Directory.CreateTempSubdirectory("karvon-fake-arche-").FullName;
        foreach (var (tool, folder) in tools.Select(t => (t, t switch { "arche" => "compiler/build", "arche-test" => "compiler", _ => "driver/build" })))
        {
            Directory.CreateDirectory(Path.Combine(home, folder));
            File.WriteAllText(Path.Combine(home, folder, tool), "#!/bin/sh\n");
        }
        return home;
    }

    static string Empty() => Directory.CreateTempSubdirectory("karvon-empty-").FullName;

    /// <summary>A new package by a template, in a folder of its own.</summary>
    static Task<string> Made(string identity, string name)
    {
        var template = ArcheTemplates.All.Single(t => t.Identity == identity);
        return template.Create!(new NewProjectRequest(template, name, Empty(), name, true, []), _ => { }, CancellationToken.None);
    }

    [Fact]
    public void A_package_with_programs_has_its_tasks_and_its_errors_read()
    {
        var dir = NewPackage(("build.arche", """
            import std.build;

            const BUILD: build.Build = build.Build {
                programs: [build.program("webd", "main.arche"), build.program( "tool" , "tool.arche")],
            };
            """));
        var tasks = ArcheTasks.Read(dir);
        Assert.Equal(["arche: build", "arche: run webd", "arche: run tool", "arche: clean"], tasks.Select(t => t.Name));
        Assert.Equal(["arche: build", "arche: run webd"], tasks.Where(t => t.IsDefault).Select(t => t.Name));
        Assert.Equal(["run", ".", "tool"], tasks[2].Arguments);
        Assert.All(tasks, t => Assert.Equal(ArcheTools.Driver, t.Program));

        // As the compiler writes them, and what it says last is no error.
        var problem = ArcheTasks.ReadProblem("./main.arche:5:12: error[E0301]: unknown name 'nope'")!;
        Assert.Equal((DiagnosticSeverity.Error, "E0301", "unknown name 'nope'", "./main.arche", 5, 12), (problem.Severity, problem.Code, problem.Message, problem.File, problem.Line, problem.Column));
        Assert.Null(ArcheTasks.ReadProblem("error: ErrorsFound"));
        Assert.Null(ArcheTasks.ReadProblem("greeter: 7 modules, 7 from the cache"));
    }

    [Fact]
    public void A_library_is_checked_and_a_folder_with_no_manifest_is_no_package()
    {
        var tasks = ArcheTasks.Read(NewPackage(("words.arche", "pub fn a() {\n}\n"), ("lib.arche", "pub fn b() {\n}\n")));
        Assert.Equal(["arche: check lib.arche", "arche: check words.arche"], tasks.Select(t => t.Name));
        Assert.Equal("arche: check lib.arche", Assert.Single(tasks, t => t.IsDefault).Name);
        Assert.Equal(["--package", ".", "--no-main", "check", "lib.arche"], tasks[0].Arguments);
        Assert.Empty(ArcheTasks.Read(Empty()));
    }

    [Fact]
    public void The_tools_are_on_the_path_or_in_a_build_of_arche_lang_and_never_the_open_folders()
    {
        var empty = Empty();
        var home = FakeHome("arche", "arche-driver", "arche-lsp", "arche-test");
        Assert.Null(ArcheTools.Find(ArcheTools.Server, empty, home: empty));
        Assert.Equal(Path.Combine(home, "driver", "build", "arche-lsp"), ArcheTools.Find(ArcheTools.Server, empty, home));
        Assert.Equal(Path.Combine(home, "compiler", "build", "arche"), ArcheTools.Find(ArcheTools.Compiler, empty, home));
        Assert.Equal(Path.Combine(home, "compiler", "arche-test"), ArcheTools.Find(ArcheTools.TestRunner, empty, home));
        Assert.Equal([Path.Combine(home, "compiler", "build"), Path.Combine(home, "driver", "build")], ArcheTools.DirectoriesOffPath(empty, home));
        Assert.Equal(Path.Combine(home, "compiler", "build") + Path.PathSeparator + Path.Combine(home, "driver", "build") + Path.PathSeparator + empty, ArcheTools.PathWithTools(empty, home));
        Assert.Equal(Path.Combine(home, "compiler", "build", "arche"), ArcheTools.ToolEnvironment(empty, home)!["ARCHE"]);

        // What the PATH has comes first, and arche-test goes with the compiler there.
        var other = FakeHome("arche", "arche-test");
        var path = Path.Combine(other, "compiler", "build");
        Assert.Equal(Path.Combine(path, "arche"), ArcheTools.Find(ArcheTools.Compiler, path, home));
        Assert.Equal(Path.Combine(other, "compiler", "arche-test"), ArcheTools.Find(ArcheTools.TestRunner, path, home: empty));
        Assert.Equal([Path.Combine(home, "driver", "build")], ArcheTools.DirectoriesOffPath(path, home));
    }

    [Fact]
    public async Task Test_blocks_are_found_in_the_packages_files_and_their_outcomes_read()
    {
        var dir = NewPackage(
            ("main.arche", "fn f() {\n}\n\ntest \"adds\" {\n}\n\n  test \"a \\\"quoted\\\" name\" {\n}\n"),
            ("sub/part.arche", "test \"deep\" {\n}\n"),
            ("build/copy.arche", "test \"built\" {\n}\n"),
            ("vendor/dep/dep.arche", "test \"vendored\" {\n}\n"),
            (".hidden/x.arche", "test \"hidden\" {\n}\n"));
        Assert.True(ArcheTestFramework.IsUsedIn(dir));
        Assert.False(ArcheTestFramework.IsUsedIn(Empty()));
        using var framework = new ArcheTestFramework(new TestFrameworkStart(dir, "", _ => { }));
        var found = (await framework.DiscoverAsync(CancellationToken.None)).Tests;
        Assert.Equal(["adds", "a \"quoted\" name", "deep"], found.Select(t => t.Name));
        Assert.Equal((4, Path.Combine(dir, "main.arche")), (found[0].Line, found[0].File));
        Assert.Equal(7, found[1].Line);

        // arche-test --json: each outcome, what a failing one wrote, and the one it did not get to.
        var results = new List<TestItemResult>();
        ArcheTestFramework.Report("""
            {"seed": 1, "tests": [
              {"name": "adds", "ok": true, "stdout": "", "stderr": ""},
              {"name": "a \"quoted\" name", "ok": false, "stdout": "said\n", "stderr": "panic: assertion failed\n  at main.arche:8:5\n"},
              {"name": "not asked for", "ok": true, "stdout": "", "stderr": ""}
            ], "passed": 2, "failed": 1, "filtered": 0}
            """, found, results.Add);
        Assert.Equal([(found[0].Id, TestState.Passed), (found[1].Id, TestState.Failed), (found[2].Id, TestState.NotRun)], results.Select(r => (r.Id, r.State)));
        Assert.Null(results[0].ErrorMessage);
        Assert.StartsWith("panic: assertion failed", results[1].ErrorMessage);
        Assert.Contains("said", results[1].ErrorMessage);

        // A file that does not compile fails its tests with what the compiler said.
        results.Clear();
        ArcheTestFramework.Report("main.arche:2:12: error[E0301]: unknown name 'nope'\nerror: ErrorsFound", [found[0]], results.Add);
        Assert.Equal(TestState.Failed, Assert.Single(results).State);
        Assert.Contains("E0301", results[0].ErrorMessage);
    }

    [Fact]
    public void The_module_brings_its_server_grammar_icon_and_templates()
    {
        var module = new ArcheModule();
        Assert.True(Assert.Single(module.LanguageServers).Serves("/a/main.arche"));
        Assert.False(Assert.Single(module.LanguageServers).Serves("/a/main.jwc"));
        using var grammar = Assert.Single(module.Grammars).Open();
        Assert.Contains("source.arche", new StreamReader(grammar).ReadToEnd());
        using var icon = Assert.Single(module.FileIcons).Open();
        Assert.True(icon.Length > 100);
        Assert.Equal([ArcheTools.Manifest, ArcheTools.BuildFile], module.WorkspaceTaskFiles);
    }

    [Fact]
    public async Task A_new_package_has_a_name_imports_can_call_it_and_its_lines_end_with_a_newline()
    {
        var dir = await Made("karvon.arche.program", "Shop App");
        Assert.EndsWith("Shop App", dir);
        Assert.Contains("name: \"shop_app\"", File.ReadAllText(Path.Combine(dir, "package.arche")));
        var main = File.ReadAllText(Path.Combine(dir, "main.arche"));
        Assert.DoesNotContain('\r', main);
        Assert.True(main.EndsWith("}\n", StringComparison.Ordinal), "one newline at its end, as arche fmt leaves it");
        Assert.Equal(["arche: build", "arche: run main", "arche: clean"], ArcheTasks.Read(dir).Select(t => t.Name));
        Assert.Equal(["arche: check lib.arche"], ArcheTasks.Read(await Made("karvon.arche.library", "words")).Select(t => t.Name));
    }

    static bool HasArche() =>
        Environment.GetEnvironmentVariable("KARVON_ARCHE_LIVE") == "1" && ArcheTools.Find(ArcheTools.Server, Environment.GetEnvironmentVariable("PATH") ?? "") is not null;

    [AvaloniaFact]
    public async Task Arche_lsp_checks_what_is_typed_and_the_package_builds_and_tests()
    {
        Assert.SkipUnless(HasArche(), "Set KARVON_ARCHE_LIVE=1, with a build of Arche, to try arche-lsp, arche-driver and arche-test.");
        var dir = await Made("karvon.arche.program", "shop");
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        try
        {
            await vm.OpenSolutionAsync(dir);
            Assert.Equal("arche: run main", vm.StartupItem?.Name);

            // The error as it is typed, not saved, then gone.
            var main = Path.Combine(dir, "main.arche");
            var doc = vm.OpenDocument(main)!;
            var original = doc.Host.Document.Text;
            var number = Array.FindIndex(original.Split('\n'), l => l.Contains("return \"hello, world\";", StringComparison.Ordinal));
            var line = doc.Host.Document.GetLineByNumber(number + 1);
            var kept = doc.Host.Document.GetText(line);
            doc.Host.Document.Replace(line.Offset, line.Length, "    return missing;");
            await WaitUntil(() => doc.Host.Diagnostics.Any(d => d.Message.Contains("missing")), TimeSpan.FromSeconds(30));
            var error = doc.Host.Diagnostics.First(d => d.Message.Contains("missing"));
            Assert.Equal((DiagnosticSeverity.Error, number), (error.Severity, error.Range.Start.Line));
            line = doc.Host.Document.GetLineByNumber(number + 1);
            doc.Host.Document.Replace(line.Offset, line.Length, kept);
            await WaitUntil(() => doc.Host.Diagnostics.Count == 0, TimeSpan.FromSeconds(30));

            // Hover with the doc comment, go to definition, find references and completion.
            var server = Assert.IsType<ArcheServer>(Assert.Single(vm.ModuleLanguageServers));
            var text = original.Split('\n');
            var call = Array.FindIndex(text, l => l.Contains("write(greeting())", StringComparison.Ordinal));
            var at = new LspPosition(call, text[call].IndexOf("greeting", StringComparison.Ordinal) + 1);
            var hover = await server.HoverAsync(main, at, CancellationToken.None);
            Assert.Contains("fn greeting() -> str", hover);
            Assert.Contains("What the program says.", hover);
            var definition = Assert.Single(await server.DefinitionAsync(main, at, CancellationToken.None));
            Assert.Equal(main, new Uri(definition.Uri).LocalPath);
            Assert.Equal(Array.FindIndex(text, l => l.StartsWith("fn greeting", StringComparison.Ordinal)), definition.Range.Start.Line);
            Assert.True((await server.ReferencesAsync(main, at, CancellationToken.None)).Count >= 3);
            var afterDot = new LspPosition(call, text[call].IndexOf("stdout.", StringComparison.Ordinal) + "stdout.".Length);
            Assert.Contains((await server.CompletionAsync(main, afterDot, ".", CancellationToken.None)).Items, i => i.Label == "write");

            // Format Document, of the text not yet saved.
            line = doc.Host.Document.GetLineByNumber(number + 1);
            doc.Host.Document.Replace(line.Offset, line.Length, "  return     \"hello, world\" ;");
            await Task.Delay(500);
            await doc.Host.FormatDocumentAsync();
            Assert.Equal(original, doc.Host.Document.Text);

            // Its tests in Test Explorer, by arche-test.
            using var framework = new ArcheTestFramework(new TestFrameworkStart(dir, path, _ => { }));
            var found = (await framework.DiscoverAsync(CancellationToken.None)).Tests;
            var results = new List<TestItemResult>();
            await framework.RunAsync(null, false, results.Add, CancellationToken.None);
            Assert.Equal(TestState.Passed, results.Single(r => r.Id == found[0].Id && r.State != TestState.Running).State);
        }
        finally
        {
            window.Close();
        }
    }
}
