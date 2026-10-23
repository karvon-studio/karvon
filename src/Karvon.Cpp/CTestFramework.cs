using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Karvon.Build;
using Karvon.Modules;
using Karvon.Testing;
using Karvon.Workspace;

namespace Karvon.Cpp;

/// <summary>
/// CTest's tests in Test Explorer: a CMake folder that enables testing, configured and built into build/ as its tasks
/// build it, its tests listed by ctest (each where its add_test is) and run by ctest with its JUnit report; a test
/// debugs as its command under CodeLLDB. It runs only when Test Explorer asks it to: it is the folder's code.
/// </summary>
public sealed partial class CTestFramework(TestFrameworkStart start) : TestFramework
{
    readonly string _project = ProjectName(start.Workspace);

    /// <summary>Each test's command, by its name, as ctest runs it.</summary>
    readonly Dictionary<string, List<string>> _commands = [];

    public override string Id => "ctest";

    public override string Name => "CTest";

    public override bool CanDebug => start.DebugAsync is not null;

    public static bool IsUsedIn(string workspace)
    {
        var file = Path.Combine(workspace, "CMakeLists.txt");
        try
        {
            return File.Exists(file) && EnablesTesting().IsMatch(File.ReadAllText(file));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"^\s*(?:enable_testing|add_test|include\s*\(\s*CTest)\b", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex EnablesTesting();

    [GeneratedRegex(@"^\s*project\s*\(\s*([A-Za-z_][\w.-]*)", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex ProjectLine();

    static string ProjectName(string workspace)
    {
        try
        {
            return ProjectLine().Match(File.ReadAllText(Path.Combine(workspace, "CMakeLists.txt"))) is { Success: true } m ? m.Groups[1].Value : Path.GetFileName(workspace);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Path.GetFileName(workspace);
        }
    }

    public override async Task<TestDiscovery> DiscoverAsync(CancellationToken ct)
    {
        if (await ShellAsync(CppTasks.ConfigureAndBuild, _ => { }, ct) is not 0)
            return TestDiscovery.Failed("The build failed: fix its errors to find the tests (see Output, Tests).");
        var json = new List<string>();
        if (await RunAsync("ctest", ["--test-dir", "build", "--show-only=json-v1"], json.Add, ct, log: false) is not 0)
            return TestDiscovery.Failed("ctest could not list the tests (see Output, Tests).");
        JsonNode? doc;
        try
        {
            doc = JsonNode.Parse(string.Join('\n', json));
        }
        catch (JsonException ex)
        {
            return TestDiscovery.Failed($"ctest's list could not be read: {ex.Message}");
        }
        var files = doc?["backtraceGraph"]?["files"]?.AsArray().Select(f => f!.GetValue<string>()).ToList() ?? [];
        var nodes = doc?["backtraceGraph"]?["nodes"]?.AsArray() ?? [];
        var tests = new List<TestItem>();
        _commands.Clear();
        foreach (var test in doc?["tests"]?.AsArray() ?? [])
        {
            var name = test!["name"]!.GetValue<string>();
            if (test["command"] is JsonArray command)
                _commands[name] = [.. command.Select(c => c!.GetValue<string>())];
            // Where its add_test is.
            string? file = null;
            var line = 0;
            if (test["backtrace"]?.GetValue<int>() is { } index && index < nodes.Count && nodes[index] is { } node)
            {
                file = node["file"]?.GetValue<int>() is { } f && f < files.Count ? files[f] : null;
                line = node["line"]?.GetValue<int>() ?? 0;
            }
            tests.Add(ItemOf(name, file, line));
        }
        return new TestDiscovery(tests);
    }

    TestItem ItemOf(string name, string? file, int line) =>
        new(name, [new(TestNodeKind.Project, _project)], name, file, line) { FullName = name, Container = file };

    public override async Task RunAsync(IReadOnlyList<TestItem>? tests, bool debug, Action<TestItemResult> onResult, CancellationToken ct)
    {
        foreach (var test in tests ?? [])
            onResult(new TestItemResult(test.Id, TestState.Running, TimeSpan.Zero, null, null, null));
        if (debug && start.DebugAsync is { } debugAsync)
        {
            // Each test as its command, under CodeLLDB: passed when it exits with 0, as ctest has it.
            foreach (var test in tests ?? [])
            {
                if (!_commands.TryGetValue(test.Id, out var command) || command.Count == 0)
                    continue;
                var output = await debugAsync(new DebugLaunch($"ctest: {test.Name}", new JsonObject
                {
                    ["type"] = "lldb",
                    ["request"] = "launch",
                    ["name"] = $"ctest: {test.Name}",
                    ["program"] = command[0],
                    ["args"] = new JsonArray([.. command.Skip(1).Select(a => JsonValue.Create(a))]),
                    ["cwd"] = Path.Combine(start.Workspace, "build"),
                })
                { Source = "ctest" });
                var exit = ExitCode().Match(output);
                if (exit.Success)
                    onResult(new TestItemResult(test.Id, exit.Groups[1].Value == "0" ? TestState.Passed : TestState.Failed, TimeSpan.Zero, null, null, null));
            }
            return;
        }
        if (await ShellAsync(CppTasks.ConfigureAndBuild, _ => { }, ct) is not 0)
        {
            start.Log("The build failed: the tests did not run.");
            return;
        }
        var report = Path.Combine(Path.GetTempPath(), $"karvon-ctest-{Guid.NewGuid():N}.xml");
        List<string> args = ["--test-dir", "build", "--output-junit", report];
        if (tests is not null)
            args.AddRange(["-R", "^(" + string.Join('|', tests.Select(t => Regex.Escape(t.Id))) + ")$"]);
        try
        {
            await RunAsync("ctest", args, _ => { }, ct);
            if (!File.Exists(report))
                return;
            foreach (var testcase in XDocument.Load(report).Descendants("testcase"))
            {
                var name = (string?)testcase.Attribute("name") ?? "";
                var failure = testcase.Element("failure");
                var output = testcase.Element("system-out")?.Value is { Length: > 0 } text ? text.TrimEnd() : null;
                var state = (string?)testcase.Attribute("status") switch
                {
                    "run" when failure is null => TestState.Passed,
                    "fail" => TestState.Failed,
                    _ when failure is not null => TestState.Failed,
                    _ => TestState.Skipped,
                };
                var seconds = double.TryParse((string?)testcase.Attribute("time"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 0;
                onResult(new TestItemResult(name, state, TimeSpan.FromSeconds(seconds),
                    state == TestState.Failed ? output?.Split('\n')[^1] ?? (string?)failure?.Attribute("message") : null, null, output));
            }
        }
        finally
        {
            File.Delete(report);
        }
    }

    [GeneratedRegex(@"has exited with code (-?\d+)")]
    private static partial Regex ExitCode();

    Task<int> ShellAsync(string command, Action<string> onLine, CancellationToken ct)
    {
        var (program, arguments) = TaskVariables.Shell(command);
        return RunAsync(program, arguments, onLine, ct);
    }

    /// <summary>A program in the folder, its lines to the log (and <paramref name="onLine"/>); its exit code, -1 when it is not there.</summary>
    async Task<int> RunAsync(string program, IReadOnlyList<string> args, Action<string> onLine, CancellationToken ct, bool log = true)
    {
        if (TaskVariables.Find(program, start.Path, start.Workspace) is not { } exe)
        {
            start.Log($"{program} was not found.");
            return -1;
        }
        start.Log($"> {Path.GetFileName(exe)} {string.Join(' ', args.Select(TaskVariables.QuoteForShell))}");
        using var runner = ProcessRunner.Start(exe, args, start.Workspace, line =>
        {
            if (log)
                start.Log(line);
            onLine(line);
        }, new Dictionary<string, string> { ["PATH"] = start.Path });
        return await runner.WaitAsync(ct);
    }
}
