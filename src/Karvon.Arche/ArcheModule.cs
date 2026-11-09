using Karvon.Modules;

namespace Karvon.Arche;

/// <summary>
/// Arche: its language server (arche-lsp) in the editor of .arche files, a package's arche-driver commands as its tasks (their
/// errors in the Error List), and its test blocks in Test Explorer, with the Arche the user has. It has no debugger yet.
/// </summary>
public sealed class ArcheModule : KarvonModule
{
    public override string Id => "arche";

    public override string Name => "Arche";

    public override string Icon => "arche";

    public override string Description =>
        "The language server (arche-lsp) in the editor of .arche files: errors as you type, unsaved text included, hover, go to "
        + "definition, find references, completion, Format Document and the compiler's fixes. A package's arche-driver build and "
        + "run as its tasks, its test blocks in Test Explorer, and new packages. Arche itself is yours (on the PATH, or a build of "
        + "arche-lang at ARCHE_HOME or ~/.arche); it has no debugger yet, so F5 runs without one.";

    public override IReadOnlyList<LanguageServerInfo> LanguageServers =>
        [new("arche", "Arche language server (arche-lsp)", ArcheServer.Serves, ArcheServer.StartAsync)];

    public override IReadOnlyList<Editor.SyntaxGrammar> Grammars =>
        [new("source.arche", [".arche"], () => typeof(ArcheModule).Assembly.GetManifestResourceStream("Grammars/arche.tmLanguage.json")!)];

    public override IReadOnlyList<Views.FileIcon> FileIcons =>
        [new("arche", [".arche"], () => typeof(ArcheModule).Assembly.GetManifestResourceStream("Assets/arche.png")!)];

    public override IReadOnlyList<WorkspaceTask> WorkspaceTasks(string workspace) => ArcheTasks.Read(workspace);

    public override IReadOnlyList<string> WorkspaceTaskFiles => [ArcheTools.Manifest, ArcheTools.BuildFile];

    /// <summary>The folders of its tools when Karvon's PATH has them not (Karvon started from the desktop).</summary>
    public override IEnumerable<string> ToolDirectories => ArcheTools.DirectoriesOffPath(Environment.GetEnvironmentVariable("PATH") ?? "");

    public override IReadOnlyList<Testing.TestFramework> TestFrameworks(TestFrameworkStart start) =>
        ArcheTestFramework.IsUsedIn(start.Workspace) ? [new ArcheTestFramework(start)] : [];

    public override IReadOnlyList<Templates.ProjectTemplate> ProjectTemplates => ArcheTemplates.All;
}
