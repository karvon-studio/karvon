using System.Text.Json;
using Karvon.Modules;
using Karvon.Testing;

namespace Karvon.TypeScript;

/// <summary>
/// Playwright Test's tests (@playwright/test): found by <c>playwright test --list</c>, run by <c>playwright test</c> (a test
/// by its file and line), both with its JSON reporter; a test runs in each project of its configuration (chromium,
/// firefox, a phone), each project's run a test of its own when there are several.
/// </summary>
public sealed class PlaywrightFramework(string packageDirectory, TestFrameworkStart start) : JsTestFramework(packageDirectory, start)
{
    public override string Name => "Playwright";

    protected override string Package => "@playwright/test";

    protected override string EntryFile => "cli.js";

    protected override List<string> RunArguments(string report) => ["test", "--reporter=json"];

    protected override IReadOnlyList<string> DebugArguments => ["--workers=1", "--timeout=0"];

    /// <summary>The packages of a folder that test with Playwright, each a framework of its own.</summary>
    public static IEnumerable<TestFramework> In(TestFrameworkStart start) =>
        PackagesUsing(start.Workspace, "@playwright/test").Select(dir => new PlaywrightFramework(dir, start));

    /// <summary>Where its JSON reporter writes (its own variable for it).</summary>
    static Dictionary<string, string> ReportTo(string report) => new() { ["PLAYWRIGHT_JSON_OUTPUT_NAME"] = report };

    public override async Task<TestDiscovery> DiscoverAsync(CancellationToken ct)
    {
        var report = Path.Combine(Path.GetTempPath(), $"karvon-playwright-{Guid.NewGuid():N}.json");
        try
        {
            if (await RunAsync(["test", "--list", "--reporter=json"], ct, ReportTo(report)) is { } problem)
                return TestDiscovery.Failed(problem);
            if (!File.Exists(report))
                return TestDiscovery.Failed($"Playwright found no tests in {Project} (see Output, Tests).");
            return new TestDiscovery([.. Read(await File.ReadAllTextAsync(report, ct)).Select(r => r.Item)]);
        }
        catch (JsonException ex)
        {
            return TestDiscovery.Failed($"Playwright's list could not be read: {ex.Message}");
        }
        finally
        {
            File.Delete(report);
        }
    }

    public override async Task RunAsync(IReadOnlyList<TestItem>? tests, bool debug, Action<TestItemResult> onResult, CancellationToken ct)
    {
        var report = Path.Combine(Path.GetTempPath(), $"karvon-playwright-{Guid.NewGuid():N}.json");
        var args = RunArguments(report);
        var only = tests?.Select(t => t.Id).ToHashSet();
        // A test by its file and line (all its projects run it).
        if (tests is not null)
            args.AddRange(tests.Where(t => t.File is not null).Select(t => $"{RelativeTo(PackageDirectory, t.File!)}:{t.Line}").Distinct());
        foreach (var test in tests ?? [])
            onResult(new TestItemResult(test.Id, TestState.Running, TimeSpan.Zero, null, null, null));
        try
        {
            if ((debug && Start.DebugAsync is { } debugAsync ? await DebugAsync(debugAsync, args, ReportTo(report)) : await RunAsync(args, ct, ReportTo(report))) is { } problem)
            {
                Start.Log(problem);
                return;
            }
            if (!File.Exists(report))
            {
                Start.Log("Playwright wrote no report (see above).");
                return;
            }
            foreach (var (item, state, duration, message, stack) in Read(await File.ReadAllTextAsync(report, ct)))
                if (only is null || only.Contains(item.Id))
                    onResult(new TestItemResult(item.Id, state, duration, message, stack, null) { Item = item });
        }
        catch (JsonException ex)
        {
            Start.Log($"Playwright's report could not be read: {ex.Message}");
        }
        finally
        {
            File.Delete(report);
        }
    }

    /// <summary>
    /// Its JSON report (or list): each test of each project, by its file (from the configuration's root) and its titles
    /// (its describe blocks', then its own, its project's name after it when there are several), how it went.
    /// </summary>
    internal IReadOnlyList<(TestItem Item, TestState State, TimeSpan Duration, string? Message, string? Stack)> Read(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var rootDir = root.TryGetProperty("config", out var config) && config.TryGetProperty("rootDir", out var r) && r.GetString() is { Length: > 0 } dir ? dir : PackageDirectory;
        var projects = config.ValueKind == JsonValueKind.Object && config.TryGetProperty("projects", out var p) && p.ValueKind == JsonValueKind.Array ? p.GetArrayLength() : 1;
        var found = new List<(TestItem, TestState, TimeSpan, string?, string?)>();
        void Suite(JsonElement suite, IReadOnlyList<string> titles)
        {
            foreach (var spec in Items(suite, "specs"))
            {
                var file = Path.GetFullPath(spec.GetProperty("file").GetString() ?? "", rootDir);
                var line = spec.TryGetProperty("line", out var l) ? l.GetInt32() : 0;
                foreach (var test in Items(spec, "tests"))
                {
                    var title = spec.GetProperty("title").GetString() ?? "";
                    if (projects > 1 && test.TryGetProperty("projectName", out var project) && project.GetString() is { Length: > 0 } name)
                        title += $" [{name}]";
                    var item = ItemOf(file, [.. titles, title], line);
                    var results = Items(test, "results").ToList();
                    var state = (test.TryGetProperty("status", out var s) ? s.GetString() : null) switch
                    {
                        "expected" or "flaky" => TestState.Passed,
                        "unexpected" => TestState.Failed,
                        "skipped" => TestState.Skipped,
                        _ => results.Count == 0 ? TestState.NotRun : TestState.Skipped,
                    };
                    var duration = TimeSpan.FromMilliseconds(results.Sum(x => x.TryGetProperty("duration", out var d) ? d.GetDouble() : 0));
                    var error = results.LastOrDefault(x => x.TryGetProperty("error", out _)) is { ValueKind: JsonValueKind.Object } failed ? failed.GetProperty("error") : default;
                    var message = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var m) ? m.GetString() : null;
                    var stack = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("stack", out var st) ? st.GetString() : null;
                    found.Add((item, state, duration, message, stack is not null && message is not null && stack.StartsWith(message, StringComparison.Ordinal) ? stack[message.Length..].Trim() : stack));
                }
            }
            foreach (var child in Items(suite, "suites"))
                Suite(child, [.. titles, child.GetProperty("title").GetString() ?? ""]);
        }
        // The top suites are the files: their titles are not a test's.
        foreach (var fileSuite in Items(root, "suites"))
            Suite(fileSuite, []);
        return found;
    }

    static IEnumerable<JsonElement> Items(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var items) && items.ValueKind == JsonValueKind.Array ? items.EnumerateArray() : [];
}
