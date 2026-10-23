using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Karvon.Build;
using Karvon.Modules;
using Karvon.Testing;
using Karvon.Workspace;

namespace Karvon.Kotlin;

/// <summary>
/// A Kotlin Gradle build's tests in Test Explorer: JUnit's (kotlin.test's) tests in the workspace's Kotlin files, found as
/// written (each class's @Test functions, where they are); run by the build's Gradle (cleanTest test --tests ...), its
/// XML reports read; debugged as Gradle's test JVM waiting (--debug-jvm) for kotlin-lsp's debugger to attach. It runs
/// only when Test Explorer asks it to: it is the workspace's code.
/// </summary>
public sealed partial class KotlinTestFramework(TestFrameworkStart start) : TestFramework
{
    /// <summary>Each test's file and class (its binary name: shop.CartTest$Empty), by its id.</summary>
    readonly Dictionary<string, (string File, string Class)> _tests = [];

    public override string Id => "kotlin-gradle";

    public override string Name => "Kotlin (Gradle)";

    public override bool CanDebug => start.StartDebuggingAsync is not null;

    static readonly HashSet<string> BuildFolders = new(StringComparer.OrdinalIgnoreCase) { "build", "out", "bin", "node_modules", ".git", ".gradle", ".idea", ".kotlin" };

    /// <summary>Whether a folder has Kotlin tests: a Kotlin Gradle build, and a Kotlin file of its own that has a test.</summary>
    public static bool IsUsedIn(string workspace) => KotlinTasks.IsKotlinBuild(workspace) && TestFiles(workspace).Any();

    /// <summary>The folder's Kotlin files with a test annotation.</summary>
    static IEnumerable<string> TestFiles(string workspace) => KotlinFiles(workspace).Where(file =>
    {
        try
        {
            return TestAnnotation().IsMatch(File.ReadAllText(file));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    });

    static IEnumerable<string> KotlinFiles(string folder)
    {
        IEnumerable<string> files;
        IEnumerable<string> folders;
        try
        {
            files = Directory.EnumerateFiles(folder, "*.kt").ToList();
            folders = Directory.EnumerateDirectories(folder).Where(d => !BuildFolders.Contains(Path.GetFileName(d)) && !Path.GetFileName(d).StartsWith('.')).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        foreach (var file in files)
            yield return file;
        foreach (var sub in folders)
            foreach (var file in KotlinFiles(sub))
                yield return file;
    }

    [GeneratedRegex(@"^\s*package\s+([\w.]+)", RegexOptions.Multiline)]
    private static partial Regex PackageLine();

    /// <summary>A class or an object (a companion's has no name of its own: its functions are its class's).</summary>
    [GeneratedRegex(@"\b(?:class|object|interface)\s+([A-Za-z_]\w*)")]
    private static partial Regex TypeDeclaration();

    /// <summary>kotlin.test's and JUnit's test annotations, by their names or fully.</summary>
    [GeneratedRegex(@"@(?:kotlin\.test\.|org\.junit\.(?:jupiter\.api\.|jupiter\.params\.)?)?(?:Test|ParameterizedTest|RepeatedTest|TestFactory|TestTemplate)\b")]
    private static partial Regex TestAnnotation();

    [GeneratedRegex(@"^\s*(?:@[\w.]+(?:\([^)]*\))?\s*)+")]
    private static partial Regex LeadingAnnotations();

    /// <summary>A function: its name, or its name in backticks (`adds two numbers`).</summary>
    [GeneratedRegex(@"\bfun\s+(?:<[^>]*>\s*)?(?:`([^`]+)`|([A-Za-z_]\w*))\s*\(")]
    private static partial Regex FunctionDeclaration();

    /// <summary>
    /// The tests of a Kotlin file: their class's binary name (package.Outer$Inner), their function's name, and the line
    /// they are on (1-based). Read as written, not compiled: a class's tests are its functions with a test annotation.
    /// </summary>
    internal static List<(string Class, string Method, int Line)> TestsIn(string text)
    {
        var package = PackageLine().Match(text) is { Success: true } p ? p.Groups[1].Value + "." : "";
        var found = new List<(string, string, int)>();
        var classes = new Stack<(string Name, int Depth)>();
        string? declared = null;
        var annotated = false;
        var depth = 0;
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.IndexOf("//", StringComparison.Ordinal) is var comment and >= 0)
                line = line[..comment];
            if (TestAnnotation().IsMatch(line))
                annotated = true;
            var code = LeadingAnnotations().Replace(line, "");
            if (FunctionDeclaration().Match(code) is { Success: true } function)
            {
                if (annotated && classes.Count > 0)
                    found.Add((package + string.Join('$', classes.Reverse().Select(c => c.Name)), function.Groups[1].Success ? function.Groups[1].Value : function.Groups[2].Value, i + 1));
                annotated = false;
            }
            else if (TypeDeclaration().Match(code) is { Success: true } type)
            {
                declared = type.Groups[1].Value;
                annotated = false;
            }
            foreach (var c in line)
            {
                if (c == '{')
                {
                    if (declared is not null)
                    {
                        classes.Push((declared, depth));
                        declared = null;
                    }
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (classes.Count > 0 && classes.Peek().Depth == depth)
                        classes.Pop();
                }
            }
        }
        return found;
    }

