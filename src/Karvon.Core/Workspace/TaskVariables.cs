using System.Text.RegularExpressions;
using Karvon.Modules;

namespace Karvon.Workspace;

/// <summary>
/// What a task needs to run as VS Code runs it: its variables (${workspaceFolder}, ${env:HOME}, ${file}...), the shell of a
/// shell task, and its program found in the PATH.
/// </summary>
public static partial class TaskVariables
{
    [GeneratedRegex(@"\$\{([^}]+)\}")]
    private static partial Regex Variable();

    /// <summary>
    /// The folder's variables and the environment's put in, and those of the editor's file once there is one (when the
    /// task runs); one Karvon does not know stays as it is.
    /// </summary>
    public static string Expand(string text, string workspace, string? file = null) => Variable().Replace(text, m =>
    {
        var name = m.Groups[1].Value;
        if (name.StartsWith("env:", StringComparison.Ordinal))
            return Environment.GetEnvironmentVariable(name[4..]) ?? "";
        return name switch
        {
            "workspaceFolder" or "workspaceRoot" or "cwd" => workspace,
            "workspaceFolderBasename" => Path.GetFileName(workspace.TrimEnd('/', '\\')),
            "userHome" => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "pathSeparator" or "/" => Path.DirectorySeparatorChar.ToString(),
            "file" when file is not null => file,
            "fileDirname" when file is not null => Path.GetDirectoryName(file) ?? "",
            "fileBasename" when file is not null => Path.GetFileName(file),
            "fileBasenameNoExtension" when file is not null => Path.GetFileNameWithoutExtension(file),
            "fileExtname" when file is not null => Path.GetExtension(file),
            "relativeFile" when file is not null => Path.GetRelativePath(workspace, file),
            "relativeFileDirname" when file is not null => Path.GetRelativePath(workspace, Path.GetDirectoryName(file) ?? workspace),
            _ => m.Value,
        };
    });

    /// <summary>The task as it runs: the variables of the editor's file put in.</summary>
    public static WorkspaceTask ForFile(WorkspaceTask task, string workspace, string? file) => file is null ? task : task with
    {
        Program = Expand(task.Program, workspace, file),
        Arguments = task.Arguments.Select(a => Expand(a, workspace, file)).ToList(),
        WorkingDirectory = Expand(task.WorkingDirectory, workspace, file),
    };

    /// <summary>A command line as the shell runs it: the user's shell (or sh) with -c, cmd /d /s /c on Windows.</summary>
    public static (string Program, List<string> Arguments) Shell(string commandLine)
    {
        if (OperatingSystem.IsWindows())
            return ("cmd.exe", ["/d", "/s", "/c", commandLine]);
        var shell = Environment.GetEnvironmentVariable("SHELL") is { Length: > 0 } own && File.Exists(own) ? own : "/bin/sh";
        return (shell, ["-c", commandLine]);
    }

    /// <summary>An argument of a shell task, quoted when the shell would split it or read something in it.</summary>
    public static string QuoteForShell(string argument)
    {
        if (argument.Length > 0 && argument.All(c => char.IsLetterOrDigit(c) || "-_./=:,+@%".Contains(c)))
            return argument;
        return OperatingSystem.IsWindows() ? $"\"{argument.Replace("\"", "\\\"")}\"" : $"'{argument.Replace("'", "'\\''")}'";
    }

    /// <summary>
    /// The program's file: a path as it is (relative to where the task runs), a name looked up in the PATH (with PATHEXT
    /// on Windows) as the shell would; null when it is not there.
    /// </summary>
    public static string? Find(string program, string path, string workingDirectory)
    {
        if (program.Contains('/') || program.Contains('\\'))
        {
            var full = Path.GetFullPath(program, workingDirectory);
            return File.Exists(full) ? full : null;
        }
        string[] extensions = OperatingSystem.IsWindows() ? ["", .. (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';')] : [""];
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, program + extension);
                if (File.Exists(candidate))
                    return candidate;
            }
        return null;
    }
}
