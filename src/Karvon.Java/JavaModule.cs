using Karvon.Modules;

namespace Karvon.Java;

/// <summary>
/// Java: jdtls in the editor of .java files, java-debug for F5 (both in the one jdtls of each workspace), and a Maven or
/// Gradle project's builds as its tasks (their errors in the Error List), with the JDK the user has.
/// </summary>
public sealed class JavaModule : KarvonModule
{
    /// <summary>The jdtls of each workspace: the editor's and F5's, one for both (two would share its data folder).</summary>
    readonly Dictionary<string, JdtlsServer> _servers = new(StringComparer.Ordinal);
    readonly SemaphoreSlim _starting = new(1, 1);

    public override string Id => "java";

    public override string Name => "Java";

    public override string Description =>
        "jdtls, Eclipse's Java language server, in the editor of .java files (errors as you type, completion, hover, go to "
        + "definition, references, rename), with a Maven or Gradle project or the files alone; F5 with java-debug; Maven's "
        + "and Gradle's builds and tests as a folder's tasks. jdtls and java-debug download the first time they are needed; "
        + "they run on the JDK the user has (21 or newer). JUnit's tests in Test Explorer, run and debugged.";

    public override IReadOnlyList<LanguageServerInfo> LanguageServers =>
        [new("jdtls", "Java language server (jdtls)", JdtlsServer.Serves, async start => await ServerAsync(start))];

    public override IReadOnlyList<WorkspaceTask> WorkspaceTasks(string workspace) => JavaTasks.Read(workspace);

    /// <summary>JUnit's tests in Test Explorer, with the workspace's jdtls (its build and class path).</summary>
    public override IReadOnlyList<Testing.TestFramework> TestFrameworks(TestFrameworkStart start) =>
        JUnitFramework.IsUsedIn(start.Workspace) ? [new JUnitFramework(start, ct => ServerAsync(new LanguageServerStart(start.Workspace, start.Log, ct)))] : [];

    public override IReadOnlyList<Templates.ProjectTemplate> ProjectTemplates => JavaTemplates.All;

    public override IReadOnlyList<string> WorkspaceTaskFiles => ["pom.xml", "build.gradle", "build.gradle.kts"];

    /// <summary>A Maven or Gradle project's Dependencies in Solution Explorer.</summary>
    public override Packages.PackageSource? PackagesIn(string folder) =>
        new[] { "pom.xml", "build.gradle.kts", "build.gradle" }.Any(f => File.Exists(Path.Combine(folder, f))) ? new MavenPackages(folder) : null;

    /// <summary>A Maven or Gradle project's Manage Maven Packages...: Maven Central's, into its pom.xml or build.gradle.</summary>
    public override IReadOnlyList<ModuleMenuItem> ExplorerMenu(Workspace.ExplorerNode node) =>
        (FolderWith(node, "pom.xml") ?? FolderWith(node, "build.gradle.kts") ?? FolderWith(node, "build.gradle")) is { } dir
            ? [new($"Manage {(File.Exists(Path.Combine(dir, "pom.xml")) ? "Maven" : "Gradle")} Packages...", () => Workbench.ManagePackagesAsync(new MavenPackages(dir))) { After = "Add" }]
            : [];

    public override IReadOnlyList<DebuggerInfo> Debuggers =>
        [new("java-debug", $"java-debug {JavaTools.JavaDebugVersion}", JavaDebugger.Types,
            async start => await JavaDebugger.StartAsync(start, await ServerAsync(new LanguageServerStart(start.Workspace, start.Log, start.Cancellation))))];

    public override IReadOnlyList<ModuleComponent> Components =>
    [
        new("jdtls", "Java language server (jdtls)", JavaTools.JdtlsVersion, JavaTools.JdtlsRoot, () => JavaTools.FindJdtls() is not null, JavaTools.EnsureJdtlsAsync)
        {
            Description = "jdtls (EPL-2.0), Eclipse's Java language server, from download.eclipse.org; it runs on the JDK.",
        },
        new("java-debug", "Java debugger (java-debug)", JavaTools.JavaDebugVersion, JavaTools.JavaDebugRoot, () => JavaTools.FindJavaDebug() is not null, JavaTools.EnsureJavaDebugAsync)
        {
            Description = "java-debug's plugin for jdtls (EPL-1.0), from VS Code's Java debugger (MIT) on Open VSX.",
        },
        new("junit", "JUnit console launcher", JavaTools.JUnitConsoleVersion, JavaTools.JUnitConsoleRoot, () => JavaTools.FindJUnitConsole() is not null, JavaTools.EnsureJUnitConsoleAsync)
        {
            Description = "JUnit 5's console launcher (EPL-2.0), from Maven Central: Test Explorer runs JUnit 5 and 4 tests with it.",
        },
    ];

    /// <summary>The workspace's jdtls: the one running, else a new one (installed the first time).</summary>
    async Task<JdtlsServer> ServerAsync(LanguageServerStart start)
    {
        await _starting.WaitAsync(start.Cancellation);
        try
        {
            if (_servers.TryGetValue(start.Workspace, out var running) && running.IsRunning)
                return running;
            var installing = JavaTools.FindJdtls() is null;
            if (installing)
                Workbench.Status($"Installing jdtls {JavaTools.JdtlsVersion}...");
            var server = await JdtlsServer.StartAsync(start);
            if (installing)
                Workbench.Status($"jdtls {JavaTools.JdtlsVersion} installed.");
            return _servers[start.Workspace] = server;
        }
        finally
        {
            _starting.Release();
        }
    }

    public override async ValueTask DisposeAsync()
    {
        foreach (var server in _servers.Values)
            await server.DisposeAsync();
        _servers.Clear();
        await base.DisposeAsync();
    }
}
