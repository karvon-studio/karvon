using System.Net;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
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
        Assert.Equal(["shop/README.md", "shop/orders.py"], store.Files().Keys.Order(StringComparer.Ordinal));
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
        await indexer.ScanAsync();
        Assert.All(store.Files().Values, f => Assert.False(f.Outlined));
        // The server not running: the next scans ask nothing for a while, and not for each file.
        Assert.Equal(1, asked);
        await indexer.ScanAsync();
        Assert.Equal(1, asked);
        // A file of its language opened: its server runs, and the map reads them all.
        running = true;
        indexer.RetryWaiting();
        await indexer.ScanAsync();
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
