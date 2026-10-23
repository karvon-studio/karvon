using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Karvon.Build;
using Karvon.Modules;
using Karvon.Testing;
using Karvon.Workspace;

namespace Karvon.Rust;

/// <summary>
/// cargo test's tests in Test Explorer: cargo builds the test binaries (cargo test --no-run), and each binary lists its
/// own tests (--list) and runs them with libtest's own lines ("test tests::zero ... FAILED", then each failure's
/// output), run as cargo test runs it, or debugged by CodeLLDB. Doc tests are rustdoc's, and not among them.
/// Under its package, each test is under its target (src/lib.rs, tests/outside.rs) and its modules. It runs only when
/// Test Explorer asks it to: it is the package's code.
/// </summary>
public sealed partial class CargoTestFramework(TestFrameworkStart start) : TestFramework
{
    readonly string _package = PackageName(start.Workspace);

    public override string Id => "cargo";

    public override string Name => "cargo test";

    public override bool CanDebug => start.DebugAsync is not null;

    public static bool IsUsedIn(string workspace) => File.Exists(Path.Combine(workspace, "Cargo.toml"));

    static string PackageName(string workspace)
    {
        try
        {
            var name = File.ReadLines(Path.Combine(workspace, "Cargo.toml")).Select(l => NameLine().Match(l)).FirstOrDefault(m => m.Success);
            return name?.Groups[1].Value ?? Path.GetFileName(workspace);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Path.GetFileName(workspace);
        }
    }

    [GeneratedRegex(@"^\s*name\s*=\s*""([^""]+)""")]
    private static partial Regex NameLine();

    [GeneratedRegex(@"^(?<name>\S+): test$")]
    private static partial Regex ListedTest();

    [GeneratedRegex(@"^test (?<name>\S+) \.\.\. (?<result>ok|FAILED|ignored)")]
    private static partial Regex ResultLine();

    [GeneratedRegex(@"^---- (?<name>\S+) stdout ----$")]
    private static partial Regex FailureStart();

    [GeneratedRegex(@"panicked at (?<where>[^\s]+):$")]
    private static partial Regex PanickedAt();

    public override async Task<TestDiscovery> DiscoverAsync(CancellationToken ct)
    {
        var binaries = await TestBinariesAsync(ct);
        if (binaries is null)
            return TestDiscovery.Failed("cargo was not found: install Rust with rustup (rustup.rs).");
        if (binaries.Count == 0)
            return TestDiscovery.Failed("cargo built no test binary (see Output, Tests).");
        // Each binary lists its own tests: what it writes is its target's alone.
        var tests = new List<TestItem>();
        foreach (var (target, exe) in binaries)
            await RunBinaryAsync(exe, ["--list", "--format=terse"], line =>
            {
                if (ListedTest().Match(line) is { Success: true } listed)
                    tests.Add(ItemOf(target, listed.Groups["name"].Value));
            }, ct);
        return new TestDiscovery(tests);
    }

    /// <summary>A test by its target and its path in it (tests::zero): under the package, the target, its modules.</summary>
    TestItem ItemOf(string target, string name)
    {
        var parts = name.Split("::");
        var (file, line) = Locate(target, parts[^1]);
        return new TestItem($"{target}::{name}", [new(TestNodeKind.Project, _package), new(TestNodeKind.Namespace, target), .. parts[..^1].Select(m => new TestGroup(TestNodeKind.Class, m))],
            parts[^1], file, line)
        {
            FullName = name,
            Container = Path.Combine(start.Workspace, target),
        };
    }

