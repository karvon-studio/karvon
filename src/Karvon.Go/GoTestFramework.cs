using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Karvon.Build;
using Karvon.Modules;
using Karvon.Testing;
using Karvon.Workspace;

namespace Karvon.Go;

/// <summary>
/// go test's tests in Test Explorer: the Test, Example and Fuzz functions of the module's packages' test files (found by
/// go list and read from the files, so nothing builds to find them), run by go test -json, debugged by Delve in its
/// test mode. It runs only when Test Explorer asks it to: it is the module's code.
/// </summary>
public sealed partial class GoTestFramework(TestFrameworkStart start) : TestFramework
{
    readonly string _module = ModuleName(start.Workspace);

    public override string Id => "go";

    public override string Name => "go test";

    public override bool CanDebug => start.DebugAsync is not null;

    /// <summary>The package a test is in (its import path and folder), by its id.</summary>
    readonly Dictionary<string, (string ImportPath, string Dir)> _packages = [];

    public static bool IsUsedIn(string workspace) => File.Exists(Path.Combine(workspace, "go.mod"));

    static string ModuleName(string workspace)
    {
        try
        {
            var line = File.ReadLines(Path.Combine(workspace, "go.mod")).FirstOrDefault(l => l.StartsWith("module ", StringComparison.Ordinal));
            return line?["module ".Length..].Trim() ?? Path.GetFileName(workspace);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Path.GetFileName(workspace);
        }
    }

    [GeneratedRegex(@"^func\s+(?<name>(?:Test|Fuzz)\w*)\s*\(\s*\w+\s+\*testing\.[TF]\s*\)|^func\s+(?<name>Example\w*)\s*\(\s*\)", RegexOptions.Multiline)]
    private static partial Regex TestFunction();

    public override async Task<TestDiscovery> DiscoverAsync(CancellationToken ct)
    {
        var lines = new List<string>();
        if (await GoAsync(["list", "-f", "{{.ImportPath}}|{{.Dir}}|{{join .TestGoFiles \",\"}}|{{join .XTestGoFiles \",\"}}", "./..."], lines.Add, ct) is { } problem)
            return TestDiscovery.Failed(problem);
        var tests = new List<TestItem>();
        _packages.Clear();
        foreach (var line in lines)
        {
            var parts = line.Split('|');
            if (parts.Length != 4 || !Directory.Exists(parts[1]))
                continue;
            var (importPath, dir) = (parts[0], parts[1]);
            foreach (var file in parts[2].Split(',', StringSplitOptions.RemoveEmptyEntries).Concat(parts[3].Split(',', StringSplitOptions.RemoveEmptyEntries)))
            {
                var path = Path.Combine(dir, file);
                var text = await File.ReadAllTextAsync(path, ct);
                foreach (Match m in TestFunction().Matches(text))
                {
                    var item = ItemOf(importPath, m.Groups["name"].Value, path, text[..m.Index].Count(c => c == '\n') + 1);
                    _packages[item.Id] = (importPath, dir);
                    tests.Add(item);
                }
            }
        }
        return new TestDiscovery(tests);
    }

    /// <summary>A test function: under the module, its package.</summary>
    TestItem ItemOf(string importPath, string name, string? file, int line) =>
        new($"{importPath}.{name}", [new(TestNodeKind.Project, _module), new(TestNodeKind.Namespace, importPath)], name, file, line)
        {
            FullName = $"{importPath}.{name}",
            Container = file,
        };

    public override async Task RunAsync(IReadOnlyList<TestItem>? tests, bool debug, Action<TestItemResult> onResult, CancellationToken ct)
    {
        foreach (var test in tests ?? [])
            onResult(new TestItemResult(test.Id, TestState.Running, TimeSpan.Zero, null, null, null));
        var only = tests?.Select(t => t.Id).ToHashSet();
        var byPackage = (tests ?? []).Where(t => _packages.ContainsKey(t.Id)).GroupBy(t => _packages[t.Id]).ToList();
        if (debug && start.DebugAsync is { } debugAsync)
        {
            // One package at a time under Delve, its tests by -test.run; their outcomes read from -test.v's lines.
            foreach (var package in byPackage)
            {
                var output = await debugAsync(new DebugLaunch($"go test: {package.Key.ImportPath}", new JsonObject
                {
                    ["type"] = "go",
                    ["request"] = "launch",
                    ["name"] = $"go test: {package.Key.ImportPath}",
                    ["mode"] = "test",
                    ["program"] = package.Key.Dir,
                    ["args"] = new JsonArray("-test.v", "-test.run", RunPattern(package)),
                })
                { Source = "go" });
                foreach (Match m in VerboseResult().Matches(output))
                {
                    var id = $"{package.Key.ImportPath}.{m.Groups["name"].Value}";
                    if (only?.Contains(id) == false)
                        continue;
                    onResult(new TestItemResult(id, StateOf(m.Groups["result"].Value.ToLowerInvariant()), TimeSpan.FromSeconds(double.Parse(m.Groups["time"].Value, System.Globalization.CultureInfo.InvariantCulture)), null, null, null));
                }
            }
            return;
        }
        List<string> args = ["test", "-json"];
        if (tests is not null)
            args.AddRange(["-run", "^(?:" + string.Join('|', tests.Select(t => Regex.Escape(t.Name)).Distinct()) + ")$", .. byPackage.Select(p => p.Key.ImportPath)]);
        else
            args.Add("./...");
        var outputs = new Dictionary<string, List<string>>();
        if (await GoAsync(args, line => OnEvent(line, outputs, only, onResult), ct) is { } problem)
            start.Log(problem);
    }

