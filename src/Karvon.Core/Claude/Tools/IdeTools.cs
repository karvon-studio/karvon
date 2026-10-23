using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using Karvon.Debugging;
using Karvon.Lsp;
using Karvon.Testing;
using Karvon.ViewModels;
using static Karvon.Claude.Tools.ToolInput;

namespace Karvon.Claude.Tools;

/// <summary>The core's tools for Claude Code: the build, the tests, the debugger, the app, the editor.</summary>
public static class IdeClaudeTools
{
    public static IEnumerable<IClaudeTool> Create(MainViewModel vm) =>
    [
        new RunBuildTool(vm),
        new BuildErrorsTool(vm),
        new RunTestsTool(vm),
        new TestFailuresTool(vm),
        new DebugStateTool(vm),
        new DebugLocalsTool(vm),
        new DebugEvaluateTool(vm),
        new DebugControlTool(vm),
        new AppControlTool(vm),
        new EditorSelectionTool(vm),
        new OpenDocumentTool(vm),
        new CodeDiagnosticsTool(vm),
        new FindInFilesTool(vm),
    ];
}

/// <summary>A match of <c>find_in_files</c>: Karvon lists them in Find Results.</summary>
public sealed record FoundText(string Path, int Line, int Column, string Text);

/// <summary>
/// Karvon itself for Claude: the build and its errors, the tests, the debugger where it stopped, the editor. Each runs on the
/// UI thread, as the Build menu, Test Explorer and the debugger do, so what Claude does shows in Karvon as the user's own.
/// </summary>
abstract class IdeTool(MainViewModel vm) : IClaudeTool
{
    protected MainViewModel Vm { get; } = vm;

    public abstract string Name { get; }

    public abstract string Description { get; }

    public virtual JsonObject InputSchema => Schema("""{"type":"object","properties":{}}""");

    public abstract ClaudeToolPlan Plan(JsonElement input);

    public async Task<ClaudeToolOutput> CallAsync(JsonElement input, CancellationToken cancellationToken)
    {
        try
        {
            return await Dispatcher.UIThread.InvokeAsync(() => RunAsync(input, cancellationToken));
        }
        catch (Exception ex) when (ex is InvalidOperationException or DapException or IOException or UnauthorizedAccessException)
        {
            return ClaudeToolOutput.Error(ex.Message);
        }
    }

    /// <summary>On the UI thread.</summary>
    protected abstract Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken);

    protected string SolutionDirectory => Vm.SolutionDirectoryForClaude ?? throw new InvalidOperationException("No solution is open in Karvon.");

    /// <summary>A path as Claude gives and reads it: relative to the solution's folder, with forward slashes.</summary>
    protected string Relative(string? path) => path is null ? "" : RelativeOf(Vm, path);

    /// <summary>A path as Claude gives and reads it: relative to the solution's folder, with forward slashes (Windows' too).</summary>
    internal static string RelativeOf(MainViewModel vm, string path) =>
        vm.SolutionDirectoryForClaude is { } dir && Services.FilePaths.IsUnder(path, dir) ? Path.GetRelativePath(dir, path).Replace('\\', '/') : path;

    /// <summary>A file of the solution's folder, from a path relative to it (or absolute inside it).</summary>
    protected string FileOf(string path)
    {
        var dir = SolutionDirectory;
        var full = Path.GetFullPath(path, dir);
        if (!Services.FilePaths.IsUnder(full, dir))
            throw new InvalidOperationException($"{path} is not in the solution's folder.");
        return full;
    }
}

// ---- .NET ----------------------------------------------------------------------------------------------------------

/// <summary>The build's errors as the compiler prints them, relative to the solution: path(line,column): error CODE: message.</summary>
static class BuildReport
{
    public static string Describe(MainViewModel vm, bool warnings, int limit = 50)
    {
        if (vm.LastBuildForClaude is not { } build)
            return "No build has run since Karvon opened the solution; call run_build.";
        var problems = vm.BuildErrorsForClaude
            .Where(e => e.Severity == DiagnosticSeverity.Error || warnings && e.Severity == DiagnosticSeverity.Warning)
            .OrderBy(e => e.Severity).ToList();
        var errors = vm.BuildErrorsForClaude.Count(e => e.Severity == DiagnosticSeverity.Error);
        var warningCount = vm.BuildErrorsForClaude.Count(e => e.Severity == DiagnosticSeverity.Warning);
        var text = new StringBuilder(build.Succeeded
            ? $"{build.Verb} of {build.Target} succeeded{(warningCount > 0 ? $" with {warningCount} warning(s)" : "")} in {build.Took.TotalSeconds:0.0} s."
            : $"{build.Verb} of {build.Target} failed: {errors} error(s), {warningCount} warning(s).");
        foreach (var e in problems.Take(limit))
        {
            var where = e.FilePath is { } file
                ? $"{IdeTool.RelativeOf(vm, file)}({e.Line},{e.Column})"
                : e.Project;
            text.Append('\n').Append($"{where}: {(e.Severity == DiagnosticSeverity.Error ? "error" : "warning")} {e.Code}: {e.Description}");
        }
        if (problems.Count > limit)
            text.Append($"\n… and {problems.Count - limit} more.");
        return text.ToString();
    }
}

/// <summary><c>run_build</c> (runs): Build > Build Solution, or a project, and its errors.</summary>
sealed class RunBuildTool(MainViewModel vm) : IdeTool(vm)
{
    public override string Name => "run_build";

