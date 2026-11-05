using System.Net.Http;
using Avalonia.Headless.XUnit;
using Karvon.Editor;
using Karvon.Jwc;
using Karvon.Lsp;
using Karvon.Modules;
using Karvon.Templates;
using Karvon.Testing;
using Karvon.Views;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>The JWC module: jwc lsp, jwc fmt, a project's jwc tasks and jwc test's tests, with the jwc the user has.</summary>
public class JwcTests
{
    static string NewProject(string type = "app")
    {
        var dir = Directory.CreateTempSubdirectory("karvon-jwc-").FullName;
        Directory.CreateDirectory(Path.Combine(dir, "src"));
        File.WriteAllText(Path.Combine(dir, "jwcproj.json"), $$"""{ "name": "shop", "type": "{{type}}", "version": "0.1.0" }""");
        return dir;
    }

    /// <summary>A folder with a jwc that does what the test says, for the PATH.</summary>
    static string FakeJwc(string body)
    {
        var bin = Directory.CreateTempSubdirectory("karvon-fake-jwc-").FullName;
        var file = Path.Combine(bin, "jwc");
        File.WriteAllText(file, $"#!/bin/sh\necho \"$*\" >> \"{bin}/calls\"\n{body}\n");
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return bin;
    }

    [Fact]
    public void A_jwc_project_has_its_tasks_and_its_errors_read()
    {
        var dir = NewProject();
        var tasks = JwcTasks.Read(dir);
        Assert.Equal(["jwc: check", "jwc: build", "jwc: lint", "jwc: fmt", "jwc: test", "jwc: serve", "jwc: run"], tasks.Select(t => t.Name));
        Assert.Equal(["jwc: check", "jwc: test", "jwc: serve"], tasks.Where(t => t.IsDefault).Select(t => t.Name));
        Assert.Equal(["serve", "--dev", "--request-logging"], tasks[5].Arguments);

        // Its src folder is no Java project's; one with Java in it is.
        Assert.Empty(Karvon.Java.JavaTasks.Read(dir));
        var java = NewProject();
        Directory.CreateDirectory(Path.Combine(java, "src", "main", "java"));
        File.WriteAllText(Path.Combine(java, "src", "main", "java", "App.java"), "class App { }");
        Assert.Equal("java: run main", Assert.Single(Karvon.Java.JavaTasks.Read(java)).Name);

        // A package has nothing to run, and a folder with no manifest is no project.
        Assert.Equal(["jwc: check", "jwc: build", "jwc: lint", "jwc: fmt", "jwc: test"], JwcTasks.Read(NewProject("pkg")).Select(t => t.Name));
        Assert.Empty(JwcTasks.Read(Directory.CreateTempSubdirectory("karvon-jwc-").FullName));

        // As jwc check writes them: two lines each, and a last line of its own.
        var read = tasks[0].ProblemReader!();
        var found = new[]
        {
            "error[E0211]: unknown name `missing`",
            "  --> ./src/bad.jwc:5:16",
            "  |",
            "5 |         return missing;",
            "  |                ^^^^^^^",
            "  = help: declare it with `let`, take it as a parameter, or qualify it with the declaration it belongs to",
            "  = spec: names.md §5.3",
            "warning[W0102]: namespace `notes` does not match the path (`bad`)",
            "  --> ./src/bad.jwc:1:1",
            "Error: 1 error in 6 files",
        }.Select(read).OfType<Build.BuildDiagnostic>().ToList();
        Assert.Equal(2, found.Count);
        Assert.Equal((DiagnosticSeverity.Error, "E0211", "unknown name `missing`", "./src/bad.jwc", 5, 16),
            (found[0].Severity, found[0].Code, found[0].Message, found[0].File, found[0].Line, found[0].Column));
        Assert.Equal((DiagnosticSeverity.Warning, "W0102", 1), (found[1].Severity, found[1].Code, found[1].Line));
    }

