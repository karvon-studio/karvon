using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Karvon.Debugging;
using Karvon.Lsp;
using Karvon.Modules;

namespace Karvon.Kotlin;

/// <summary>
/// kotlin-lsp for a workspace: Kotlin's files (.kt, .kts) by the workspace's Gradle or Maven build, which it imports
/// itself (Gradle by the wrapper's distribution, which it downloads), and the debugger of the programs it runs.
/// </summary>
public sealed class KotlinServer : LspClient
{
    readonly string _root;

    KotlinServer(Process process, JsonRpcConnection rpc, string root) : base(process, rpc, root) => _root = root;

    public static bool Serves(string path) => Path.GetExtension(path).ToLowerInvariant() is ".kt" or ".kts";

    public override string LanguageIdFor(string path) => Serves(path) ? "kotlin" : "plaintext";

    public bool IsRunning => !IsDisposed;

    /// <summary>
    /// A launch configuration as its adapter reads it, as kotlin-lsp's own client resolves it: an intellij_gradle one
    /// gets its Gradle target (Gradle builds and runs it) and the class path its breakpoints are scoped to; an
    /// intellij_jvm one, its module built first (the build command the server says) and the paths java runs it with.
    /// </summary>
    public async Task ResolveLaunchAsync(JsonObject configuration, Action<string> log, CancellationToken ct)
    {
        var mainClass = configuration["mainClass"]?.GetValue<string>()
            ?? throw new InvalidOperationException("its configuration has no \"mainClass\".");
        var uri = configuration["file"]?.GetValue<string>() is { Length: > 0 } file
            ? ToUri(file)
            : Str(await ExecuteCommandAsync("intellij.java.resolveClassDocument", [new { fqn = mainClass }], ct), "uri")
                ?? throw new InvalidOperationException($"kotlin-lsp found no file of {mainClass}.");
        if (configuration["type"]?.GetValue<string>() == "intellij_gradle")
        {
            var launch = await ExecuteCommandAsync("intellij.java.resolveBuildToolLaunch", [new { uri, mainClass }], ct);
            if (Str(launch, "tool") is null)
                throw new InvalidOperationException($"no build tool launches {mainClass}; an \"intellij_jvm\" configuration does.");
            // What the configuration does not say, it leaves out (the server reads no null).
            var target = new JsonObject { ["uri"] = uri };
            if (Str(launch, "moduleName") is { } moduleName)
                target["moduleName"] = moduleName;
            foreach (var (field, from) in new[] { ("projectPath", "projectPath"), ("sourceSet", "sourceSet"), ("toolArgs", "gradleArgs") })
                if (configuration[from] is { } value)
                    target[field] = value.DeepClone();
            configuration["buildToolTarget"] = target;
            configuration["classPaths"] = Strings(launch, "scopeClassPaths");
            return;
        }

        // A JVM launch builds nothing itself: the module is built first, by the command the server says, when it says one.
        var build = await ExecuteCommandAsync("intellij.java.resolveBuildCommand", [new { uri }], ct);
        if (build.ValueKind == JsonValueKind.Object && build.TryGetProperty("supported", out var supported) && supported.ValueKind == JsonValueKind.True
            && Strings(build, "command").Select(a => a!.GetValue<string>()).ToList() is [var program, .. var args])
        {
            log($"> {string.Join(' ', [program, .. args])}");
            using var runner = Build.ProcessRunner.Start(program, args, Str(build, "cwd") ?? _root, log);
            if (await runner.WaitAsync(ct) is var code and not 0)
                throw new InvalidOperationException($"its build failed (exit code {code}).");
        }
        var paths = await ExecuteCommandAsync("intellij.java.resolveLaunch", [new
        {
            uri,
            cwd = configuration["cwd"]?.GetValue<string>() ?? _root,
            overrides = new JsonObject([.. new[] { "classPaths", "modulePaths", "moduleName", "javaExec", "vmArgs" }
                .Where(f => configuration[f] is not null).Select(f => KeyValuePair.Create(f, configuration[f]!.DeepClone()))]),
        }], ct);
        configuration["classPaths"] = Strings(paths, "classpath");
        configuration["modulePaths"] = Strings(paths, "modulePath");
        configuration["moduleContentPaths"] = Strings(paths, "moduleContentPaths");
        if (Str(paths, "moduleName") is { } module)
            configuration["moduleName"] = module;
        if (Str(paths, "workingDirectory") is { } cwd)
            configuration["cwd"] = cwd;
        if (Str(paths, "javaExec") is { } java)
            configuration["javaExec"] = java;
        if (paths.ValueKind == JsonValueKind.Object && paths.TryGetProperty("vmArgs", out var vmArgs) && vmArgs.ValueKind == JsonValueKind.Array)
            configuration["vmArgs"] = Strings(paths, "vmArgs");
    }

    static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static JsonArray Strings(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? new JsonArray([.. v.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.String).Select(s => (JsonNode?)s.GetString())])
            : [];

    /// <summary>
    /// Its debugger (a DAP server it starts for the workspace, on a port of its own), for a configuration's type
    /// (intellij_gradle, intellij_jvm), which the adapter knows its launches by.
    /// </summary>
    public async Task<DebugAdapter> DebugAdapterAsync(string type, CancellationToken ct)
    {
        var port = await ExecuteCommandAsync("start_debug_server", [ToUri(_root)], ct);
        if (port.ValueKind != JsonValueKind.Number)
            throw new InvalidOperationException("kotlin-lsp did not start its debugger.");
        return DebugAdapter.AtServer(type, "127.0.0.1", port.GetInt32());
    }

    public static async Task<KotlinServer> StartAsync(LanguageServerStart start)
    {
        var launcher = await KotlinLspLocator.EnsureInstalledAsync(start.Log, start.Cancellation);
        Directory.CreateDirectory(KotlinLspLocator.SystemPath);
        var (process, rpc) = Launch(new ProcessStartInfo(launcher, ["--stdio", $"--system-path={KotlinLspLocator.SystemPath}", "--data-sharing=none"])
        {
            WorkingDirectory = start.Workspace,
        });
        var server = new KotlinServer(process, rpc, start.Workspace);
        try
        {
            await server.InitializeAsync(start.Workspace, null, start.Cancellation);
        }
        catch
        {
            await server.DisposeAsync();
            throw;
        }
        return server;
    }
}