    public override string Description =>
        "Builds the open solution (or a project) in Karvon with dotnet build, as Build > Build Solution does, and returns the result: "
        + "whether it succeeded, then each error as path(line,column): error CODE: message. The Output window and the Error List show it too. "
        + "A folder opened as it is builds by its build task (.vscode/tasks.json's build group, or package.json's build script).";

    public override JsonObject InputSchema => Schema("""
        {"type":"object","properties":{
          "project":{"type":"string","description":"A project's name, or a solution or project path relative to the solution's folder; default the solution."},
          "verb":{"type":"string","enum":["build","rebuild","clean"],"description":"Default build."}}}
        """);

    static string Verb(JsonElement input) => Str(input, "verb") is "rebuild" or "clean" ? Str(input, "verb")! : "build";

    public override ClaudeToolPlan Plan(JsonElement input) =>
        new(ToolAccess.Run, Verb(input) switch { "rebuild" => "Rebuild", "clean" => "Clean", _ => "Build" }, Str(input, "project") ?? "the solution", "");

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        _ = SolutionDirectory;
        if (Vm.IsBuildingForClaude)
            return ClaudeToolOutput.Error("Karvon is building or running tests already; wait for it, then call run_build again.");
        if (Vm.IsFolderWorkspace)
        {
            if (Vm.BuildTask is null)
                return ClaudeToolOutput.Error("The open folder has no build task: give .vscode/tasks.json one (group build), or package.json a build script.");
            if (Str(input, "project") is not null || Verb(input) == "clean")
                return ClaudeToolOutput.Error($"A folder builds by its build task ({Vm.BuildTask.Name}) only: call run_build without project and verb.");
        }
        string? target = null;
        if (Str(input, "project") is { } project && (target = Vm.ProjectPathForClaude(project)) is null)
            return ClaudeToolOutput.Error($"{project} is not a project of the solution, nor a solution or project file in its folder.");
        var ok = await Vm.BuildForClaudeAsync(Verb(input), target);
        return new ClaudeToolOutput(BuildReport.Describe(Vm, warnings: false), !ok);
    }
}

/// <summary><c>build_errors</c>: the last build's errors (the user's or Claude's).</summary>
sealed class BuildErrorsTool(MainViewModel vm) : IdeTool(vm)
{
    public override string Name => "build_errors";

    public override string Description => "The errors of the last build in Karvon (the user's or yours), as path(line,column): error CODE: message.";

    public override JsonObject InputSchema => Schema("""{"type":"object","properties":{"warnings":{"type":"boolean","description":"Include the warnings."}}}""");

    public override ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Read, "Build Errors", null, "");

    protected override Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken) =>
        Task.FromResult(new ClaudeToolOutput(BuildReport.Describe(Vm, Bool(input, "warnings"))));
}

/// <summary>The tests of the last run: the counts, then each failure with its message, stack and file:line.</summary>
static class TestReport
{
    public static string Describe(MainViewModel vm, TimeSpan? took = null)
    {
        var run = vm.LastTestRunForClaude();
        var passed = run.Count(t => t.Node.State == TestState.Passed);
        var failed = run.Where(t => t.Node.State == TestState.Failed).ToList();
        var skipped = run.Count(t => t.Node.State == TestState.Skipped);
        var text = new StringBuilder($"{run.Count} test(s): {passed} passed, {failed.Count} failed, {skipped} skipped{(took is { } t ? $", in {t.TotalSeconds:0.0} s" : "")}.");
        foreach (var (name, node) in failed.Take(20))
        {
            text.Append("\n\n").Append(name).Append(": failed");
            if (node.SourcePath is { } source)
                text.Append($" ({IdeTool.RelativeOf(vm, source)}:{node.Line})");
            if (node.ErrorMessage is { Length: > 0 } message)
                text.Append('\n').Append(message.Trim());
            if (node.StackTrace is { Length: > 0 } stack)
                foreach (var line in stack.Split('\n').Select(l => l.Trim())
                             .Where(l => l.Length > 0 && !l.StartsWith("at System.", StringComparison.Ordinal) && !l.StartsWith("at Microsoft.", StringComparison.Ordinal)
                                 && !l.StartsWith("at Xunit.", StringComparison.Ordinal) && !l.StartsWith("at NUnit.", StringComparison.Ordinal)).Take(8))
                    text.Append('\n').Append(line);
        }
        if (failed.Count > 20)
            text.Append($"\n\n… and {failed.Count - 20} more.");
        return text.ToString();
    }
}

/// <summary><c>run_tests</c> (runs): Test Explorer's Run, for the tests a filter names.</summary>
sealed class RunTestsTool(MainViewModel vm) : IdeTool(vm)
{
    public override string Name => "run_tests";

    public override string Description =>
        "Builds the open solution and runs its tests (xUnit, NUnit, MSTest; Vitest in a package that depends on it) in Karvon's Test "
        + "Explorer; returns the counts, then each failure with its message, stack trace and file:line. A folder opened as it is with no "
        + "test framework Karvon knows runs its test task (.vscode/tasks.json's test group, or package.json's test script) and returns "
        + "whether it passed and its output; filter and failedOnly do not apply there.";

    public override JsonObject InputSchema => Schema("""
        {"type":"object","properties":{
          "filter":{"type":"string","description":"Run only the tests whose fully qualified or display name contains this text."},
          "failedOnly":{"type":"boolean","description":"Run only the tests that failed last time."}}}
        """);

