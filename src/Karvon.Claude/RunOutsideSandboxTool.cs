using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Karvon.Claude;

/// <summary>
/// <c>run_outside_sandbox</c>: one shell command outside Karvon's strict sandbox, for what the sandbox keeps from the shell
/// and none of Karvon's tools does (a file of the user's outside the project, a program that needs the user's keys or
/// sockets). It always asks: the card shows the command and why, and no rule lets it through (destructive). Its
/// environment has none of Karvon's secrets; what it prints has its secrets marked, as every tool's output.
/// </summary>
public sealed class RunOutsideSandboxTool(Func<string?> workspace, Func<bool> strict) : IClaudeTool
{
    const int MaxOutput = 30_000;

    public string Name => "run_outside_sandbox";

    public string Description =>
        "Runs one shell command outside Karvon's strict sandbox, with the user's own access, after the user says yes on a card that shows "
        + "it (every time). Only for what the sandbox keeps from the shell and no tool of Karvon's does: a file of the user's outside "
        + "the project, a program that needs the user's keys or sockets. Not for building, tests, git, packages, databases, containers "
        + "or documents: Karvon's tools do those. Say in why what the command needs that the sandbox does not have.";

    public JsonObject InputSchema => JsonNode.Parse("""
        {"type":"object","properties":{
          "command":{"type":"string","description":"The bash command, run in the project's folder"},
          "why":{"type":"string","description":"What it needs outside the sandbox, for the user"}},
         "required":["command","why"]}
        """)!.AsObject();

    public ClaudeToolPlan Plan(JsonElement input) =>
        new(ToolAccess.Destructive, "Outside the sandbox", Str(input, "command"), "Run this command outside Karvon's sandbox, with your own access?")
        {
            Code = Str(input, "command"),
            Warning = "It can read and change any of your files and use your keys and sign-ins. Why: " + (Str(input, "why") ?? "(not said)"),
        };

    public string? Refusal(JsonElement input) =>
        !strict() ? "Karvon's strict sandbox is off: run the command with the shell (Bash)."
        : workspace() is null ? "No folder is open in Karvon."
        : null;

    public async Task<ClaudeToolOutput> CallAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var command = Str(input, "command") is { Length: > 0 } c ? c : null;
        if (command is null || workspace() is not { } folder)
            return ClaudeToolOutput.Error("Give the command to run.");
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("powershell.exe") { ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", command } }
            : new ProcessStartInfo("/bin/bash") { ArgumentList = { "-c", command } };
        start.WorkingDirectory = folder;
        start.RedirectStandardOutput = start.RedirectStandardError = start.RedirectStandardInput = true;
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        ClaudeEnvironment.Clean(start.Environment, []);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The shell did not start.");
        process.StandardInput.Close();
        var output = new StringBuilder();
        void Add(string? line)
        {
            if (line is not null)
                lock (output)
                    if (output.Length < MaxOutput)
                        output.AppendLine(line);
        }
        process.OutputDataReceived += (_, e) => Add(e.Data);
        process.ErrorDataReceived += (_, e) => Add(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            if (cancellationToken.IsCancellationRequested)
                throw;
            return ClaudeToolOutput.Error(output + "\n(Stopped: it ran for ten minutes.)");
        }
        await process.WaitForExitAsync(CancellationToken.None);
        var text = output.ToString().TrimEnd();
        return process.ExitCode == 0 ? new ClaudeToolOutput(text) : ClaudeToolOutput.Error((text.Length > 0 ? text + "\n" : "") + $"Exit code {process.ExitCode}");
    }

    static string? Str(JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
