using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Karvon.Lsp;
using Karvon.Testing;

namespace Karvon.ViewModels;

/// <summary>
/// Test Explorer: the tests of the workspace's test frameworks (the modules': .NET's through vstest, Vitest...),
/// found and run by each, their results in a Project/Namespace/Class/Method tree.
/// </summary>
public partial class MainViewModel
{
    /// <summary>The tree's tests and what they are, by key: their framework's id, a colon, their own id.</summary>
    readonly Dictionary<string, TestNode> _testCases = [];
    readonly Dictionary<string, (TestFramework Framework, TestItem Item)> _discovered = [];
    int _testRuns;
    List<string> _lastRun = [];

    /// <summary>The modules' test frameworks of the workspace, read with its tasks.</summary>
    List<TestFramework> _moduleTestFrameworks = [];
    CancellationTokenSource? _testCts;

    public ObservableCollection<TestNode> TestRoots { get; } = [];

    [ObservableProperty]
    public partial TestNode? SelectedTest { get; set; }

    [ObservableProperty]
    public partial string TestSummary { get; set; } = "Build the solution to discover tests.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunAllTestsCommand), nameof(RunFailedTestsCommand), nameof(RunSelectedTestsCommand),
        nameof(RepeatLastTestRunCommand), nameof(RefreshTestsCommand), nameof(CancelTestRunCommand),
        nameof(DebugAllTestsCommand), nameof(DebugSelectedTestsCommand))]
    public partial bool IsTesting { get; set; }

    [ObservableProperty]
    public partial int PassedCount { get; set; }

    [ObservableProperty]
    public partial int FailedCount { get; set; }

    [ObservableProperty]
    public partial int SkippedCount { get; set; }

    static string KeyOf(TestFramework framework, string id) => framework.Id + ":" + id;

    /// <summary>The workspace's test frameworks that have its tests now (.NET's when the solution has a test project).</summary>
    IEnumerable<TestFramework> TestFrameworks => _moduleTestFrameworks.Where(f => f.IsAvailable);

    /// <summary>The modules' test frameworks read again (the folder opened, a package.json saved).</summary>
    void RefreshTestFrameworks()
    {
        foreach (var framework in _moduleTestFrameworks)
            framework.Dispose();
        _moduleTestFrameworks = [];
        if (Solution is not { } solution)
            return;
        var start = new Modules.TestFrameworkStart(solution.Directory, TaskPath(), TestsOutput.Append)
        {
            DebugAsync = DebugToEndAsync,
            StartDebuggingAsync = launch => Dispatcher.UIThread.InvokeAsync(() => StartDebuggingAsync(launch)),
        };
        foreach (var module in Modules)
        {
            try
            {
                _moduleTestFrameworks.AddRange(module.TestFrameworks(start));
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
            {
                TestsOutput.Append($"{module.Name} could not read the tests: {ex.Message}");
            }
        }
        if (_moduleTestFrameworks.Any(f => f.IsAvailable) && TestRoots.Count == 0)
            TestSummary = "Run All Tests to discover the tests.";
    }

    bool CanRunTests => TestFrameworks.Any() && !IsTesting && !IsBuilding && Mode == DebugMode.Design;

    /// <summary>The workspace's tests, or a folder's test task when no framework of its is known.</summary>
    bool CanRunAllTests => CanRunTests || IsFolderWorkspace && TestTask is not null && !IsTesting && !IsBuilding && Mode == DebugMode.Design;

    [RelayCommand(CanExecute = nameof(CanRunAllTests))]
    Task RunAllTests() => !TestFrameworks.Any() && IsFolderWorkspace && TestTask is { } task ? RunTestTaskAsync(task) : RunTestsAsync(null);

    /// <summary>Run Tests on a test project in Solution Explorer: its tests only.</summary>
    [RelayCommand(CanExecute = nameof(CanRunTests))]
    Task RunProjectTests(Workspace.ProjectInfo? project) => project is null ? Task.CompletedTask
        : RunTestsAsync(null, match: (_, item) => item.Container is { } container && Services.FilePaths.IsUnder(container, project.Directory));

    [RelayCommand(CanExecute = nameof(CanRunTests))]
    Task RunFailedTests() => RunTestsAsync(_testCases.Where(kv => kv.Value.State == TestState.Failed).Select(kv => kv.Key).ToList());

    [RelayCommand(CanExecute = nameof(CanRunTests))]
    Task RunSelectedTests() => RunTestsAsync(SelectedTest?.CaseIds().ToList() ?? []);

    [RelayCommand(CanExecute = nameof(CanRunTests))]
    Task RepeatLastTestRun() => RunTestsAsync(_lastRun.Count > 0 ? _lastRun : null);

    /// <summary>The workspace's tests under the debugger, or a folder's test task when its module debugs it (a package.json test script).</summary>
    bool CanDebugAllTests => TestFrameworks.Any(f => f.CanDebug) && CanRunTests
        || IsFolderWorkspace && TestTask?.Debug is not null && !IsTesting && !IsBuilding && Mode == DebugMode.Design;

    [RelayCommand(CanExecute = nameof(CanDebugAllTests))]
    Task DebugAllTests() => !TestFrameworks.Any(f => f.CanDebug) && IsFolderWorkspace && TestTask is { Debug: { } launch }
        ? LaunchConfigurationAsync(launch, noDebug: false)
        : RunTestsAsync(null, debug: true);

    [RelayCommand(CanExecute = nameof(CanRunTests))]
    Task DebugSelectedTests() => RunTestsAsync(SelectedTest?.CaseIds().ToList() ?? [], debug: true);

    [RelayCommand(CanExecute = nameof(CanRunTests))]
    async Task RefreshTests()
    {
        ShowToolWindow(ToolWindows.TestExplorer);
        IsTesting = true;
        try
        {
            await DiscoverTestsAsync();
        }
        finally
        {
            IsTesting = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsTesting))]
    void CancelTestRun() => _testCts?.Cancel();

    /// <summary>
    /// Discovers and runs the given tests (by key; null = all, as far as <paramref name="match"/> takes them), each
    /// framework its own, streaming results into the tree.
    /// </summary>
    async Task RunTestsAsync(IReadOnlyList<string>? keys, bool debug = false, Func<string, TestItem, bool>? match = null)
    {
        ShowToolWindow(ToolWindows.TestExplorer);
        IsTesting = true;
        try
        {
            if (!await DiscoverTestsAsync())
                return;
            var selected = (keys ?? [.. _discovered.Keys]).Where(k => _discovered.TryGetValue(k, out var d) && match?.Invoke(k, d.Item) != false).ToList();
            var everything = keys is null && match is null;
            // All of them: every framework runs, one that names its tests only as it runs them (Jest) too.
            var frameworks = everything ? TestFrameworks.ToList() : selected.Select(k => _discovered[k].Framework).Distinct().ToList();
            if (selected.Count == 0 && !(everything && frameworks.Count > 0))
            {
                TestSummary = "No tests to run.";
                return;
            }
            _lastRun = selected;
            _testRuns++;
            foreach (var key in selected)
                _testCases[key].SetResult(TestState.NotRun, TimeSpan.Zero, null, null, null);
            SetStatus(selected.Count > 0 ? $"Running {selected.Count} test(s)..." : "Running the tests...", StatusKind.Busy);
            var cts = _testCts = new CancellationTokenSource();
            var sw = Stopwatch.StartNew();
            foreach (var framework in frameworks)
            {
                if (debug && !framework.CanDebug)
                    TestsOutput.Append($"{framework.Name} tests cannot be debugged yet: they run without the debugger.");
                void OnResult(TestItemResult r) => Dispatcher.UIThread.Post(() => ApplyResult(framework, r));
                List<TestItem>? mine = everything ? null : [.. selected.Where(k => _discovered[k].Framework == framework).Select(k => _discovered[k].Item)];
                await framework.RunAsync(mine, debug && framework.CanDebug, OnResult, cts.Token);
                if (cts.IsCancellationRequested)
                    break;
            }
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            UpdateTestCounts();
            // With those a framework found as it ran them.
            var ran = _lastRun.Count(k => _testCases.TryGetValue(k, out var n) && n.State is TestState.Passed or TestState.Failed or TestState.Skipped);
            TestSummary = cts.IsCancellationRequested
                ? $"Test run canceled: {ran} of {_lastRun.Count} test(s) ran."
                : $"Test run finished: {ran} test(s) ({PassedCount} Passed, {FailedCount} Failed, {SkippedCount} Skipped) run in {sw.Elapsed.TotalSeconds:0.0} sec";
            SetStatus(FailedCount > 0 ? $"{FailedCount} test(s) failed" : "Tests passed", StatusKind.Ready);
        }
        catch (Exception ex)
        {
            TestSummary = "Test run failed: " + ex.Message;
            SetStatus("Ready", StatusKind.Ready);
        }
        finally
        {
            IsTesting = false;
        }
    }

    /// <summary>A result into the tree; a test its framework found only as it ran (Vitest's skipped ones) joins the tree.</summary>
    void ApplyResult(TestFramework framework, TestItemResult result)
    {
        var key = KeyOf(framework, result.Id);
        if (!_testCases.TryGetValue(key, out var node))
        {
            if (result.Item is not { } item)
                return;
            _discovered[key] = (framework, item);
            node = AddToTree(key, item);
            if (!_lastRun.Contains(key))
                _lastRun.Add(key);
        }
        node.SetResult(result.State, result.Duration, result.ErrorMessage, result.StackTrace, result.Output);
    }

    /// <summary>Each framework's tests (built first when it needs that), keeping earlier results by key.</summary>
    async Task<bool> DiscoverTestsAsync()
    {
        if (Solution is null)
            return false;
        var frameworks = TestFrameworks.ToList();
        if (frameworks.Count == 0)
            return false;
        TestSummary = "Discovering tests...";
        var found = new List<(string Key, TestFramework Framework, TestItem Item)>();
        var problems = new List<string>();
        foreach (var framework in frameworks)
        {
            var discovery = await framework.DiscoverAsync(CancellationToken.None);
            found.AddRange(discovery.Tests.Select(t => (KeyOf(framework, t.Id), framework, t)));
            if (discovery.Problem is { } problem)
                problems.Add(problem);
        }
        if (found.Count == 0 && problems.Count > 0)
        {
            TestSummary = string.Join(" ", problems);
            return false;
        }
        _discovered.Clear();
        foreach (var (key, framework, item) in found)
            _discovered[key] = (framework, item);
        BuildTestTree(found.Select(f => (f.Key, f.Item)).ToList());
        UpdateTestCounts();
        TestSummary = $"{found.Count} test(s) discovered.";
        return true;
    }

    void BuildTestTree(IReadOnlyList<(string Key, TestItem Item)> tests)
    {
        // Same tests as before: keep the tree, so expansion, selection and results survive a run.
        if (_testCases.Count == tests.Count && tests.All(t => _testCases.ContainsKey(t.Key)))
        {
            foreach (var (key, item) in tests)
            {
                _testCases[key].SourcePath = item.File;
                _testCases[key].Line = item.Line;
            }
            return;
        }
        var previous = _testCases.ToDictionary(kv => kv.Key, kv => kv.Value);
        _testCases.Clear();
        TestRoots.Clear();
        foreach (var (key, item) in tests)
        {
            var node = AddToTree(key, item);
            if (previous.TryGetValue(key, out var old))
                node.SetResult(old.State, old.Duration, old.ErrorMessage, old.StackTrace, old.Output);
        }
    }

    /// <summary>A test's row, under its groups' (made as they are first needed).</summary>
    TestNode AddToTree(string key, TestItem item)
    {
        TestNode? parent = null;
        foreach (var group in item.Groups)
        {
            if (parent is null)
            {
                parent = TestRoots.FirstOrDefault(r => r.Kind == group.Kind && r.Name == group.Name);
                if (parent is null)
                    TestRoots.Add(parent = new TestNode(group.Kind, group.Name, null) { IsExpanded = true });
            }
            else
                parent = parent.Child(group.Kind, group.Name);
            // A theory's method goes to where it is written, as its rows do.
            if (group.Kind == TestNodeKind.Method && parent.SourcePath is null)
            {
                parent.SourcePath = item.File;
                parent.Line = item.Line;
            }
        }
        var node = parent is null ? new TestNode(TestNodeKind.Test, item.Name, null) : parent.AddTest(item.Name);
        if (parent is null)
            TestRoots.Add(node);
        node.CaseId = key;
        node.SourcePath = item.File;
        node.Line = item.Line;
        _testCases[key] = node;
        return node;
    }

    void UpdateTestCounts()
    {
        PassedCount = _testCases.Values.Count(n => n.State == TestState.Passed);
        FailedCount = _testCases.Values.Count(n => n.State == TestState.Failed);
        SkippedCount = _testCases.Values.Count(n => n.State == TestState.Skipped);
    }

    public void NavigateToTest(TestNode? node)
    {
        if (node?.SourcePath is { } path && File.Exists(path))
        {
            var pos = new LspPosition(Math.Max(0, node.Line - 1), 0);
            NavigateTo(path, new LspRange(pos, pos));
        }
    }

    void ResetTesting()
    {
        _testCts?.Cancel();
        foreach (var framework in _moduleTestFrameworks)
            framework.Dispose();
        _moduleTestFrameworks = [];
        _testCases.Clear();
        _discovered.Clear();
        _lastRun = [];
        TestRoots.Clear();
        SelectedTest = null;
        PassedCount = FailedCount = SkippedCount = 0;
        TestSummary = "Build the solution to discover tests.";
    }
}