    public override ClaudeToolPlan Plan(JsonElement input) =>
        new(ToolAccess.Run, "Run Tests", Str(input, "filter") ?? (Bool(input, "failedOnly") ? "the failed ones" : "all"), "");

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        _ = SolutionDirectory;
        if (Vm.IsBuildingForClaude)
            return ClaudeToolOutput.Error("Karvon is building or running tests already; wait for it, then call run_tests again.");
        if (Vm.IsFolderWorkspace && !Vm.HasTestFrameworksForClaude)
        {
            if (Vm.TestTask is not { } task)
                return ClaudeToolOutput.Error("The open folder has no test task: give .vscode/tasks.json one (group test), or package.json a test script.");
            var exit = await Vm.RunTestTaskForClaudeAsync(task);
            var output = Vm.TestsOutput.Document.Text.TrimEnd().Split('\n');
            var text = new StringBuilder(exit == 0 ? $"{task.Name} passed." : $"{task.Name} failed (exit code {exit}).");
            text.Append($"\n\nOutput (last {Math.Min(80, output.Length)} of {output.Length} lines):");
            foreach (var line in output.TakeLast(80))
                text.Append('\n').Append(Redaction.Text(line.TrimEnd('\r')));
            return new ClaudeToolOutput(text.ToString(), exit != 0);
        }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        if (!await Vm.RunTestsForClaudeAsync(Str(input, "filter"), Bool(input, "failedOnly")))
        {
            var why = Vm.TestSummaryForClaude;
            if (why.StartsWith("Build failed", StringComparison.Ordinal))
                why = "The build failed; fix its errors to run the tests.\n" + BuildReport.Describe(Vm, warnings: false);
            else if (why == "No tests to run.")
                why = Str(input, "filter") is { } filter ? $"No test's name contains {filter}." : "There are no tests to run.";
            return new ClaudeToolOutput(why);
        }
        var report = TestReport.Describe(Vm, clock.Elapsed);
        return new ClaudeToolOutput(report);
    }
}

/// <summary><c>test_failures</c>: the last run's failures (the user's or Claude's).</summary>
sealed class TestFailuresTool(MainViewModel vm) : IdeTool(vm)
{
    public override string Name => "test_failures";

    public override string Description => "The tests that failed in the last test run in Karvon (the user's or yours), with their messages, stack traces and file:line.";

    public override ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Read, "Test Failures", null, "");

    protected override Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken) =>
        Task.FromResult(new ClaudeToolOutput(Vm.HasTestRunForClaude ? TestReport.Describe(Vm) : "No tests have run since Karvon opened the solution; call run_tests."));
}

// ---- Debugger -----------------------------------------------------------------------------------------------------

/// <summary>Where the debugged program is: not debugged, running, or stopped (why, where, the call stack).</summary>
static class DebugReport
{
    public static string Describe(MainViewModel vm, bool stack = true)
    {
        if (vm.DebugSessionForClaude is null || vm.Mode == DebugMode.Design)
            return "No debug session is running. The user starts one with F5, or call debug_control with start.";
        if (vm.Mode != DebugMode.Break || vm.CallStack.Count == 0)
            return "The program is running under the debugger; it has not stopped.";
        var (reason, detail) = vm.LastStopForClaude;
        var frame = vm.SelectedFrame ?? vm.CallStack[0];
        var text = new StringBuilder($"Stopped ({reason}{(detail is { Length: > 0 } ? ": " + detail : "")}) in {frame.Name} at {Where(vm, frame)}.");
        if (!stack)
            return text.ToString();
        text.Append("\nCall stack:");
        foreach (var (f, i) in vm.CallStack.Take(20).Select((f, i) => (f, i)))
            text.Append($"\n{(f == frame ? "> " : "  ")}{i}: {f.Name} {Where(vm, f)}");
        if (vm.CallStack.Count > 20)
            text.Append($"\n  … and {vm.CallStack.Count - 20} more frames.");
        return text.ToString();
    }

    static string Where(MainViewModel vm, StackFrameItem frame) =>
        frame.SourcePath is { } path
            ? $"{IdeTool.RelativeOf(vm, path)}:{frame.Line}"
            : "(external code)";
}

/// <summary><c>debug_state</c>: whether a program is debugged, where it stopped and why, the call stack.</summary>
sealed class DebugStateTool(MainViewModel vm) : IdeTool(vm)
{
    public override string Name => "debug_state";

    public override string Description =>
        "The debug session's state in Karvon: whether a program is being debugged, where it stopped and why (a breakpoint, a step, an "
        + "exception and its message), and the call stack (file:line per frame).";

    public override ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Read, "Debug State", null, "");

    protected override Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken) =>
        Task.FromResult(new ClaudeToolOutput(DebugReport.Describe(Vm)));
}

/// <summary><c>debug_locals</c>: the stopped frame's variables, by scope, nested to a depth.</summary>
sealed class DebugLocalsTool(MainViewModel vm) : IdeTool(vm)
{
    const int PerLevel = 100;

    public override string Name => "debug_locals";

    public override string Description => "The variables where the debugged program stopped, by scope, with their types and values, nested objects to a depth.";

