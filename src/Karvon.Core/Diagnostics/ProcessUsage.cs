using System.Diagnostics;
using Karvon.Services;

namespace Karvon.Diagnostics;

/// <summary>A process tree's CPU time and memory at a moment.</summary>
public readonly record struct UsageSample(DateTime At, TimeSpan Cpu, long Memory);

/// <summary>
/// The program's processes (the one Karvon started and those under it: dotnet run's app, npm's node) and what they use,
/// read from the system (/proc on Linux, ps's on macOS; the process itself on Windows).
/// </summary>
public static class ProcessUsage
{
    public static IReadOnlyList<DiagnosedProcess> Processes(int root)
    {
        var all = new List<DiagnosedProcess>();
        foreach (var pid in new[] { root }.Concat(ProcessTree.Descendants(root).Select(m => m.Pid)))
            if (Describe(pid) is { } process)
                all.Add(process);
        return all;
    }

    static DiagnosedProcess? Describe(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.HasExited ? null : new DiagnosedProcess(pid, process.ProcessName, ProcessTree.CommandLineOf(pid) ?? process.ProcessName);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Their CPU time and working sets, summed (one gone meanwhile counts for nothing).</summary>
    public static UsageSample Sample(IEnumerable<int> pids)
    {
        var cpu = TimeSpan.Zero;
        long memory = 0;
        foreach (var pid in pids)
            try
            {
                using var process = Process.GetProcessById(pid);
                cpu += process.TotalProcessorTime;
                memory += process.WorkingSet64;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
            }
        return new UsageSample(DateTime.UtcNow, cpu, memory);
    }

    /// <summary>The CPU used between two samples, as a share of all the processors (as Visual Studio's graph shows it).</summary>
    public static double CpuPercent(UsageSample before, UsageSample after)
    {
        var wall = (after.At - before.At).TotalMilliseconds * Environment.ProcessorCount;
        return wall <= 0 ? 0 : Math.Clamp((after.Cpu - before.Cpu).TotalMilliseconds / wall * 100, 0, 100);
    }
}
