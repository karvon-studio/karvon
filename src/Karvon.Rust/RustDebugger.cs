using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Karvon.Build;
using Karvon.Debugging;
using Karvon.Modules;
using Karvon.Workspace;

namespace Karvon.Rust;

/// <summary>
/// F5 for Rust (and C and C++ built with debug information): CodeLLDB's adapter on stdio, for launch.json's lldb
/// configurations. Its "cargo" (what to build) is the VS Code extension's, not the adapter's: Karvon builds with cargo
/// first and debugs the program cargo says it built.
/// </summary>
public static class RustDebugger
{
    public static async Task<DebugAdapter> StartAsync(DebuggerStart start)
    {
        var path = RustTools.PathWithCargo(start.Path);
        if (start.Configuration["cargo"] is JsonObject cargo)
            start.Configuration["program"] = await BuildAsync(cargo, start.Workspace, path, start.Log, start.Cancellation);
        start.Configuration.Remove("cargo");
        start.Configuration["cwd"] ??= start.Workspace;
        return await Lldb.CodeLldb.StartAsync(start.Workspace, path, start.Log, start.Cancellation);
    }

    /// <summary>
    /// cargo's build (its args, as CodeLLDB's "cargo" has them), read as JSON: the executable it made (the one its filter
    /// names when there are several), as the extension finds it.
    /// </summary>
    internal static async Task<string> BuildAsync(JsonObject cargo, string workspace, string path, Action<string> log, CancellationToken ct)
    {
        var args = cargo["args"] is JsonArray given ? given.Select(a => a!.GetValue<string>()).ToList() : ["build"];
        if (!args.Any(a => a.StartsWith("--message-format", StringComparison.Ordinal)))
            args.Add("--message-format=json");
        var cargoExe = TaskVariables.Find(OperatingSystem.IsWindows() ? "cargo.exe" : "cargo", path, workspace)
            ?? throw new InvalidOperationException("cargo was not found: install Rust with rustup (rustup.rs).");
        var filter = cargo["filter"] as JsonObject;
        var executables = new List<(string Path, string Name, string Kind)>();
        log("> cargo " + string.Join(' ', args));
        using (var runner = ProcessRunner.Start(cargoExe, args, workspace, line =>
        {
            if (!line.StartsWith('{'))
            {
                log(line);
                return;
            }
            try
            {
                var message = JsonNode.Parse(line)!;
                if (message["reason"]?.GetValue<string>() == "compiler-message" && message["message"]?["rendered"]?.GetValue<string>() is { } rendered)
                    log(rendered.TrimEnd());
                if (message["reason"]?.GetValue<string>() == "compiler-artifact" && message["executable"]?.GetValue<string>() is { } exe)
                    executables.Add((exe, message["target"]?["name"]?.GetValue<string>() ?? "", message["target"]?["kind"]?[0]?.GetValue<string>() ?? ""));
            }
            catch (JsonException)
            {
                log(line);
            }
        }, new Dictionary<string, string> { ["PATH"] = path }))
        {
            if (await runner.WaitAsync(ct) != 0)
                throw new InvalidOperationException("cargo did not build it (see Output, Debug).");
        }
        var chosen = executables.Where(e => filter is null
            || (filter["name"]?.GetValue<string>() is not { } name || e.Name == name) && (filter["kind"]?.GetValue<string>() is not { } kind || e.Kind == kind)).ToList();
        return chosen.Count switch
        {
            1 => chosen[0].Path,
            0 => throw new InvalidOperationException("cargo built no program to debug."),
            _ => throw new InvalidOperationException($"cargo built {chosen.Count} programs ({string.Join(", ", chosen.Select(e => e.Name))}): a filter in \"cargo\" names the one to debug."),
        };
    }
}
