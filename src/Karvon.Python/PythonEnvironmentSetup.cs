using Karvon.Modules;

namespace Karvon.Python;

/// <summary>
/// A folder's virtual environment made for it, as VS Code's Python extension offers it: Pyright cannot resolve the
/// imports of a folder whose packages are nowhere. A uv project is synced (uv sync makes its .venv); a folder with a
/// requirements.txt gets a virtual environment (python3 -m venv: named as its .gitignore names one, else .venv) with
/// requirements.txt installed into it by pip, or, having one already, requirements.txt installed into it.
/// </summary>
public static class PythonEnvironmentSetup
{
    /// <summary>What it would do in the folder (a menu item's, a question's title), or null when the folder needs nothing.</summary>
    public static string? Offer(string folder)
    {
        var hasVenv = PythonEnvironment.VirtualEnvironmentBin(folder) is not null;
        if (UvProject.Is(folder))
            return hasVenv ? null : "Sync the uv project (uv sync)";
        if (!File.Exists(Path.Combine(folder, "requirements.txt")))
            return null;
        return hasVenv ? "Install requirements.txt into the virtual environment" : $"Create a virtual environment ({VenvName(folder)}) and install requirements.txt";
    }

    /// <summary>Whether the folder has no environment its packages are in (what the question at the first .py file asks about).</summary>
    public static bool IsMissing(string folder) =>
        PythonEnvironment.VirtualEnvironmentBin(folder) is null && (UvProject.Is(folder) || File.Exists(Path.Combine(folder, "requirements.txt")));

    /// <summary>The folder's virtual environment's name: the one its .gitignore leaves out, else .venv (VS Code's).</summary>
    public static string VenvName(string folder)
    {
        var ignored = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var gitignore = Path.Combine(folder, ".gitignore");
            if (File.Exists(gitignore))
                foreach (var line in File.ReadLines(gitignore))
                    ignored.Add(line.Trim().Trim('/'));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return new[] { ".venv", "venv", "env" }.FirstOrDefault(ignored.Contains) ?? ".venv";
    }

    /// <summary>Its steps, in order (each run when the one before succeeded); null when there is no Python to make it with.</summary>
    public static IReadOnlyList<WorkspaceTask>? Steps(string folder, string path)
    {
        if (UvProject.Is(folder))
            return [new("uv: sync", WorkspaceTaskKind.Other, "uv", ["sync"], folder)];
        if (PythonEnvironment.VirtualEnvironmentBin(folder) is { } bin)
            return [Install(Path.Combine(bin, OperatingSystem.IsWindows() ? "python.exe" : "python"), folder)];
        if (PythonEnvironment.Interpreter(folder, path) is not { } python)
            return null;
        var name = VenvName(folder);
        return [new($"python: venv {name}", WorkspaceTaskKind.Other, python, ["-m", "venv", name], folder),
            Install(PythonEnvironment.InterpreterIn(Path.Combine(folder, name)), folder)];
    }

    static WorkspaceTask Install(string python, string folder) =>
        new("pip: install -r requirements.txt", WorkspaceTaskKind.Other, python, ["-m", "pip", "install", "-r", "requirements.txt"], folder);
}
