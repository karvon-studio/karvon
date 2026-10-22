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

    /// <summary>Variables set for it over Karvon's own (a PATH with its runtime's folder first...).</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>The command as the Output window shows it: the program by its name, an argument with a space quoted.</summary>
    public string CommandLine =>
        string.Join(' ', [Path.IsPathRooted(Program) ? Path.GetFileName(Program) : Program, .. Arguments.Select(a => a.Length == 0 || a.Any(char.IsWhiteSpace) ? $"\"{a}\"" : a)]);

    public override string ToString() => Name;
}
