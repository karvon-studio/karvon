using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Karvon.Build;
using Karvon.Modules;
using Karvon.Testing;
using Karvon.Workspace;

namespace Karvon.TypeScript;

/// <summary>
/// A JavaScript test framework's tests in Test Explorer (Vitest's, Jest's): those of a package whose package.json depends
/// on it, run with its JSON report (the same for both: Vitest writes Jest's) by the package's own copy (installed in its
/// node_modules) on Node.js, or under js-debug. It runs only when Test Explorer asks it to: it is the package's code.
/// </summary>
public abstract partial class JsTestFramework(string packageDirectory, TestFrameworkStart start) : TestFramework
{
    protected readonly string PackageDirectory = packageDirectory;
    protected readonly TestFrameworkStart Start = start;
    protected readonly string Project = Path.GetFileName(packageDirectory.TrimEnd(Path.DirectorySeparatorChar));

    /// <summary>Its package's name ("vitest"), in a package.json's dependencies.</summary>
    protected abstract string Package { get; }

    /// <summary>Its program in its package (vitest.mjs, bin/jest.js).</summary>
    protected abstract string EntryFile { get; }

    /// <summary>A run of these files (all with none) writing its JSON report to a file.</summary>
    protected abstract List<string> RunArguments(string report);

    /// <summary>What a run under the debugger takes besides (the test files one at a time, in one process).</summary>
    protected abstract IReadOnlyList<string> DebugArguments { get; }

    public override string Id => Package + ":" + RelativeTo(Start.Workspace, PackageDirectory);

    /// <summary>Under js-debug, as F5 debugs a launch configuration: the framework and the test files' process it starts.</summary>
    public override bool CanDebug => Start.DebugAsync is not null;

    /// <summary>The packages of a folder that test with the framework <paramref name="package"/>.</summary>
    protected static IEnumerable<string> PackagesUsing(string workspace, string package) =>
        PackageScripts.PackageFiles(workspace).Where(file => Uses(file, package)).Select(file => Path.GetDirectoryName(file)!);

    static bool Uses(string packageFile, string package)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(packageFile), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && new[] { "devDependencies", "dependencies" }.Any(section => doc.RootElement.TryGetProperty(section, out var deps)
                    && deps.ValueKind == JsonValueKind.Object && deps.TryGetProperty(package, out _));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The package's copy: in its node_modules, or a workspace's above it up to the folder (hoisted).</summary>
    protected string? Entry()
    {
        for (var dir = PackageDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            var entry = Path.Combine(dir, "node_modules", Package, EntryFile);
            if (File.Exists(entry))
                return entry;
            if (string.Equals(dir.TrimEnd(Path.DirectorySeparatorChar), Start.Workspace.TrimEnd(Path.DirectorySeparatorChar), Services.FilePaths.Comparison))
                break;
        }
        return null;
    }

    protected string NotInstalled => $"{Name} is not installed in {Project}: install its packages (npm install).";

    /// <summary>A test by its file and titles (its describe blocks', then its own): under its package, its file, its blocks.</summary>
    protected TestItem ItemOf(string file, IReadOnlyList<string> titles, int line)
    {
        var fullName = string.Join(" > ", titles);
        TestGroup[] groups = [new(TestNodeKind.Project, Project), new(TestNodeKind.Namespace, RelativeTo(PackageDirectory, file)), .. titles.SkipLast(1).Select(t => new TestGroup(TestNodeKind.Class, t))];
        return new TestItem($"{RelativeTo(PackageDirectory, file)}::{fullName}", groups, titles[^1], file, line) { FullName = fullName, Container = file };
    }

