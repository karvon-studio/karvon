using Karvon.Modules;

namespace Karvon.Rust;

/// <summary>
/// Rust: rust-analyzer in the editor of .rs files, CodeLLDB for F5, and a Cargo package's build, check, clippy, test and
/// run as its tasks (their errors in the Error List), with the cargo the user has.
/// </summary>
public sealed class RustModule : KarvonModule
{
    public override string Id => "rust";

    public override string Name => "Rust";

    public override string Description =>
        "rust-analyzer in the editor of .rs files (errors as you type, completion, hover, go to definition, references, rename), "
        + "CodeLLDB for F5, a Cargo package's build, check, clippy, test and run as its tasks, and cargo test's tests in Test "
        + "Explorer. rust-analyzer and CodeLLDB "
        + "download the first time they are needed (Rust itself is the user's, from rustup).";

    public override IReadOnlyList<LanguageServerInfo> LanguageServers =>
        [new("rust-analyzer", "Rust language server (rust-analyzer)", RustAnalyzerServer.Serves, StartAsync)];

    public override IReadOnlyList<WorkspaceTask> WorkspaceTasks(string workspace) => CargoTasks.Read(workspace);

    public override IReadOnlyList<Templates.ProjectTemplate> ProjectTemplates => RustTemplates.All;

    public override IReadOnlyList<string> WorkspaceTaskFiles => ["Cargo.toml"];

    /// <summary>A Cargo package's Manage Cargo Packages...: crates.io's crates, by cargo add and cargo remove.</summary>
    public override IReadOnlyList<ModuleMenuItem> ExplorerMenu(Workspace.ExplorerNode node) =>
        FolderWith(node, "Cargo.toml") is { } dir
            ? [new("Manage Cargo Packages...", () => Workbench.ManagePackagesAsync(new CratesPackages(dir))) { After = "Add" }]
            : [];

    /// <summary>cargo's folder when Karvon's PATH has none (Karvon started from the desktop).</summary>
    public override IEnumerable<string> ToolDirectories => RustTools.DirectoryOffPath() is { } cargo ? [cargo] : [];

    /// <summary>Test Explorer: cargo test's tests, in a Cargo package.</summary>
    public override IReadOnlyList<Testing.TestFramework> TestFrameworks(TestFrameworkStart start) =>
        CargoTestFramework.IsUsedIn(start.Workspace) ? [new CargoTestFramework(start)] : [];

    public override IReadOnlyList<DebuggerInfo> Debuggers =>
        [new("codelldb", $"CodeLLDB {Lldb.CodeLldb.Version}", Lldb.CodeLldb.Types, RustDebugger.StartAsync)];

    public override IReadOnlyList<ModuleComponent> Components =>
    [
        new("rust-analyzer", "Rust language server (rust-analyzer)", RustTools.RustAnalyzerVersion, RustTools.RustAnalyzerRoot,
            () => RustTools.FindRustAnalyzer() is not null, RustTools.EnsureRustAnalyzerAsync)
        {
            Description = "rust-analyzer (MIT or Apache-2.0), from its GitHub release.",
        },
        Lldb.CodeLldb.Component,
    ];

    async Task<Lsp.LspClient> StartAsync(LanguageServerStart start)
    {
        var installing = RustTools.FindRustAnalyzer() is null;
        if (installing)
            Workbench.Status($"Installing rust-analyzer {RustTools.RustAnalyzerVersion}...");
        var server = await RustAnalyzerServer.StartAsync(start);
        if (installing)
            Workbench.Status($"rust-analyzer {RustTools.RustAnalyzerVersion} installed.");
        return server;
    }
}