    /// <summary>
    /// Where a test function is: its target's file first, then the package's other .rs files (a module of its own), the
    /// fn of that name with #[test] just above it.
    /// </summary>
    (string? File, int Line) Locate(string target, string function)
    {
        var first = Path.Combine(start.Workspace, target);
        IEnumerable<string> files = [first, .. Directory.Exists(Path.Combine(start.Workspace, "src")) ? Directory.EnumerateFiles(Path.Combine(start.Workspace, "src"), "*.rs", SearchOption.AllDirectories) : []];
        var fn = new Regex($@"^\s*(?:pub\s+)?(?:async\s+)?fn\s+{Regex.Escape(function)}\s*\(");
        foreach (var file in files.Distinct().Where(File.Exists))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
                if (fn.IsMatch(lines[i]) && lines.Skip(Math.Max(0, i - 4)).Take(Math.Min(i, 4)).Any(l => l.Contains("#[test]") || l.Contains("#[tokio::test")))
                    return (file, i + 1);
        }
        return (File.Exists(first) ? first : null, 0);
    }

    public override async Task RunAsync(IReadOnlyList<TestItem>? tests, bool debug, Action<TestItemResult> onResult, CancellationToken ct)
    {
        foreach (var test in tests ?? [])
            onResult(new TestItemResult(test.Id, TestState.Running, TimeSpan.Zero, null, null, null));
        var only = tests?.Select(t => t.Id).ToHashSet();
        var results = new List<(string Target, string Name, string Result)>();
        var failures = new Dictionary<string, List<string>>();
        foreach (var (target, exe) in await TestBinariesAsync(ct) ?? [])
        {
            // Its tests among those to run (all of them: none named), by their names in it.
            var mine = tests?.Where(t => t.Id.StartsWith(target + "::", StringComparison.Ordinal)).Select(t => t.FullName).Distinct().ToList();
            if (mine is { Count: 0 })
                continue;
            List<string> args = mine is null ? [] : [.. mine, "--exact"];
            if (debug && start.DebugAsync is { } debugAsync)
            {
                var output = await debugAsync(new DebugLaunch($"cargo test: {target}", new JsonObject
                {
                    ["type"] = "lldb",
                    ["request"] = "launch",
                    ["name"] = $"cargo test: {target}",
                    ["program"] = exe,
                    ["args"] = new JsonArray([.. args.Concat(["--nocapture", "--test-threads=1"]).Select(a => JsonValue.Create(a))]),
                    ["cwd"] = start.Workspace,
                    ["env"] = new JsonObject([.. Environment(target).Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value)))]),
                })
                { Source = "cargo" });
                Read(output.Split('\n'), target, results, failures);
            }
            else
            {
                var lines = new List<string>();
                await RunBinaryAsync(exe, args, lines.Add, ct, target);
                Read(lines, target, results, failures);
            }
            if (ct.IsCancellationRequested)
                break;
        }
        foreach (var (target, name, result) in results)
        {
            var item = ItemOf(target, name);
            if (only?.Contains(item.Id) == false)
                continue;
            var failure = failures.GetValueOrDefault($"{target}::{name}");
            string? message = null;
            if (failure is not null)
            {
                // What it panicked with: the lines after "panicked at file:line:col:", to the first blank one.
                var at = failure.FindIndex(l => PanickedAt().IsMatch(l));
                message = at < 0 ? failure.FirstOrDefault(l => l.Length > 0) : string.Join('\n', failure.Skip(at + 1).TakeWhile(l => l.Length > 0 && !l.StartsWith("note:", StringComparison.Ordinal)));
            }
            onResult(new TestItemResult(item.Id, result switch { "ok" => TestState.Passed, "FAILED" => TestState.Failed, _ => TestState.Skipped },
                TimeSpan.Zero, message, failure is null ? null : string.Join('\n', failure).Trim(), null) { Item = item });
        }
    }

    /// <summary>A test binary's lines: its results, then its failures' output.</summary>
    static void Read(IEnumerable<string> lines, string target, List<(string, string, string)> results, Dictionary<string, List<string>> failures)
    {
        List<string>? failure = null;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (ResultLine().Match(line) is { Success: true } result)
                results.Add((target, result.Groups["name"].Value, result.Groups["result"].Value));
            else if (FailureStart().Match(line) is { Success: true } start)
                failures[$"{target}::{start.Groups["name"].Value}"] = failure = [];
            else if (line is "failures:" || line.StartsWith("test result:", StringComparison.Ordinal))
                failure = null;
            else
                failure?.Add(line);
        }
    }

    /// <summary>The test binaries cargo builds (cargo test --no-run), by their target's file; null with no cargo.</summary>
    async Task<List<(string Target, string Exe)>?> TestBinariesAsync(CancellationToken ct)
    {
        var binaries = new List<(string, string)>();
        var problem = await CargoAsync(["test", "--no-run", "--message-format=json"], line =>
        {
            if (!line.StartsWith('{'))
                return;
            try
            {
                var message = JsonNode.Parse(line)!;
                if (message["reason"]?.GetValue<string>() == "compiler-message" && message["message"]?["rendered"]?.GetValue<string>() is { } rendered)
                    start.Log(rendered.TrimEnd());
                if (message["reason"]?.GetValue<string>() == "compiler-artifact" && message["profile"]?["test"]?.GetValue<bool>() == true
                    && message["executable"]?.GetValue<string>() is { } exe && message["target"]?["src_path"]?.GetValue<string>() is { } source)
                    binaries.Add((Path.GetRelativePath(start.Workspace, source).Replace('\\', '/'), exe));
            }
            catch (JsonException)
            {
            }
        }, ct, logJson: false);
        // By their targets (src/ before tests/), not by when each was built.
        return problem is null ? [.. binaries.OrderBy(b => b.Item1, StringComparer.Ordinal)] : null;
    }

    /// <summary>What cargo test sets for a test binary as it runs it (some tests read their package's folder).</summary>
    Dictionary<string, string> Environment(string target) => new()
    {
        ["CARGO_MANIFEST_DIR"] = start.Workspace,
        ["CARGO_PKG_NAME"] = _package,
        ["RUST_BACKTRACE"] = "0",
        ["PATH"] = RustTools.PathWithCargo(start.Path),
    };

    /// <summary>A test binary in the package's folder, as cargo test runs it; each line it writes to <paramref name="onLine"/>.</summary>
    async Task RunBinaryAsync(string exe, IReadOnlyList<string> args, Action<string> onLine, CancellationToken ct, string? target = null)
    {
        if (target is not null)
            start.Log($"> {target}: {Path.GetFileName(exe)} {string.Join(' ', args.Select(TaskVariables.QuoteForShell))}");
        using var runner = ProcessRunner.Start(exe, args, start.Workspace, line =>
        {
            if (target is not null)
                start.Log(line);
            onLine(line);
        }, Environment(target ?? ""));
        await runner.WaitAsync(ct);
    }

    /// <summary>cargo in the package's folder, each line it writes to <paramref name="onLine"/> and the log; why it could not run, or null.</summary>
    async Task<string?> CargoAsync(IReadOnlyList<string> args, Action<string> onLine, CancellationToken ct, bool logJson = true)
    {
        var path = RustTools.PathWithCargo(start.Path);
        if (TaskVariables.Find(OperatingSystem.IsWindows() ? "cargo.exe" : "cargo", path, start.Workspace) is not { } cargo)
            return "cargo was not found: install Rust with rustup (rustup.rs).";
        start.Log($"> cargo {string.Join(' ', args.Select(TaskVariables.QuoteForShell))}");
        using var runner = ProcessRunner.Start(cargo, args, start.Workspace, line =>
        {
            if (logJson || !line.StartsWith('{'))
                start.Log(line);
            onLine(line);
        }, new Dictionary<string, string> { ["PATH"] = path });
        await runner.WaitAsync(ct);
        return null;
    }
}
