using System.Text.Json.Nodes;
using Karvon.Modules;

namespace Karvon.Python;

/// <summary>
/// A Python folder's run tasks, for a folder with no launch.json (as VS Code's Python extension offers them): its entry
/// point at its root (main.py, app.py; manage.py runserver), and the file in the editor. Each runs with the workspace's
/// interpreter and debugs with debugpy. A uv project has uv sync, and its scripts by uv run (debugged as the script
/// its .venv has).
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
        var uv = Directory.Exists(workspace) && UvProject.Is(workspace);
        if (!Directory.Exists(workspace) || !uv && !Directory.EnumerateFiles(workspace, "*.py").Any())
            return [];
        var path = PythonEnvironment.PathFor(workspace, Environment.GetEnvironmentVariable("PATH") ?? "");
        var python = PythonEnvironment.Interpreter(workspace, path) ?? (OperatingSystem.IsWindows() ? "python.exe" : "python3");
        var tasks = new List<WorkspaceTask>();
        if (uv)
        {
            tasks.Add(new WorkspaceTask("uv: sync", WorkspaceTaskKind.Other, "uv", ["sync"], workspace) { Source = Source });
            foreach (var script in UvProject.Scripts(workspace))
                tasks.Add(UvScript(script, workspace, isDefault: tasks.Count(t => t.Kind == WorkspaceTaskKind.Run) == 0));
        }
        foreach (var (file, arguments) in EntryPoints)
            if (File.Exists(Path.Combine(workspace, file)))
                tasks.Add(TaskOf($"python: {file}", python, Path.Combine(workspace, file), arguments, workspace, isDefault: tasks.All(t => t.Kind != WorkspaceTaskKind.Run)));
        tasks.Add(TaskOf("python: current file", python, "${file}", [], workspace, isDefault: tasks.All(t => t.Kind != WorkspaceTaskKind.Run)));
        return tasks;
    }

    /// <summary>A uv project's script: uv run, which makes the .venv first when it is not there; debugged as the .venv's script.</summary>
    static WorkspaceTask UvScript(string script, string workspace, bool isDefault)
    {
        var installed = PythonEnvironment.VirtualEnvironmentBin(workspace) is { } bin && File.Exists(Path.Combine(bin, script)) ? Path.Combine(bin, script) : null;
        return new WorkspaceTask($"uv: run {script}", WorkspaceTaskKind.Run, "uv", ["run", script], workspace)
        {
            Source = Source,
            IsDefault = isDefault,
            Debug = installed is null ? null : new DebugLaunch($"uv: run {script}", new JsonObject
            {
                ["type"] = "debugpy",
                ["request"] = "launch",
                ["name"] = $"uv: run {script}",
                ["program"] = installed,
                ["cwd"] = workspace,
            })
            { Source = Source },
        };
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
