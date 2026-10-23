using System.Text.Json;
using System.Text.Json.Nodes;
using Karvon.Build;
using Karvon.Modules;
using Karvon.Testing;
using Karvon.Workspace;

namespace Karvon.Python;

/// <summary>
/// pytest's tests in Test Explorer: found and run by the workspace's own pytest (its interpreter's: the .venv's first),
/// with a small plugin of Karvon's that writes what pytest found, where, and each test's outcome. Under the debugger it
/// runs as debugpy's module. It runs only when Test Explorer asks it to: it is the workspace's code.
/// </summary>
public sealed class PytestFramework(TestFrameworkStart start) : TestFramework
{
    /// <summary>Karvon's pytest plugin: what was collected (the file KARVON_PYTEST_COLLECT names), and each report (KARVON_PYTEST_RESULTS).</summary>
    const string Plugin = """
        import json, os

        def pytest_collection_finish(session):
            path = os.environ.get("KARVON_PYTEST_COLLECT")
            if not path:
                return
            items = []
            for item in session.items:
                file = str(getattr(item, "path", None) or item.fspath)
                line = item.location[1]
                items.append({"id": item.nodeid, "file": file, "line": (line or 0) + 1})
            with open(path, "w", encoding="utf-8") as f:
                json.dump(items, f)

        def pytest_runtest_logreport(report):
            path = os.environ.get("KARVON_PYTEST_RESULTS")
            if not path:
                return
            if report.when != "call" and not (report.failed or (report.skipped and report.when == "setup")):
                return
            message = stack = None
            if report.failed:
                crash = getattr(report.longrepr, "reprcrash", None)
                message = crash.message if crash is not None else str(report.longrepr).splitlines()[-1]
                stack = report.longreprtext
            elif report.skipped:
                message = report.longrepr[2] if isinstance(report.longrepr, tuple) else str(report.longrepr)
            outcome = "skipped" if hasattr(report, "wasxfail") and not report.failed else report.outcome
            with open(path, "a", encoding="utf-8") as f:
                f.write(json.dumps({"id": report.nodeid, "file": str(report.fspath), "line": (report.location[1] or 0) + 1,
                    "outcome": outcome, "duration": report.duration, "message": message, "stack": stack,
                    "output": report.capstdout or None}) + "\n")
        """;

    readonly string _project = Path.GetFileName(start.Workspace.TrimEnd(Path.DirectorySeparatorChar));

    public override string Id => "pytest";

    public override string Name => "pytest";

    public override bool CanDebug => start.DebugAsync is not null;

