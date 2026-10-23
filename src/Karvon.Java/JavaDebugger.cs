using System.Text.Json;
using System.Text.Json.Nodes;
using Karvon.Debugging;
using Karvon.Modules;

namespace Karvon.Java;

/// <summary>
/// F5 for Java: java-debug's DAP server, which jdtls starts (vscode.java.startDebugSession) and says the port of. A
/// configuration is completed as VS Code's Java debugger completes it, by jdtls: its main class (the one of the file in
/// the editor, or the first), its project, its class and module paths, the workspace built first.
/// </summary>
public static class JavaDebugger
{
    public static readonly IReadOnlyList<string> Types = ["java"];

    public static async Task<DebugAdapter> StartAsync(DebuggerStart start, JdtlsServer server)
    {
        var configuration = start.Configuration;
        var ct = start.Cancellation;
        if (configuration["mainClass"] is not JsonValue main || !main.TryGetValue<string>(out var mainClass) || mainClass.Length == 0)
        {
            var found = await server.ExecuteCommandAsync("vscode.java.resolveMainClass", [new Uri(start.Workspace).AbsoluteUri], ct);
            var candidates = found.ValueKind == JsonValueKind.Array ? found.EnumerateArray().ToList() : [];
            var file = configuration["mainFile"]?.GetValue<string>();
            var chosen = candidates.FirstOrDefault(c => file is not null && c.TryGetProperty("filePath", out var p) && p.GetString() == file);
            if (chosen.ValueKind == JsonValueKind.Undefined)
                chosen = candidates.FirstOrDefault();
            if (chosen.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("jdtls found no main class in the workspace (a class with public static void main).");
            configuration["mainClass"] = chosen.GetProperty("mainClass").GetString();
            if (chosen.TryGetProperty("projectName", out var project) && project.GetString() is { } name)
                configuration["projectName"] ??= name;
        }
        configuration.Remove("mainFile");
        var projectName = configuration["projectName"]?.GetValue<string>() ?? "";
        mainClass = configuration["mainClass"]!.GetValue<string>();
        // Built (jdtls builds as files change; this waits for it), then its paths.
        var built = await server.ExecuteCommandAsync("vscode.java.buildWorkspace", [JsonSerializer.Serialize(new { mainClass, projectName, isFullBuild = false })], ct);
        if (built.ValueKind == JsonValueKind.Number && built.GetInt32() is not (0 or 1))
            start.Log($"jdtls's build: {built.GetInt32()} (see the Error List).");
        if (configuration["classPaths"] is null)
        {
            var paths = await server.ExecuteCommandAsync("vscode.java.resolveClasspath", [mainClass, projectName], ct);
            if (paths.ValueKind == JsonValueKind.Array && paths.GetArrayLength() == 2)
            {
                configuration["modulePaths"] = JsonNode.Parse(paths[0].GetRawText());
                configuration["classPaths"] = JsonNode.Parse(paths[1].GetRawText());
            }
        }
        configuration["cwd"] ??= start.Workspace;
        configuration["console"] = "internalConsole";
        if (JavaTools.FindJava(start.Path) is { } java)
            configuration["javaExec"] ??= java;
        var port = await server.ExecuteCommandAsync("vscode.java.startDebugSession", [], ct);
        if (port.ValueKind != JsonValueKind.Number)
            throw new InvalidOperationException("jdtls did not start java-debug (its plugin may not be loaded).");
        return DebugAdapter.AtServer("java", "127.0.0.1", port.GetInt32());
    }
}
