using Karvon.Modules;

namespace Karvon.Kotlin;

/// <summary>
/// Kotlin: JetBrains' Kotlin language server (kotlin-lsp) in the editor of .kt and .kts files, by the workspace's Gradle
/// or Maven build, which it imports; its debugger for F5 of a Gradle application, and a Gradle application's template.
/// One server per workspace, the editor's and the debugger's.
/// </summary>
public sealed class KotlinModule : KarvonModule
{
    readonly Dictionary<string, KotlinServer> _servers = new(StringComparer.Ordinal);
    readonly SemaphoreSlim _starting = new(1, 1);

    public override string Id => "kotlin";

    public override string Name => "Kotlin";


    public override string Icon => "kotlin";

    public override string Description =>
        "JetBrains' Kotlin language server (kotlin-lsp) for .kt and .kts files: errors as you type, completion, hover, go to "
        + "definition, references, rename and formatting, by the folder's Gradle or Maven build, which it imports itself. F5 "
        + "debugs a Gradle application with its debugger, and Test Explorer runs and debugs the build's tests with Gradle. It "
        + "downloads the first time a Kotlin file opens (about 350 MB, with its own Java runtime); it is partly closed source, "
        + "and it sends nothing.";

    public override IReadOnlyList<LanguageServerInfo> LanguageServers =>
        [new("kotlin-lsp", "Kotlin language server (kotlin-lsp)", KotlinServer.Serves, async start => await ServerAsync(start))];

    public override IReadOnlyList<DebuggerInfo> Debuggers =>
    [
        new("kotlin-lsp", $"the Kotlin debugger (kotlin-lsp {KotlinLspLocator.Version})", ["intellij_gradle", "intellij_jvm"], async start =>
        {
            // Its "terminal" is Karvon's (runInTerminal): the program's output in Output (Debug), all of it (its Gradle says a
            // task's output once the task is done, after the program's end, which the server's internal console misses).
            start.Configuration["console"] ??= "integratedTerminal";
            var server = await ServerAsync(new LanguageServerStart(start.Workspace, start.Log, start.Cancellation));
            if (start.Configuration["request"]?.GetValue<string>() != "attach")
            {
                // A JVM launch's java: the user's JDK (JAVA_HOME's, else the PATH's), else the server's own runtime. A
                // Gradle one's is Gradle's (its toolchain), so it is not said.
                if (start.Configuration["type"]?.GetValue<string>() != "intellij_gradle")
                    start.Configuration["javaExec"] ??= JavaOf(start.Path) ?? KotlinLspLocator.BundledJava();
                else
                    await BuildAsync(start);
                await server.ResolveLaunchAsync(start.Configuration, start.Log, start.Cancellation);
            }
            return await server.DebugAdapterAsync(start.Configuration["type"]?.GetValue<string>() ?? "intellij_jvm", start.Cancellation);
        }),
    ];

    public override IReadOnlyList<WorkspaceTask> WorkspaceTasks(string workspace) => KotlinTasks.Read(workspace);

    /// <summary>A Kotlin Gradle build's tests in Test Explorer, run by its Gradle, debugged with kotlin-lsp's debugger.</summary>
    public override IReadOnlyList<Testing.TestFramework> TestFrameworks(TestFrameworkStart start) =>
        KotlinTestFramework.IsUsedIn(start.Workspace) ? [new KotlinTestFramework(start)] : [];

    public override IReadOnlyList<Templates.ProjectTemplate> ProjectTemplates => KotlinTemplates.All;

    public override IReadOnlyList<ModuleComponent> Components =>
    [
        new("kotlin-lsp", "Kotlin language server (kotlin-lsp)", KotlinLspLocator.Version, KotlinLspLocator.InstallRoot,
            () => KotlinLspLocator.Find() is not null, async (log, ct) => await KotlinLspLocator.EnsureInstalledAsync(log, ct))
        {
            ApproximateSize = 1147L << 20,
            Description = "JetBrains' Kotlin language server and debugger, from download.jetbrains.com, with its own Java runtime: "
                + "IntelliJ's open source parts (Apache-2.0) and proprietary ones of JetBrains Air and Fleet.",
        },
    ];

    /// <summary>
    /// A Gradle launch's build, first: Gradle's (its daemon, Kotlin's compiler) first start can take longer than
    /// kotlin-lsp's debugger waits for the program it launches to listen, so the launch's own build is up to date by then.
    /// </summary>
    static async Task BuildAsync(DebuggerStart start)
    {
        var folder = start.Configuration["cwd"]?.GetValue<string>() is { Length: > 0 } cwd && Directory.Exists(cwd) ? cwd : start.Workspace;
        var gradle = KotlinTasks.GradleOf(folder);
        start.Log($"> {Path.GetFileName(gradle)} classes");
        using var runner = Build.ProcessRunner.Start(gradle, ["classes", "--console=plain"], folder, start.Log,
            new Dictionary<string, string> { ["PATH"] = start.Path });
        if (await runner.WaitAsync(start.Cancellation) is var code and not 0)
            throw new InvalidOperationException($"it did not build (Gradle's exit code {code}): see Output (Debug).");
    }

    /// <summary>The user's java: JAVA_HOME's, else the tasks' PATH's; null with neither.</summary>
    static string? JavaOf(string path)
    {
        var java = OperatingSystem.IsWindows() ? "java.exe" : "java";
        if (Environment.GetEnvironmentVariable("JAVA_HOME") is { Length: > 0 } home && File.Exists(Path.Combine(home, "bin", java)))
            return Path.Combine(home, "bin", java);
        return Workspace.TaskVariables.Find("java", path, "/");
    }

    /// <summary>The workspace's server: the running one, else one started (and installed the first time).</summary>
    async Task<KotlinServer> ServerAsync(LanguageServerStart start)
    {
        await _starting.WaitAsync(start.Cancellation);
        try
        {
            if (_servers.TryGetValue(start.Workspace, out var running) && running.IsRunning)
                return running;
            var installing = KotlinLspLocator.Find() is null;
            if (installing)
                Workbench.Status($"Installing kotlin-lsp {KotlinLspLocator.Version} (about 350 MB)...");
            var server = await KotlinServer.StartAsync(start);
            if (installing)
                Workbench.Status($"kotlin-lsp {KotlinLspLocator.Version} installed.");
            _servers[start.Workspace] = server;
            return server;
        }
        finally
        {
            _starting.Release();
        }
    }
}