    string ProjectName => Path.GetFileName(start.Workspace.TrimEnd('/', '\\'));

    TestItem ItemOf(string @class, string method, string? file, int line)
    {
        var dot = @class.LastIndexOf('.');
        var ns = dot < 0 ? "<default>" : @class[..dot];
        var simple = (dot < 0 ? @class : @class[(dot + 1)..]).Replace('$', '.');
        return new TestItem($"{@class}#{method}", [new(TestNodeKind.Project, ProjectName), new(TestNodeKind.Namespace, ns), new(TestNodeKind.Class, simple)], method, file, line)
        {
            FullName = $"{@class.Replace('$', '.')}.{method}",
            Container = file,
        };
    }

    public override Task<TestDiscovery> DiscoverAsync(CancellationToken ct)
    {
        var items = new List<TestItem>();
        _tests.Clear();
        foreach (var file in TestFiles(start.Workspace))
        {
            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var (@class, method, line) in TestsIn(text))
            {
                var item = ItemOf(@class, method, file, line);
                if (_tests.TryAdd(item.Id, (file, @class)))
                    items.Add(item);
            }
        }
        return Task.FromResult(items.Count > 0 ? new TestDiscovery(items) : TestDiscovery.Failed("No Kotlin tests were found (functions with @Test)."));
    }

    [GeneratedRegex(@"Listening for transport dt_socket at address: (\d+)")]
    private static partial Regex Listening();

    /// <summary>
    /// Gradle's arguments for a run: its tests run again (cleanTest, which also takes the last run's reports away), the
    /// chosen ones by their filters (a class, or its function), the test JVM waiting for a debugger when debugging.
    /// </summary>
    internal static List<string> Arguments(IEnumerable<string>? filters, bool debug)
    {
        List<string> args = ["cleanTest", "test", "--console=plain"];
        foreach (var filter in filters ?? [])
            args.AddRange(["--tests", filter]);
        if (debug)
            args.Add("--debug-jvm");
        return args;
    }

    public override async Task RunAsync(IReadOnlyList<TestItem>? tests, bool debug, Action<TestItemResult> onResult, CancellationToken ct)
    {
        var chosen = (tests?.Select(t => t.Id) ?? _tests.Keys).Where(_tests.ContainsKey).ToList();
        foreach (var id in chosen)
            onResult(new TestItemResult(id, TestState.Running, TimeSpan.Zero, null, null, null));
        if (chosen.Count == 0)
            return;
        // All of them: the build's whole test task; else each test by its class and function (a nested class's with '$').
        var filters = tests is null ? null : chosen.Select(id => id.Replace('#', '.'));
        var gradle = KotlinTasks.GradleOf(start.Workspace);
        var args = Arguments(filters, debug && CanDebug);
        start.Log($"> {Path.GetFileName(gradle)} {string.Join(' ', args.Select(TaskVariables.QuoteForShell))}");
        var attached = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var runner = ProcessRunner.Start(gradle, args, start.Workspace, line =>
        {
            start.Log(line);
            if (Listening().Match(line) is { Success: true } listening)
                attached.TrySetResult(int.Parse(listening.Groups[1].Value, CultureInfo.InvariantCulture));
        }, new Dictionary<string, string> { ["PATH"] = start.Path });
        if (debug && start.StartDebuggingAsync is { } startDebugging)
        {
            // The test JVM waits for the debugger before any test runs, so breakpoints hit.
            var exited = runner.WaitAsync(ct);
            if (await Task.WhenAny(attached.Task, exited) == attached.Task)
                await startDebugging(new DebugLaunch("Kotlin tests", new JsonObject
                {
                    ["name"] = "Kotlin tests",
                    ["type"] = "intellij_jvm",
                    ["request"] = "attach",
                    ["port"] = attached.Task.Result,
                })
                { Source = "kotlin" });
        }
        await runner.WaitAsync(ct);
        foreach (var result in Results(Reports(start.Workspace)))
            onResult(result);
    }

    /// <summary>The build's test reports: each project's build/test-results (the root project's, and its subprojects').</summary>
    static IEnumerable<string> Reports(string workspace)
    {
        IEnumerable<string> In(string folder, int depth)
        {
            var results = Path.Combine(folder, "build", "test-results");
            if (Directory.Exists(results))
                foreach (var report in Directory.EnumerateFiles(results, "TEST-*.xml", SearchOption.AllDirectories))
                    yield return report;
            if (depth == 3)
                yield break;
            string[] folders;
            try
            {
                folders = Directory.GetDirectories(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                yield break;
            }
            foreach (var sub in folders.Where(d => !BuildFolders.Contains(Path.GetFileName(d)) && !Path.GetFileName(d).StartsWith('.') && Path.GetFileName(d) != "src"))
                foreach (var report in In(sub, depth + 1))
                    yield return report;
        }
        return In(workspace, 0);
    }

    /// <summary>The results of the reports; a test the files did not show (a Java one, a generated one) joins the tree.</summary>
    internal IEnumerable<TestItemResult> Results(IEnumerable<string> reports) =>
        JUnitReports.Read(reports).Select(r => _tests.ContainsKey(r.Result.Id) ? r.Result : r.Result with { Item = ItemOf(r.Class, r.Method, null, 0) });
}
