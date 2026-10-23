using Karvon.Modules;

namespace Karvon.Cpp;

/// <summary>
/// C and C++: clangd in the editor, CodeLLDB for F5, and a CMake or Make folder's builds as its tasks (their errors in
/// the Error List), with the compilers and build tools the user has.
/// </summary>
public sealed class CppModule : KarvonModule
{
    public override string Id => "cpp";

    public override string Name => "C and C++";

    public override string Description =>
        "clangd in the editor of C and C++ files (errors as you type, completion, hover, go to definition, references, rename), "
        + "CodeLLDB for F5, and a CMake folder's configure, build, ctest and run (or a Makefile's make, test and clean) as its "
        + "tasks. clangd and CodeLLDB download the first time they are needed; the compilers are the user's.";

    public override IReadOnlyList<LanguageServerInfo> LanguageServers =>
        [new("clangd", "C and C++ language server (clangd)", ClangdServer.Serves, StartAsync)];

    public override IReadOnlyList<WorkspaceTask> WorkspaceTasks(string workspace) => CppTasks.Read(workspace);

    public override IReadOnlyList<Templates.ProjectTemplate> ProjectTemplates => CppTemplates.All;

    public override IReadOnlyList<string> WorkspaceTaskFiles => ["CMakeLists.txt", "Makefile", "makefile"];

    /// <summary>Test Explorer: CTest's tests, in a CMake folder that enables testing.</summary>
    public override IReadOnlyList<Testing.TestFramework> TestFrameworks(TestFrameworkStart start) =>
        CTestFramework.IsUsedIn(start.Workspace) ? [new CTestFramework(start)] : [];

    /// <summary>F5: CodeLLDB, for launch.json's lldb configurations (when the Rust module, which brings it too, is off).</summary>
    public override IReadOnlyList<DebuggerInfo> Debuggers =>
        [new("codelldb", $"CodeLLDB {Lldb.CodeLldb.Version}", Lldb.CodeLldb.Types,
            start => Lldb.CodeLldb.StartAsync(start.Workspace, start.Path, start.Log, start.Cancellation))];

    public override IReadOnlyList<ModuleComponent> Components =>
    [
        new("clangd", "C and C++ language server (clangd)", ClangdLocator.Version, ClangdLocator.InstallRoot,
            () => ClangdLocator.Find() is not null, ClangdLocator.EnsureInstalledAsync)
        {
            Description = "clangd (Apache-2.0 with LLVM's exception), LLVM's language server, from its GitHub release.",
        },
        Lldb.CodeLldb.Component,
    ];

    async Task<Lsp.LspClient> StartAsync(LanguageServerStart start)
    {
        var installing = ClangdLocator.Find() is null;
        if (installing)
            Workbench.Status($"Installing clangd {ClangdLocator.Version}...");
        var server = await ClangdServer.StartAsync(start);
        if (installing)
            Workbench.Status($"clangd {ClangdLocator.Version} installed.");
        return server;
    }
}