    public override JsonObject InputSchema => Schema("""
        {"type":"object","properties":{
          "frame":{"type":"integer","description":"The call stack frame to read (0 is the innermost); default the selected one."},
          "depth":{"type":"integer","description":"How deep to open objects and collections, 1 to 4 (default 2)."}}}
        """);

    public override ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Read, "Locals", input.TryGetProperty("frame", out var f) ? $"frame {f}" : null, "");

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        if (Vm.DebugSessionForClaude is not { } session || Vm.Mode != DebugMode.Break || Vm.CallStack.Count == 0)
            return ClaudeToolOutput.Error("The program is not stopped, so it has no variables to read.");
        // Another frame is selected as in the Call Stack window: the editor and Locals follow it.
        if (input.TryGetProperty("frame", out var f) && f.TryGetInt32(out var index) && index >= 0 && index < Vm.CallStack.Count)
            Vm.SelectedFrame = Vm.CallStack[index];
        var frame = Vm.SelectedFrame ?? Vm.CallStack[0];
        var depth = Int(input, "depth", 2, 1, 4);
        var text = new StringBuilder();
        foreach (var scope in (await session.ScopesAsync(frame.Id, cancellationToken)).Where(s => !s.Expensive))
        {
            text.Append(text.Length > 0 ? "\n" : "").Append(scope.Name).Append(':');
            await Variables(session, scope.VariablesReference, 1);
        }
        return new ClaudeToolOutput(text.Length > 0 ? text.ToString() : "No variables here.");

        async Task Variables(DebugSession s, int reference, int level)
        {
            var variables = await s.VariablesAsync(reference, cancellationToken);
            foreach (var v in variables.Take(PerLevel))
            {
                text.Append('\n').Append(new string(' ', level * 2)).Append(v.Name)
                    .Append(v.Type is { Length: > 0 } type ? ": " + type : "").Append(" = ").Append(v.Value);
                if (v.VariablesReference > 0 && level < depth && v.Value != "null")
                    await Variables(s, v.VariablesReference, level + 1);
            }
            if (variables.Count > PerLevel)
                text.Append('\n').Append(new string(' ', level * 2)).Append($"… and {variables.Count - PerLevel} more");
        }
    }
}

/// <summary><c>debug_evaluate</c>: an expression in the stopped frame, as the Watch window does.</summary>
sealed class DebugEvaluateTool(MainViewModel vm) : IdeTool(vm)
{
    public override string Name => "debug_evaluate";

    public override string Description =>
        "Evaluates an expression in the language of the debugged program (C#), in the frame where it stopped, and returns its value and type.";

    public override JsonObject InputSchema => Schema("""{"type":"object","properties":{"expression":{"type":"string"}},"required":["expression"]}""");

    public override ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Read, "Evaluate", Str(input, "expression"), "");

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        if (Vm.DebugSessionForClaude is not { } session || Vm.Mode != DebugMode.Break || Vm.CallStack.Count == 0)
            return ClaudeToolOutput.Error("The program is not stopped, so there is nothing to evaluate in.");
        var expression = Str(input, "expression") ?? throw new InvalidOperationException("Give the expression to evaluate.");
        try
        {
            var result = await session.EvaluateAsync(expression, (Vm.SelectedFrame ?? Vm.CallStack[0]).Id, "watch", cancellationToken);
            return new ClaudeToolOutput($"{expression} = {result.Value}{(result.Type is { Length: > 0 } type ? $" ({type})" : "")}");
        }
        catch (DapException ex)
        {
            return ClaudeToolOutput.Error(ex.Message);
        }
    }
}

/// <summary><c>debug_control</c>: F5, F10, F11, Shift+F11, Break All, Shift+F5; where the program is afterwards.</summary>
sealed class DebugControlTool(MainViewModel vm) : IdeTool(vm)
{
    static readonly string[] Actions = ["start", "continue", "pause", "stepOver", "stepInto", "stepOut", "stop"];

    public override string Name => "debug_control";

    public override string Description =>
        "Controls the debugger in Karvon: start (F5: debug the startup project, or a folder's startup item: its launch.json "
        + "configuration, a package.json script under the JavaScript debugger), continue, pause, stepOver, stepInto, stepOut or stop; "
        + "returns where the program is afterwards. Starting and stopping ask the user first. To run it without the debugger, use app_control.";

    public override JsonObject InputSchema => Schema("""
        {"type":"object","properties":{"action":{"type":"string","enum":["start","continue","pause","stepOver","stepInto","stepOut","stop"]}},"required":["action"]}
        """);

