using System.Globalization;

namespace VSL.Servers;

/// <summary>A disk of a machine: where it is mounted, its size and how much is used, in bytes.</summary>
public sealed record ServerDisk(string Mount, long Size, long Used)
{
    public double UsedRatio => Size > 0 ? (double)Used / Size : 0;
}

/// <summary>A machine's state: its system, how long it has run, its load, CPU, memory and disks.</summary>
public sealed record ServerMetrics(string System, TimeSpan? Uptime, double[] Load, int Cpus, double? CpuPercent, long MemoryTotal, long MemoryAvailable,
    long SwapTotal, long SwapFree, IReadOnlyList<ServerDisk> Disks)
{
    public long MemoryUsed => Math.Max(MemoryTotal - MemoryAvailable, 0);
}

/// <summary>A systemd service: its unit, whether it is loaded, active and running, and what it is.</summary>
public sealed record ServerService(string Unit, string Load, string Active, string Sub, string Description)
{
    public string Name => Unit.EndsWith(".service", StringComparison.Ordinal) ? Unit[..^8] : Unit;

    public bool IsRunning => Active == "active" && Sub == "running";

    public bool IsFailed => Active == "failed";

    public string State => $"{Active} ({Sub})";
}

/// <summary>The commands VSL runs on a machine, and how it reads what they print.</summary>
public static class RemoteCommands
{
    /// <summary>A word for the remote shell: single-quoted, with its quotes escaped.</summary>
    public static string Quote(string word) => "'" + word.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    /// <summary>One command for the Overview: each part after a marker line, so a missing tool leaves its part empty.</summary>
    public const string Metrics =
        "echo '@os'; (. /etc/os-release 2>/dev/null && echo \"$PRETTY_NAME\") || uname -sr; "
        + "echo '@uptime'; cat /proc/uptime 2>/dev/null; "
        + "echo '@load'; cat /proc/loadavg 2>/dev/null; "
        + "echo '@cpus'; nproc 2>/dev/null; "
        + "echo '@mem'; grep -E '^(MemTotal|MemAvailable|SwapTotal|SwapFree):' /proc/meminfo 2>/dev/null; "
        + "echo '@disk'; df -kP -x tmpfs -x devtmpfs -x squashfs -x overlay 2>/dev/null | tail -n +2; "
        + "echo '@cpu'; head -1 /proc/stat 2>/dev/null; sleep 0.5; head -1 /proc/stat 2>/dev/null";

    public static ServerMetrics ParseMetrics(string output)
    {
        var parts = new Dictionary<string, List<string>>();
        List<string>? current = null;
        foreach (var line in output.Split('\n').Select(l => l.TrimEnd('\r')))
        {
            if (line.StartsWith('@') && line.Length > 1 && !line.Contains(' '))
                parts[line[1..]] = current = [];
            else if (line.Length > 0)
                current?.Add(line);
        }
        List<string> Part(string name) => parts.GetValueOrDefault(name) ?? [];
        double Number(string text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 0;

        TimeSpan? uptime = Part("uptime").FirstOrDefault()?.Split(' ') is [var seconds, ..] && Number(seconds) > 0 ? TimeSpan.FromSeconds(Number(seconds)) : null;
        var load = Part("load").FirstOrDefault()?.Split(' ').Take(3).Select(Number).ToArray() ?? [];
        var cpus = int.TryParse(Part("cpus").FirstOrDefault(), out var n) ? n : 0;
        var memory = Part("mem").Select(l => l.Split(':', 2)).Where(p => p.Length == 2)
            .ToDictionary(p => p[0], p => (long)Number(p[1].Trim().Split(' ')[0]) * 1024);
        var disks = Part("disk").Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Where(p => p.Length >= 6)
            .Select(p => new ServerDisk(p[5], (long)Number(p[1]) * 1024, (long)Number(p[2]) * 1024))
            .Where(d => d.Size > 0).DistinctBy(d => d.Mount).ToList();
        double? cpu = null;
        if (Part("cpu") is [var before, var after] && Ticks(before) is { } a && Ticks(after) is { } b && b.Total > a.Total)
            cpu = 100.0 * (1 - (double)(b.Idle - a.Idle) / (b.Total - a.Total));
        return new ServerMetrics(Part("os").FirstOrDefault() ?? "", uptime, load, cpus, cpu, memory.GetValueOrDefault("MemTotal"), memory.GetValueOrDefault("MemAvailable"),
            memory.GetValueOrDefault("SwapTotal"), memory.GetValueOrDefault("SwapFree"), disks);

        static (long Total, long Idle)? Ticks(string line)
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 5 || fields[0] != "cpu")
                return null;
            var values = fields.Skip(1).Select(f => long.TryParse(f, out var v) ? v : 0).ToArray();
            // idle and iowait are idle time.
            return (values.Sum(), values[3] + (values.Length > 4 ? values[4] : 0));
        }
    }

    public const string Services = "systemctl list-units --type=service --all --no-pager --no-legend --plain";

    public static IReadOnlyList<ServerService> ParseServices(string output) =>
        output.Split('\n').Select(l => l.Trim().TrimStart('●', '*', ' ')).Where(l => l.Length > 0)
            .Select(l => l.Split(' ', 5, StringSplitOptions.RemoveEmptyEntries))
            .Where(p => p.Length >= 4 && p[0].Contains('.'))
            .Select(p => new ServerService(p[0], p[1], p[2], p[3], p.Length > 4 ? p[4].Trim() : ""))
            .OrderBy(s => s.IsFailed ? 0 : s.IsRunning ? 1 : 2).ThenBy(s => s.Name, StringComparer.Ordinal)
            .ToList();

    /// <summary>systemctl start / stop / restart, through sudo without a password prompt (sudo -n says when it needs one).</summary>
    public static string ServiceAction(string action, string unit) => $"sudo -n systemctl {action} {Quote(unit)}";

    /// <summary>The last lines of a unit's journal, the whole journal, or a log file; following it with <paramref name="follow"/>.</summary>
    public static string Logs(string? unit, string? file, int lines, bool follow = false)
    {
        lines = Math.Clamp(lines, 1, 10_000);
        if (file is { Length: > 0 })
            return $"tail -n {lines}{(follow ? " -F" : "")} {Quote(file)}";
        return $"journalctl{(unit is { Length: > 0 } ? " -u " + Quote(unit) : "")} -n {lines} --no-pager{(follow ? " -f" : "")}";
    }

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 40 => $"{bytes / (double)(1L << 40):0.0} TB",
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0.0} KB",
        _ => $"{bytes} B",
    };

    public static string Duration(TimeSpan span) => span.TotalDays >= 1 ? $"{(int)span.TotalDays} d {span.Hours} h"
        : span.TotalHours >= 1 ? $"{span.Hours} h {span.Minutes} min" : $"{Math.Max(span.Minutes, 1)} min";
}
