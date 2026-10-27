using System.Diagnostics;
using System.Text.Json.Nodes;
using Karvon.Debugging;
using Karvon.Modules;
using Karvon.Workspace;

namespace Karvon.TypeScript;

/// <summary>
/// F5 for JavaScript and TypeScript: js-debug's DAP server on Node.js, for launch.json's node, chrome and msedge
/// configurations and the package.json scripts (a script runs under it as VS Code's npm debugging does: every node process
/// it starts is a child session, so a dev server's code stops at its breakpoints too).
/// </summary>
public static class JsDebugger
{
    /// <summary>The launch.json types it debugs: VS Code's names and js-debug's own.</summary>
    public static readonly IReadOnlyList<string> Types = ["node", "pwa-node", "chrome", "pwa-chrome", "msedge", "pwa-msedge"];

    /// <summary>
    /// A package.json script under the debugger: its package manager runs it (bun is no Node.js, so it has none); one
    /// that reloads by itself (a dev server) gets no Hot Reload agent.
    /// </summary>
    public static DebugLaunch? ForScript(string name, string manager, string script, string directory, bool reloadsItself = false) => manager == "bun" ? null : new(name, new JsonObject
    {
        [ReloadsItselfKey] = reloadsItself,
        ["type"] = "pwa-node",
        ["request"] = "launch",
        ["name"] = name,
        ["runtimeExecutable"] = manager,
        ["runtimeArgs"] = new JsonArray("run", script),
        ["cwd"] = directory,
        // What npm prints, and the processes it starts (their console.log as well), as the terminal would show it.
        ["outputCapture"] = "std",
        // npm's own code is no one's to step through, nor are its source maps (which it does not ship) to read.
        ["skipFiles"] = new JsonArray("<node_internals>/**", "**/node_modules/**"),
        ["resolveSourceMapLocations"] = new JsonArray("${workspaceFolder}/**", "!**/node_modules/**"),
    })
    { Source = PackageScripts.Source };

    /// <summary>A package.json script's configuration says it reloads by itself (a key js-debug leaves alone, as its own __workspaceFolder).</summary>
    const string ReloadsItselfKey = "__karvonReloadsItself";

    /// <param name="hotReload">Debug > Enable Hot Reload: a Node.js program it launches gets the Hot Reload agent.</param>
    public static async Task<DebugAdapter> StartAsync(DebuggerStart start, bool hotReload = false)
    {
        var node = TaskVariables.Find(OperatingSystem.IsWindows() ? "node.exe" : "node", start.Path, start.Workspace)
            ?? throw new InvalidOperationException("Node.js was not found: the JavaScript debugger runs on it (install it from nodejs.org, or with nvm).");
        var server = await JsDebugLocator.EnsureInstalledAsync(start.Log, start.Cancellation);
        var reloadsItself = start.Configuration[ReloadsItselfKey]?.GetValue<bool>() == true;
        Complete(start.Configuration, start.Workspace);
        if (hotReload && !reloadsItself)
            AddHotReload(start.Configuration, start.Workspace);
        var program = new ProcessStartInfo(node) { WorkingDirectory = start.Workspace };
        program.ArgumentList.Add(server);
        // Any free port, on this machine only.
        program.ArgumentList.Add("0");
        program.ArgumentList.Add("127.0.0.1");
        // The program it launches (node, npm) is found in the tasks' PATH, as in the user's terminal.
        program.Environment["PATH"] = start.Path;
        return await DebugAdapter.StartServerAsync("pwa-node", program, start.Cancellation);
    }

    /// <summary>
    /// A configuration as js-debug's server takes it: its own type names (VS Code maps node to pwa-node), the folder its
    /// defaults are relative to (cwd, outFiles, webRoot: ${workspaceFolder}, which VS Code would give it), and the Output
    /// window as the console (Karvon has no terminal to give it).
    /// </summary>
    /// <summary>
    /// The Hot Reload agent in a Node.js program it launches (not one it attaches to, nor a browser's page): in its env's
    /// NODE_OPTIONS, which js-debug adds its own to.
    /// </summary>
    internal static void AddHotReload(JsonObject configuration, string workspace)
    {
        if (configuration["type"]?.GetValue<string>() != "pwa-node" || (configuration["request"]?.GetValue<string>() ?? "launch") != "launch")
            return;
        var env = configuration["env"] as JsonObject ?? new JsonObject();
        var options = env["NODE_OPTIONS"]?.GetValue<string>() ?? Environment.GetEnvironmentVariable("NODE_OPTIONS");
        if (NodeHotReload.Environment(workspace, options) is not { } variables)
            return;
        foreach (var (name, value) in variables)
            env[name] = value;
        configuration["env"] = env;
    }

    internal static void Complete(JsonObject configuration, string workspace)
    {
        configuration.Remove(ReloadsItselfKey);
        var type = configuration["type"]?.GetValue<string>();
        if (type is "node" or "chrome" or "msedge")
            configuration["type"] = "pwa-" + type;
        configuration["__workspaceFolder"] = workspace;
        configuration["console"] = "internalConsole";
    }
}
