using System.Text.Json.Nodes;
using Karvon.Modules;
using Karvon.Testing;
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

    public override string Id => "dotnet";

    public override string Name => ".NET";

    public override bool CanDebug => true;

    public override bool IsAvailable => workbench.Solution is { } solution && solution.Projects.Any(p => p.IsTestProject);

    public override async Task<TestDiscovery> DiscoverAsync(CancellationToken ct)
    {
        if (workbench.Solution is not { } solution)
            return TestDiscovery.Failed("No solution is open.");
        if (!await workbench.BuildAsync())
            return TestDiscovery.Failed("Build failed; fix the errors to discover tests.");
        var assemblies = new List<string>();
        foreach (var project in solution.Projects.Where(p => p.IsTestProject))
            if (await DotNetProjects.TargetPathAsync(project, workbench.Configuration) is { } dll && File.Exists(dll))
                assemblies.Add(dll);
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
        _host?.Dispose();
        _host = null;
    }
}
