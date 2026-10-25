using System.Text.Json.Nodes;
using Karvon.Modules;
using Karvon.Testing;
using Karvon.Workspace;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;

namespace Karvon.DotNet.Testing;

/// <summary>
/// .NET's tests: the solution built, its test projects' assemblies discovered and run through the SDK's
/// vstest.console (xUnit, NUnit, MSTest), debugged by attaching to the test host vstest starts.
/// </summary>
sealed class DotNetTests(IWorkbench workbench, TestFrameworkStart start) : TestFramework
{
    VsTestHost? _host;
    readonly Dictionary<Guid, TestCase> _cases = [];
    IReadOnlyList<ProjectInfo> _testProjects = [];
    /// <summary>Where the last coverage run's reports are (deleted with the next run, or when the framework goes).</summary>
    string? _coverageResults;

    public override string Id => "dotnet";

    public override string Name => ".NET";

    public override bool CanDebug => true;

    public override bool IsAvailable => workbench.Solution is { } solution && ProjectsOf(solution).Any(p => p.IsTestProject);

    /// <summary>A solution's projects; a folder's, those in it (it builds them as dotnet does, Debug).</summary>
    static IReadOnlyList<ProjectInfo> ProjectsOf(SolutionInfo solution) => solution.IsFolder ? DotNetFolder.Find(solution.Directory).Projects : solution.Projects;

    public override async Task<TestDiscovery> DiscoverAsync(CancellationToken ct)
    {
        if (workbench.Solution is not { } solution)
            return TestDiscovery.Failed("No solution is open.");
        if (!await workbench.BuildAsync())
            return TestDiscovery.Failed("Build failed; fix the errors to discover tests.");
        var assemblies = new List<string>();
        var built = new List<ProjectInfo>();
        foreach (var project in ProjectsOf(solution).Where(p => p.IsTestProject))
            if (await DotNetProjects.TargetPathAsync(project, solution.IsFolder ? "Debug" : workbench.Configuration) is { } dll && File.Exists(dll))
            {
                assemblies.Add(dll);
                built.Add(project);
            }
        _testProjects = built;
        if (assemblies.Count == 0)
            return TestDiscovery.Failed("No test assemblies were built.");
        if (_host is null)
        {
            var console = await VsTestHost.FindConsoleAsync(solution.Directory);
            if (console is null)
                return TestDiscovery.Failed("vstest.console was not found in the .NET SDK.");
            _host = VsTestHost.Start(console, Path.Combine(Path.GetTempPath(), "Karvon", "vstest-logs"));
            _host.Log += start.Log;
        }
        var cases = await _host.DiscoverAsync(assemblies, ct);
        _cases.Clear();
        foreach (var c in cases)
            _cases[c.Id] = c;
        return new TestDiscovery(ItemsOf(cases));
    }

    /// <summary>The cases as Test Explorer shows them: by project, namespace, class; a theory's rows under its method, named as VS names them.</summary>
    static List<TestItem> ItemsOf(IReadOnlyList<TestCase> cases)
    {
        var items = new List<TestItem>();
        foreach (var byProject in cases.GroupBy(c => Path.GetFileNameWithoutExtension(c.Source)).OrderBy(g => g.Key))
            foreach (var byClass in byProject.GroupBy(c => Before(c.FullyQualifiedName)).OrderBy(g => g.Key))
            {
                var ns = Before(byClass.Key);
                TestGroup[] groups = [new(TestNodeKind.Project, byProject.Key), new(TestNodeKind.Namespace, ns.Length == 0 ? "<global>" : ns), new(TestNodeKind.Class, After(byClass.Key))];
                foreach (var byMethod in byClass.GroupBy(c => c.FullyQualifiedName).OrderBy(g => g.Key))
                {
                    var list = byMethod.ToList();
                    var methodName = After(byMethod.Key);
                    if (list.Count == 1)
                    {
                        items.Add(ItemOf(list[0], groups, methodName));
                        continue;
                    }
                    // Theory / parameterized test: one row per data row, under its method.
                    TestGroup[] method = [.. groups, new(TestNodeKind.Method, methodName)];
                    foreach (var c in list.OrderBy(c => c.DisplayName, StringComparer.Ordinal))
                        items.Add(ItemOf(c, method, c.DisplayName.StartsWith(byMethod.Key, StringComparison.Ordinal) ? methodName + c.DisplayName[byMethod.Key.Length..] : c.DisplayName));
                }
            }
        return items;

        static TestItem ItemOf(TestCase c, IReadOnlyList<TestGroup> groups, string name) => new(c.Id.ToString(), groups, name, c.CodeFilePath, c.LineNumber)
        {
            // With the data row of a theory.
            FullName = c.DisplayName.StartsWith(c.FullyQualifiedName, StringComparison.Ordinal) ? c.DisplayName : c.FullyQualifiedName,
            Container = c.Source,
        };
    }

