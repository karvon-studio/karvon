using System.Text.RegularExpressions;
using Karvon.Build;
using Karvon.Lsp;
using Karvon.Modules;

namespace Karvon.Jwc;

/// <summary>
/// A JWC project's tasks (a folder with its jwcproj.json). Its build is jwc check, as serve and run compile it themselves;
/// jwc build makes the native binary, and needs Rust. A package ("type": "pkg") does not run.
/// </summary>
public static partial class JwcTasks
{
    public const string Source = "jwc";

    public static IReadOnlyList<WorkspaceTask> Read(string workspace)
    {
        if (!File.Exists(Path.Combine(workspace, JwcTools.Manifest)))
            return [];
        WorkspaceTask Task(string name, WorkspaceTaskKind kind, bool isDefault, params string[] arguments) =>
            new("jwc: " + name, kind, "jwc", arguments, workspace) { Source = Source, IsDefault = isDefault, ProblemReader = NewProblemReader };
        List<WorkspaceTask> tasks =
        [
            Task("check", WorkspaceTaskKind.Build, true, "check"),
            Task("build", WorkspaceTaskKind.Build, false, "build"),
            Task("lint", WorkspaceTaskKind.Other, false, "lint"),
            Task("fmt", WorkspaceTaskKind.Other, false, "fmt"),
            Task("test", WorkspaceTaskKind.Test, true, "test"),
        ];
        if (JwcTools.ReadManifest(workspace).Type != "pkg")
        {
            tasks.Add(Task("serve", WorkspaceTaskKind.Run, true, "serve", "--dev", "--request-logging"));
            tasks.Add(Task("run", WorkspaceTaskKind.Run, false, "run", "--dev", "--request-logging"));
        }
        return tasks;
    }

    [GeneratedRegex(@"^(?<sev>error|warning)\[(?<code>[^\]]+)\]: (?<msg>.+)$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^\s*--> (?<file>.+):(?<line>\d+):(?<col>\d+)\s*$")]
    private static partial Regex Place();

    /// <summary>
    /// A reader of jwc's errors, which take two lines: "error[E0211]: unknown name `missing`", then "  --> ./src/app.jwc:5:16".
    /// Its last line ("Error: 1 error in 6 files"), and what an error says below those two, are not errors of their own.
    /// </summary>
    public static Func<string, BuildDiagnostic?> NewProblemReader()
    {
        (DiagnosticSeverity Severity, string Code, string Message)? heading = null;
        return line =>
        {
            if (Heading().Match(line) is { Success: true } h)
            {
                heading = (h.Groups["sev"].Value == "error" ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning, h.Groups["code"].Value, h.Groups["msg"].Value);
                return null;
            }
            if (heading is not { } error)
                return null;
            heading = null;
            return Place().Match(line) is { Success: true } m
                ? new BuildDiagnostic(error.Severity, error.Code, error.Message, m.Groups["file"].Value, int.Parse(m.Groups["line"].Value), int.Parse(m.Groups["col"].Value), null)
                : null;
        };
    }
}
