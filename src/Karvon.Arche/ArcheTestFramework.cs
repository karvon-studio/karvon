using System.Text.Json;
using System.Text.RegularExpressions;
using Karvon.Build;
using Karvon.Modules;
using Karvon.Testing;

namespace Karvon.Arche;

/// <summary>
/// A package's test blocks in Test Explorer: read from its .arche files, so nothing runs to find them, and run a file at a time by
/// arche-test, whose --json gives each test's outcome and what it wrote. A file that does not compile fails its tests with what
/// the compiler said. Arche has no debugger for them yet.
/// </summary>
public sealed partial class ArcheTestFramework(TestFrameworkStart start) : TestFramework
{
    readonly List<TestItem> _tests = [];

    public override string Id => "arche";

    public override string Name => "arche test";

    public static bool IsUsedIn(string workspace) => File.Exists(Path.Combine(workspace, ArcheTools.Manifest));

    [GeneratedRegex("""^[ \t]*test[ \t]+"(?<name>(?:[^"\\\r\n]|\\.)*)"[ \t]*\{""", RegexOptions.Multiline)]
    private static partial Regex TestBlock();

    public override async Task<TestDiscovery> DiscoverAsync(CancellationToken ct)
    {
        _tests.Clear();
        var project = Path.GetFileName(Path.TrimEndingDirectorySeparator(start.Workspace));
        foreach (var path in SourcesUnder(start.Workspace).Order(StringComparer.Ordinal))
        {
            var text = await File.ReadAllTextAsync(path, ct);
            var relative = Path.GetRelativePath(start.Workspace, path);
            foreach (Match m in TestBlock().Matches(text))
            {
                var name = m.Groups["name"].Value.Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
                _tests.Add(new TestItem($"{relative}::{name}", [new(TestNodeKind.Project, project), new(TestNodeKind.Namespace, relative)], name, path, text[..m.Index].Count(c => c == '\n') + 1)
                {
                    FullName = $"{relative}: {name}",
                    Container = path,
                });
            }
        }
        return new TestDiscovery([.. _tests]);
    }

    /// <summary>A package's own sources: not what a build, vendor/ or a hidden folder holds.</summary>
    static IEnumerable<string> SourcesUnder(string folder)
    {
        foreach (var file in Directory.EnumerateFiles(folder, "*.arche"))
            yield return file;
        foreach (var dir in Directory.EnumerateDirectories(folder))
            if (Path.GetFileName(dir) is var name && !name.StartsWith('.') && name is not ("build" or "vendor"))
                foreach (var file in SourcesUnder(dir))
                    yield return file;
    }

    public override async Task RunAsync(IReadOnlyList<TestItem>? tests, bool debug, Action<TestItemResult> onResult, CancellationToken ct)
    {
        if (_tests.Count == 0)
            await DiscoverAsync(ct);
        var chosen = tests ?? _tests;
        foreach (var test in chosen)
            onResult(new TestItemResult(test.Id, TestState.Running, TimeSpan.Zero, null, null, null));
        foreach (var file in chosen.GroupBy(t => t.File!))
            await RunFileAsync(file.Key, [.. file], onResult, ct);
    }

    async Task RunFileAsync(string file, IReadOnlyList<TestItem> tests, Action<TestItemResult> onResult, CancellationToken ct)
    {
        var path = ArcheTools.PathWithTools(start.Path);
        if (ArcheTools.Find(ArcheTools.TestRunner, path) is not { } runnerScript)
        {
            start.Log(ArcheTools.NotFound);
            Finish(tests, TestState.Failed, ArcheTools.NotFound, onResult);
            return;
        }
        // Its --filter takes one text, which any part of a name may match: only the tests asked for are said.
        List<string> args = [runnerScript, Path.GetRelativePath(start.Workspace, file), "--json"];
        if (tests.Count == 1)
            args.AddRange(["--filter", tests[0].Name]);
        start.Log($"> sh {string.Join(' ', args.Select(Workspace.TaskVariables.QuoteForShell))}");
        Dictionary<string, string> environment = new() { ["PATH"] = path };
        foreach (var (name, value) in ArcheTools.ToolEnvironment(path) ?? new Dictionary<string, string>())
            environment[name] = value;
        var said = new List<string>();
        var gate = new object();
        using var runner = ProcessRunner.Start("sh", args, start.Workspace, line => { lock (gate) said.Add(line); }, environment);
        try
        {
            await runner.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            Finish(tests, TestState.NotRun, null, onResult);
            return;
        }
        string output;
        lock (gate)
            output = string.Join('\n', said);
        start.Log(output);
        Report(output, tests, onResult);
    }

    /// <summary>
    /// What arche-test said: {"seed": 1, "tests": [{"name", "ok", "stdout", "stderr"}...], "passed", "failed", "filtered"}. Without
    /// it the file did not get to run, and what was said instead is why.
    /// </summary>
    internal static void Report(string output, IReadOnlyList<TestItem> tests, Action<TestItemResult> onResult)
    {
        var at = output.IndexOf("{\"seed\"", StringComparison.Ordinal);
        if (at < 0)
        {
            Finish(tests, TestState.Failed, output.Trim(), onResult);
            return;
        }
        var left = tests.ToDictionary(t => t.Name);
        try
        {
            using var report = JsonDocument.Parse(output[at..]);
            foreach (var outcome in report.RootElement.GetProperty("tests").EnumerateArray())
            {
                if (!left.Remove(outcome.GetProperty("name").GetString() ?? "", out var test))
                    continue;
                var ok = outcome.GetProperty("ok").GetBoolean();
                var wrote = string.Concat(outcome.GetProperty("stderr").GetString(), outcome.GetProperty("stdout").GetString()).TrimEnd();
                onResult(new TestItemResult(test.Id, ok ? TestState.Passed : TestState.Failed, TimeSpan.Zero, ok || wrote.Length == 0 ? null : wrote, null, ok && wrote.Length > 0 ? wrote : null));
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Finish([.. left.Values], TestState.Failed, output.Trim(), onResult);
            return;
        }
        Finish([.. left.Values], TestState.NotRun, null, onResult);
    }

    static void Finish(IReadOnlyList<TestItem> tests, TestState state, string? reason, Action<TestItemResult> onResult)
    {
        foreach (var test in tests)
            onResult(new TestItemResult(test.Id, state, TimeSpan.Zero, reason, null, null));
    }
}
