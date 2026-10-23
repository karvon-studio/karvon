using Karvon.Debugging;
using Karvon.Testing;

namespace Karvon.ViewModels;

/// <summary>
/// What Claude's IDE tools (Karvon.Claude.Tools.IdeTools) read and do in Karvon: the build and its errors, the tests, the
/// debugger, the editor. Called on the UI thread.
/// </summary>
public partial class MainViewModel
{
    internal bool HasOpenSolution => Solution is not null;

    internal string? SolutionDirectoryForClaude => Solution?.Directory;

    /// <summary>Builds (rebuilds, cleans) the solution or a project as the Build menu does; false when it failed or could not start.</summary>
    internal Task<bool> BuildForClaudeAsync(string verb, string? project) =>
        RunBuildAsync(verb, verb == "rebuild" ? "Rebuild All" : verb == "clean" ? "Clean" : "Build", project);

    internal bool IsBuildingForClaude => IsBuilding || IsTesting;

    /// <summary>Whether Test Explorer has a framework for the workspace (.NET's, Vitest...), rather than only a test task.</summary>
    internal bool HasTestFrameworksForClaude => TestFrameworks.Any();

    /// <summary>The last build: what, whether it succeeded, how long it took; null before the first.</summary>
    internal (string Verb, string Target, bool Succeeded, TimeSpan Took)? LastBuildForClaude => _lastBuild;

    /// <summary>The last build's errors and warnings (not IntelliSense's).</summary>
    internal IReadOnlyList<ErrorItem> BuildErrorsForClaude => _buildErrors.ToList();

    /// <summary>The project a name stands for (or the path of a solution or project in the solution's folder).</summary>
    internal string? ProjectPathForClaude(string name)
    {
        if (Solution is not { } solution)
            return null;
        if (solution.Projects.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } project)
            return project.FilePath;
        var path = Path.GetFullPath(name, solution.Directory);
        return Services.FilePaths.IsUnder(path, solution.Directory) && File.Exists(path)
            && Path.GetExtension(path) is ".sln" or ".slnx" or ".csproj" or ".fsproj" or ".vbproj" ? path : null;
    }

    /// <summary>
    /// Builds, discovers and runs the tests whose fully qualified or display name contains <paramref name="filter"/> (all with
    /// none; only those that failed last time with <paramref name="failedOnly"/>), as Test Explorer does. False when no test
    /// ran: the summary says why.
    /// </summary>
    /// <summary>A folder's test task, run as Run All Tests runs it: its exit code (-1 when it did not run).</summary>
    internal async Task<int> RunTestTaskForClaudeAsync(Modules.WorkspaceTask task)
    {
        _lastTestTask = null;
        await RunTestTaskAsync(task);
        return _lastTestTask is { } run && run.Task == task.Name ? run.ExitCode : -1;
    }

    internal async Task<bool> RunTestsForClaudeAsync(string? filter, bool failedOnly)
    {
        var runs = _testRuns;
        await RunTestsAsync(null, match: (key, test) =>
            (filter is not { Length: > 0 } f || test.FullName.Contains(f, StringComparison.OrdinalIgnoreCase) || test.Name.Contains(f, StringComparison.OrdinalIgnoreCase))
            && (!failedOnly || _testCases.TryGetValue(key, out var node) && node.State == TestState.Failed));
        return _testRuns != runs;
    }

    internal string TestSummaryForClaude => TestSummary;

    internal bool HasTestRunForClaude => _testRuns > 0;

    /// <summary>The tests of the last run, by fully qualified name (with the data row of a theory), with their results.</summary>
    internal IReadOnlyList<(string Name, TestNode Node)> LastTestRunForClaude() =>
        _lastRun.Where(_testCases.ContainsKey).Select(key => (_discovered.TryGetValue(key, out var d) && d.Item.FullName.Length > 0
                ? d.Item.FullName
                : _testCases[key].Name, _testCases[key]))
            .ToList();

    /// <summary>The exit code of the program Start Without Debugging ran last; null while it runs or before it ran.</summary>
    internal int? LastExitCodeForClaude => _lastExitCode;

    internal DebugSession? DebugSessionForClaude => _session;

    internal int StoppedThreadForClaude => _stoppedThread;

    internal Lsp.LspClient? LanguageServerForClaude(string path) => LanguageServerFor(path);

    /// <summary>Why the debugger last stopped ("breakpoint", "step", "exception", "pause"...) and the exception's text.</summary>
    internal (string Reason, string? Text) LastStopForClaude => (_stopReason, _stopText);
}
