using Karvon.Modules;

namespace Karvon.Jwc;

/// <summary>
/// JWC: the compiler's language server (jwc lsp) in the editor of .jwc files, a project's jwc commands as its tasks (their
/// errors in the Error List), and jwc test's tests in Test Explorer, with the jwc the user has. It has no debugger.
/// </summary>
public sealed class JwcModule : KarvonModule
{
    public override string Id => "jwc";

    public override string Name => "JWC";

    public override string Icon => "jwc";

    public override string Description =>
        "The compiler's language server (jwc lsp) in the editor of .jwc files: errors as you type, hover with a query's SQL, go to "
        + "definition, completion and parameter info. A project's jwc check, lint, build, serve and run as its tasks, jwc test's tests "
        + "in Test Explorer, and jwc new's templates. JWC itself is yours (its installer puts it in ~/.jwc/bin); it has no debugger, "
        + "so F5 runs without one.";

    public override IReadOnlyList<LanguageServerInfo> LanguageServers =>
        [new("jwc", "JWC language server (jwc lsp)", JwcServer.Serves, JwcServer.StartAsync)];

    public override IReadOnlyList<Editor.SyntaxGrammar> Grammars =>
        [new("source.jwc", [".jwc"], () => typeof(JwcModule).Assembly.GetManifestResourceStream("Grammars/jwc.tmLanguage.json")!)];

    public override IReadOnlyList<Views.FileIcon> FileIcons =>
        [new("jwc", [".jwc"], () => typeof(JwcModule).Assembly.GetManifestResourceStream("Assets/jwc.png")!)];

    public override IReadOnlyList<WorkspaceTask> WorkspaceTasks(string workspace) => JwcTasks.Read(workspace);

    public override IReadOnlyList<string> WorkspaceTaskFiles => [JwcTools.Manifest];

    /// <summary>jwc's folder when Karvon's PATH has none (Karvon started from the desktop).</summary>
    public override IEnumerable<string> ToolDirectories => JwcTools.DirectoryOffPath() is { } jwc ? [jwc] : [];

    public override IReadOnlyList<Testing.TestFramework> TestFrameworks(TestFrameworkStart start) =>
        JwcTestFramework.IsUsedIn(start.Workspace) ? [new JwcTestFramework(start)] : [];

    public override IReadOnlyList<Templates.ProjectTemplate> ProjectTemplates => JwcTemplates.All;
}
