using System.Text.RegularExpressions;
using Karvon.Build;
using Karvon.Lsp;
using Karvon.Modules;

namespace Karvon.Arche;

/// <summary>
/// A package's tasks (a folder with its package.arche): arche-driver's build and run for the programs its build.arche lists, each
/// run by name. A library has no build.arche and nothing to run: its build is the compiler's check of each file at its top.
/// </summary>
public static partial class ArcheTasks
{
    public const string Source = "arche";

    public static IReadOnlyList<WorkspaceTask> Read(string workspace)
    {
        if (!File.Exists(Path.Combine(workspace, ArcheTools.Manifest)))
            return [];
        var environment = ArcheTools.ToolEnvironment(Environment.GetEnvironmentVariable("PATH") ?? "");
        WorkspaceTask Task(string name, WorkspaceTaskKind kind, bool isDefault, string program, params string[] arguments) =>
            new("arche: " + name, kind, program, arguments, workspace) { Source = Source, IsDefault = isDefault, Problems = ReadProblem, Environment = environment };
        List<WorkspaceTask> tasks = [];
        var programs = Programs(workspace);
        if (programs.Count > 0)
        {
            tasks.Add(Task("build", WorkspaceTaskKind.Build, true, ArcheTools.Driver, "build", "."));
            for (var i = 0; i < programs.Count; i++)
                tasks.Add(Task("run " + programs[i], WorkspaceTaskKind.Run, i == 0, ArcheTools.Driver, "run", ".", programs[i]));
            tasks.Add(Task("clean", WorkspaceTaskKind.Other, false, ArcheTools.Driver, "clean", "."));
        }
        else
        {
            var first = true;
            foreach (var file in LibraryFiles(workspace))
            {
                tasks.Add(Task("check " + file, WorkspaceTaskKind.Build, first, ArcheTools.Compiler, "--package", ".", "--no-main", "check", file));
                first = false;
            }
        }
        return tasks;
    }

    /// <summary>The programs build.arche lists, by name. It is Arche, read here only for its list; the compiler says what is wrong with it.</summary>
    public static IReadOnlyList<string> Programs(string workspace)
    {
        var file = Path.Combine(workspace, ArcheTools.BuildFile);
        if (!File.Exists(file))
            return [];
        try
        {
            return [.. Program().Matches(File.ReadAllText(file)).Select(m => m.Groups["name"].Value)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    static IEnumerable<string> LibraryFiles(string workspace) =>
        Directory.EnumerateFiles(workspace, "*.arche").Select(f => Path.GetFileName(f)!)
            .Where(f => f is not (ArcheTools.Manifest or ArcheTools.BuildFile)).Order(StringComparer.Ordinal);

    [GeneratedRegex("""build\.program\(\s*"(?<name>[^"]+)"\s*,""")]
    private static partial Regex Program();

    [GeneratedRegex(@"^(?<file>.+?):(?<line>\d+):(?<col>\d+): (?<sev>error|warning)\[(?<code>[^\]]+)\]: (?<msg>.+)$")]
    private static partial Regex Problem();

    /// <summary>The compiler's errors, one line each: "main.arche:5:12: error[E0301]: unknown name 'nope'". Its last line ("error: ErrorsFound") is none.</summary>
    public static BuildDiagnostic? ReadProblem(string line) =>
        Problem().Match(line) is { Success: true } m
            ? new BuildDiagnostic(m.Groups["sev"].Value == "error" ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning, m.Groups["code"].Value, m.Groups["msg"].Value,
                m.Groups["file"].Value, int.Parse(m.Groups["line"].Value), int.Parse(m.Groups["col"].Value), null)
            : null;
}
