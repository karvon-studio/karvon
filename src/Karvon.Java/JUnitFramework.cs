using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Karvon.Build;
using Karvon.Modules;
using Karvon.Testing;
using Karvon.Workspace;

namespace Karvon.Java;

/// <summary>
/// JUnit's tests in Test Explorer (JUnit 5's, and JUnit 4's through its Vintage engine): found in the workspace's Java
/// files (each class's @Test, @ParameterizedTest... methods, where they are), built by jdtls, and run by JUnit's console
/// launcher on the project's test class path as jdtls has it, its XML report read; debugged as the launcher's JVM
/// waiting for java-debug to attach. It runs only when Test Explorer asks it to: it is the workspace's code.
/// </summary>
/// <param name="server">The workspace's jdtls (started the first time it is asked for).</param>
public sealed partial class JUnitFramework(TestFrameworkStart start, Func<CancellationToken, Task<JdtlsServer>> server) : TestFramework
{
    /// <summary>Each test's file and class (its binary name: shop.CalcTest$Inner), by its id.</summary>
    readonly Dictionary<string, (string File, string Class)> _tests = [];

    public override string Id => "junit";

    public override string Name => "JUnit";

    public override bool CanDebug => start.StartDebuggingAsync is not null;

    static readonly HashSet<string> BuildFolders = new(StringComparer.OrdinalIgnoreCase) { "target", "build", "bin", "out", "node_modules", ".git", ".gradle", ".idea" };

    /// <summary>Whether a folder has JUnit tests: a Java file of its own (its build folders aside) that uses JUnit.</summary>
    public static bool IsUsedIn(string workspace) => TestFiles(workspace).Any();