    public override ClaudeToolPlan Plan(JsonElement input)
    {
        var action = Str(input, "action");
        if (!Actions.Contains(action))
            throw new InvalidOperationException("action is one of " + string.Join(", ", Actions) + ".");
        return action switch
        {
            "start" => new(ToolAccess.Write, "Start Debugging", null, "Allow Claude to start debugging the startup project?") { Warning = "It builds and runs the program under the debugger." },
            "stop" => new(ToolAccess.Write, "Stop Debugging", null, "Allow Claude to stop the program being debugged?"),
            _ => new(ToolAccess.Run, "Debug", action, ""),
        };
    }

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var action = Str(input, "action");
        if (action != "start" && (Vm.DebugSessionForClaude is null || Vm.Mode == DebugMode.Design))
            return ClaudeToolOutput.Error("No debug session is running.");
        var stopped = Vm.Mode == DebugMode.Break;
        if (action is "continue" or "stepOver" or "stepInto" or "stepOut" && !stopped)
            return ClaudeToolOutput.Error("The program is not stopped.");
        switch (action)
        {
            case "start" when Vm.Mode != DebugMode.Design:
                return ClaudeToolOutput.Error("A debug session is running already.");
            case "start" or "continue":
                if (!Vm.StartDebuggingCommand.CanExecute(null))
                    return ClaudeToolOutput.Error("Karvon cannot start debugging now: open a solution with a startup project (or a folder with a startup item), and wait for any build.");
                await Vm.StartDebuggingCommand.ExecuteAsync(null);
                break;
            case "stepOver":
                await Vm.StepOverCommand.ExecuteAsync(null);
                break;
            case "stepInto":
                await Vm.StepIntoCommand.ExecuteAsync(null);
                break;
            case "stepOut":
                await Vm.StepOutCommand.ExecuteAsync(null);
                break;
            case "pause":
                await Vm.BreakAllCommand.ExecuteAsync(null);
                break;
            case "stop":
                await Vm.StopCommand.ExecuteAsync(null);
                break;
        }
        // Where it is afterwards: stopped again, ended, or still running when it does not stop soon.
        var wait = action switch { "start" => TimeSpan.FromMinutes(2), "continue" => TimeSpan.FromSeconds(10), _ => TimeSpan.FromSeconds(30) };
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await Task.Delay(100, cancellationToken);
        while (clock.Elapsed < wait && !(Vm.Mode == DebugMode.Break && Vm.CallStack.Count > 0) && !(Vm.Mode == DebugMode.Design && action != "start"))
            await Task.Delay(100, cancellationToken);
        return new ClaudeToolOutput(Vm.Mode == DebugMode.Design ? "The session has ended." : DebugReport.Describe(Vm, stack: false));
    }
}

/// <summary>
/// <c>app_control</c>: Start Without Debugging (Ctrl+F5), Stop (Shift+F5), both, and how the program is doing: what it
/// listens on and what it printed, as the Output window (Debug) shows it.
/// </summary>
sealed partial class AppControlTool(MainViewModel vm) : IdeTool(vm)
{
    static readonly string[] Actions = ["start", "stop", "restart", "status"];

    public override string Name => "app_control";

    public override string Description =>
        "Runs the app in Karvon as Debug > Start Without Debugging (Ctrl+F5) does: start builds and runs the startup project (or the "
        + "project given, which becomes the startup project) with its launch profile, stop ends it (Shift+F5), restart does both, "
        + "status tells whether it runs. Each returns the URLs it listens on and its latest output, which the Output window shows "
        + "too. Use it rather than dotnet run or npm start in the shell: the app runs in Karvon, where the user sees it and stops it. "
        + "A folder opened as it is runs its startup item instead (a .vscode/launch.json configuration, or a run task: package.json's "
        + "start or dev script...; project names one). For breakpoints and stepping use debug_control.";

    public override JsonObject InputSchema => Schema("""
        {"type":"object","properties":{
          "action":{"type":"string","enum":["start","stop","restart","status"]},
          "project":{"type":"string","description":"start, restart: a runnable project's name (in a folder, a startup item's); default the startup project."},
          "profile":{"type":"string","description":"start, restart: a launch profile of the project (Properties/launchSettings.json); default the one chosen in Karvon."},
          "lines":{"type":"integer","description":"How many of the latest output lines to return; default 40."}},"required":["action"]}
        """);