    /// <summary>A workspace that tests with pytest: its configuration, a conftest.py, or test_*.py files at its root or in tests/.</summary>
    public static bool IsUsedIn(string workspace)
    {
        if (File.Exists(Path.Combine(workspace, "pytest.ini")) || File.Exists(Path.Combine(workspace, "conftest.py")))
            return true;
        foreach (var (file, section) in new[] { ("pyproject.toml", "[tool.pytest"), ("setup.cfg", "[tool:pytest]"), ("tox.ini", "[pytest]") })
        {
            var path = Path.Combine(workspace, file);
            try
            {
                if (File.Exists(path) && File.ReadAllText(path).Contains(section, StringComparison.Ordinal))
                    return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return new[] { workspace, Path.Combine(workspace, "tests"), Path.Combine(workspace, "test") }.Any(dir => Directory.Exists(dir)
            && Directory.EnumerateFiles(dir, "*.py").Select(Path.GetFileName).Any(n => n!.StartsWith("test_", StringComparison.Ordinal) || n.EndsWith("_test.py", StringComparison.Ordinal)));
    }

    public override async Task<TestDiscovery> DiscoverAsync(CancellationToken ct)
    {
        var collected = Path.Combine(Path.GetTempPath(), $"karvon-pytest-{Guid.NewGuid():N}.json");
        try
        {
            if (await PytestAsync(["--collect-only", "-q", "-p", "no:cacheprovider"], new() { ["KARVON_PYTEST_COLLECT"] = collected }, ct) is { } problem)
                return TestDiscovery.Failed(problem);
            if (!File.Exists(collected))
                return TestDiscovery.Failed("pytest found no tests (see Output, Tests).");
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(collected, ct));
            return new TestDiscovery([.. doc.RootElement.EnumerateArray().Select(t => ItemOf(t.GetProperty("id").GetString()!, t.GetProperty("file").GetString(), t.GetProperty("line").GetInt32()))]);
        }
        catch (JsonException ex)
        {
            return TestDiscovery.Failed($"pytest's tests could not be read: {ex.Message}");
        }
        finally
        {
            File.Delete(collected);
        }
    }

    /// <summary>
    /// A test by its node id ("tests/test_shapes.py::TestArea::test_zero[0-4]"): under the workspace, its file, its
    /// classes, and a parametrized test's function.
    /// </summary>
    TestItem ItemOf(string nodeId, string? file, int line)
    {
        var parts = nodeId.Split("::");
        var name = parts[^1];
        List<TestGroup> groups = [new(TestNodeKind.Project, _project), new(TestNodeKind.Namespace, parts[0]), .. parts[1..^1].Select(c => new TestGroup(TestNodeKind.Class, c))];
        if (name.IndexOf('[') is var bracket and > 0)
            groups.Add(new TestGroup(TestNodeKind.Method, name[..bracket]));
        var path = file is null ? null : Path.GetFullPath(file, start.Workspace);
        return new TestItem(nodeId, groups, name, path, line) { FullName = nodeId, Container = path };
    }

    public override async Task RunAsync(IReadOnlyList<TestItem>? tests, bool debug, Action<TestItemResult> onResult, CancellationToken ct)
    {
        var results = Path.Combine(Path.GetTempPath(), $"karvon-pytest-{Guid.NewGuid():N}.jsonl");
        List<string> args = tests is null ? [] : [.. tests.Select(t => t.Id)];
        foreach (var test in tests ?? [])
            onResult(new TestItemResult(test.Id, TestState.Running, TimeSpan.Zero, null, null, null));
        var environment = new Dictionary<string, string> { ["KARVON_PYTEST_RESULTS"] = results };
        try
        {
            var problem = debug && start.DebugAsync is { } debugAsync ? await DebugAsync(debugAsync, args, environment) : await PytestAsync(args, environment, ct);
            if (problem is not null)
            {
                start.Log(problem);
                return;
            }
            if (!File.Exists(results))
                return;
            foreach (var line in await File.ReadAllLinesAsync(results, ct))
            {
                if (line.Length == 0)
                    continue;
                var r = JsonNode.Parse(line)!;
                var item = ItemOf(r["id"]!.GetValue<string>(), r["file"]?.GetValue<string>(), r["line"]?.GetValue<int>() ?? 0);
                var state = r["outcome"]!.GetValue<string>() switch
                {
                    "passed" => TestState.Passed,
                    "failed" => TestState.Failed,
                    _ => TestState.Skipped,
                };
                onResult(new TestItemResult(item.Id, state, TimeSpan.FromSeconds(r["duration"]?.GetValue<double>() ?? 0),
                    r["message"]?.GetValue<string>(), r["stack"]?.GetValue<string>(), r["output"]?.GetValue<string>()) { Item = item });
            }
        }
        catch (JsonException ex)
        {
            start.Log($"pytest's results could not be read: {ex.Message}");
        }
        finally
        {
            File.Delete(results);
        }
    }

    /// <summary>The plugin's folder, for PYTHONPATH: written the first time, in Karvon's temp folder.</summary>
    static string PluginDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "karvon-pytest-plugin");
        var file = Path.Combine(dir, "karvon_pytest.py");
        if (!File.Exists(file) || File.ReadAllText(file) != Plugin)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(file, Plugin);
        }
        return dir;
    }

    (string Python, Dictionary<string, string> Environment)? Interpreter(Dictionary<string, string> environment)
    {
        var path = PythonEnvironment.PathFor(start.Workspace, start.Path);
        if (PythonEnvironment.Interpreter(start.Workspace, path) is not { } python)
            return null;
        environment["PATH"] = path;
        environment["PYTHONPATH"] = Environment.GetEnvironmentVariable("PYTHONPATH") is { Length: > 0 } own ? PluginDirectory() + System.IO.Path.PathSeparator + own : PluginDirectory();
        environment["PYTHONUNBUFFERED"] = "1";
        return (python, environment);
    }

    /// <summary>The workspace's pytest with Karvon's plugin, its output in Output (Tests); why it could not run, or null.</summary>
    async Task<string?> PytestAsync(IReadOnlyList<string> args, Dictionary<string, string> environment, CancellationToken ct)
    {
        if (Interpreter(environment) is not var (python, env))
            return "Python was not found: install it, or make the workspace a virtual environment (python3 -m venv .venv).";
        List<string> all = ["-m", "pytest", "-p", "karvon_pytest", .. args];
        start.Log($"> {Path.GetFileName(python)} {string.Join(' ', all.Select(TaskVariables.QuoteForShell))}");
        var output = new List<string>();
        using var runner = ProcessRunner.Start(python, all, start.Workspace, line =>
        {
            output.Add(line);
            start.Log(line);
        }, env);
        var exit = await runner.WaitAsync(ct);
        // pytest's exit codes: 0 passed, 1 failed, 5 none collected; 2-4 it could not run.
        if (exit is 2 or 3 or 4 && output.Any(l => l.Contains("No module named pytest", StringComparison.Ordinal)))
            return $"pytest is not installed for {python}: pip install pytest.";
        return exit is 2 or 3 or 4 ? $"pytest could not run (exit code {exit}): see Output (Tests)." : null;
    }

    /// <summary>pytest as debugpy's module, as VS Code debugs a test; why it could not start, or null.</summary>
    async Task<string?> DebugAsync(Func<DebugLaunch, Task<string>> debugAsync, IReadOnlyList<string> args, Dictionary<string, string> environment)
    {
        if (Interpreter(environment) is not var (python, env))
            return "Python was not found: install it, or make the workspace a virtual environment (python3 -m venv .venv).";
        start.Log($"> pytest {string.Join(' ', args.Select(TaskVariables.QuoteForShell))}   (under the debugger)");
        var configuration = new JsonObject
        {
            ["type"] = "debugpy",
            ["request"] = "launch",
            ["name"] = $"pytest: {_project}",
            ["module"] = "pytest",
            ["args"] = new JsonArray([.. new[] { "-p", "karvon_pytest" }.Concat(args).Select(a => JsonValue.Create(a))]),
            ["python"] = python,
            ["cwd"] = start.Workspace,
            ["env"] = new JsonObject([.. env.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value)))]),
        };
        await debugAsync(new DebugLaunch($"pytest: {_project}", configuration) { Source = "pytest" });
        return null;
    }
}
