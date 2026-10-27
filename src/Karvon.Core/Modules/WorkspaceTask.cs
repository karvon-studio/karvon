using System.Text.Json.Nodes;

namespace Karvon.Modules;

/// <summary>What a task is for: in a folder, Build runs its build task, Start Without Debugging a run task, Run All Tests its test task.</summary>
public enum WorkspaceTaskKind
{
    Build,
    Run,
    Test,
    Other,
}

/// <summary>
/// A command of the open folder (a script of its package.json, a task of .vscode/tasks.json...): a program, its arguments
/// and where it runs. Modules find theirs in <see cref="KarvonModule.WorkspaceTasks"/>; Karvon reads tasks.json itself.
/// ${file} and the other variables of the editor's file are put in when it runs.
/// </summary>
public sealed record WorkspaceTask(string Name, WorkspaceTaskKind Kind, string Program, IReadOnlyList<string> Arguments, string WorkingDirectory)
{
    /// <summary>Where it comes from ("npm", "tasks.json"), shown beside its name.</summary>
    public string Source { get; init; } = "";

    /// <summary>The one its kind runs when there are several (tasks.json's isDefault).</summary>
    public bool IsDefault { get; init; }

    /// <summary>It keeps running (a watch, a dev server): it runs beside the rest until it is stopped.</summary>
    public bool IsBackground { get; init; }

    /// <summary>How F5 debugs it, when its module knows how (a package.json script under the JavaScript debugger); else F5 runs it.</summary>
    public DebugLaunch? Debug { get; init; }

    /// <summary>
    /// How its output's lines are read as errors for the Error List (its problem matcher): null, as MSBuild and tsc
    /// write them (file(line,col): error CODE: message).
    /// </summary>
    public Func<string, Build.BuildDiagnostic?>? Problems { get; init; }

    /// <summary>
    /// Its problem matcher when that reads lines together (tasks.json's patterns of several lines, as ESLint's stylish
    /// output has them): it makes a new reader for each run, and wins over Problems.
    /// </summary>
    public Func<Func<string, Build.BuildDiagnostic?>>? ProblemReader { get; init; }

    /// <summary>Variables set for it over Karvon's own (a PATH with its runtime's folder first...).</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>The tasks it runs first, by their names (tasks.json's dependsOn); one with no program of its own only runs them.</summary>
    public IReadOnlyList<string> DependsOn { get; init; } = [];

    /// <summary>Its dependencies run one after another, each once the one before succeeded (dependsOrder "sequence"); else together.</summary>
    public bool DependsInSequence { get; init; }

    /// <summary>The inputs its ${input:id} asks for when it runs (its tasks.json's "inputs"), by their ids.</summary>
    public IReadOnlyDictionary<string, Workspace.TaskInput> Inputs { get; init; } = new Dictionary<string, Workspace.TaskInput>();

    /// <summary>A task that only runs its dependencies (tasks.json's task with dependsOn and no command).</summary>
    public bool IsCompound => Program.Length == 0;

    /// <summary>
    /// A run that is a console program (a console app's Ctrl+F5): it runs in a console of its own in the Terminal window,
    /// as Visual Studio's console window, its output there and the keyboard its input; not in Output.
    /// </summary>
    public bool RunsInConsole { get; init; }

    /// <summary>A run's serverReadyAction, as a launch configuration has it: the browser opens once its output says it listens.</summary>
    public System.Text.Json.Nodes.JsonObject? ServerReadyAction { get; init; }

    /// <summary>
    /// A run whose code changes apply while it runs, as they are saved (Hot Reload: dotnet watch's): Debug > Hot Reload
    /// (Alt+F10) saves them.
    /// </summary>
    public bool HotReloads { get; init; }

    /// <summary>
    /// Reads a run's output in Output, line by line, for what the command says beside the program's own output (Hot
    /// Reload's results); it makes a new reader for each run.
    /// </summary>
    public Func<Func<string, RunNews?>>? OutputReader { get; init; }

    /// <summary>The command as the Output window shows it: the program by its name, an argument with a space quoted.</summary>
    public string CommandLine => IsCompound ? "dependsOn: " + string.Join(", ", DependsOn) :
        string.Join(' ', [Path.IsPathRooted(Program) ? Path.GetFileName(Program) : Program, .. Arguments.Select(a => a.Length == 0 || a.Any(char.IsWhiteSpace) ? $"\"{a}\"" : a)]);

    public override string ToString() => Name;
}

/// <summary>
/// How F5 debugs something: a debugger's launch (or attach) configuration, as .vscode/launch.json writes it ("type",
/// "request", "program"..., its variables put in when it starts). Its type picks the debugger (a module's).
/// </summary>
public sealed record DebugLaunch(string Name, JsonObject Configuration)
{
    /// <summary>Where it comes from ("launch.json", "npm"), shown beside its name.</summary>
    public string Source { get; init; } = "";

    /// <summary>The inputs its ${input:id} asks for when it starts (its launch.json's "inputs"), by their ids.</summary>
    public IReadOnlyDictionary<string, Workspace.TaskInput> Inputs { get; init; } = new Dictionary<string, Workspace.TaskInput>();

    public string Type => Text("type") ?? "";

    /// <summary>"launch", or "attach" to a program running already.</summary>
    public string Request => Text("request") ?? "launch";

    /// <summary>The task run before it starts (a build): it starts only when that succeeds.</summary>
    public string? PreLaunchTask => Text("preLaunchTask");

    /// <summary>The task run once the session ends.</summary>
    public string? PostDebugTask => Text("postDebugTask");

    string? Text(string name) => Configuration[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    public override string ToString() => Name;
}

/// <summary>
/// What a line of a run's output says (<see cref="WorkspaceTask.OutputReader"/>): a status for the status bar, and that
/// the program ended with an exit code though the command goes on (dotnet watch then waits for changes), which ends the run.
/// </summary>
public sealed record RunNews(string? Status, int? Ended = null);

/// <summary>A program running in a console of its own (<see cref="IWorkbench.StartInConsoleAsync"/>): its process, and its exit code once it exits.</summary>
public sealed record ProgramConsole(int ProcessId, Task<int> Exited);
