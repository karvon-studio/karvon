using System.Net;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Karvon.Claude;
using Karvon.CodeMap;
using Karvon.Lsp;
using Karvon.Modules;
using Karvon.Views;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>The Code Map: its index and search, its scans that keep it up to date, its summaries, Claude's tools and its window.</summary>
public class CodeMapTests
{
    /// <summary>A folder of its own, with files in it.</summary>
    static string Workspace(params (string Path, string Text)[] files)
    {
        var root = Directory.CreateTempSubdirectory("karvon-codemap-").FullName;
        foreach (var (path, text) in files)
            Write(root, path, text);
        return root;
    }

    static void Write(string root, string path, string text)
    {
        var file = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
        // A second's difference at least, as a later save has.
        File.SetLastWriteTimeUtc(file, File.GetLastWriteTimeUtc(file).AddSeconds(Random.Shared.Next(1, 1000)));
    }

    /// <summary>The tests' language server: "class X" and "def y" lines of .py files, a def indented under a class its method.</summary>
    sealed class FakeOutline
    {
        public List<string> Asked { get; } = [];

        public Task<IReadOnlyList<LspDocumentSymbol>?> OutlineAsync(string file, CancellationToken cancellationToken)
        {
            Asked.Add(Path.GetFileName(file));
            if (!file.EndsWith(".py", StringComparison.Ordinal))
                return Task.FromResult<IReadOnlyList<LspDocumentSymbol>?>(null);
            var lines = File.ReadAllLines(file);
            var top = new List<LspDocumentSymbol>();
            List<LspDocumentSymbol>? members = null;
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                LspRange Range(int length) => new(new LspPosition(i, 0), new LspPosition(i, length));
                if (line.StartsWith("class ", StringComparison.Ordinal))
                {
                    members = [];
                    top.Add(new LspDocumentSymbol(line[6..].TrimEnd(':'), null, LspSymbolKind.Class, Range(line.Length), Range(line.Length), members));
                }
                else if (line.StartsWith("def ", StringComparison.Ordinal))
                {
                    members = null;
                    top.Add(new LspDocumentSymbol(line[4..].Split('(')[0], null, LspSymbolKind.Function, Range(line.Length), Range(line.Length), []));
                }
                else if (line.StartsWith("    def ", StringComparison.Ordinal) && members is not null)
                    members.Add(new LspDocumentSymbol(line[8..].Split('(')[0], null, LspSymbolKind.Method, Range(line.Length), Range(line.Length), []));
            }
            return Task.FromResult<IReadOnlyList<LspDocumentSymbol>?>(top);
        }
    }

    [Fact]
    public void Names_split_into_words_and_doc_comments_give_their_first_sentence()
    {
        Assert.Equal("ports secrets portssecrets", CodeText.Words("PortsSecrets"));
        Assert.Equal("http client httpclient", CodeText.Words("HTTPClient"));
        Assert.Equal("get user id getuserid", CodeText.Words("get_user_id"));
        Assert.Equal("run", CodeText.Words("Run"));

        string[] csharp =
        [
            "    /// <summary>",
            "    /// The services' API keys: in the <see cref=\"Keyring\"/> when kept there. Never in a file.",
            "    /// </summary>",
            "    [Obsolete]",
            "    public sealed class PortsSecrets",
        ];
        Assert.Equal("The services' API keys: in the Keyring when kept there.", CodeText.Doc(csharp, 4, 4));
        string[] python = ["def total(items):", "    \"\"\"Adds the items' prices.", "    More.\"\"\"", "    return 0"];
        Assert.Equal("Adds the items' prices.", CodeText.Doc(python, 0, 3));
        string[] go = ["// Server serves the API.", "type Server struct {}"];
        Assert.Equal("Server serves the API.", CodeText.Doc(go, 1, 1));
        Assert.Null(CodeText.Doc(["x = 1", "def f():"], 1, 1));
    }

    [Fact]
    public void The_store_finds_declarations_by_their_words_and_files_by_what_they_do()
    {
        var root = Workspace();
        using var store = new CodeMapStore(Path.Combine(root, ".vs", "codemap.db"));
        store.Put(new MapFile("src/Ports/PortsService.cs", 10, 1, "h1", "C#", 120, true),
        [
            new MapSymbol("class", "PortsSecrets", null, null, 100, 140, "The services' API keys, in the system keyring.", 0),
            new MapSymbol("method", "Get", "PortsSecrets", "Get(string) : string?", 108, 120, null, 1),
            new MapSymbol("class", "PortsSettings", null, null, 13, 55, null, 0),
        ]);
        store.Put(new MapFile("src/Jira/JiraClient.cs", 10, 1, "h2", "C#", 80, true), [new MapSymbol("class", "JiraClient", null, null, 5, 80, null, 0)]);
        store.SetSummary("src/Jira/JiraClient.cs", "h2", "Talks to Jira's REST API with a site's token.");
        store.SetFolderSummary("src/Ports", "x", "Port forwarding through a tunnel service.");

        // By a word of a name, the start of one, a type and its member.
        Assert.Equal("PortsSecrets", store.Search("secrets")[0].Name);
        Assert.Equal("PortsSecrets", store.Search("ports sec")[0].Name);
        var get = Assert.Single(store.Search("PortsSecrets.Get", kind: "declaration"));
        Assert.Equal(("PortsSecrets", "Get(string) : string?", 108), (get.Container, get.Detail, get.Line));
        // The name itself first (a folder's here), then what has it as a word.
        Assert.Equal("PortsSettings", store.Search("PortsSettings")[0].Name);
        var ports = store.Search("ports");
        Assert.Equal(("Ports", true), (ports[0].Name, ports[0].IsFolder));
        Assert.Contains(ports, h => h.Name == "PortsService.cs");
        // By what it does: a doc comment, a file's summary, a folder's.
        Assert.Equal("PortsSecrets", store.Search("keyring").Single().Name);
        var jira = Assert.Single(store.Search("token rest", kind: "file"));
        Assert.Equal("src/Jira/JiraClient.cs", jira.Path);
        Assert.Equal("Talks to Jira's REST API with a site's token.", jira.Text);
        Assert.True(Assert.Single(store.Search("tunnel", kind: "folder")).IsFolder);
        // Under a folder only; any word when not all of them match.
        Assert.NotEmpty(store.Search("c", under: "src/Jira"));
        Assert.All(store.Search("c", under: "src/Jira"), h => Assert.StartsWith("src/Jira/", h.Path));
        Assert.Contains(store.Search("jira keyring"), h => h.Name == "JiraClient");
        Assert.Empty(store.Search("\"(*"));

        // Of the same name, a type before a file and a member.
        store.Put(new MapFile("src/Keyring.cs", 10, 1, "h4", "C#", 50, true),
            [new MapSymbol("class", "Keyring", null, null, 14, 50, "The system keyring.", 0), new MapSymbol("property", "Keyring", "Store", null, 40, 40, null, 1)]);
        Assert.Equal(["class", "file", "property"], store.Search("keyring").Take(3).Select(h => h.Kind));
        store.Remove("src/Keyring.cs");

        // A file read again: its old declarations go; a file gone: all of it.
        store.Put(new MapFile("src/Ports/PortsService.cs", 11, 2, "h3", "C#", 20, true), [new MapSymbol("class", "Tunnels", null, null, 1, 20, null, 0)]);
        Assert.Empty(store.Search("secrets"));
        Assert.Single(store.Search("tunnels", kind: "declaration"));
        store.Remove("src/Jira/JiraClient.cs");
        Assert.Empty(store.Search("jira"));
        Assert.Equal(["src/Ports/PortsService.cs"], store.Files().Keys);
    }

    [Fact]
    public async Task A_scan_reads_only_what_changed_and_leaves_out_build_output_and_ignored_files()
    {
        var root = Workspace(
            ("shop/orders.py", "class Orders:\n    def total(self):\n        pass\n"),
            ("shop/README.md", "# Shop\n"),
            ("bin/Debug/app.dll.py", "def built(): pass\n"),
            ("node_modules/lib/index.py", "def dependency(): pass\n"),
            ("logo.png", "\0PNG"));
        var outline = new FakeOutline();
        using var store = new CodeMapStore(Path.Combine(root, ".vs", "codemap.db"));
        var indexer = new CodeMapIndexer(root, store, outline.OutlineAsync);

        await indexer.ScanAsync();
        Assert.Equal(["logo.png", "shop/README.md", "shop/orders.py"], store.Files().Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["logo.png", "shop/README.md", "shop/orders.py"], store.Search("logo shop", kind: "file").Select(h => h.Path).Order(StringComparer.Ordinal));
        Assert.Equal(["orders.py"], outline.Asked);
        Assert.Equal(["Orders", "total"], store.Symbols("shop/orders.py").Select(s => s.Name));
        Assert.Equal("Orders", store.Symbols("shop/orders.py")[1].Container);

        // Nothing changed: nothing read. One file changed, one new, one gone: only those.
        outline.Asked.Clear();
        await indexer.ScanAsync();
        Assert.Empty(outline.Asked);
        Write(root, "shop/orders.py", "class Orders:\n    def total(self):\n        pass\n    def refund(self):\n        pass\n");
        Write(root, "shop/stock.py", "def reserve(): pass\n");
        File.Delete(Path.Combine(root, "shop/README.md"));
        await indexer.ScanAsync();
        Assert.Equal(["orders.py", "stock.py"], outline.Asked.Order(StringComparer.Ordinal));
        Assert.Contains(store.Search("refund"), h => h.Name == "refund");
        Assert.DoesNotContain("shop/README.md", store.Files().Keys);

        // In a git repository, git's files: what .gitignore leaves out is not in the map.
        var repo = Workspace(("app.py", "def main(): pass\n"), ("secrets.py", "def key(): pass\n"), (".gitignore", "secrets.py\n"));
        var git = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", ["init", "-q", repo]) { UseShellExecute = false })!;
        await git.WaitForExitAsync(TestContext.Current.CancellationToken);
        using var repoStore = new CodeMapStore(Path.Combine(repo, ".vs", "codemap.db"));
        await new CodeMapIndexer(repo, repoStore, new FakeOutline().OutlineAsync).ScanAsync();
        Assert.Equal([".gitignore", "app.py"], repoStore.Files().Keys.Order(StringComparer.Ordinal));
    }

    /// <summary>A wait for a condition off the window's thread (a test without a window has none).</summary>
    static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.True(condition());
    }

    static async Task Git(string folder, params string[] arguments)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git",
            ["-c", "user.name=test", "-c", "user.email=test@localhost", "-c", "protocol.file.allow=always", .. arguments])
        {
            WorkingDirectory = folder,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var git = System.Diagnostics.Process.Start(psi)!;
        var (output, error) = (git.StandardOutput.ReadToEndAsync(), git.StandardError.ReadToEndAsync());
        await git.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(git.ExitCode == 0, await output + await error);
    }

    [Fact]
    public async Task In_a_repository_the_map_has_what_git_tracks_wherever_it_is_and_the_submodules_files()
    {
        var library = Workspace(("core.py", "def shared(): pass\n"));
        await Git(library, "init", "-q");
        await Git(library, "add", "-A");
        await Git(library, "commit", "-q", "-m", "x");
        var root = Workspace((".gitignore", "*.log\n"), ("bin/cli.py", "def main(): pass\n"), ("src/target/plan.py", "def plan(): pass\n"), ("dist/app.py", "def app(): pass\n"),
            ("node_modules/lib/index.py", "def dependency(): pass\n"), ("assets/logo.png", "\0PNG"));
        await Git(root, "init", "-q");
        await Git(root, "add", "-A", "-f");
        await Git(root, "commit", "-q", "-m", "x");
        await Git(root, "submodule", "add", "-q", library, "external/lib");
        // New and not tracked: build output is left out, and what is ignored.
        Write(root, "src/new.py", "def fresh(): pass\n");
        Write(root, "build/target/out.py", "def built(): pass\n");
        Write(root, "debug.log", "x");
        Write(root, "external/lib/draft.py", "def draft(): pass\n");
        using var store = new CodeMapStore(Path.Combine(root, ".vs", "codemap.db"));
        await new CodeMapIndexer(root, store, new FakeOutline().OutlineAsync).ScanAsync();
        Assert.Equal([".gitignore", ".gitmodules", "assets/logo.png", "bin/cli.py", "dist/app.py", "external/lib/core.py", "external/lib/draft.py", "src/new.py", "src/target/plan.py"],
            store.Files().Keys.Order(StringComparer.Ordinal));
        Assert.Equal("external/lib/core.py", Assert.Single(store.Search("shared")).Path);
        // Not text: listed, not read.
        Assert.False(store.Files()["assets/logo.png"].IsRead);
        Assert.True(Assert.Single(store.Search("logo")).IsFile);
        Assert.DoesNotContain("assets/logo.png", store.Files().Values.Where(CodeSummarizer.IsSummarized).Select(f => f.Path));
    }

    [Fact]
    public void A_damaged_map_is_made_anew_and_one_that_cannot_be_written_is_kept_elsewhere()
    {
        MapFile File(int i) => new($"src/file{i}.py", 1, 1, "h", "Python", 1, true);
        MapSymbol[] symbols = [new("class", "Orders", null, null, 1, 2, "What a customer ordered, and when.", 0)];
        // Not a database at all.
        var root = Workspace((".vs/codemap.db", new string('x', 5000)));
        var file = Path.Combine(root, ".vs", "codemap.db");
        using (var store = new CodeMapStore(file))
        {
            Assert.Empty(store.Files());
            for (var i = 0; i < 200; i++)
                store.Put(File(i), symbols);
            Assert.Equal(200, store.Files().Count);
        }
        // A database with a part of it lost: opened, or read later, it is made anew.
        using (var stream = System.IO.File.OpenWrite(file))
        {
            stream.Position = 4096;
            stream.Write([.. Enumerable.Repeat((byte)0xAB, (int)stream.Length - 4096)]);
        }
        using (var store = new CodeMapStore(file))
        {
            store.Put(File(1), symbols);
            Assert.Equal(["src/file1.py"], store.Files().Keys);
            Assert.NotEmpty(store.Search("orders"));
        }
        // Not writable (here its folder is a file): the next one, or the memory.
        var blocked = Path.Combine(Workspace((".vs", "a file")), ".vs", "codemap.db");
        var other = Path.Combine(Workspace(), "codemap", "x.db");
        using (var store = CodeMapStore.OpenFirst(blocked, other))
            Assert.True(store.IsKept && System.IO.File.Exists(other));
        using (var store = CodeMapStore.OpenFirst(blocked))
        {
            Assert.False(store.IsKept);
            store.Put(File(1), symbols);
            Assert.Single(store.Search("customer"));
        }
    }

    [AvaloniaFact]
    public async Task A_solution_opens_whatever_its_maps_file_is()
    {
        var root = Workspace(("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"), ("a.cs", "class A {}"));
        var state = Path.GetDirectoryName(Karvon.Services.SolutionState.FileFor(Path.Combine(root, "App.csproj")))!;
        Write(root, Path.GetRelativePath(root, Path.Combine(state, "codemap.db")), new string('x', 5000));
        var vm = NewViewModel();
        vm.Module<CodeMapModule>()!.Indexes = true;
        await vm.OpenSolutionAsync(Path.Combine(root, "App.csproj"));
        var map = vm.Module<CodeMapModule>()!.Map!;
        Assert.True(map.Store.IsKept);
        await WaitUntil(() => map.Store.Files().Count == 2, TimeSpan.FromSeconds(20));
        await vm.CloseSolutionCoreAsync();
    }

    [AvaloniaFact]
    public async Task Closing_a_map_that_is_being_made_does_not_wait_on_the_window_s_thread()
    {
        var root = Workspace([.. Enumerable.Range(0, 50).Select(i => ($"f{i}.py", "x"))]);
        // As the module asks: on the window's thread, where the servers are.
        async Task<IReadOnlyList<LspDocumentSymbol>?> Slow(CancellationToken cancellationToken)
        {
            await Task.Delay(60_000, cancellationToken);
            return null;
        }
        var map = new CodeMapWorkspace(root, Path.Combine(root, ".vs", "codemap.db"), (_, ct) => Dispatcher.UIThread.InvokeAsync(() => Slow(ct), DispatcherPriority.Background));
        map.Start();
        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.True(map.Indexer.IsScanning);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var closed = map.CloseAsync();
        Assert.True(clock.ElapsedMilliseconds < 500, $"{clock.ElapsedMilliseconds} ms");
        await closed.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(TaskStatus.RanToCompletion, map.Indexer.Idle.Status);
        Assert.True(map.Indexer.ScanAsync(TestContext.Current.CancellationToken).IsCompletedSuccessfully);
    }

    [Fact]
    public async Task The_calls_made_while_the_map_is_being_made_wait_two_seconds_in_all()
    {
        var root = Workspace([.. Enumerable.Range(0, 400).Select(i => ($"f{i}.py", "x"))]);
        async Task<IReadOnlyList<LspDocumentSymbol>?> Slow(string file, CancellationToken cancellationToken)
        {
            await Task.Delay(50, cancellationToken);
            return [];
        }
        using var map = new CodeMapWorkspace(root, Path.Combine(root, ".vs", "codemap.db"), Slow);
        map.Start();
        var tool = CodeMapTools.Create(() => map).Single(t => t.Name == "code_search");
        using var input = JsonDocument.Parse("""{"query":"f1"}""");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 4; i++)
        {
            var output = await tool.CallAsync(input.RootElement, TestContext.Current.CancellationToken);
            Assert.StartsWith("(The Code Map is still being made:", output.Text);
        }
        Assert.InRange(clock.Elapsed, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_language_server_that_is_not_running_is_asked_again_once_it_runs()
    {
        var root = Workspace(("a.py", "def alpha(): pass\n"), ("b.py", "def beta(): pass\n"));
        var running = false;
        var asked = 0;
        var fake = new FakeOutline();
        Task<IReadOnlyList<LspDocumentSymbol>?> Outline(string file, CancellationToken ct)
        {
            asked++;
            return running ? fake.OutlineAsync(file, ct) : throw new LanguageServerNotRunningException("Fake");
        }
        using var store = new CodeMapStore(Path.Combine(root, ".vs", "codemap.db"));
        var indexer = new CodeMapIndexer(root, store, Outline);
        var changed = 0;
        indexer.Changed += () => changed++;
        await indexer.ScanAsync();
        Assert.All(store.Files().Values, f => Assert.False(f.Outlined));
        // The server not running: the next scans ask nothing for a while, and not for each file.
        Assert.Equal(1, asked);
        await indexer.ScanAsync();
        Assert.Equal(1, asked);
        // Asked again and still not running: the map has what it had, and nothing is told it changed.
        indexer.RetryWaiting();
        await indexer.ScanAsync();
        Assert.Equal((2, 1), (asked, changed));
        // A file of its language opened: its server runs, and the map reads them all.
        running = true;
        indexer.RetryWaiting();
        await indexer.ScanAsync();
        Assert.Equal(2, changed);
        Assert.All(store.Files().Values, f => Assert.True(f.Outlined));
        Assert.Single(store.Search("beta"));
    }

    /// <summary>The tests' model: a line for each path asked about, its sentence saying which.</summary>
    static string Answer(string prompt) => string.Join("\n", System.Text.RegularExpressions.Regex.Matches(prompt, "<(file|folder) path=\"([^\"]*)\"")
        .Select(m => $"{m.Groups[2].Value}\tAbout {m.Groups[2].Value}."));

    [Fact]
    public async Task Summaries_are_written_once_and_again_only_for_what_changed()
    {
        var root = Workspace(("shop/orders.py", "class Orders:\n    def total(self): pass\n"), ("shop/stock.py", "def reserve(): pass\n"), ("tools/run.py", "def main(): pass\n"),
            ("data.json", "{}"));
        using var store = new CodeMapStore(Path.Combine(root, ".vs", "codemap.db"));
        await new CodeMapIndexer(root, store, new FakeOutline().OutlineAsync).ScanAsync();
        var prompts = new List<string>();
        var word = "About";
        var summarizer = new CodeSummarizer(store, (prompt, _) =>
        {
            prompts.Add(prompt);
            return Task.FromResult(Answer(prompt).Replace("About ", word + " "));
        });

        await summarizer.RunAsync(root, TestContext.Current.CancellationToken);
        Assert.Null(summarizer.Problem);
        Assert.Equal("About shop/orders.py.", store.Files()["shop/orders.py"].Summary);
        Assert.Null(store.Files()["data.json"].Summary);
        Assert.Contains("Declarations: class Orders; method Orders.total", prompts[0]);
        var folders = store.Folders();
        Assert.Equal(["", "shop", "tools"], folders.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("About /.", folders[""].Summary);
        Assert.Contains("shop/ — About shop.", prompts[^1]);

        // Nothing changed: nothing asked. A file changed (and what it is for): it, and the folders above it.
        prompts.Clear();
        await summarizer.RunAsync(root, TestContext.Current.CancellationToken);
        Assert.Empty(prompts);
        word = "Now";
        var orders = store.Files()["shop/orders.py"];
        store.Put(orders with { Hash = "changed" }, store.Symbols(orders.Path));
        await summarizer.RunAsync(root, TestContext.Current.CancellationToken);
        Assert.Contains("<file path=\"shop/orders.py\"", prompts[0]);
        Assert.DoesNotContain("stock.py\"", prompts[0]);
        Assert.Equal(3, prompts.Count);
        Assert.Equal("Now /.", store.Folders()[""].Summary);
        Assert.Equal("About shop/stock.py.", store.Files()["shop/stock.py"].Summary);

        // The model's error stops the run and says why.
        var failing = new CodeSummarizer(store, (_, _) => throw new InvalidOperationException("Ollama is not running."));
        store.Put(orders with { Hash = "again" }, []);
        await failing.RunAsync(root, TestContext.Current.CancellationToken);
        Assert.Equal("Ollama is not running.", failing.Problem);
    }

    [Fact]
    public async Task An_answer_without_summaries_stops_the_run_and_summarizes_nothing_and_what_one_left_out_is_asked_once_more()
    {
        var root = Workspace(("a.py", "def a(): pass\n"), ("b.py", "def b(): pass\n"));
        using var store = new CodeMapStore(Path.Combine(root, ".vs", "codemap.db"));
        await new CodeMapIndexer(root, store, new FakeOutline().OutlineAsync).ScanAsync();
        var limit = new CodeSummarizer(store, (_, _) => Task.FromResult("You've hit your limit, it resets at 5pm."));
        await limit.RunAsync(root, TestContext.Current.CancellationToken);
        Assert.Equal("the model answered: You've hit your limit, it resets at 5pm.", limit.Problem);
        Assert.Equal(2, CodeSummarizer.Pending(store.Files()));
        Assert.All(store.Files().Values, f => Assert.Null(f.Summary));
        Assert.Empty(store.Folders());

        // b.py is left out of the first answer: it is asked about again, alone.
        var prompts = new List<string>();
        var leftOut = new HashSet<string>();
        var forgets = true;
        Task<string> Ask(string prompt)
        {
            prompts.Add(prompt);
            return Task.FromResult(forgets && prompt.Contains("<file path=\"a.py\"", StringComparison.Ordinal) ? "a.py\tAbout a.py." : Answer(prompt));
        }
        await new CodeSummarizer(store, (prompt, _) => Ask(prompt), leftOut).RunAsync(root, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("<file path=\"a.py\"", prompts[1]);
        Assert.Equal(["About a.py.", "About b.py."], store.Files().Values.OrderBy(f => f.Path).Select(f => f.Summary));
        Assert.Empty(leftOut);

        // Left out twice: not asked about again in this workspace's runs, not taken as summarized either.
        store.Put(store.Files()["b.py"] with { Hash = "changed" }, []);
        prompts.Clear();
        Task<string> Never(string prompt)
        {
            prompts.Add(prompt);
            return Task.FromResult(prompt.Contains("<folder", StringComparison.Ordinal) ? Answer(prompt) : prompts.Count == 1 ? "a.py\tAbout a.py." : "No.");
        }
        store.Put(store.Files()["a.py"] with { Hash = "changed" }, []);
        await new CodeSummarizer(store, (prompt, _) => Never(prompt), leftOut).RunAsync(root, TestContext.Current.CancellationToken);
        Assert.Equal(1, CodeSummarizer.Pending(store.Files()));
        prompts.Clear();
        await new CodeSummarizer(store, (prompt, _) => Never(prompt), leftOut).RunAsync(root, TestContext.Current.CancellationToken);
        Assert.Empty(prompts);
    }

    [Fact]
    public async Task Another_writer_chosen_while_one_writes_starts_at_once_and_the_first_s_answer_is_not_used()
    {
        var root = Workspace(("a.py", "def a(): pass\n"));
        using var map = new CodeMapWorkspace(root, Path.Combine(root, ".vs", "codemap.db"), new FakeOutline().OutlineAsync);
        await map.Indexer.ScanAsync(TestContext.Current.CancellationToken);
        var asked = new TaskCompletionSource();
        var slow = new TaskCompletionSource<string>();
        map.SetWriter((_, _) =>
        {
            asked.TrySetResult();
            return slow.Task;
        });
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        map.SetWriter((prompt, _) => Task.FromResult(Answer(prompt).Replace("About", "The second's about")));
        await Until(() => map.Store.Files()["a.py"].Summary is not null);
        slow.SetResult("a.py\tThe first's.");
        await Until(() => !map.IsSummarizing);
        Assert.Equal("The second's about a.py.", map.Store.Files()["a.py"].Summary);
        // No one chosen: the run stops, and no other starts.
        map.SetWriter((_, _) => new TaskCompletionSource<string>().Task);
        map.SetWriter(null);
        await Until(() => !map.IsSummarizing);
    }

    [Fact]
    public void Names_and_comments_in_any_script_are_found()
    {
        Assert.Equal("платёж заказа платёжзаказа", CodeText.Words("ПлатёжЗаказа"));
        Assert.Equal("straße", CodeText.Words("Straße"));
        Assert.Equal("注文", CodeText.Words("注文"));
        var root = Workspace();
        using var store = new CodeMapStore(Path.Combine(root, "codemap.db"));
        store.Put(new MapFile("a.cs", 1, 1, "h", "C#", 9, true),
        [
            new MapSymbol("method", "ПлатёжЗаказа", "Касса", null, 1, 2, "Считает налог заказа.", 0),
            new MapSymbol("class", "Платёж", null, null, 3, 4, null, 0),
            new MapSymbol("class", "Café", null, null, 5, 6, null, 0),
            new MapSymbol("function", "注文", null, null, 7, 8, null, 0),
        ]);
        Assert.Equal("ПлатёжЗаказа", Assert.Single(store.Search("налог")).Name);
        // The name itself first, whatever its case.
        Assert.Equal(["Платёж", "ПлатёжЗаказа"], store.Search("платёж").Select(h => h.Name));
        Assert.Equal("ПлатёжЗаказа", Assert.Single(store.Search("зак плат")).Name);
        Assert.Single(store.Search("café"));
        Assert.Single(store.Search("注文"));
    }

    [Fact]
    public void A_models_lines_are_read_by_the_paths_asked_about()
    {
        var lines = CodeSummarizer.Lines("""
            Here are the summaries:
            src/a.cs	Reads the settings.
            - `src/b.cs`: Writes **them**.
            src/ab.cs — Neither.
            src/c.cs
            """, ["src/a.cs", "src/b.cs", "src/ab.cs", "src/c.cs"]);
        Assert.Equal(new Dictionary<string, string> { ["src/a.cs"] = "Reads the settings.", ["src/b.cs"] = "Writes them.", ["src/ab.cs"] = "Neither." }, lines);
    }

    [Fact]
    public async Task Claudes_tools_answer_from_a_map_as_fresh_as_the_workspace()
    {
        var root = Workspace(("shop/orders.py", "class Orders:\n    def total(self): pass\n"), ("shop/stock.py", "def reserve(): pass\n"), ("README.md", "# Shop\n"));
        using var map = new CodeMapWorkspace(root, Path.Combine(root, ".vs", "codemap.db"), new FakeOutline().OutlineAsync);
        map.Start();
        var tools = CodeMapTools.Create(() => map).ToDictionary(t => t.Name);
        async Task<string> Call(string tool, string input)
        {
            using var json = JsonDocument.Parse(input);
            var output = await tools[tool].CallAsync(json.RootElement, TestContext.Current.CancellationToken);
            Assert.False(output.IsError, output.Text);
            return output.Text;
        }

        var tree = await Call("code_map", "{}");
        Assert.Equal($"""
            {Path.GetFileName(root)}/ (3 files)
              shop/ (2 files)
                orders.py: class Orders
                stock.py: function reserve
              README.md
            """, tree);
        map.Store.SetSummary("shop/stock.py", map.Store.Files()["shop/stock.py"].Hash, "Reserves the items of an order.");
        Assert.Contains("stock.py — Reserves the items of an order.", await Call("code_map", """{"path":"shop"}"""));
        Assert.Equal("""
            shop/orders.py (3 lines, Python)
              1: class Orders
                2: method total
            """, await Call("code_map", """{"path":"shop/orders.py"}"""));
        Assert.Equal("shop/stock.py:1: function reserve", await Call("code_search", """{"query":"reserve","kind":"declaration"}"""));

        // What is written is in the map at the next call, no save in Karvon needed.
        Write(root, "shop/stock.py", "def reserve(): pass\ndef release(): pass\n");
        Assert.Equal("shop/stock.py:2: function release", await Call("code_search", """{"query":"release"}"""));
        Assert.Contains("Nothing in the Code Map matches", await Call("code_search", """{"query":"zzz"}"""));
        using var missing = JsonDocument.Parse("""{"path":"nowhere"}""");
        Assert.True((await tools["code_map"].CallAsync(missing.RootElement, TestContext.Current.CancellationToken)).IsError);
    }

    [Fact]
    public async Task Claudes_tools_take_a_path_as_it_gives_it()
    {
        var root = Workspace((".github/workflows/ci.yml", "on: push\n"), ("src/app.py", "def app(): pass\n"), ("docs/app.md", "# App\n"),
            ("art/a.png", "\0"), ("art/b.png", "\0"), ("art/c.png", "\0"), ("art/d.ico", "\0"), ("art/draw.py", "def draw(): pass\n"));
        using var map = new CodeMapWorkspace(root, Path.Combine(root, ".vs", "codemap.db"), new FakeOutline().OutlineAsync);
        var tools = CodeMapTools.Create(() => map).ToDictionary(t => t.Name);
        async Task<ClaudeToolOutput> Call(string tool, object input)
        {
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(input));
            return await tools[tool].CallAsync(json.RootElement, TestContext.Current.CancellationToken);
        }

        foreach (var path in new[] { ".github", "./.github", ".github/", "/.github", root + "/.github" })
            Assert.StartsWith(".github/ (1 file)", (await Call("code_map", new { path })).Text);
        foreach (var path in new[] { "", ".", "./", "/", root, root + "/", "src/.." })
            Assert.StartsWith(Path.GetFileName(root) + "/ (8 files)", (await Call("code_map", new { path })).Text);
        foreach (var path in new[] { "src", "./src", "src/", @".\src", "/src", root + "/src", "docs/../src" })
            Assert.Equal("src/app.py\nsrc/app.py:1: function app", (await Call("code_search", new { query = "app", path })).Text.ReplaceLineEndings("\n"));
        Assert.Equal("src/app.py\nsrc/app.py:1: function app", (await Call("code_search", new { query = "app", path = "./src/app.py" })).Text);
        // A path the map does not have is said so, not answered as if nothing matched.
        foreach (var path in new[] { "nowhere", "../" + Path.GetFileName(root) + "x", "/etc/passwd" })
            Assert.Contains("is not a folder or a file of the map", (await Call("code_search", new { query = "app", path })) is { IsError: true } error ? error.Text : "");
        // What is not read: by its name alone, counted when there are many.
        Assert.Equal("art/ (5 files)\n  draw.py: function draw\n  also 3 .png, 1 .ico", (await Call("code_map", new { path = "art" })).Text);
        Assert.Contains("not read", (await Call("code_map", new { path = "art/a.png" })).Text);
        // What the tools' descriptions show is no one's project's.
        Assert.All(tools.Values, t => Assert.DoesNotContain("Secrets", t.Description));
    }

    [AvaloniaFact]
    public async Task Only_a_command_that_lists_what_the_map_has_is_sent_to_it()
    {
        var root = Workspace(("src/app.py", "def app(): pass\n"), ("src/shop/orders.py", "def order(): pass\n"), ("README.md", "# App\n"));
        var vm = NewViewModel();
        var module = vm.Module<CodeMapModule>()!;
        var rule = module.ClaudeShellRules().Single();
        Assert.False(rule.Applies("find . -type f", root));
        module.Indexes = true;
        await vm.OpenSolutionAsync(root);
        await WaitUntil(() => module.Map!.Indexer.IsComplete, TimeSpan.FromSeconds(20));
        string[] listings =
        [
            "find . -type f", "find . -name '*.py'", "find src -type f -name '*.py' -not -path '*/bin/*' | head -50", "tree", "tree -L 2 src", "ls -R", "ls -aR src",
            "cd src && find . -type d", $"cd {root}; ls -R src 2>/dev/null | head", $"find {root}/src -iname 'order*'", "find . -type f | grep -v test | sort",
            @"find . \( -name bin -o -name obj \) -prune -o -type f -name '*.py' -print", "pwd; tree -d",
        ];
        foreach (var command in listings)
            Assert.True(rule.Applies(command, root), command);
        string[] shells =
        [
            // Something else is done with the files.
            "find . -name '*.py' -delete", "find . -type f -name '*.py' -exec grep -l TODO {} +", "find . -name '*.py' | xargs cat", "find . -type f | wc -l",
            "find . -type f > files.txt", "for f in $(find . -name '*.py'); do head -3 $f; done", "cat $(find . -name orders.py)", "find . -type f | grep -rn order src",
            // With other work in the same command, which would not run either.
            "cd src; grep -n order *.py | head; find . -name 'app*'", "git ls-files; cat README.md; find . -name '*.py' -not -path './bin/*' | xargs cat", "ls -R; git log --oneline | head",
            // What the map does not have: sizes and times, other folders, files that are not code.
            "find . -type f -size +1M", "find . -name '*.py' -newer README.md", "find . -maxdepth 1 -mmin -5", "ls -lRt src", "tree -s", "find . -name '*.png'", "find bin -name '*.dll'",
            "find node_modules -name package.json", "find . -name node_modules", "find obj -type d -empty", "find logs -type f", "find /usr/lib -name '*.py'", "find ~/.cache -type f",
            "find $HOME -name x", "ls -R ..",
            // Not a listing.
            "git commit -m 'x\nfind files with -name'", "ls", "grep -rn order .", "echo 'find . -type f'", "cd src",
        ];
        foreach (var command in shells)
            Assert.False(rule.Applies(command, root), command);
        await vm.CloseSolutionCoreAsync();
    }

    /// <summary>A language whose server outlines the open .hint files' "def" lines.</summary>
    sealed class FakeSymbolsModule : KarvonModule
    {
        public override string Id => "fake-symbols";

        public override string Name => "Fake symbols";

        public override string Description => "A language whose server outlines open files, for the tests.";

        public override IReadOnlyList<LanguageServerInfo> LanguageServers =>
            [new("fake-symbols", "Fake symbols server", path => path.EndsWith(".hint", StringComparison.Ordinal), start => FakeLanguageClient.StartAsync(start, null, "--symbols"))];
    }

    sealed class FakeOllama : HttpMessageHandler
    {
        public List<string> Prompts { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var prompt = json.RootElement.GetProperty("prompt").GetString()!;
            lock (Prompts)
                Prompts.Add(prompt);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { response = Answer(prompt), done = true }), Encoding.UTF8, "application/json"),
            };
        }
    }

    [AvaloniaFact]
    public async Task The_window_maps_the_workspace_searches_it_as_you_type_and_shows_the_summaries_ollama_wrote()
    {
        var root = Workspace(("tools.hint", "def alpha\ndef beta\n"), ("notes/plan.md", "# Plan\n"));
        var vm = NewViewModel(modules: [new FakeSymbolsModule(), .. Karvon.BuiltInModules.Create()]);
        var codeMap = vm.Module<CodeMapModule>()!;
        var ollama = new FakeOllama();
        codeMap.Http = new HttpClient(ollama);
        CodeMapWorkspace.SummaryDelay = TimeSpan.FromMilliseconds(100);
        codeMap.Indexes = true;
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        window.Show();
        await vm.OpenSolutionAsync(root);
        // The fake language's server runs once one of its files is open; the map reads its declarations then.
        vm.OpenDocument(Path.Combine(root, "tools.hint"));
        vm.ShowToolWindow(CodeMapModule.ToolWindowId);
        var view = codeMap.EnsureView();
        await WaitUntil(() => codeMap.Map?.Store.SymbolCount() == 2, TimeSpan.FromSeconds(30));
        await WaitUntil(() => view.Status.StartsWith("2 files", StringComparison.Ordinal), TimeSpan.FromSeconds(5));
        Assert.Equal("2 files · 2 declarations", view.Status);
        Assert.Equal(["notes", "tools.hint"], view.Roots.Select(r => r.Name));
        Assert.Equal("1 file", view.Roots[0].Detail);
        Assert.Equal("No summaries: choose who writes them below.", view.SummaryStatus);

        view.Query = "bet";
        await WaitUntil(() => view.Results.Count > 0, TimeSpan.FromSeconds(5));
        var hit = view.Results[0];
        Assert.Equal(("beta", "tools.hint:2"), (hit.Title, hit.Location));
        view.Query = "";

        // Summaries by Ollama: written, shown in the tree, kept in codemap.json.
        view.SourceIndex = (int)SummarySource.Ollama;
        await WaitUntil(() => view.SummaryStatus.EndsWith("up to date.", StringComparison.Ordinal), TimeSpan.FromSeconds(30));
        Assert.Equal("1 file summarized: up to date.", view.SummaryStatus);
        Assert.Equal("About tools.hint.", view.Roots.Single(r => r.Name == "tools.hint").Detail);
        Assert.Contains("\"summaries\": \"ollama\"", File.ReadAllText(codeMap.SettingsFile));
        Assert.Contains(ollama.Prompts, p => p.Contains("def alpha", StringComparison.Ordinal));

        // A file's declarations under it; opening one goes to its line.
        var file = view.Roots.Single(r => r.Name == "tools.hint");
        file.IsExpanded = true;
        Assert.Equal(["alpha", "beta"], file.Children.Select(c => c.Name));
        view.Open("tools.hint", 2);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, vm.ActiveEditor!.Host.CaretPosition.Item1);

        // Claude's code_search through the module's tools.
        var search = codeMap.ClaudeTools().Single(t => t.Name == "code_search");
        using var input = JsonDocument.Parse("""{"query":"alpha"}""");
        Assert.Equal("tools.hint:1: function alpha", (await search.CallAsync(input.RootElement, TestContext.Current.CancellationToken)).Text);
        window.Close();
    }
}
