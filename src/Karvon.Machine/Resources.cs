using System.Globalization;
using System.Net.NetworkInformation;

namespace Karvon.Machine;

/// <summary>The CPU the whole machine used since the last look (percent), and each core's.</summary>
public sealed record CpuUsage(double Total, IReadOnlyList<double> Cores);

/// <summary>Memory and swap, in bytes: "used" is what is not free to be taken (Linux's MemAvailable left out).</summary>
public sealed record MemoryUsage(long Total, long Used, long SwapTotal, long SwapUsed);

/// <summary>A file system's space, where it is mounted; its label where the system gives it (Windows).</summary>
public sealed record DiskUsage(string Mount, string Format, long Total, long Free, string? Label = null)
{
    public long Used => Total - Free;

    public double Share => Total > 0 ? 100.0 * Used / Total : 0;
}

/// <summary>A network interface's traffic: since the last look (per second), and in all.</summary>
public sealed record NetworkUsage(string Name, long ReceivedPerSecond, long SentPerSecond, long Received, long Sent);

/// <summary>What the machine uses now.</summary>
public sealed record ResourceUsage(CpuUsage Cpu, MemoryUsage Memory, IReadOnlyList<DiskUsage> Disks, IReadOnlyList<NetworkUsage> Network, TimeSpan Uptime)
{
    public static ResourceUsage None { get; } = new(new CpuUsage(0, []), new MemoryUsage(0, 0, 0, 0), [], [], TimeSpan.Zero);
}

/// <summary>
/// The machine's CPU, memory, disks and network, looked at again and again (what is per second is since the look before).
/// Linux reads /proc (stat, meminfo); elsewhere the CPU is what the processes used, the memory what they hold.
/// </summary>
public sealed class ResourceSampler
{
    long[]? _cpu;
    long[][]? _cores;
    Dictionary<string, (long Received, long Sent)> _network = [];
    DateTime _at;

    /// <summary>File systems that are no disk: memory's, the kernel's, a snap's or a container's layers.</summary>
    static readonly HashSet<string> Pseudo = new(StringComparer.OrdinalIgnoreCase)
    {
        "tmpfs", "devtmpfs", "squashfs", "overlay", "proc", "sysfs", "cgroup", "cgroup2", "devpts", "mqueue", "debugfs", "tracefs",
        "securityfs", "pstore", "bpf", "configfs", "fusectl", "hugetlbfs", "autofs", "binfmt_misc", "efivarfs", "ramfs", "nsfs", "fuse.portal",
        "fuse.gvfsd-fuse", "rpc_pipefs", "tracefs", "zram",
    };

    public ResourceUsage Sample(IReadOnlyList<ProcessInfo> processes)
    {
        var now = DateTime.UtcNow;
        var seconds = _at == default ? 0 : (now - _at).TotalSeconds;
        var usage = new ResourceUsage(Cpu(processes), Memory(processes), Disks(), Network(seconds), TimeSpan.FromMilliseconds(Environment.TickCount64));
        _at = now;
        return usage;
    }

    CpuUsage Cpu(IReadOnlyList<ProcessInfo> processes)
    {
        if (!OperatingSystem.IsLinux() || Read("/proc/stat") is not { } stat)
            return new CpuUsage(Math.Min(100, processes.Sum(p => p.Cpu)), []);
        var lines = stat.Split('\n').Where(l => l.StartsWith("cpu", StringComparison.Ordinal)).ToList();
        var total = Ticks(lines[0]);
        var cores = lines.Skip(1).Select(Ticks).ToArray();
        var share = _cpu is null ? 0 : Busy(_cpu, total);
        var shares = _cores is { } before && before.Length == cores.Length ? cores.Select((c, i) => Busy(before[i], c)).ToList() : [.. cores.Select(_ => 0.0)];
        _cpu = total;
        _cores = cores;
        return new CpuUsage(share, shares);
    }