    /// <summary>The folder's Java files that use JUnit.</summary>
    static IEnumerable<string> TestFiles(string workspace) => JavaFiles(workspace).Where(file =>
    {
        try
        {
            return File.ReadAllText(file).Contains("org.junit", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    });

    static IEnumerable<string> JavaFiles(string folder)
    {
        IEnumerable<string> files;
        IEnumerable<string> folders;
        try
        {
            files = Directory.EnumerateFiles(folder, "*.java").ToList();
            folders = Directory.EnumerateDirectories(folder).Where(d => !BuildFolders.Contains(Path.GetFileName(d)) && !Path.GetFileName(d).StartsWith('.')).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        foreach (var file in files)
            yield return file;
        foreach (var sub in folders)
            foreach (var file in JavaFiles(sub))
                yield return file;
    }

    [GeneratedRegex(@"^\s*package\s+([\w.]+)\s*;", RegexOptions.Multiline)]
    private static partial Regex PackageLine();

    [GeneratedRegex(@"\b(?:class|record|interface|enum)\s+([A-Za-z_]\w*)")]
    private static partial Regex TypeDeclaration();

    /// <summary>JUnit 5's test annotations (and JUnit 4's @Test), by their names or fully.</summary>
    [GeneratedRegex(@"@(?:org\.junit\.(?:jupiter\.api\.|jupiter\.params\.)?)?(?:Test|ParameterizedTest|RepeatedTest|TestFactory|TestTemplate)\b")]
    private static partial Regex TestAnnotation();

    [GeneratedRegex(@"^\s*(?:@[\w.]+(?:\([^)]*\))?\s*)+")]
    private static partial Regex LeadingAnnotations();

    [GeneratedRegex(@"\b(?:void|[\w<>\[\].,?]+)\s+([A-Za-z_]\w*)\s*\(")]
    private static partial Regex MethodDeclaration();

    /// <summary>
    /// The test methods of a Java file: their class's binary name (package.Outer$Inner), their name, and the line they are
    /// on (1-based). Read as written, not compiled: a class's test methods are those with a test annotation before them.
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
            if (TypeDeclaration().Match(code) is { Success: true } type)
            {
                declared = type.Groups[1].Value;
                annotated = false;
            }
            else if (annotated && classes.Count > 0 && MethodDeclaration().Match(code) is { Success: true } method)
            {
                found.Add((package + string.Join('$', classes.Reverse().Select(c => c.Name)), method.Groups[1].Value, i + 1));
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
        return Task.FromResult(items.Count > 0 ? new TestDiscovery(items) : TestDiscovery.Failed("No JUnit tests were found (methods with @Test, @ParameterizedTest...)."));
    }

    public override async Task RunAsync(IReadOnlyList<TestItem>? tests, bool debug, Action<TestItemResult> onResult, CancellationToken ct)
    {
        var chosen = (tests?.Select(t => t.Id) ?? _tests.Keys).Where(_tests.ContainsKey).ToList();
        foreach (var id in chosen)
            onResult(new TestItemResult(id, TestState.Running, TimeSpan.Zero, null, null, null));
        if (chosen.Count == 0)
            return;
        var jdtls = await server(ct);
        var console = await JavaTools.EnsureJUnitConsoleAsync(start.Log, ct);
        if (!await BuildAsync(jdtls, ct))
            start.Log("jdtls's build has errors (see the Error List): the tests that do not compile do not run.");
        // Each project its own run, on its test class path as jdtls has it.
        var byProject = new Dictionary<string, (IReadOnlyList<string> ClassPath, List<string> Tests)>();
        foreach (var file in chosen.Select(id => _tests[id].File).Distinct())
        {
            if (await ClassPathOfAsync(jdtls, file, ct) is not { } classPath)
            {
                start.Log($"jdtls has no Java project for {Path.GetRelativePath(start.Workspace, file)}: its tests do not run.");
                continue;
            }
            if (!byProject.TryGetValue(classPath.Root, out var project))
                byProject[classPath.Root] = project = (classPath.ClassPath, []);
            project.Tests.AddRange(chosen.Where(id => _tests[id].File == file));
        }
        foreach (var (root, (classPath, ids)) in byProject)
        {
            ct.ThrowIfCancellationRequested();
            // All of a class: the class (its nested classes too, its rows as JUnit names them); else its methods.
            var selectors = tests is null
                ? ids.Select(id => _tests[id].Class).Distinct().Select(c => "--select-class=" + c)
                : ids.Select(id => "--select-method=" + id);
            await RunProjectAsync(console, root, classPath, [.. selectors], debug, onResult, ct);
        }
    }

    /// <summary>
    /// The workspace built by jdtls: a project it is still importing (its libraries not in yet) builds with errors at
    /// first, so a build with errors is made again a few times, a while apart.
    /// </summary>
    static async Task<bool> BuildAsync(JdtlsServer jdtls, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (await jdtls.BuildAsync(ct))
                return true;
            if (attempt == 5)
                return false;
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    /// <summary>A file's project's test class path; jdtls may still be importing the project, so it is asked again a while.</summary>
    static async Task<(string Root, IReadOnlyList<string> ClassPath)?> ClassPathOfAsync(JdtlsServer jdtls, string file, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            if (await jdtls.TestClassPathAsync(file, ct) is { } found)
                return found;
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        return null;
    }

    [GeneratedRegex(@"Listening for transport dt_socket at address: (\d+)")]
    private static partial Regex Listening();

    async Task RunProjectAsync(string console, string root, IReadOnlyList<string> classPath, IReadOnlyList<string> selectors, bool debug,
        Action<TestItemResult> onResult, CancellationToken ct)
    {
        var java = JavaTools.FindJava(start.Path) ?? "java";
        var work = Directory.CreateTempSubdirectory("karvon-junit-").FullName;
        var reports = Path.Combine(work, "reports");
        // The class path can be longer than a command line: the launcher reads its options from a file.
        var options = Path.Combine(work, "options");
        File.WriteAllLines(options, [.. new[] { "execute", "--disable-banner", "--details=none", "--class-path=" + string.Join(Path.PathSeparator, classPath), "--reports-dir=" + reports }
            .Concat(selectors).Select(Quote)]);
        List<string> args = debug ? ["-agentlib:jdwp=transport=dt_socket,server=y,suspend=y,address=127.0.0.1:0"] : [];
        args.AddRange(["-jar", console, "--disable-ansi-colors", "@" + options]);
        start.Log($"> {Path.GetFileName(java)} {string.Join(' ', args.Select(TaskVariables.QuoteForShell))}");
        var attached = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            using var runner = ProcessRunner.Start(java, args, root, line =>
            {
                start.Log(line);
                if (Listening().Match(line) is { Success: true } listening)
                    attached.TrySetResult(int.Parse(listening.Groups[1].Value, CultureInfo.InvariantCulture));
            }, new Dictionary<string, string> { ["PATH"] = start.Path });
            if (debug && start.StartDebuggingAsync is { } startDebugging)
            {
                // The JVM waits for the debugger before any test runs, so breakpoints hit.
                var port = await attached.Task.WaitAsync(TimeSpan.FromSeconds(60), ct);
                await startDebugging(new DebugLaunch("JUnit", new JsonObject
                {
                    ["name"] = "JUnit",
                    ["type"] = "java",
                    ["request"] = "attach",
                    ["hostName"] = "127.0.0.1",
                    ["port"] = port,
                })
                { Source = "junit" });
            }
            await runner.WaitAsync(ct);
            foreach (var result in Results(reports))
                onResult(result);
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>An option as the launcher's options file has it: in quotes, its backslashes and quotes escaped.</summary>
    static string Quote(string option) => "\"" + option.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>The results of a run's XML reports (JUnit's legacy ones: Jupiter's, Vintage's); a test the files did not show joins the tree.</summary>
    internal IEnumerable<TestItemResult> Results(string reports) =>
        !Directory.Exists(reports)
            ? []
            : JUnitReports.Read(Directory.EnumerateFiles(reports, "TEST-*.xml"))
                .Select(r => _tests.ContainsKey(r.Result.Id) ? r.Result : r.Result with { Item = ItemOf(r.Class, r.Method, null, 0) });
}
