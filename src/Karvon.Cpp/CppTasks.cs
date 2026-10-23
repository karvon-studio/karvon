using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Karvon.Build;
using Karvon.Lsp;
using Karvon.Modules;
using Karvon.Workspace;

namespace Karvon.Cpp;

/// <summary>
/// A C or C++ folder's tasks: CMake's (configure into build/ with its compile commands for clangd, build, ctest, and
/// each executable it adds, run and debugged), or a Makefile's (make, its test or check target, clean). Their errors
/// are read as GCC and Clang write them, and as MSVC does.
/// </summary>
public static partial class CppTasks
{
    public const string Source = "cpp";

    /// <summary>CMake's configure for Karvon's build folder: a debug build, with the compile commands clangd reads.</summary>
    static string Configure => "cmake -S . -B build -DCMAKE_BUILD_TYPE=Debug -DCMAKE_EXPORT_COMPILE_COMMANDS=ON";

    public static IReadOnlyList<WorkspaceTask> Read(string workspace)
    {
        if (File.Exists(Path.Combine(workspace, "CMakeLists.txt")))
            return CMake(workspace);
        if (File.Exists(Path.Combine(workspace, "Makefile")) || File.Exists(Path.Combine(workspace, "makefile")))
            return Make(workspace);
        return [];
    }

    static WorkspaceTask Shell(string name, WorkspaceTaskKind kind, string command, string workspace)
    {
        var (program, arguments) = TaskVariables.Shell(command);
        return new WorkspaceTask(name, kind, program, arguments, workspace) { Source = Source, Problems = Problem };
    }

    static List<WorkspaceTask> CMake(string workspace)
    {
        // Configured as it builds, as CMake Tools does: quick once it is.
        List<WorkspaceTask> tasks =
        [
            Shell("cmake: build", WorkspaceTaskKind.Build, $"{Configure} && cmake --build build", workspace) with { IsDefault = true },
            Shell("cmake: configure", WorkspaceTaskKind.Other, Configure, workspace),
            Shell("ctest", WorkspaceTaskKind.Test, $"{Configure} && cmake --build build && ctest --test-dir build --output-on-failure", workspace) with { IsDefault = true },
        ];
        var executables = AddExecutable().Matches(File.ReadAllText(Path.Combine(workspace, "CMakeLists.txt"))).Select(m => m.Groups[1].Value).Distinct().ToList();
        foreach (var name in executables)
        {
            var program = Path.Combine(workspace, "build", OperatingSystem.IsWindows() ? name + ".exe" : name);
            tasks.Add(Shell($"cmake: run {name}", WorkspaceTaskKind.Run, $"{Configure} && cmake --build build && {TaskVariables.QuoteForShell(program)}", workspace) with
            {
                IsDefault = name == executables[0],
                Debug = new DebugLaunch($"cmake: run {name}", new JsonObject
                {
                    ["type"] = "lldb",
                    ["request"] = "launch",
                    ["name"] = $"cmake: run {name}",
                    ["program"] = program,
                    ["cwd"] = workspace,
                    ["preLaunchTask"] = "cmake: build",
                })
                { Source = Source },
            });
        }
        return tasks;
    }

    [GeneratedRegex(@"^\s*add_executable\s*\(\s*([A-Za-z_][\w.-]*)", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex AddExecutable();

    static List<WorkspaceTask> Make(string workspace)
    {
        var makefile = File.Exists(Path.Combine(workspace, "Makefile")) ? Path.Combine(workspace, "Makefile") : Path.Combine(workspace, "makefile");
        var targets = MakeTarget().Matches(File.ReadAllText(makefile)).Select(m => m.Groups[1].Value).ToHashSet();
        List<WorkspaceTask> tasks = [new("make", WorkspaceTaskKind.Build, "make", [], workspace) { Source = Source, IsDefault = true, Problems = Problem }];
        foreach (var test in new[] { "test", "check" }.Where(targets.Contains))
            tasks.Add(new($"make {test}", WorkspaceTaskKind.Test, "make", [test], workspace) { Source = Source, IsDefault = tasks.All(t => t.Kind != WorkspaceTaskKind.Test), Problems = Problem });
        if (targets.Contains("clean"))
            tasks.Add(new("make clean", WorkspaceTaskKind.Other, "make", ["clean"], workspace) { Source = Source });
        return tasks;
    }

    [GeneratedRegex(@"^([A-Za-z][\w.-]*)\s*:(?!=)", RegexOptions.Multiline)]
    private static partial Regex MakeTarget();

    [GeneratedRegex(@"^(?<file>[^\s:][^:]*?):(?<line>\d+):(?<col>\d+): (?<sev>fatal error|error|warning): (?<msg>.+?)(?: \[(?<code>-W[\w-]+)\])?$")]
    private static partial Regex GccMessage();

    /// <summary>
    /// GCC's and Clang's form of a message ("src/main.c:5:3: error: use of undeclared identifier 'x'"), else MSBuild's and
    /// MSVC's (file(line,col): error C2065: ...).
    /// </summary>
    public static BuildDiagnostic? Problem(string line) => GccMessage().Match(line) is { Success: true } m
        ? new BuildDiagnostic(m.Groups["sev"].Value == "warning" ? DiagnosticSeverity.Warning : DiagnosticSeverity.Error, m.Groups["code"].Value,
            m.Groups["msg"].Value, m.Groups["file"].Value, int.Parse(m.Groups["line"].Value), int.Parse(m.Groups["col"].Value), null)
        : BuildOutputParser.Parse(line);
}