    public override ClaudeToolPlan Plan(JsonElement input)
    {
        var action = Str(input, "action");
        if (!Actions.Contains(action))
            throw new InvalidOperationException("action is one of " + string.Join(", ", Actions) + ".");
        var project = Str(input, "project") ?? RunTarget;
        return action switch
        {
            "start" => new(ToolAccess.Write, "Start Without Debugging", project, $"Allow Claude to run {project ?? "the startup project"}?")
            {
                Warning = "It builds and runs the program, as Ctrl+F5 does.",
            },
            "restart" => new(ToolAccess.Write, "Restart", project, $"Allow Claude to restart {project ?? "the startup project"}?")
            {
                Warning = "It stops the program and builds and runs it again.",
            },
            "stop" => new(ToolAccess.Write, "Stop", RunTarget, "Allow Claude to stop the running program?"),
            _ => new(ToolAccess.Read, "App", "status", ""),
        };
    }

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        _ = SolutionDirectory;
        var lines = Int(input, "lines", 40, 1, 500);
        switch (Str(input, "action"))
        {
            case "status":
                return new ClaudeToolOutput(Report(lines));
            case "stop":
                if (!Vm.IsRunning && Vm.Mode == DebugMode.Design)
                    return ClaudeToolOutput.Error("Nothing is running.");
                await StopAsync(cancellationToken);
                return new ClaudeToolOutput(Report(lines));
            case "restart":
                if (Vm.IsRunning || Vm.Mode != DebugMode.Design)
                    await StopAsync(cancellationToken);
                return await StartAsync(input, lines, cancellationToken);
            default:
                if (Vm.IsRunning)
                    return ClaudeToolOutput.Error($"{RunTarget ?? "The program"} is running already: restart it, or stop it first.");
                if (Vm.Mode != DebugMode.Design)
                    return ClaudeToolOutput.Error("A debug session is running: stop it first (debug_control stop).");
                return await StartAsync(input, lines, cancellationToken);
        }
    }

    /// <summary>What Start runs: the startup project, or a folder's startup item.</summary>
    string? RunTarget => Vm.IsFolderWorkspace ? Vm.StartupItem?.Name : Vm.StartupProject?.Name;

    async Task<ClaudeToolOutput> StartAsync(JsonElement input, int lines, CancellationToken cancellationToken)
    {
        if (Vm.IsFolderWorkspace && Str(input, "project") is { } itemName)
        {
            if (Vm.StartupItems.FirstOrDefault(i => i.Name.Equals(itemName, StringComparison.OrdinalIgnoreCase)) is not { } item)
                return ClaudeToolOutput.Error($"{itemName} is not a startup item of the folder. Startup items: {(Vm.StartupItems.Count == 0 ? "none" : string.Join(", ", Vm.StartupItems.Select(i => i.Name)))}.");
            Vm.StartupItem = item;
        }
        else if (Str(input, "project") is { } name)
        {
            if (Vm.StartupProjects.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is not { } project)
                return ClaudeToolOutput.Error($"{name} is not a runnable project of the solution. Runnable: {string.Join(", ", Vm.StartupProjects.Select(p => p.Name))}.");
            Vm.StartupProject = project;
        }
        if (Str(input, "profile") is { } profile)
        {
            if (!Vm.LaunchProfiles.Contains(profile))
                return ClaudeToolOutput.Error($"{Vm.StartupProject?.Name} has no launch profile {profile}. It has: {(Vm.LaunchProfiles.Count == 0 ? "none" : string.Join(", ", Vm.LaunchProfiles))}.");
            Vm.LaunchProfile = profile;
        }
        if (!Vm.StartWithoutDebuggingCommand.CanExecute(null))
            return ClaudeToolOutput.Error(Vm.IsFolderWorkspace
                ? "Karvon cannot run it now: the folder has no startup item (a launch.json configuration, package.json's start or dev script...), or a build runs."
                : "Karvon cannot run it now: open a solution with a startup project, and wait for any build.");
        var run = Vm.StartWithoutDebuggingCommand.ExecuteAsync(null);
        // Built and started: until it listens (a web host), ends, or has run a while; the build may take minutes.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        TimeSpan? started = null;
        // A launch configuration runs as a debug session without the debugger, which goes on after the command is done.
        while (clock.Elapsed < TimeSpan.FromMinutes(5))
        {
            if (Vm.IsRunning || Vm.Mode != DebugMode.Design)
            {
                started ??= clock.Elapsed;
                if (Listening().Count > 0 || clock.Elapsed - started > TimeSpan.FromSeconds(20))
                    break;
            }
            else if (run.IsCompleted)
                break;
            await Task.Delay(200, cancellationToken);
        }
        // The last lines the program printed land on the UI thread after its exit is known.
        await Task.Delay(200, cancellationToken);
        if (run.IsCompleted && !Vm.IsFolderWorkspace && Vm.LastBuildForClaude is { Succeeded: false })
            return ClaudeToolOutput.Error(BuildReport.Describe(Vm, warnings: false));
        return new ClaudeToolOutput(Report(lines), !Vm.IsRunning && Vm.LastExitCodeForClaude is not (null or 0));
    }

    async Task StopAsync(CancellationToken cancellationToken)
    {
        await Vm.StopCommand.ExecuteAsync(null);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while ((Vm.IsRunning || Vm.Mode != DebugMode.Design) && clock.Elapsed < TimeSpan.FromSeconds(30))
            await Task.Delay(100, cancellationToken);
        await Task.Delay(200, cancellationToken);
    }

    /// <summary>Whether it runs, where it listens, and the latest lines of its output (secrets hidden).</summary>
    string Report(int lines)
    {
        var name = RunTarget ?? "The program";
        var profile = Vm.LaunchProfile is { } p ? $" (launch profile {p})" : "";
        var text = new StringBuilder(Vm.IsRunning
            ? $"{name} is running{profile}."
            : Vm.Mode != DebugMode.Design
                ? $"{name} is running under the debugger (see debug_state)."
                : Vm.LastExitCodeForClaude is { } code
                    ? $"{name} is not running: it exited with code {code}."
                    : $"{name} is not running.");
        if (Vm.IsRunning && Listening() is { Count: > 0 } urls)
            text.Append(" Listening on ").Append(string.Join(", ", urls)).Append('.');
        var output = Vm.DebugOutput.Document.Text.TrimEnd().Split('\n');
        if (output is not [""])
        {
            text.Append($"\n\nOutput (last {Math.Min(lines, output.Length)} of {output.Length} lines):");
            foreach (var line in output.TakeLast(lines))
                text.Append('\n').Append(Redaction.Text(line.TrimEnd('\r')));
        }
        return text.ToString();
    }

    /// <summary>ASP.NET Core's "Now listening on:" URLs; a task's local URLs (Vite's "Local: http://localhost:5173/"...).</summary>
    List<string> Listening() =>
        (Vm.IsFolderWorkspace ? LocalUrl() : ListeningLine()).Matches(Vm.DebugOutput.Document.Text).Select(m => m.Groups[1].Value.TrimEnd('/')).Distinct().ToList();

    [GeneratedRegex(@"Now listening on: (\S+)")]
    private static partial Regex ListeningLine();

    [GeneratedRegex(@"(https?://(?:localhost|127\.0\.0\.1|0\.0\.0\.0|\[::1?\])(?::\d+)?[^\s\u001b'"")]*)")]
    private static partial Regex LocalUrl();
}

