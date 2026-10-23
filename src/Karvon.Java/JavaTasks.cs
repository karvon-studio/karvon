using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Karvon.Build;
using Karvon.Lsp;
using Karvon.Modules;

namespace Karvon.Java;

/// <summary>
/// A Java folder's tasks: Maven's (its wrapper when it has one: compile, test, package) or Gradle's (build, test, and
/// run for an application), their errors read as javac writes them through each; and the main class, run and debugged.
/// </summary>
public static partial class JavaTasks
{
    public const string Source = "java";

    public static IReadOnlyList<WorkspaceTask> Read(string workspace)
    {
        var tasks = new List<WorkspaceTask>();
        if (File.Exists(Path.Combine(workspace, "pom.xml")))
        {
            var mvn = Wrapper(workspace, "mvnw") ?? "mvn";
            tasks.Add(new("maven: compile", WorkspaceTaskKind.Build, mvn, ["-q", "compile"], workspace) { Source = Source, IsDefault = true, Problems = Problem });
            tasks.Add(new("maven: test", WorkspaceTaskKind.Test, mvn, ["test"], workspace) { Source = Source, IsDefault = true, Problems = Problem });
            tasks.Add(new("maven: package", WorkspaceTaskKind.Other, mvn, ["package"], workspace) { Source = Source, Problems = Problem });
        }
        else if (new[] { "build.gradle", "build.gradle.kts", "settings.gradle", "settings.gradle.kts" }.Any(f => File.Exists(Path.Combine(workspace, f))))
        {
            var gradle = Wrapper(workspace, "gradlew") ?? "gradle";
            tasks.Add(new("gradle: build", WorkspaceTaskKind.Build, gradle, ["build", "-x", "test"], workspace) { Source = Source, IsDefault = true, Problems = Problem });
            tasks.Add(new("gradle: test", WorkspaceTaskKind.Test, gradle, ["test"], workspace) { Source = Source, IsDefault = true, Problems = Problem });
            if (new[] { "build.gradle", "build.gradle.kts" }.Select(f => Path.Combine(workspace, f)).Where(File.Exists).Any(f => File.ReadAllText(f).Contains("application", StringComparison.Ordinal)))
                tasks.Add(new("gradle: run", WorkspaceTaskKind.Run, gradle, ["run"], workspace) { Source = Source, Problems = Problem });
        }
        if (tasks.Count > 0 || Directory.EnumerateFiles(workspace, "*.java", SearchOption.TopDirectoryOnly).Any() || Directory.Exists(Path.Combine(workspace, "src")))
        {
            // The main class, found by jdtls when it starts (that of the file in the editor first), under java-debug.
            tasks.Add(new("java: run main", WorkspaceTaskKind.Run, "java", ["${file}"], workspace)
            {
                Source = Source,
                IsDefault = tasks.All(t => t.Kind != WorkspaceTaskKind.Run),
                Debug = new DebugLaunch("java: run main", new JsonObject
                {
                    ["type"] = "java",
                    ["request"] = "launch",
                    ["name"] = "java: run main",
                    ["mainFile"] = "${file}",
                    ["cwd"] = workspace,
                })
                { Source = Source },
            });
        }
        return tasks;
    }

    static string? Wrapper(string workspace, string name)
    {
        var path = Path.Combine(workspace, OperatingSystem.IsWindows() ? name + ".cmd" : name);
        return File.Exists(path) ? path : null;
    }

    [GeneratedRegex(@"^\[(?<sev>ERROR|WARNING)\] (?<file>.+?\.java):\[(?<line>\d+),(?<col>\d+)\] (?<msg>.+)$")]
    private static partial Regex MavenMessage();

    [GeneratedRegex(@"^(?<file>[^\s:][^:]*?\.java):(?<line>\d+): (?<sev>error|warning): (?<msg>.+)$")]
    private static partial Regex JavacMessage();

    /// <summary>javac's errors as Maven ("[ERROR] /w/App.java:[12,5] cannot find symbol") and Gradle ("/w/App.java:12: error: ...") write them.</summary>
    public static BuildDiagnostic? Problem(string line)
    {
        if (MavenMessage().Match(line) is { Success: true } m)
            return new BuildDiagnostic(m.Groups["sev"].Value == "ERROR" ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning, "", m.Groups["msg"].Value,
                m.Groups["file"].Value, int.Parse(m.Groups["line"].Value), int.Parse(m.Groups["col"].Value), null);
        if (JavacMessage().Match(line) is { Success: true } j)
            return new BuildDiagnostic(j.Groups["sev"].Value == "error" ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning, "", j.Groups["msg"].Value,
                j.Groups["file"].Value, int.Parse(j.Groups["line"].Value), 1, null);
        return null;
    }
}
