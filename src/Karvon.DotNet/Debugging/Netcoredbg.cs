using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Karvon.Build;
using Karvon.Debugging;
using Karvon.Lsp;

namespace Karvon.DotNet.Debugging;

/// <summary>A .NET program to launch under netcoredbg: what runs, with what, where.</summary>
public sealed record LaunchRequest(
    string Program,
    IReadOnlyList<string> Args,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment);

/// <summary>netcoredbg's sessions: a .NET program launched under it, or a running .NET process attached to.</summary>
public static class Netcoredbg
{
    /// <summary>Launches a .NET program under netcoredbg.</summary>
    public static Task<DebugSession> LaunchAsync(
        string adapterPath,
        LaunchRequest launch,
        IReadOnlyDictionary<string, IReadOnlyList<int>> breakpoints,
        Action<string, IReadOnlyList<BreakpointResult>> onBreakpointsVerified,
        Action<DebugSession>? beforeConfigurationDone,
        CancellationToken ct) =>
        DebugSession.StartAsync(Adapter(adapterPath), "launch", Json(new
        {
            name = "Karvon Launch",
            type = "coreclr",
            request = "launch",
            program = launch.Program,
            args = launch.Args,
            cwd = launch.WorkingDirectory,
            env = launch.Environment,
            stopAtEntry = false,
            justMyCode = true,
            enableStepFiltering = true,
            console = "internalConsole",
        }), breakpoints, onBreakpointsVerified, beforeConfigurationDone, ct);

    /// <summary>Attaches to a running .NET process (used to debug tests: vstest's testhost waits for us).</summary>
    public static Task<DebugSession> AttachAsync(
        string adapterPath,
        int processId,
        IReadOnlyDictionary<string, IReadOnlyList<int>> breakpoints,
        Action<string, IReadOnlyList<BreakpointResult>> onBreakpointsVerified,
        Action<DebugSession>? beforeConfigurationDone,
        CancellationToken ct) =>
        DebugSession.StartAsync(Adapter(adapterPath), "attach", Json(new
        {
            name = "Karvon Attach",
            type = "coreclr",
            request = "attach",
            processId,
            justMyCode = true,
            enableStepFiltering = true,
        }), breakpoints, onBreakpointsVerified, beforeConfigurationDone, ct);

    /// <summary>netcoredbg, on its stdin and stdout.</summary>
    public static DebugAdapter Adapter(string adapterPath)
    {
        var psi = new ProcessStartInfo(adapterPath);
        psi.ArgumentList.Add("--interpreter=vscode");
        DotnetCli.ApplyEnvironment(psi);
        return DebugAdapter.Start("coreclr", psi);
    }

    static JsonObject Json(object value) => JsonSerializer.SerializeToNode(value, value.GetType(), JsonRpcConnection.JsonOptions)!.AsObject();
}