    [Fact]
    public void Jwc_is_found_where_its_installer_puts_it()
    {
        var home = Directory.CreateTempSubdirectory("karvon-jwc-home-").FullName;
        var bin = Path.Combine(home, ".jwc", "bin");
        Assert.Null(JwcTools.DirectoryOffPath("", home));
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "jwc"), "");
        Assert.Equal(bin, JwcTools.DirectoryOffPath("", home));
        // On the PATH already: nothing to add.
        Assert.Null(JwcTools.DirectoryOffPath(bin, home));
    }

    static string Apply(string text, IReadOnlyList<LspTextEdit> edits)
    {
        foreach (var edit in edits)
        {
            int OffsetOf(LspPosition p)
            {
                var offset = 0;
                for (var line = 0; line < p.Line; line++)
                    offset = text.IndexOf('\n', offset) + 1;
                return offset + p.Character;
            }
            var (start, end) = (OffsetOf(edit.Range.Start), OffsetOf(edit.Range.End));
            text = text[..start] + edit.NewText + text[end..];
        }
        return text;
    }

    [Theory]
    [InlineData("a\nb\nc\n", "a\nB\nc\n")]
    [InlineData("a\nc\n", "a\nb\nc\n")]
    [InlineData("a\nb\nc\n", "a\nc\n")]
    [InlineData("  a\n  b\n", "a\nb\n")]
    [InlineData("a\nb", "a\nb\n")]
    [InlineData("x", "a\nb\n")]
    [InlineData("", "a\n")]
    [InlineData("a\n\n\nb\n", "a\n\nb\n")]
    [InlineData("a\n\n", "a\n")]
    [InlineData("a\n\n\n", "a\n")]
    public void Jwc_fmts_text_comes_back_as_one_edit_of_the_lines_it_changed(string text, string formatted)
    {
        var edit = Assert.Single(JwcServer.EditsFrom(text, formatted));
        Assert.Equal(formatted, Apply(text, [edit]));
    }

    [Fact]
    public void Jwc_fmt_leaves_what_it_does_not_change_and_the_files_line_endings()
    {
        Assert.Empty(JwcServer.EditsFrom("a\nb\n", "a\nb\n"));
        Assert.Empty(JwcServer.EditsFrom("a\r\nb\r\n", "a\nb\n"));
        var edit = Assert.Single(JwcServer.EditsFrom("one\ntwo\n  three\nfour\n", "one\ntwo\nthree\nfour\n"));
        Assert.Equal((new LspRange(new LspPosition(2, 0), new LspPosition(3, 0)), "three\n"), (edit.Range, edit.NewText));
        Assert.Equal("a\r\nB\r\n", Apply("a\r\nb\r\n", JwcServer.EditsFrom("a\r\nb\r\n", "a\nB\n")));
    }

    [AvaloniaFact]
    public void Jwc_files_have_their_grammar_and_their_icon()
    {
        var module = new JwcModule();
        foreach (var grammar in module.Grammars)
            TextMateSupport.Add(grammar);
        Assert.Equal("source.jwc", TextMateSupport.ScopeFor("/src/app.jwc"));
        var jwc = TextMateSupport.LoadGrammar("source.jwc")!;
        string ScopeOf(string line, string word)
        {
            var at = line.IndexOf(word, StringComparison.Ordinal);
            return jwc.TokenizeLine(line, null, TimeSpan.FromSeconds(5)).Tokens.First(t => t.StartIndex <= at && at < t.EndIndex).Scopes[^1];
        }
        Assert.StartsWith("comment.line", ScopeOf("// the notes of a user", "notes"));
        Assert.StartsWith("keyword", ScopeOf("        return json(page);", "return"));
        Assert.StartsWith("string", ScopeOf("    route GET \"/notes\" {", "notes"));
        // A column's type is colored as a type of the program's is.
        Assert.StartsWith("entity.name.type", ScopeOf("    id bigint primary key identity;", "bigint"));
        Assert.StartsWith("entity.name.type", ScopeOf("    state NoteState;", "NoteState"));

        foreach (var icon in module.FileIcons)
            MaterialIcons.Add(icon);
        Assert.Equal("jwc", MaterialIcons.ForFile("/src/App.JWC"));
        Assert.Equal("csharp", MaterialIcons.ForFile("Program.cs"));
        Assert.True(MaterialIcons.Exists(module.Icon!));
        Assert.NotNull(MaterialIcons.Image(module.Icon!));
    }

    static string TestsProject()
    {
        var dir = NewProject();
        File.WriteAllText(Path.Combine(dir, "src", "a.jwc"), """
            namespace a;

            test "adds up" {
                assert 1 == 1;
            }

            // test "a comment" {
            test "a \"quoted\" one" {
                assert 1 == 1;
            }
            test "same" { }
            test "same" { }
            """);
        Directory.CreateDirectory(Path.Combine(dir, "src", "deep"));
        File.WriteAllText(Path.Combine(dir, "src", "deep", "b.jwc"), "namespace deep;\n\ntest \"deep\" { }\n");
        // What jwc does not load.
        foreach (var skipped in new[] { "target", "node_modules", ".hidden" })
        {
            Directory.CreateDirectory(Path.Combine(dir, skipped));
            File.WriteAllText(Path.Combine(dir, skipped, "c.jwc"), "test \"not loaded\" { }\n");
        }
        return dir;
    }

    /// <summary>What each test ended as (its last result), by its id.</summary>
    static async Task<Dictionary<string, TestItemResult>> RunAsync(JwcTestFramework framework, IReadOnlyList<TestItem>? tests)
    {
        var results = new Dictionary<string, TestItemResult>();
        await framework.RunAsync(tests, debug: false, r => results[r.Id] = r, TestContext.Current.CancellationToken);
        return results;
    }

    [Fact]
    public async Task Test_blocks_are_found_in_the_files_jwc_loads()
    {
        var dir = TestsProject();
        var framework = new JwcTestFramework(new TestFrameworkStart(dir, "", _ => { }));
        var tests = (await framework.DiscoverAsync(TestContext.Current.CancellationToken)).Tests;
        Assert.Equal(["adds up", "a \"quoted\" one", "same", "same", "deep"], tests.Select(t => t.Name));
        Assert.Equal(5, tests.Select(t => t.Id).Distinct().Count());
        var first = tests[0];
        Assert.Equal((Path.Combine(dir, "src", "a.jwc"), 3), (first.File, first.Line));
        Assert.Equal(("shop", Path.Combine("src", "a.jwc")), (first.Groups[0].Name, first.Groups[1].Name));
        Assert.Equal((Path.Combine(dir, "src", "deep", "b.jwc"), 3), (tests[4].File, tests[4].Line));
    }

    [Fact]
    public async Task Jwc_tests_outcomes_are_read_with_the_colors_it_writes()
    {
        var dir = TestsProject();
        var bin = FakeJwc("""
            printf '\033[32mok\033[0m    adds up\n'
            printf '\033[31mFAILED\033[0m a "quoted" one\n        Conflict: taken\n        at the second line\n'
            printf '\033[32mok\033[0m    same\n'
            printf '\033[31mFAILED\033[0m same\n        wrong\n'
            printf '\033[32mok\033[0m    deep\n'
            printf '\n5 tests, 2 failed\nError: 2 tests failed\n'
            exit 1
            """);
        var log = new List<string>();
        var framework = new JwcTestFramework(new TestFrameworkStart(dir, bin, line => { lock (log) log.Add(line); }));
        var tests = (await framework.DiscoverAsync(TestContext.Current.CancellationToken)).Tests;

        var results = await RunAsync(framework, null);
        Assert.Equal(TestState.Passed, results[tests[0].Id].State);
        var failed = results[tests[1].Id];
        Assert.Equal((TestState.Failed, "Conflict: taken\nat the second line"), (failed.State, failed.ErrorMessage?.ReplaceLineEndings("\n")));
        // Two of one name: each its own, in their order.
        Assert.Equal((TestState.Passed, TestState.Failed), (results[tests[2].Id].State, results[tests[3].Id].State));
        Assert.Equal(TestState.Passed, results[tests[4].Id].State);
        Assert.Equal("test", File.ReadAllLines(Path.Combine(bin, "calls")).Single());
        Assert.Contains("FAILED same", log);
        Assert.DoesNotContain(log, l => l.Contains('\x1b'));

        // Some of them: a run a name, and only theirs reported.
        File.Delete(Path.Combine(bin, "calls"));
        results = await RunAsync(framework, [tests[0], tests[4]]);
        Assert.Equal([tests[0].Id, tests[4].Id], results.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["test --filter adds up", "test --filter deep"], File.ReadAllLines(Path.Combine(bin, "calls")));

        // All of them, selected: one run.
        File.Delete(Path.Combine(bin, "calls"));
        await RunAsync(framework, tests);
        Assert.Equal("test", File.ReadAllLines(Path.Combine(bin, "calls")).Single());
    }

    [Fact]
    public async Task A_jwc_test_that_could_not_run_says_why_and_one_it_did_not_get_to_did_not_run()
    {
        var dir = TestsProject();
        var bin = FakeJwc("""
            echo 'Error: DATABASE_URL (or JWC_DATABASE_URL) is required for db access.' >&2
            exit 1
            """);
        var framework = new JwcTestFramework(new TestFrameworkStart(dir, bin, _ => { }));
        var tests = (await framework.DiscoverAsync(TestContext.Current.CancellationToken)).Tests;
        var results = await RunAsync(framework, null);
        Assert.Equal(5, results.Count);
        Assert.All(results.Values, r => Assert.Equal((TestState.Failed, "Error: DATABASE_URL (or JWC_DATABASE_URL) is required for db access."), (r.State, r.ErrorMessage)));

        // jwc ran and has no such test (the words are in a comment, say): not a failure.
        var quiet = new JwcTestFramework(new TestFrameworkStart(dir, FakeJwc("echo 'no tests'"), _ => { }));
        await quiet.DiscoverAsync(TestContext.Current.CancellationToken);
        Assert.All((await RunAsync(quiet, null)).Values, r => Assert.Equal(TestState.NotRun, r.State));
    }

    static bool HasJwc() => Environment.GetEnvironmentVariable("KARVON_JWC_LIVE") == "1"
        && JwcTools.Find(Environment.GetEnvironmentVariable("PATH") ?? "") is not null;

    static async Task<string> MadeBy(string template)
    {
        var made = JwcTemplates.All.Single(t => t.Identity == "karvon.jwc." + template);
        var request = new NewProjectRequest(made, "Shop App", Directory.CreateTempSubdirectory("karvon-new-").FullName, "Shop App", true, []);
        Directory.CreateDirectory(request.ProjectDirectory);
        return await made.Create!(request, _ => { }, TestContext.Current.CancellationToken);
    }

    [AvaloniaFact]
    public async Task Jwc_lsp_checks_what_is_typed_and_jwc_fmt_formats_it()
    {
        Assert.SkipUnless(HasJwc(), "Set KARVON_JWC_LIVE=1, with jwc installed, to try jwc lsp and jwc fmt.");
        var dir = await MadeBy("api");
        try
        {
            Assert.Contains("\"name\": \"shop-app\"", File.ReadAllText(Path.Combine(dir, "jwcproj.json")));
            var vm = NewViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            await vm.OpenSolutionAsync(dir);
            Assert.Equal("jwc: serve", vm.StartupItem?.Name);

            // The error as it is typed, then gone.
            var routes = Path.Combine(dir, "src", "routes", "notes.jwc");
            var doc = vm.OpenDocument(routes)!;
            var original = doc.Host.Document.Text;
            var line = doc.Host.Document.GetLineByNumber(11);
            Assert.Contains("return json(page);", doc.Host.Document.GetText(line));
            doc.Host.Document.Replace(line.Offset, line.Length, "        return json(missing);");
            await WaitUntil(() => doc.Host.Diagnostics.Any(d => d.Message.Contains("missing")), TimeSpan.FromSeconds(30));
            var error = doc.Host.Diagnostics.First(d => d.Message.Contains("missing"));
            Assert.Equal((DiagnosticSeverity.Error, 10), (error.Severity, error.Range.Start.Line));
            doc.Host.Document.Replace(line.Offset, line.Length, "        return json(page);");
            await WaitUntil(() => doc.Host.Diagnostics.Count == 0, TimeSpan.FromSeconds(30));

            // Hover on a query (its SQL), go to definition of a class, completion and parameter info.
            var server = Assert.IsType<JwcServer>(Assert.Single(vm.ModuleLanguageServers));
            var service = Path.Combine(dir, "src", "services", "notes.jwc");
            var serviceText = vm.OpenDocument(service)!.Host.Document.Text.Split('\n');
            var query = Array.FindIndex(serviceText, l => l.Contains("return select N", StringComparison.Ordinal));
            var sql = await server.HoverAsync(service, new LspPosition(query, serviceText[query].IndexOf("select", StringComparison.Ordinal) + 2), CancellationToken.None);
            Assert.Contains("select", sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("notes", sql, StringComparison.OrdinalIgnoreCase);
            Assert.True(vm.OpenDocument(routes) == doc);
            var body = Array.FindIndex(original.Split('\n'), l => l.Contains("as NoteCreate", StringComparison.Ordinal));
            var definition = Assert.Single(await server.DefinitionAsync(routes, new LspPosition(body, original.Split('\n')[body].IndexOf("NoteCreate", StringComparison.Ordinal) + 2), CancellationToken.None));
            Assert.EndsWith(Path.Combine("dto", "notes.jwc"), new Uri(definition.Uri).LocalPath);
            var afterDot = new LspPosition(8, original.Split('\n')[8].IndexOf("NoteService.", StringComparison.Ordinal) + "NoteService.".Length);
            Assert.Contains((await server.CompletionAsync(routes, afterDot, ".", CancellationToken.None)).Items, i => i.Label == "list");
            var inCall = new LspPosition(8, original.Split('\n')[8].IndexOf("list(", StringComparison.Ordinal) + "list(".Length);
            Assert.NotNull(await server.SignatureHelpAsync(routes, inCall, CancellationToken.None));

            // Format Document: jwc fmt's, of the text not yet saved, and only the line it changes; what does not parse stays.
            line = doc.Host.Document.GetLineByNumber(7);
            doc.Host.Document.Replace(line.Offset, line.Length, "route   GET \"\"   {");
            await doc.Host.FormatDocumentAsync();
            Assert.Equal(original, doc.Host.Document.Text);
            doc.Host.Document.Insert(0, "service {\n");
            var broken = doc.Host.Document.Text;
            await doc.Host.FormatDocumentAsync();
            Assert.Equal(broken, doc.Host.Document.Text);
            doc.Host.Document.Remove(0, "service {\n".Length);

            // Ctrl+K, Ctrl+C: its line comment.
            doc.Host.Editor.CaretOffset = doc.Host.Document.GetLineByNumber(11).Offset;
            doc.Host.ToggleLineComment(true);
            Assert.Matches(@"^\s*//\s*return json\(page\);\r?$", doc.Host.Document.Text.Split('\n')[10]);
            doc.Host.ToggleLineComment(false);
            Assert.Equal(original, doc.Host.Document.Text);

            // Build: jwc check, its errors in the Error List where they are.
            var brokenFile = Path.Combine(dir, "src", "broken.jwc");
            File.WriteAllText(brokenFile, "namespace broken;\n\nservice Broken {\n    function list() {\n        return missing;\n    }\n}\n");
            await vm.BuildSolutionCommand.ExecuteAsync(null);
            var built = Assert.Single(vm.Errors);
            Assert.Equal((brokenFile, 5, 16), (built.FilePath, built.Line, built.Column));
            Assert.Contains("unknown name", built.Description);
            File.Delete(brokenFile);
            Assert.True(await vm.CloseSolutionCoreAsync());
            window.Close();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Jwc_test_runs_in_Test_Explorer_and_f5_serves_the_project_and_restarts_it_on_save()
    {
        Assert.SkipUnless(HasJwc() && Environment.GetEnvironmentVariable("JWC_DATABASE_URL") is { Length: > 0 },
            "Set KARVON_JWC_LIVE=1 and JWC_DATABASE_URL to a scratch database, with jwc installed, to run its tests and serve.");
        var dir = await MadeBy("empty");
        Karvon.ViewModels.MainViewModel? vm = null;
        try
        {
            File.WriteAllText(Path.Combine(dir, "src", "checks.jwc"), """
                namespace checks;

                test "adds up" {
                    assert 1 + 1 == 2;
                }

                test "does not add up" {
                    assert 1 + 1 == 3;
                }
                """);
            var port = System.Net.IPEndPoint.Parse("127.0.0.1:0");
            using (var listener = new System.Net.Sockets.TcpListener(port))
            {
                listener.Start();
                port = (System.Net.IPEndPoint)listener.LocalEndpoint;
            }
            var app = Path.Combine(dir, "src", "app.jwc");
            File.WriteAllText(app, File.ReadAllText(app).Replace("8080", port.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));

            vm = NewViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            await vm.OpenSolutionAsync(dir);

            await vm.RunAllTestsCommand.ExecuteAsync(null);
            Assert.True(vm.TestSummary.StartsWith("Test run finished: 2 test(s) (1 Passed, 1 Failed, 0 Skipped)", StringComparison.Ordinal), vm.TestSummary + "\n" + vm.TestsOutput.Document.Text);
            var file = Assert.Single(Assert.Single(vm.TestRoots).Children);
            Assert.Equal(("shop-app", Path.Combine("src", "checks.jwc")), (vm.TestRoots[0].Name, file.Name));
            var adds = file.Children.Single(n => n.Name == "adds up");
            Assert.Equal((TestState.Passed, Path.Combine(dir, "src", "checks.jwc"), 3), (adds.State, adds.SourcePath, adds.Line));
            var wrong = file.Children.Single(n => n.Name == "does not add up");
            Assert.Equal(TestState.Failed, wrong.State);
            Assert.False(string.IsNullOrEmpty(wrong.ErrorMessage));

            // Run Failed Tests: that one, by its name.
            await vm.RunFailedTestsCommand.ExecuteAsync(null);
            Assert.Equal((TestState.Passed, TestState.Failed), (adds.State, wrong.State));
            Assert.Contains("--filter 'does not add up'", vm.TestsOutput.Document.Text);

            // F5: no debugger, so it runs without one, and answers.
            _ = vm.StartDebuggingCommand.ExecuteAsync(null);
            using var http = new HttpClient();
            string? answer = null;
            await WaitUntil(() =>
            {
                try
                {
                    answer = http.GetStringAsync($"http://127.0.0.1:{port.Port}/").GetAwaiter().GetResult();
                    return true;
                }
                catch (HttpRequestException)
                {
                    return false;
                }
            }, TimeSpan.FromSeconds(30));
            Assert.True(answer?.Contains("\"status\"", StringComparison.Ordinal) == true, vm.DebugOutput.Document.Text + "\n" + vm.TasksOutput.Document.Text);
            Assert.Contains("jwc: serve has no debugger", vm.DebugOutput.Document.Text + vm.TasksOutput.Document.Text);

            // Restart on Save: saved, it answers with the change.
            var source = vm.OpenDocument(app)!;
            source.Host.Document.Text = source.Host.Document.Text.Replace("status: \"ok\"", "status: \"saved\"", StringComparison.Ordinal);
            source.Host.Save();
            await WaitUntil(() =>
            {
                try
                {
                    answer = http.GetStringAsync($"http://127.0.0.1:{port.Port}/").GetAwaiter().GetResult();
                    return answer.Contains("saved", StringComparison.Ordinal);
                }
                catch (HttpRequestException)
                {
                    return false;
                }
            }, TimeSpan.FromSeconds(30));
            Assert.True(answer?.Contains("saved", StringComparison.Ordinal) == true, answer + "\n" + vm.DebugOutput.Document.Text + "\n" + vm.TasksOutput.Document.Text);
            Assert.True(await vm.CloseSolutionCoreAsync());
            window.Close();
        }
        finally
        {
            // Whatever failed above: no server left listening.
            if (vm is not null)
                await vm.StopCommand.ExecuteAsync(null);
            Directory.Delete(dir, recursive: true);
        }
    }
}
