using System.Globalization;
using System.Text.RegularExpressions;
using Karvon.Diagnostics;

namespace Karvon.DotNet.Diagnostics;

/// <summary>
/// Take Memory Snapshot: the program's GC heap written by dotnet-gcdump, its types counted (dotnet-gcdump report), the
/// largest first; the dump deleted once read.
/// </summary>
sealed partial class MemorySnapshot : ProcessDiagnostic
{
    public override string Name => "Take Memory Snapshot";

    public override string Description => "What is on the program's GC heap now, by type (dotnet-gcdump). The program pauses for a moment while it is written.";

    public override bool Applies(DiagnosedProcess process) => DiagnosticsTools.IsDotNetProgram(process);

    public override async Task<DiagnosticReport> RunAsync(DiagnosticRun run, CancellationToken ct)
    {
        var tool = await DiagnosticsTools.EnsureAsync("dotnet-gcdump", run, ct);
        var file = Path.Combine(Path.GetTempPath(), $"karvon-{run.Process.Pid}-{Guid.NewGuid():N}.gcdump");
        try
        {
            run.Log($"Taking a memory snapshot of {run.Process.Label}...");
            var (code, output) = await DiagnosticsTools.RunAsync(tool, ["collect", "-p", run.Process.Pid.ToString(CultureInfo.InvariantCulture), "-o", file], Path.GetTempPath(), ct);
            if (code != 0 || !File.Exists(file))
                throw new InvalidOperationException(DiagnosticsTools.LastLine(output));
            var (reported, report) = await DiagnosticsTools.RunAsync(tool, ["report", file], Path.GetTempPath(), ct);
            if (reported != 0)
                throw new InvalidOperationException(DiagnosticsTools.LastLine(report));
            return Parse(report, run.Process);
        }
        finally
        {
            DiagnosticsTools.Delete(file);
        }
    }

    [GeneratedRegex(@"^\s*([\d,]+)\s+([\d,]+)\s+(\S.*?)\s*$")]
    private static partial Regex TypeLine();

    [GeneratedRegex(@"^(.*?)\s+\[([^\]]+)\]$")]
    private static partial Regex ModuleSuffix();

    /// <summary>
    /// dotnet-gcdump report's table: "1,163,002  GC Heap bytes", "20,925  GC Heap objects", then after "Object Bytes
    /// Count  Type" a line a type: "262,168  1  System.String[] (Bytes > 100K)  [System.Private.CoreLib.dll]".
    /// </summary>
    internal static DiagnosticReport Parse(string report, DiagnosedProcess process)
    {
        long bytes = 0, objects = 0;
        var rows = new List<(long Size, DiagnosticRow Row)>();
        var inTable = false;
        foreach (var raw in report.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Contains("GC Heap bytes", StringComparison.Ordinal))
                bytes = Number(line.Trim().Split(' ')[0]);
            else if (line.Contains("GC Heap objects", StringComparison.Ordinal))
                objects = Number(line.Trim().Split(' ')[0]);
            else if (line.TrimStart().StartsWith("Object Bytes", StringComparison.Ordinal))
                inTable = true;
            else if (inTable && TypeLine().Match(line) is { Success: true } m)
            {
                var (type, module) = ModuleSuffix().Match(m.Groups[3].Value) is { Success: true } s ? (s.Groups[1].Value, s.Groups[2].Value) : (m.Groups[3].Value, "");
                var size = Number(m.Groups[1].Value);
                rows.Add((size, new DiagnosticRow(type, module, Number(m.Groups[2].Value).ToString("N0", CultureInfo.InvariantCulture), size.ToString("N0", CultureInfo.InvariantCulture))));
            }
        }
        if (!inTable)
            throw new InvalidOperationException("dotnet-gcdump's report had no table: " + DiagnosticsTools.LastLine(report));
        return new DiagnosticReport($"Memory snapshot of {process.Label}",
            $"{bytes.ToString("N0", CultureInfo.InvariantCulture)} bytes in {objects.ToString("N0", CultureInfo.InvariantCulture)} objects on {process.Label}'s GC heap ({DateTime.Now:HH:mm:ss}), by type, the largest first.",
            "Object Type", "Count", "Size (Bytes)",
            [.. rows.OrderByDescending(r => r.Size).Select(r => r.Row)]);
    }

    static long Number(string text) => long.TryParse(text.Replace(",", "", StringComparison.Ordinal), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
}