/// <summary>A Test Explorer row; group rows aggregate their children's outcome like VS.</summary>
public partial class TestNode(TestNodeKind kind, string name, TestNode? parent) : ObservableObject
{
    public TestNodeKind Kind { get; } = kind;
    public string Name { get; } = name;
    public TestNode? Parent { get; } = parent;
    public ObservableCollection<TestNode> Children { get; } = [];
    /// <summary>A test's key in Test Explorer (its framework's id, a colon, its own); null for a group.</summary>
    public string? CaseId { get; set; }
    public string? SourcePath { get; set; }
    public int Line { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateIcon))]
    public partial TestState State { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationText))]
    public partial TimeSpan Duration { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial string? StackTrace { get; set; }

    [ObservableProperty]
    public partial string? Output { get; set; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    /// <summary><see cref="Views.VsIcon"/> kind for the outcome.</summary>
    public string StateIcon => State switch
    {
        TestState.Passed => "Ok",
        TestState.Failed => "Error",
        TestState.Skipped => "Warning",
        TestState.Running => "Running",
        _ => "Pending",
    };

    public string DurationText => Duration <= TimeSpan.Zero ? ""
        : Duration.TotalSeconds < 1 ? $"{Math.Max(1, (int)Duration.TotalMilliseconds)} ms"
        : $"{Duration.TotalSeconds:0.0} sec";

    public string Location => SourcePath is null ? "" : $"{Path.GetFileName(SourcePath)} line {Line}";

    public TestNode Child(TestNodeKind kind, string name)
    {
        var existing = Children.FirstOrDefault(c => c.Name == name && c.Kind == kind);
        if (existing is not null)
            return existing;
        var node = new TestNode(kind, name, this) { IsExpanded = kind != TestNodeKind.Method };
        Children.Add(node);
        return node;
    }

    /// <summary>A test's row, made each time (two tests may have the same name).</summary>
    public TestNode AddTest(string name)
    {
        var node = new TestNode(TestNodeKind.Test, name, this);
        Children.Add(node);
        return node;
    }

    public IEnumerable<string> CaseIds() =>
        CaseId is { } id ? [id] : Children.SelectMany(c => c.CaseIds());

    public void SetResult(TestState state, TimeSpan duration, string? error, string? stack, string? output)
    {
        State = state;
        Duration = duration;
        ErrorMessage = error;
        StackTrace = stack;
        Output = output;
        Parent?.Recompute();
    }

    void Recompute()
    {
        var states = Children.Select(c => c.State).ToList();
        State = states.Contains(TestState.Failed) ? TestState.Failed
            : states.Contains(TestState.Running) ? TestState.Running
            : states.Count > 0 && states.All(s => s == TestState.Skipped) ? TestState.Skipped
            : states.Count > 0 && states.All(s => s is TestState.Passed or TestState.Skipped) ? TestState.Passed
            : TestState.NotRun;
        Duration = Children.Aggregate(TimeSpan.Zero, (t, c) => t + c.Duration);
        Parent?.Recompute();
    }
}
