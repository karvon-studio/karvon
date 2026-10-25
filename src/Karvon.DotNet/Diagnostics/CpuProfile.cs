using System.Globalization;
using System.Text.Json;
using Karvon.Diagnostics;

namespace Karvon.DotNet.Diagnostics;

/// <summary>
/// Record CPU Profile: the program's threads sampled for a while (dotnet-trace's cpu-sampling, as speedscope's file),
/// then its functions by the CPU spent in them and what they call (Total) and in them only (Self), as Visual Studio's
/// CPU Usage shows; the trace deleted once read.
/// </summary>
sealed class CpuProfile : ProcessDiagnostic
{
    public override string Name => "Record CPU Profile";

    public override string Description => "Where the program spends its CPU for the seconds chosen, by function (dotnet-trace).";

    public override bool IsTimed => true;

    public override bool Applies(DiagnosedProcess process) => DiagnosticsTools.IsDotNetProgram(process);

    public override async Task<DiagnosticReport> RunAsync(DiagnosticRun run, CancellationToken ct)
    {
        var tool = await DiagnosticsTools.EnsureAsync("dotnet-trace", run, ct);
        var trace = Path.Combine(Path.GetTempPath(), $"karvon-{run.Process.Pid}-{Guid.NewGuid():N}.nettrace");
        var speedscope = Path.ChangeExtension(trace, ".speedscope.json");
        try
        {
            run.Log($"Recording {run.Process.Label}'s CPU for {run.Duration.TotalSeconds:0} seconds...");
            var d = run.Duration;
            var (code, output) = await DiagnosticsTools.RunAsync(tool,
                ["collect", "-p", run.Process.Pid.ToString(CultureInfo.InvariantCulture), "--duration", $"{d.Days:00}:{d.Hours:00}:{d.Minutes:00}:{d.Seconds:00}", "--format", "speedscope", "-o", trace],
                Path.GetTempPath(), ct);
            if (code != 0 || !File.Exists(speedscope))
                throw new InvalidOperationException(DiagnosticsTools.LastLine(output));
            return Parse(await File.ReadAllTextAsync(speedscope, ct), run.Process, run.Duration);
        }
        finally
        {
            DiagnosticsTools.Delete(trace, speedscope);
        }
    }

    /// <summary>
    /// Speedscope's evented profiles (a thread each): frames opened and closed at times. A sample on the CPU ends in the
    /// CPU_TIME frame (one waiting, in UNMANAGED_CODE_TIME); the functions are the frames with a module ("module!Method"),
    /// not the process's, the threads' and the activities' that hold them.
    /// </summary>
    internal static DiagnosticReport Parse(string json, DiagnosedProcess process, TimeSpan duration)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var names = root.GetProperty("shared").GetProperty("frames").EnumerateArray().Select(f => f.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "").ToList();
        var total = new double[names.Count];
        var self = new double[names.Count];
        double cpu = 0;
        var threads = 0;
        foreach (var profile in root.GetProperty("profiles").EnumerateArray())
        {
            if (!profile.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array)
                continue;
            var stack = new List<int>();
            double? last = null;
            var used = false;
            foreach (var e in events.EnumerateArray())
            {
                var at = e.GetProperty("at").GetDouble();
                var frame = e.GetProperty("frame").GetInt32();
                if (last is { } from && stack.Count > 0 && names[stack[^1]] == "CPU_TIME" && at > from)
                {
                    var spent = at - from;
                    cpu += spent;
                    used = true;
                    var counted = new HashSet<int>();
                    var top = -1;
                    foreach (var f in stack)
                        if (IsFunction(names[f]))
                        {
                            top = f;
                            if (counted.Add(f))
                                total[f] += spent;
                        }
                    if (top >= 0)
                        self[top] += spent;
                }
                last = at;
                if (e.GetProperty("type").GetString() == "O")
                    stack.Add(frame);
                else if (stack.LastIndexOf(frame) is var i and >= 0)
                    stack.RemoveRange(i, stack.Count - i);
            }
            if (used)
                threads++;
        }
        string Percent(double ms) => cpu <= 0 ? "0%" : (ms / cpu * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";
        var rows = Enumerable.Range(0, names.Count).Where(f => total[f] > 0).OrderByDescending(f => total[f]).ThenByDescending(f => self[f]).Take(300)
            .Select(f => names[f].Split('!', 2) is [var module, var method] ? new DiagnosticRow(method, module, Percent(total[f]), Percent(self[f])) : new DiagnosticRow(names[f], "", Percent(total[f]), Percent(self[f])));
        var summary = cpu <= 0
            ? $"{process.Label} used no CPU in .NET code in the {duration.TotalSeconds:0} seconds recorded."
            : $"{cpu.ToString("N0", CultureInfo.InvariantCulture)} ms of CPU in {threads} thread{(threads == 1 ? "" : "s")} of {process.Label} over {duration.TotalSeconds:0} seconds: its functions by Total (with what they call) and Self.";
        return new DiagnosticReport($"CPU profile of {process.Label}", summary, "Function Name", "Total CPU", "Self CPU", [.. rows]);
    }

    static bool IsFunction(string name) => name.Contains('!', StringComparison.Ordinal);
}
