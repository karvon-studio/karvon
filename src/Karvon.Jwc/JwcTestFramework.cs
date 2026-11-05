using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Karvon.Build;
using Karvon.Modules;
using Karvon.Testing;

namespace Karvon.Jwc;

/// <summary>
/// jwc test's tests in Test Explorer: the test blocks of the project's .jwc files (read from them, so nothing runs to find
/// them), run by jwc test, each in a transaction it rolls back. It needs the project's database (DATABASE_URL, or its .env);
/// without one the tests fail with what jwc says. jwc has no debugger for them.
/// </summary>
public sealed partial class JwcTestFramework(TestFrameworkStart start) : TestFramework
{
    readonly List<TestItem> _tests = [];

    public override string Id => "jwc";

    public override string Name => "jwc test";

    public static bool IsUsedIn(string workspace) => File.Exists(Path.Combine(workspace, JwcTools.Manifest));

    [GeneratedRegex("""^[ \t]*test[ \t]+"(?<name>(?:[^"\\\r\n]|\\.)*)"[ \t]*\{""", RegexOptions.Multiline)]
    private static partial Regex TestBlock();

    [GeneratedRegex(@"\x1b\[[0-9;]*m")]
    private static partial Regex Color();

    [GeneratedRegex("^(?:ok {4}|FAILED )(?<name>.*)$")]
    private static partial Regex Outcome();

    public override async Task<TestDiscovery> DiscoverAsync(CancellationToken ct)
    {
        _tests.Clear();
        var project = JwcTools.ReadManifest(start.Workspace).Name;
        var seen = new Dictionary<string, int>();
        foreach (var path in SourcesUnder(start.Workspace).Order(StringComparer.Ordinal))
        {
            var text = await File.ReadAllTextAsync(path, ct);
            var relative = Path.GetRelativePath(start.Workspace, path);
            foreach (Match m in TestBlock().Matches(text))
            {
                var name = m.Groups["name"].Value.Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
                // jwc lets two tests have one name: the outcomes are told apart by their order.
                var key = $"{relative}::{name}";
                seen[key] = seen.GetValueOrDefault(key) + 1;
                _tests.Add(new TestItem(seen[key] == 1 ? key : $"{key}#{seen[key]}", [new(TestNodeKind.Project, project), new(TestNodeKind.Namespace, relative)], name,
                    path, text[..m.Index].Count(c => c == '\n') + 1)
                {
                    FullName = $"{relative}: {name}",
                    Container = path,
                });
            }
        }
        return new TestDiscovery([.. _tests]);
    }

    /// <summary>The files jwc loads: all its .jwc under the folder but for hidden folders, target and node_modules.</summary>
    static IEnumerable<string> SourcesUnder(string folder)
    {
        foreach (var file in Directory.EnumerateFiles(folder, "*.jwc"))
            if (!Path.GetFileName(file).StartsWith('.'))
                yield return file;
        foreach (var dir in Directory.EnumerateDirectories(folder))
            if (Path.GetFileName(dir) is var name && !name.StartsWith('.') && name is not ("target" or "node_modules"))
                foreach (var file in SourcesUnder(dir))
                    yield return file;
    }

    public override async Task RunAsync(IReadOnlyList<TestItem>? tests, bool debug, Action<TestItemResult> onResult, CancellationToken ct)
    {
        if (_tests.Count == 0)
            await DiscoverAsync(ct);
        foreach (var test in tests ?? _tests)
            onResult(new TestItemResult(test.Id, TestState.Running, TimeSpan.Zero, null, null, null));
        var only = tests?.Select(t => t.Id).ToHashSet();
        // Its --filter takes one text: one run for all of them, one run a name for some.
        IEnumerable<string?> filters = only is null || only.Count == _tests.Count ? new string?[] { null } : tests!.Select(t => t.Name).Distinct();
        foreach (var filter in filters)
            await RunWithAsync(filter, only, onResult, ct);
    }

    /// <summary>
    /// One jwc test: an outcome line for each test ("ok    name", "FAILED name" and what failed, indented below it) as it
    /// ends. A test it did not get to is reported too: failed, with what jwc said, when it could not run (no database, an
    /// error in the code), and not run otherwise.
    /// </summary>
    async Task RunWithAsync(string? filter, HashSet<string>? only, Action<TestItemResult> onResult, CancellationToken ct)
    {
        var waiting = _tests.Where(t => filter is null || t.Name.Contains(filter, StringComparison.Ordinal)).GroupBy(t => t.Name)
            .ToDictionary(g => g.Key, g => new Queue<TestItem>(g));
        var said = new List<string>();
        var clock = Stopwatch.StartNew();
        string? failing = null;
        var failure = new StringBuilder();

        void Report(string name, string? message)
        {
            if (waiting.TryGetValue(name, out var same) && same.TryDequeue(out var test) && only?.Contains(test.Id) != false)
                onResult(new TestItemResult(test.Id, message is null ? TestState.Passed : TestState.Failed, clock.Elapsed, message, null, null));
            clock.Restart();
        }

        void Read(string raw)
        {
            var line = Color().Replace(raw, "");
            start.Log(line);
            if (failing is not null)
            {
                if (line.StartsWith("        ", StringComparison.Ordinal))
                {
                    failure.AppendLine(line.Trim());
                    return;
                }
                Report(failing, failure.ToString().TrimEnd());
                failing = null;
                failure.Clear();
            }
            if (Outcome().Match(line) is { Success: true } m)
            {
                if (line.StartsWith("ok", StringComparison.Ordinal))
                    Report(m.Groups["name"].Value, null);
                else
                    failing = m.Groups["name"].Value;
            }
            else if (line.Length > 0)
                said.Add(line);
        }

        var path = JwcTools.PathWithJwc(start.Path);
        var state = TestState.Failed;
        string? reason = JwcTools.NotFound;
        if (JwcTools.Find(path) is { } jwc)
        {
            List<string> args = filter is null ? ["test"] : ["test", "--filter", filter];
            start.Log($"> jwc {string.Join(' ', args.Select(Workspace.TaskVariables.QuoteForShell))}");
            var gate = new object();
            using var runner = ProcessRunner.Start(jwc, args, start.Workspace, line => { lock (gate) Read(line); }, new Dictionary<string, string> { ["PATH"] = path });
            try
            {
                var exit = await runner.WaitAsync(ct);
                lock (gate)
                {
                    if (failing is not null)
                        Report(failing, failure.ToString().TrimEnd());
                    (state, reason) = exit == 0 ? (TestState.NotRun, null) : (TestState.Failed, string.Join('\n', said));
                }
            }
            catch (OperationCanceledException)
            {
                (state, reason) = (TestState.NotRun, null);
            }
        }
        else
            start.Log(JwcTools.NotFound);
        foreach (var test in waiting.Values.SelectMany(q => q).Where(t => only?.Contains(t.Id) != false))
            onResult(new TestItemResult(test.Id, state, TimeSpan.Zero, reason, null, null));
    }
}
