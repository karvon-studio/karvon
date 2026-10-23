using System.Diagnostics;
using System.Text.Json.Nodes;
using Karvon.Debugging;
using Karvon.Modules;

namespace Karvon.Python;

/// <summary>
/// F5 for Python: debugpy's adapter on the workspace's interpreter (its .venv first), for launch.json's debugpy and python
/// configurations (VS Code's names for it).
/// </summary>
public static class PythonDebugger
{
    public static readonly IReadOnlyList<string> Types = ["debugpy", "python"];

    public static async Task<DebugAdapter> StartAsync(DebuggerStart start)
    {
        var path = PythonEnvironment.PathFor(start.Workspace, start.Path);
        var python = start.Configuration["python"] is JsonValue own && own.TryGetValue<string>(out var given) && File.Exists(given) ? given
            : PythonEnvironment.Interpreter(start.Workspace, path)
            ?? throw new InvalidOperationException("Python was not found: install it, or make the workspace a virtual environment (python3 -m venv .venv).");
        var root = await DebugpyLocator.EnsureInstalledAsync(start.Log, start.Cancellation);
        Complete(start.Configuration, start.Workspace, python);
        var program = new ProcessStartInfo(python, ["-m", "debugpy.adapter"]) { WorkingDirectory = start.Workspace };
        program.Environment["PATH"] = path;
        program.Environment["PYTHONPATH"] = Environment.GetEnvironmentVariable("PYTHONPATH") is { Length: > 0 } pythonPath
            ? root + System.IO.Path.PathSeparator + pythonPath
            : root;
        return DebugAdapter.Start("debugpy", program);
    }

    /// <summary>
    /// A configuration as debugpy takes it, as VS Code's Python extension completes it: its own type name, the
    /// interpreter, the folder it runs in, and the Output window as the console.
    /// </summary>
    internal static void Complete(JsonObject configuration, string workspace, string python)
    {
        configuration["type"] = "debugpy";
        configuration["python"] ??= python;
        configuration["cwd"] ??= workspace;
        configuration["console"] = "internalConsole";
    }
}
