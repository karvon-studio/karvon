using System.Text.Json;
using System.Text.RegularExpressions;
using Karvon.Build;
using Karvon.Modules;
using Karvon.Testing;
using Karvon.Workspace;

namespace Karvon.TypeScript;

/// <summary>
/// Vitest's tests in Test Explorer: those of a package whose package.json depends on vitest, found by <c>vitest list</c>
/// and run by <c>vitest run</c> with its JSON reporter, by the package's own vitest (installed in its node_modules) on
/// Node.js. It runs only when Test Explorer asks it to: it is the package's code.
/// </summary>
public sealed partial class VitestFramework(string packageDirectory, TestFrameworkStart start) : TestFramework
{
    readonly string _project = Path.GetFileName(packageDirectory.TrimEnd(Path.DirectorySeparatorChar));

    public override string Id => "vitest:" + RelativeTo(start.Workspace, packageDirectory);

    public override string Name => "Vitest";

    /// <summary>The packages of a folder that test with Vitest, each a framework of its own.</summary>
    public static IReadOnlyList<TestFramework> In(TestFrameworkStart start) =>
        [.. PackageScripts.PackageFiles(start.Workspace).Where(UsesVitest).Select(file => new VitestFramework(Path.GetDirectoryName(file)!, start))];

    static bool UsesVitest(string packageFile)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(packageFile), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && new[] { "devDependencies", "dependencies" }.Any(section => doc.RootElement.TryGetProperty(section, out var deps)
                    && deps.ValueKind == JsonValueKind.Object && deps.TryGetProperty("vitest", out _));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The package's vitest: in its node_modules, or a workspace's above it up to the folder (hoisted).</summary>
    string? Entry()
    {
        for (var dir = packageDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            var entry = Path.Combine(dir, "node_modules", "vitest", "vitest.mjs");
            if (File.Exists(entry))
                return entry;
            if (string.Equals(dir.TrimEnd(Path.DirectorySeparatorChar), start.Workspace.TrimEnd(Path.DirectorySeparatorChar), Services.FilePaths.Comparison))
                break;
        }
        return null;
    }

    public override async Task<TestDiscovery> DiscoverAsync(CancellationToken ct)
    {
        var list = Path.Combine(Path.GetTempPath(), $"karvon-vitest-{Guid.NewGuid():N}.json");
        try
        {
            if (await VitestAsync(["list", $"--json={list}", "--includeTaskLocation"], ct) is { } problem)
                return TestDiscovery.Failed(problem);
            if (!File.Exists(list))
                return TestDiscovery.Failed($"Vitest found no tests in {_project} (see Output, Tests).");
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

    /// <summary>A test by its file and titles (its describe blocks', then its own): under its package, its file, its blocks.</summary>
    TestItem ItemOf(string file, IReadOnlyList<string> titles, int line)
    {
        var fullName = string.Join(" > ", titles);
        TestGroup[] groups = [new(TestNodeKind.Project, _project), new(TestNodeKind.Namespace, RelativeTo(packageDirectory, file)), .. titles.SkipLast(1).Select(t => new TestGroup(TestNodeKind.Class, t))];
        return new TestItem($"{RelativeTo(packageDirectory, file)}::{fullName}", groups, titles[^1], file, line) { FullName = fullName, Container = file };
    }

    public override async Task RunAsync(IReadOnlyList<TestItem>? tests, bool debug, Action<TestItemResult> onResult, CancellationToken ct)
    {
        var report = Path.Combine(Path.GetTempPath(), $"karvon-vitest-{Guid.NewGuid():N}.json");
        List<string> args = ["run", "--reporter=json", $"--outputFile={report}", "--includeTaskLocation"];
        var only = tests?.Select(t => t.Id).ToHashSet();
        if (tests is not null)
        {
            // Their files, and their names (vitest's -t takes a test's names joined by spaces).
            args.AddRange(tests.Select(t => t.File).OfType<string>().Distinct());
            args.AddRange(["-t", "^(?:" + string.Join('|', tests.Select(t => Regex.Escape(t.FullName.Replace(" > ", " ")))) + ")$"]);
        }
        foreach (var test in tests ?? [])
            onResult(new TestItemResult(test.Id, TestState.Running, TimeSpan.Zero, null, null, null));
        try
        {
            if (await VitestAsync(args, ct) is { } problem)
            {
                start.Log(problem);
                return;
            }
            if (!File.Exists(report))
            {
                start.Log("Vitest wrote no report (see above).");
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
            start.Log($"Vitest's report could not be read: {ex.Message}");
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

    /// <summary>Runs the package's vitest on Node.js in its folder, its output in Output (Tests); why it could not, or null.</summary>
    async Task<string?> VitestAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (Entry() is not { } entry)
            return $"Vitest is not installed in {_project}: install its packages (npm install).";
        if (TaskVariables.Find(OperatingSystem.IsWindows() ? "node.exe" : "node", start.Path, packageDirectory) is not { } node)
            return "Node.js was not found: Vitest runs on it.";
        start.Log($"> vitest {string.Join(' ', args.Select(TaskVariables.QuoteForShell))}   ({_project})");
        // No colors in what it prints, and its own reporters' output only.
        var environment = new Dictionary<string, string> { ["PATH"] = start.Path, ["NO_COLOR"] = "1", ["FORCE_COLOR"] = "0", ["CI"] = "1" };
        using var runner = ProcessRunner.Start(node, [entry, .. args], packageDirectory, start.Log, environment);
        await runner.WaitAsync(ct);
        return null;
    }

    static string RelativeTo(string folder, string path) => Path.GetRelativePath(folder, path).Replace('\\', '/');
}