    /// <summary>A /proc/stat cpu line's times: user nice system idle iowait irq softirq steal...</summary>
    static long[] Ticks(string line) =>
        [.. line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(8).Select(t => long.TryParse(t, CultureInfo.InvariantCulture, out var n) ? n : 0)];

    /// <summary>The share of the time between two looks that was not idle (idle and iowait).</summary>
    internal static double Busy(long[] before, long[] now)
    {
        if (before.Length < 5 || now.Length < 5)
            return 0;
        var all = now.Sum() - before.Sum();
        var idle = now[3] + now[4] - before[3] - before[4];
        return all <= 0 ? 0 : Math.Clamp(100.0 * (all - idle) / all, 0, 100);
    }

    static MemoryUsage Memory(IReadOnlyList<ProcessInfo> processes)
    {
        if (OperatingSystem.IsLinux() && Read("/proc/meminfo") is { } info)
        {
            var values = info.Split('\n')
                .Select(l => l.Split(':'))
                .Where(p => p.Length == 2)
                .ToDictionary(p => p[0].Trim(), p => long.TryParse(p[1].Replace("kB", "", StringComparison.Ordinal).Trim(), CultureInfo.InvariantCulture, out var kb) ? kb * 1024 : 0);
            var total = values.GetValueOrDefault("MemTotal");
            var swap = values.GetValueOrDefault("SwapTotal");
            return new MemoryUsage(total, total - values.GetValueOrDefault("MemAvailable"), swap, swap - values.GetValueOrDefault("SwapFree"));
        }
        var all = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return new MemoryUsage(all, Math.Min(all, processes.Sum(p => p.Memory)), 0, 0);
    }

    static List<DiskUsage> Disks()
    {
        List<DiskUsage> disks = [];
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.TotalSize <= 0 || drive.DriveType is DriveType.Ram or DriveType.CDRom || Pseudo.Contains(drive.DriveFormat)
                    || drive.Name.StartsWith("/snap/", StringComparison.Ordinal) || drive.Name.StartsWith("/var/lib/docker/", StringComparison.Ordinal)
                    || drive.Name.StartsWith("/run/", StringComparison.Ordinal) || drive.Name.StartsWith("/sys/", StringComparison.Ordinal)
                    || drive.Name.StartsWith("/proc/", StringComparison.Ordinal) || drive.Name.StartsWith("/dev/", StringComparison.Ordinal))
                    continue;
                disks.Add(new DiskUsage(drive.Name, drive.DriveFormat, drive.TotalSize, drive.AvailableFreeSpace,
                    OperatingSystem.IsWindows() && drive.VolumeLabel is { Length: > 0 } label ? label : null));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return disks;
    }

    List<NetworkUsage> Network(double seconds)
    {
        List<NetworkUsage> network = [];
        Dictionary<string, (long, long)> now = [];
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                // A container's end of its virtual cable (Docker's veth) is the container's, not the machine's.
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback
                    || nic.Name.StartsWith("veth", StringComparison.Ordinal))
                    continue;
                var stats = nic.GetIPStatistics();
                var (received, sent) = (stats.BytesReceived, stats.BytesSent);
                now[nic.Name] = (received, sent);
                if (received + sent == 0)
                    continue;
                var (inBefore, outBefore) = _network.TryGetValue(nic.Name, out var before) ? before : (received, sent);
                network.Add(new NetworkUsage(nic.Name,
                    seconds > 0 ? (long)Math.Max(0, (received - inBefore) / seconds) : 0,
                    seconds > 0 ? (long)Math.Max(0, (sent - outBefore) / seconds) : 0, received, sent));
            }
        }
        catch (NetworkInformationException)
        {
        }
        _network = now;
        return [.. network.OrderByDescending(n => n.ReceivedPerSecond + n.SentPerSecond).ThenByDescending(n => n.Received + n.Sent)];
    }

    static string? Read(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>"512 MB", "8.1 GB".</summary>
    public static string Bytes(long bytes) => bytes switch
    {
        >= 1L << 40 => $"{bytes / (double)(1L << 40):0.0} TB",
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0} KB",
        _ => $"{bytes} B",
    };
}
