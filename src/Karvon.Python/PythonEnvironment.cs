using Karvon.Workspace;

namespace Karvon.Python;

/// <summary>
/// The Python a workspace uses: its own virtual environment (.venv, venv, env), as VS Code's Python extension picks it
/// first, else python3 (or python) on the PATH.
/// </summary>
public static class PythonEnvironment
{
    static readonly string[] Folders = [".venv", "venv", "env"];

    static string BinFolder => OperatingSystem.IsWindows() ? "Scripts" : "bin";

    static string Executable => OperatingSystem.IsWindows() ? "python.exe" : "python";

    /// <summary>A virtual environment's interpreter, the folder of one being given (made or not yet).</summary>
    public static string InterpreterIn(string venv) => Path.Combine(venv, BinFolder, Executable);

    /// <summary>The virtual environment's bin folder in the workspace, or null.</summary>
    public static string? VirtualEnvironmentBin(string workspace) => Folders
        .Select(folder => Path.Combine(workspace, folder, BinFolder))
        .FirstOrDefault(bin => File.Exists(Path.Combine(bin, Executable)));

    /// <summary>The interpreter: the workspace's virtual environment's, else python3 or python on the PATH; null when there is none.</summary>
    public static string? Interpreter(string workspace, string path) =>
        VirtualEnvironmentBin(workspace) is { } bin ? Path.Combine(bin, Executable)
            : TaskVariables.Find(OperatingSystem.IsWindows() ? "python.exe" : "python3", path, workspace)
                ?? TaskVariables.Find(OperatingSystem.IsWindows() ? "py.exe" : "python", path, workspace);

    /// <summary>The PATH things run with in the workspace: its virtual environment's bin first, as when it is activated.</summary>
    public static string PathFor(string workspace, string path) =>
        VirtualEnvironmentBin(workspace) is { } bin ? bin + System.IO.Path.PathSeparator + path : path;
}