    static string Before(string name) => name.LastIndexOf('.') is var i and >= 0 ? name[..i] : "";

    static string After(string name) => name.LastIndexOf('.') is var i and >= 0 ? name[(i + 1)..] : name;

    public override async Task RunAsync(IReadOnlyList<TestItem>? tests, bool debug, Action<TestItemResult> onResult, CancellationToken ct)
    {
        if (_host is not { } host)
            return;
        var cases = tests is null ? [.. _cases.Values] : tests.Select(t => _cases.GetValueOrDefault(Guid.Parse(t.Id))).OfType<TestCase>().ToList();
        void OnResult(TestResultInfo r) => onResult(new TestItemResult(r.Id.ToString(), r.State, r.Duration, r.ErrorMessage, r.StackTrace, r.Output));
        if (debug && start.StartDebuggingAsync is { } startDebugging)
        {
            // vstest starts the test host and asks us to attach before any test runs, so breakpoints hit.
            var launcher = new DebuggingTestHostLauncher(pid => startDebugging(Attach(pid)), start.Log);
            await host.DebugAsync(cases, launcher, OnResult, ct);
        }
        else
            await host.RunAsync(cases, OnResult, ct);
    }

    public override bool CanCollectCoverage => true;

    /// <summary>The tests run with coverlet's collector (the test project's coverlet.collector package): a Cobertura report each.</summary>
    public override async Task<(IReadOnlyList<string> Reports, string? Problem)> RunCoverageAsync(IReadOnlyList<TestItem>? tests, Action<TestItemResult> onResult,
        CancellationToken ct)
    {
        if (_host is not { } host)
            return ([], "The tests are not discovered yet.");
        const string NoCollector = "No coverage was collected: add the coverlet.collector package to the test project (dotnet add package coverlet.collector).";
        var collectors = _testProjects.Select(CoverletCollectorOf).OfType<string>().Distinct().ToList();
        if (collectors.Count == 0)
            return ([], NoCollector);
        DeleteCoverageResults();
        var results = _coverageResults = Path.Combine(Path.GetTempPath(), "Karvon", "coverage-" + Guid.NewGuid().ToString("N"));
        var cases = tests is null ? [.. _cases.Values] : tests.Select(t => _cases.GetValueOrDefault(Guid.Parse(t.Id))).OfType<TestCase>().ToList();
        var files = await host.RunCoverageAsync(cases, collectors, results, r => onResult(new TestItemResult(r.Id.ToString(), r.State, r.Duration, r.ErrorMessage, r.StackTrace, r.Output)), ct);
        var reports = files.Where(f => f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".info", StringComparison.OrdinalIgnoreCase)).ToList();
        return (reports, reports.Count > 0 ? null : NoCollector);
    }

    /// <summary>
    /// Where a test project's coverlet.collector package keeps the collector (its build/netstandard2.0 folder): from the
    /// restore's project.assets.json, its package folders and the version it chose.
    /// </summary>
    internal static string? CoverletCollectorOf(ProjectInfo project)
    {
        var assets = Path.Combine(project.Directory, "obj", "project.assets.json");
        try
        {
            if (!File.Exists(assets) || JsonNode.Parse(File.ReadAllText(assets)) is not JsonObject root)
                return null;
            var library = (root["libraries"] as JsonObject)?.FirstOrDefault(l => l.Key.StartsWith("coverlet.collector/", StringComparison.OrdinalIgnoreCase));
            if (library?.Value?["path"]?.GetValue<string>() is not { } path)
                return null;
            foreach (var (folder, _) in root["packageFolders"] as JsonObject ?? [])
            {
                var collector = Path.Combine(folder, path, "build", "netstandard2.0");
                if (File.Exists(Path.Combine(collector, "coverlet.collector.dll")))
                    return collector;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        {
        }
        return null;
    }

    void DeleteCoverageResults()
    {
        if (_coverageResults is { } results && Directory.Exists(results))
            try
            {
                Directory.Delete(results, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        _coverageResults = null;
    }

    /// <summary>netcoredbg's attach to vstest's test host, as a launch.json would say it.</summary>
    static DebugLaunch Attach(int processId) => new("testhost", new JsonObject
    {
        ["name"] = "testhost",
        ["type"] = "coreclr",
        ["request"] = "attach",
        ["processId"] = processId,
        ["justMyCode"] = true,
        ["enableStepFiltering"] = true,
    });

    public override void Dispose()
    {
        DeleteCoverageResults();
        _host?.Dispose();
        _host = null;
    }
}