// ---- Editor -------------------------------------------------------------------------------------------------------

/// <summary><c>editor_selection</c>: the file the user looks at, the caret and the selection.</summary>
sealed class EditorSelectionTool(MainViewModel vm) : IdeTool(vm)
{
    public override string Name => "editor_selection";

    public override string Description => "What the user is looking at in Karvon's editor: the file, its language, the caret's line and column, and the selected text.";

    public override ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Read, "Editor Selection", null, "");

    protected override Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        if (Vm.ActiveEditor is not { } doc)
            return Task.FromResult(new ClaudeToolOutput("No text document is open in the editor."));
        var editor = doc.Host.Editor;
        var (line, column) = doc.Host.CaretPosition;
        var text = new StringBuilder($"File: {Relative(doc.FilePath)}{(doc.IsDirty ? " (unsaved changes)" : "")}");
        text.Append($"\nLanguage: {Language(doc.FilePath)}");
        text.Append($"\nCaret: line {line}, column {column}");
        if (editor.SelectionLength > 0)
            text.Append($"\nSelection from line {doc.Host.Document.GetLineByOffset(editor.SelectionStart).LineNumber}:\n").Append(editor.SelectedText);
        else
            text.Append("\nNothing is selected.");
        return Task.FromResult(new ClaudeToolOutput(text.ToString()));
    }

    static string Language(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".cs" => "C#",
        ".razor" or ".cshtml" => "Razor",
        ".fs" => "F#",
        ".vb" => "Visual Basic",
        ".xaml" or ".axaml" => "XAML",
        ".json" => "JSON",
        ".xml" or ".csproj" or ".props" or ".targets" => "XML",
        ".js" => "JavaScript",
        ".ts" => "TypeScript",
        ".css" => "CSS",
        ".html" => "HTML",
        ".sql" => "SQL",
        ".md" => "Markdown",
        ".yml" or ".yaml" => "YAML",
        _ => "plain text",
    };
}

/// <summary><c>open_document</c>: a file as the editor has it (unsaved changes included), with line numbers.</summary>
sealed class OpenDocumentTool(MainViewModel vm) : IdeTool(vm)
{
    public override string Name => "open_document";

    public override string Description =>
        "Reads a file of the solution's folder with line numbers, as Karvon's editor has it (unsaved changes included). Paths are relative to the solution's folder.";

    public override JsonObject InputSchema => Schema("""
        {"type":"object","properties":{
          "path":{"type":"string","description":"The file, relative to the solution's folder."},
          "startLine":{"type":"integer","description":"The first line to read, from 1."},
          "endLine":{"type":"integer","description":"The last line to read."}},
         "required":["path"]}
        """);

    public override ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Read, "Read", Str(input, "path"), "");

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var file = FileOf(Str(input, "path") ?? throw new InvalidOperationException("Give the path of the file to read."));
        var open = Vm.Editors.FirstOrDefault(d => d.FilePath == file);
        if (open is null && !File.Exists(file))
            return ClaudeToolOutput.Error($"{Relative(file)} does not exist.");
        var text = open?.Host.Document.Text ?? await File.ReadAllTextAsync(file, cancellationToken);
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var first = Math.Clamp(Int(input, "startLine", 1, 1, int.MaxValue), 1, lines.Count);
        var last = Math.Min(Math.Clamp(Int(input, "endLine", lines.Count, 1, int.MaxValue), first, lines.Count), first + 1999);
        var output = new StringBuilder($"{Relative(file)}, lines {first}-{last} of {lines.Count}{(open?.IsDirty == true ? " (with unsaved changes)" : "")}:");
        for (var i = first; i <= last; i++)
            output.Append('\n').Append(i).Append('\t').Append(lines[i - 1]);
        return new ClaudeToolOutput(output.ToString());
    }
}

/// <summary><c>code_diagnostics</c>: what Roslyn reports for a file (checked now, open or not), or for every open file.</summary>
sealed class CodeDiagnosticsTool(MainViewModel vm) : IdeTool(vm)
{
    public override string Name => "code_diagnostics";

    public override string Description =>
        "The errors and warnings the language servers (Roslyn, TypeScript's...) and linters (ESLint) report: for one file (checked now, even if it is not open), or for every open file.";

    public override JsonObject InputSchema => Schema("""
        {"type":"object","properties":{"path":{"type":"string","description":"A file relative to the solution's folder; omit for all open files."}}}
        """);

