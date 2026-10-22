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
        return path.StartsWith(solution.Directory + Path.DirectorySeparatorChar, StringComparison.Ordinal) && File.Exists(path)
            && Path.GetExtension(path) is ".sln" or ".slnx" or ".csproj" or ".fsproj" or ".vbproj" ? path : null;
    }

    /// <summary>
    /// Builds, discovers and runs the tests whose fully qualified or display name contains <paramref name="filter"/> (all with
    /// none; only those that failed last time with <paramref name="failedOnly"/>), as Test Explorer does. False when no test
    /// ran: the summary says why.
    /// </summary>
    internal async Task<bool> RunTestsForClaudeAsync(string? filter, bool failedOnly)
    {
        var runs = _testRuns;
        await RunTestsAsync(null, match: c =>
            (filter is not { Length: > 0 } f || c.FullyQualifiedName.Contains(f, StringComparison.OrdinalIgnoreCase) || c.DisplayName.Contains(f, StringComparison.OrdinalIgnoreCase))
            && (!failedOnly || _testCases.TryGetValue(c.Id, out var node) && node.State == TestState.Failed));
        return _testRuns != runs;
    }

    internal string TestSummaryForClaude => TestSummary;

    internal bool HasTestRunForClaude => _testRuns > 0;

    /// <summary>The tests of the last run, by fully qualified name (with the data row of a theory), with their results.</summary>
    internal IReadOnlyList<(string Name, TestNode Node)> LastTestRunForClaude() =>
        _lastRun.Where(_testCases.ContainsKey).Select(id => (_discovered.TryGetValue(id, out var c)
                ? c.DisplayName.StartsWith(c.FullyQualifiedName, StringComparison.Ordinal) ? c.DisplayName : c.FullyQualifiedName
                : _testCases[id].Name, _testCases[id]))
            .ToList();

    /// <summary>The exit code of the program Start Without Debugging ran last; null while it runs or before it ran.</summary>
    internal int? LastExitCodeForClaude => _lastExitCode;

    internal DebugSession? DebugSessionForClaude => _session;

    internal int StoppedThreadForClaude => _stoppedThread;

    internal Lsp.LspClient? LanguageServerForClaude => _lsp;

    /// <summary>Why the debugger last stopped ("breakpoint", "step", "exception", "pause"...) and the exception's text.</summary>
    internal (string Reason, string? Text) LastStopForClaude => (_stopReason, _stopText);
}
