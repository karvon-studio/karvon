using System.Diagnostics;
using System.Text.Json.Nodes;
using Karvon.Debugging;
using Karvon.Modules;

namespace Karvon.Go;

/// <summary>F5 for Go: Delve's DAP server (dlv dap) for launch.json's go configurations, completed as VS Code's Go extension completes them.</summary>
public static class GoDebugger
{
    public static readonly IReadOnlyList<string> Types = ["go"];

    public static async Task<DebugAdapter> StartAsync(DebuggerStart start)
    {
        var dlv = await GoTools.EnsureDelveAsync(start.Log, start.Cancellation);
        Complete(start.Configuration, start.Workspace);
        var program = new ProcessStartInfo(dlv, ["dap", "--listen=127.0.0.1:0"]) { WorkingDirectory = start.Workspace };
        program.Environment["PATH"] = GoTools.PathWithGo(start.Path);
        return await DebugAdapter.StartServerAsync("go", program, start.Cancellation);
    }

    /// <summary>
    /// A configuration as Delve takes it: "auto" is the Go extension's (debug, or test for a test file), the program the
    /// folder when it has none, and the binary it builds out of the folder (Delve's own default writes it there).
    /// </summary>
    internal static void Complete(JsonObject configuration, string workspace)
    {
        var program = configuration["program"] is JsonValue p && p.TryGetValue<string>(out var given) ? given : workspace;
        configuration["program"] = program;
        if (configuration["mode"] is not JsonValue m || !m.TryGetValue<string>(out var mode) || mode == "auto")
            configuration["mode"] = program.EndsWith("_test.go", StringComparison.Ordinal) ? "test" : "debug";
        if (configuration["request"]?.GetValue<string>() == "launch")
            configuration["output"] ??= Path.Combine(Path.GetTempPath(), $"karvon-dlv-{Guid.NewGuid():N}");
        configuration["cwd"] ??= Directory.Exists(program) ? program : workspace;
    }
}
