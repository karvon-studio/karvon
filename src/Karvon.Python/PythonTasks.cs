using System.Text.Json.Nodes;
using Karvon.Modules;

namespace Karvon.Python;

/// <summary>
/// A Python folder's run tasks, for a folder with no launch.json (as VS Code's Python extension offers them): its entry
/// point at its root (main.py, app.py; manage.py runserver), and the file in the editor. Each runs with the workspace's
/// interpreter and debugs with debugpy.
/// </summary>
public static class PythonTasks
{
    public const string Source = "python";

    static readonly (string File, string[] Arguments)[] EntryPoints =
    [
        ("main.py", []),
        ("app.py", []),
        ("manage.py", ["runserver", "--noreload"]),
    ];

    public static IReadOnlyList<WorkspaceTask> Read(string workspace)
    {
        if (!Directory.Exists(workspace) || !Directory.EnumerateFiles(workspace, "*.py").Any())
            return [];
        var path = PythonEnvironment.PathFor(workspace, Environment.GetEnvironmentVariable("PATH") ?? "");
        var python = PythonEnvironment.Interpreter(workspace, path) ?? (OperatingSystem.IsWindows() ? "python.exe" : "python3");
        var tasks = new List<WorkspaceTask>();
        foreach (var (file, arguments) in EntryPoints)
            if (File.Exists(Path.Combine(workspace, file)))
                tasks.Add(TaskOf($"python: {file}", python, Path.Combine(workspace, file), arguments, workspace, isDefault: tasks.Count == 0));
        tasks.Add(TaskOf("python: current file", python, "${file}", [], workspace, isDefault: tasks.Count == 0));
        return tasks;
    }

    static WorkspaceTask TaskOf(string name, string python, string program, string[] arguments, string workspace, bool isDefault) =>
        new(name, WorkspaceTaskKind.Run, python, ["-u", program, .. arguments], workspace)
        {
            Source = Source,
            IsDefault = isDefault,
            Debug = new DebugLaunch(name, new JsonObject
            {
                ["type"] = "debugpy",
                ["request"] = "launch",
                ["name"] = name,
                ["program"] = program,
                ["args"] = new JsonArray([.. arguments.Select(a => JsonValue.Create(a))]),
                ["cwd"] = workspace,
            })
            { Source = Source },
        };
}