    public override async Task RunAsync(IReadOnlyList<TestItem>? tests, bool debug, Action<TestItemResult> onResult, CancellationToken ct)
    {
        var report = Path.Combine(Path.GetTempPath(), $"karvon-{Package}-{Guid.NewGuid():N}.json");
        var args = RunArguments(report);
        var only = tests?.Select(t => t.Id).ToHashSet();
        if (tests is not null)
        {
            // Their files, and their names (-t takes a test's names joined by spaces, in Vitest and in Jest).
            args.AddRange(tests.Select(t => t.File).OfType<string>().Distinct());
            args.AddRange(["-t", "^(?:" + string.Join('|', tests.Select(t => Regex.Escape(t.FullName.Replace(" > ", " ")))) + ")$"]);
        }
        foreach (var test in tests ?? [])
            onResult(new TestItemResult(test.Id, TestState.Running, TimeSpan.Zero, null, null, null));
        try
        {
            if ((debug && Start.DebugAsync is { } debugAsync ? await DebugAsync(debugAsync, args) : await RunAsync(args, ct)) is { } problem)
            {
                Start.Log(problem);
                return;
            }
            if (!File.Exists(report))
            {
                Start.Log($"{Name} wrote no report (see above).");
                return;
            }
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(report, ct));
            foreach (var file in doc.RootElement.GetProperty("testResults").EnumerateArray())
            {
                var path = file.GetProperty("name").GetString() ?? "";
                foreach (var result in file.GetProperty("assertionResults").EnumerateArray())
                {
                    string[] titles = [.. result.GetProperty("ancestorTitles").EnumerateArray().Select(t => t.GetString() ?? ""), result.GetProperty("title").GetString() ?? ""];
                    var line = result.TryGetProperty("location", out var at) && at.TryGetProperty("line", out var l) ? l.GetInt32() : 0;
                    var item = ItemOf(path, titles, line);
                    // A test left out by -t is reported as skipped: it did not run.
                    if (only is not null && !only.Contains(item.Id))
                        continue;
                    // Skipped and todo tests: Vitest's "skipped", Jest's "pending".
                    var state = result.GetProperty("status").GetString() switch
                    {
                        "passed" => TestState.Passed,
                        "failed" => TestState.Failed,
                        _ => TestState.Skipped,
                    };
                    var duration = result.TryGetProperty("duration", out var ms) && ms.ValueKind == JsonValueKind.Number ? TimeSpan.FromMilliseconds(ms.GetDouble()) : TimeSpan.Zero;
                    var failure = string.Join("\n", result.GetProperty("failureMessages").EnumerateArray().Select(m => m.GetString()));
                    var (message, stack) = failure.Length == 0 ? (null, null) : SplitFailure(failure);
                    onResult(new TestItemResult(item.Id, state, duration, message, stack, null) { Item = item });
                }
            }
        }
        catch (JsonException ex)
        {
            Start.Log($"{Name}'s report could not be read: {ex.Message}");
        }
        finally
        {
            File.Delete(report);
        }
    }

    /// <summary>A failure as Test Explorer shows it: its message (what comes before the first "    at"), then its stack.</summary>
    static (string Message, string? Stack) SplitFailure(string failure)
    {
        var at = StackLine().Match(failure);
        return at.Success ? (failure[..at.Index].TrimEnd(), failure[at.Index..].Trim()) : (failure.Trim(), null);
    }

    [GeneratedRegex(@"^\s+at ", RegexOptions.Multiline)]
    private static partial Regex StackLine();

    /// <summary>
    /// The package's copy under js-debug, as VS Code debugs it: the process that runs the test files is a child session
    /// (one file at a time), stepping goes into the test's code only. Why it could not start, or null.
    /// </summary>
    async Task<string?> DebugAsync(Func<DebugLaunch, Task> debugAsync, IReadOnlyList<string> args)
    {
        if (Entry() is not { } entry)
            return NotInstalled;
        Start.Log($"> {Package} {string.Join(' ', args.Select(TaskVariables.QuoteForShell))}   ({Project}, under the debugger)");
        var configuration = new JsonObject
        {
            ["type"] = "pwa-node",
            ["request"] = "launch",
            ["name"] = $"{Name}: {Project}",
            ["program"] = entry,
            ["args"] = new JsonArray([.. args.Concat(DebugArguments).Select(a => JsonValue.Create(a))]),
            ["cwd"] = PackageDirectory,
            ["autoAttachChildProcesses"] = true,
            ["smartStep"] = true,
            ["skipFiles"] = new JsonArray("<node_internals>/**", "**/node_modules/**"),
            ["env"] = new JsonObject { ["NO_COLOR"] = "1", ["FORCE_COLOR"] = "0", ["CI"] = "1" },
        };
        await debugAsync(new DebugLaunch($"{Name}: {Project}", configuration) { Source = Package });
        return null;
    }

    /// <summary>Runs the package's copy on Node.js in its folder, its output in Output (Tests); why it could not, or null.</summary>
    protected async Task<string?> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (Entry() is not { } entry)
            return NotInstalled;
        if (TaskVariables.Find(OperatingSystem.IsWindows() ? "node.exe" : "node", Start.Path, PackageDirectory) is not { } node)
            return $"Node.js was not found: {Name} runs on it.";
        Start.Log($"> {Package} {string.Join(' ', args.Select(TaskVariables.QuoteForShell))}   ({Project})");
        // No colors in what it prints.
        var environment = new Dictionary<string, string> { ["PATH"] = Start.Path, ["NO_COLOR"] = "1", ["FORCE_COLOR"] = "0", ["CI"] = "1" };
        using var runner = ProcessRunner.Start(node, [entry, .. args], PackageDirectory, Start.Log, environment);
        await runner.WaitAsync(ct);
        return null;
    }

    protected static string RelativeTo(string folder, string path) => Path.GetRelativePath(folder, path).Replace('\\', '/');
}