    static string RunPattern(IEnumerable<TestItem> tests) => "^(?:" + string.Join('|', tests.Select(t => Regex.Escape(t.Name))) + ")$";

    [GeneratedRegex(@"^\s*--- (?<result>PASS|FAIL|SKIP): (?<name>[^\s/]+) \((?<time>[\d.]+)s\)", RegexOptions.Multiline)]
    private static partial Regex VerboseResult();

    static TestState StateOf(string action) => action switch
    {
        "pass" => TestState.Passed,
        "fail" => TestState.Failed,
        _ => TestState.Skipped,
    };

    /// <summary>
    /// One of go test -json's events: a test's output (kept until it ends), its end (pass, fail, skip; its subtests' are
    /// in its output), and the rest (a package's build failing) to the log.
    /// </summary>
    void OnEvent(string line, Dictionary<string, List<string>> outputs, HashSet<string>? only, Action<TestItemResult> onResult)
    {
        JsonNode? e;
        try
        {
            e = line.StartsWith('{') ? JsonNode.Parse(line) : null;
        }
        catch (JsonException)
        {
            e = null;
        }
        if (e is null)
        {
            start.Log(line);
            return;
        }
        var action = e["Action"]?.GetValue<string>();
        var package = e["Package"]?.GetValue<string>() ?? "";
        var test = e["Test"]?.GetValue<string>();
        if (test is null)
        {
            if (action == "output" && e["Output"]?.GetValue<string>() is { } text)
                start.Log(text.TrimEnd('\n'));
            return;
        }
        // A subtest (TestArea/zero) is its test's: its lines in the test's output.
        var top = test.Split('/')[0];
        var id = $"{package}.{top}";
        if (action == "output")
        {
            if (!outputs.TryGetValue(id, out var list))
                outputs[id] = list = [];
            list.Add(e["Output"]?.GetValue<string>() ?? "");
            return;
        }
        if (action is not ("pass" or "fail" or "skip") || test != top || only?.Contains(id) == false)
            return;
        var output = string.Concat(outputs.GetValueOrDefault(id) ?? []);
        // The first line of the test's own (file.go:12: ...), as the failure's message.
        var message = action == "fail" ? output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => FailureLine().IsMatch(l)) : null;
        if (action == "skip")
            message = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => FailureLine().IsMatch(l));
        var item = ItemOf(package, top, null, 0);
        onResult(new TestItemResult(id, StateOf(action), TimeSpan.FromSeconds(e["Elapsed"]?.GetValue<double>() ?? 0), message,
            action == "fail" ? output.TrimEnd() : null, output.Length == 0 ? null : output.TrimEnd()));
        _ = item;
    }

    [GeneratedRegex(@"^\w[\w.-]*\.go:\d+: ")]
    private static partial Regex FailureLine();

    /// <summary>go in the module's folder, each line it writes to <paramref name="onLine"/>; why it could not run, or null.</summary>
    async Task<string?> GoAsync(IReadOnlyList<string> args, Action<string> onLine, CancellationToken ct)
    {
        var path = GoTools.PathWithGo(start.Path);
        if (TaskVariables.Find(OperatingSystem.IsWindows() ? "go.exe" : "go", path, start.Workspace) is not { } go)
            return "Go was not found: install it from go.dev/dl.";
        start.Log($"> go {string.Join(' ', args.Select(TaskVariables.QuoteForShell))}");
        using var runner = ProcessRunner.Start(go, args, start.Workspace, onLine, new Dictionary<string, string> { ["PATH"] = path });
        await runner.WaitAsync(ct);
        return null;
    }
}
