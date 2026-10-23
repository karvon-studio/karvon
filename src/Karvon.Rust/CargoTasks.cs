using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Karvon.Build;
using Karvon.Lsp;
using Karvon.Modules;

namespace Karvon.Rust;

/// <summary>
/// A Cargo package's tasks (a folder with its Cargo.toml): cargo build, check, clippy and test, their errors read from
/// cargo's short form, and cargo run of its binary, debugged with CodeLLDB.
/// </summary>
public static partial class CargoTasks
{
    public const string Source = "cargo";

    public static IReadOnlyList<WorkspaceTask> Read(string workspace)
    {
        if (!File.Exists(Path.Combine(workspace, "Cargo.toml")))
            return [];
        List<WorkspaceTask> tasks =
        [
            new("cargo: build", WorkspaceTaskKind.Build, "cargo", ["build", "--message-format=short"], workspace) { Source = Source, IsDefault = true, Problems = Problem },
            new("cargo: check", WorkspaceTaskKind.Build, "cargo", ["check", "--message-format=short"], workspace) { Source = Source, Problems = Problem },
            new("cargo: clippy", WorkspaceTaskKind.Other, "cargo", ["clippy", "--message-format=short"], workspace) { Source = Source, Problems = Problem },
            new("cargo: test", WorkspaceTaskKind.Test, "cargo", ["test"], workspace) { Source = Source, IsDefault = true },
        ];
        if (File.Exists(Path.Combine(workspace, "src", "main.rs")) || Directory.Exists(Path.Combine(workspace, "src", "bin")))
            tasks.Add(new("cargo: run", WorkspaceTaskKind.Run, "cargo", ["run"], workspace)
            {
                Source = Source,
                IsDefault = true,
                Debug = new DebugLaunch("cargo: run", new JsonObject
                {
                    ["type"] = "lldb",
                    ["request"] = "launch",
                    ["name"] = "cargo: run",
                    ["cargo"] = new JsonObject { ["args"] = new JsonArray("build") },
                    ["cwd"] = workspace,
                })
                { Source = Source },
            });
        return tasks;
    }

    [GeneratedRegex(@"^(?<file>[^\s:][^:]*\.rs):(?<line>\d+):(?<col>\d+): (?<sev>error|warning)(?:\[(?<code>[^\]]+)\])?: (?<msg>.+)$")]
    private static partial Regex ShortMessage();

    /// <summary>cargo's short form of a message: "src/main.rs:4:5: error[E0425]: cannot find value `sizes` in this scope".</summary>
    public static BuildDiagnostic? Problem(string line) => ShortMessage().Match(line) is { Success: true } m
        ? new BuildDiagnostic(m.Groups["sev"].Value == "error" ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning, m.Groups["code"].Value,
            m.Groups["msg"].Value, m.Groups["file"].Value, int.Parse(m.Groups["line"].Value), int.Parse(m.Groups["col"].Value), null)
        : null;
}