/// <summary>Vitest's tests: found by <c>vitest list</c>, run by <c>vitest run</c> with its JSON reporter.</summary>
public sealed class VitestFramework(string packageDirectory, TestFrameworkStart start) : JsTestFramework(packageDirectory, start)
{
    public override string Name => "Vitest";

    protected override string Package => "vitest";

    protected override string EntryFile => "vitest.mjs";

    protected override List<string> RunArguments(string report) => ["run", "--reporter=json", $"--outputFile={report}", "--includeTaskLocation"];

    protected override IReadOnlyList<string> DebugArguments => ["--no-file-parallelism"];

    /// <summary>The packages of a folder that test with Vitest, each a framework of its own.</summary>
    public static IEnumerable<TestFramework> In(TestFrameworkStart start) =>
        PackagesUsing(start.Workspace, "vitest").Select(dir => new VitestFramework(dir, start));

    public override async Task<TestDiscovery> DiscoverAsync(CancellationToken ct)
    {
        var list = Path.Combine(Path.GetTempPath(), $"karvon-vitest-{Guid.NewGuid():N}.json");
        try
        {
            if (await RunAsync(["list", $"--json={list}", "--includeTaskLocation"], ct) is { } problem)
                return TestDiscovery.Failed(problem);
            if (!File.Exists(list))
                return TestDiscovery.Failed($"Vitest found no tests in {Project} (see Output, Tests).");
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(list, ct));
            var tests = new List<TestItem>();
            foreach (var test in doc.RootElement.EnumerateArray())
            {
                if (test.TryGetProperty("name", out var name) && test.TryGetProperty("file", out var file) && name.GetString() is { } fullName && file.GetString() is { } path)
                    tests.Add(ItemOf(path, fullName.Split(" > "), test.TryGetProperty("location", out var at) && at.TryGetProperty("line", out var line) ? line.GetInt32() : 0));
            }
            return new TestDiscovery(tests);
        }
        catch (JsonException ex)
        {
            return TestDiscovery.Failed($"Vitest's list could not be read: {ex.Message}");
        }
        finally
        {
            File.Delete(list);
        }
    }
}

/// <summary>
/// Jest's tests: Jest names them only as it runs them, so they show after the first run (Run All Tests), with its JSON
/// report (--json, its test locations in it).
/// </summary>
public sealed class JestFramework(string packageDirectory, TestFrameworkStart start) : JsTestFramework(packageDirectory, start)
{
    public override string Name => "Jest";

    protected override string Package => "jest";

    protected override string EntryFile => Path.Combine("bin", "jest.js");

    protected override List<string> RunArguments(string report) => ["--json", $"--outputFile={report}", "--testLocationInResults"];

    protected override IReadOnlyList<string> DebugArguments => ["--runInBand"];

    /// <summary>The packages of a folder that test with Jest, each a framework of its own.</summary>
    public static IEnumerable<TestFramework> In(TestFrameworkStart start) =>
        PackagesUsing(start.Workspace, "jest").Select(dir => new JestFramework(dir, start));

    /// <summary>Nothing to find before a run: Jest has no list of its tests' names.</summary>
    public override Task<TestDiscovery> DiscoverAsync(CancellationToken ct) =>
        Task.FromResult(Entry() is null ? TestDiscovery.Failed(NotInstalled) : new TestDiscovery([]));
}