    public override ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Read, "Diagnostics", Str(input, "path") ?? "open files", "");

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var found = new List<(string File, LspDiagnostic Diagnostic)>();
        if (Str(input, "path") is { } path)
        {
            var file = FileOf(path);
            if (!File.Exists(file))
                return ClaudeToolOutput.Error($"{Relative(file)} does not exist.");
            // The file's server and its linters (ESLint).
            var servers = Vm.LanguageServersForClaude(file).ToList();
            if (servers.Count == 0)
                return new ClaudeToolOutput($"No language server checks {Relative(file)}, so there are no diagnostics for it.");
            var open = Vm.Editors.Any(d => d.FilePath == file);
            var text = open ? null : await File.ReadAllTextAsync(file, cancellationToken);
            foreach (var lsp in servers)
            {
                if (open)
                {
                    found.AddRange((await lsp.DiagnosticsAsync(file, cancellationToken)).Select(d => (file, d)));
                    continue;
                }
                // Not open in the editor: opened in the server only, for as long as it takes to check it.
                await lsp.DidOpenAsync(file, text!);
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(30));
                    found.AddRange((await lsp.DiagnosticsAsync(file, timeout.Token)).Select(d => (file, d)));
                }
                finally
                {
                    await lsp.DidCloseAsync(file);
                }
            }
        }
        else
            found.AddRange(Vm.Editors.SelectMany(d => d.Host.Diagnostics.Select(x => (d.FilePath, x))));
        var shown = found.Where(f => f.Diagnostic.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning or DiagnosticSeverity.Information)
            .OrderBy(f => f.File, StringComparer.Ordinal).ThenBy(f => f.Diagnostic.Range.Start.Line).ToList();
        if (shown.Count == 0)
            return new ClaudeToolOutput("No problems.");
        return new ClaudeToolOutput(string.Join("\n", shown.Select(f =>
            $"{Relative(f.File)}:{f.Diagnostic.Range.Start.Line + 1}:{f.Diagnostic.Range.Start.Character + 1}: {f.Diagnostic.Severity.ToString().ToLowerInvariant()}: "
            + f.Diagnostic.Message + (f.Diagnostic.Code.Length > 0 ? $" [{f.Diagnostic.Code}]" : ""))));
    }
}

/// <summary><c>find_in_files</c>: text or a regular expression in the solution's text files (not build output).</summary>
sealed class FindInFilesTool(MainViewModel vm) : IdeTool(vm)
{
    static readonly HashSet<string> SkippedFolders = new(StringComparer.Ordinal)
    {
        ".git", ".hg", ".svn", ".vs", ".idea", "bin", "obj", "node_modules", "__pycache__", ".venv", "venv", "target", "dist", ".next",
    };

    public override string Name => "find_in_files";

    public override string Description =>
        "Searches the solution folder's text files (not build output or dependencies) for text or a regular expression; returns file:line:column: line.";

    public override JsonObject InputSchema => Schema("""
        {"type":"object","properties":{
          "query":{"type":"string"},
          "regex":{"type":"boolean","description":"Treat the query as a .NET regular expression."},
          "matchCase":{"type":"boolean"},
          "limit":{"type":"integer","description":"At most this many matches (default 200)."}},
         "required":["query"]}
        """);

    public override ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Read, "Find in Files", Str(input, "query"), "");

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var root = SolutionDirectory;
        var query = Str(input, "query") ?? throw new InvalidOperationException("Give the text to find.");
        var limit = Int(input, "limit", 200, 1, 2000);
        Regex pattern;
        try
        {
            var options = RegexOptions.CultureInvariant | (Bool(input, "matchCase") ? RegexOptions.None : RegexOptions.IgnoreCase);
            pattern = new Regex(Bool(input, "regex") ? query : Regex.Escape(query), options, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException ex)
        {
            return ClaudeToolOutput.Error("Not a regular expression: " + ex.Message);
        }
        try
        {
            var (hits, more) = await Task.Run(() => Search(root, pattern, limit, cancellationToken), cancellationToken);
            var found = hits.Select(h => h.Split(':', 4)).Where(p => p.Length == 4)
                .Select(p => new FoundText(Path.Combine(root, p[0]), int.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture), int.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture), p[3].TrimStart()))
                .ToList();
            if (hits.Count == 0)
                return new ClaudeToolOutput("No matches.") { Detail = found };
            return new ClaudeToolOutput(string.Join("\n", hits) + (more ? $"\n(only the first {limit} matches)" : "")) { Detail = found };
        }
        catch (RegexMatchTimeoutException)
        {
            return ClaudeToolOutput.Error("The regular expression takes too long on these files; make it simpler.");
        }
    }

    static (List<string> Hits, bool More) Search(string root, Regex pattern, int limit, CancellationToken cancellationToken)
    {
        var hits = new List<string>();
        var folders = new Stack<string>([root]);
        while (folders.Count > 0)
        {
            var folder = folders.Pop();
            IEnumerable<string> files, subfolders;
            try
            {
                files = Directory.EnumerateFiles(folder).Order(StringComparer.Ordinal).ToList();
                subfolders = Directory.EnumerateDirectories(folder).Where(d => !SkippedFolders.Contains(Path.GetFileName(d))).OrderDescending(StringComparer.Ordinal).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsText(file))
                    continue;
                var number = 0;
                foreach (var line in File.ReadLines(file))
                {
                    number++;
                    if (pattern.Match(line) is not { Success: true } match)
                        continue;
                    if (hits.Count == limit)
                        return (hits, true);
                    hits.Add($"{Path.GetRelativePath(root, file).Replace('\\', '/')}:{number}:{match.Index + 1}: {line.Trim()}");
                }
            }
            foreach (var subfolder in subfolders)
                folders.Push(subfolder);
        }
        return (hits, false);
    }

    /// <summary>Not larger than 5 MB, and no NUL byte in its first 8 KB.</summary>
    static bool IsText(string file)
    {
        try
        {
            var info = new FileInfo(file);
            if (info.Length > 5_000_000)
                return false;
            using var stream = info.OpenRead();
            var buffer = new byte[8192];
            var read = stream.Read(buffer, 0, buffer.Length);
            return Array.IndexOf(buffer, (byte)0, 0, read) < 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
