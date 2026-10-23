using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Karvon.Build;
using Karvon.Lsp;
using Karvon.Modules;

namespace Karvon.Go;

/// <summary>A Go module's tasks (a folder with its go.mod): go build, test and vet over its packages, and go run of its main package.</summary>
public static partial class GoTasks
{
    public const string Source = "go";

    public static IReadOnlyList<WorkspaceTask> Read(string workspace)
    {
        if (!File.Exists(Path.Combine(workspace, "go.mod")))
            return [];
        List<WorkspaceTask> tasks =
        [
            new("go: build", WorkspaceTaskKind.Build, "go", ["build", "./..."], workspace) { Source = Source, IsDefault = true, Problems = Problem },
            new("go: test", WorkspaceTaskKind.Test, "go", ["test", "./..."], workspace) { Source = Source, IsDefault = true, Problems = Problem },
            new("go: vet", WorkspaceTaskKind.Other, "go", ["vet", "./..."], workspace) { Source = Source, Problems = Problem },
        ];
        if (IsMain(workspace))
            tasks.Add(new("go: run", WorkspaceTaskKind.Run, "go", ["run", "."], workspace)
            {
                Source = Source,
                IsDefault = true,
                Debug = new DebugLaunch("go: run", new JsonObject
                {
                    ["type"] = "go",
                    ["request"] = "launch",
                    ["name"] = "go: run",
                    ["mode"] = "debug",
                    ["program"] = workspace,
                })
                { Source = Source },
            });
        return tasks;
    }

    /// <summary>Whether the folder's own package is a program (package main).</summary>
    static bool IsMain(string folder) => Directory.EnumerateFiles(folder, "*.go")
        .Where(f => !f.EndsWith("_test.go", StringComparison.Ordinal))
        .Any(f => PackageMain().IsMatch(File.ReadAllText(f)));

    [GeneratedRegex(@"^package\s+main\b", RegexOptions.Multiline)]
    private static partial Regex PackageMain();

    [GeneratedRegex(@"^(?:# \S+\s*)?(?<file>[^\s:#][^:]*\.go):(?<line>\d+):(?:(?<col>\d+):)?\s*(?<msg>.+)$")]
    private static partial Regex GoError();

    /// <summary>Go's form of an error (the compiler's, vet's, a test's): "./main.go:12:5: undefined: x".</summary>
    public static BuildDiagnostic? Problem(string line) => GoError().Match(line) is { Success: true } m
        ? new BuildDiagnostic(DiagnosticSeverity.Error, "", m.Groups["msg"].Value.Trim(), m.Groups["file"].Value, int.Parse(m.Groups["line"].Value),
            m.Groups["col"].Success ? int.Parse(m.Groups["col"].Value) : 1, null)
        : null;
}
