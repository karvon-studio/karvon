using System.Diagnostics;
using System.Globalization;

namespace Karvon.Machine;

/// <summary>
/// A process of this machine: its id and parent's, name, command line and user (where they can be read), the memory it
/// holds (its working set) and the CPU it used since the last look (as a share of the whole machine).
/// </summary>
public sealed record ProcessInfo(int Pid, int? ParentPid, string Name, string? CommandLine, string? User, long Memory, double Cpu);

/// <summary>
/// The machine's processes, looked at again and again: what each used of the CPU is what it used since the look before
/// (the first look says 0). .NET's own reading for all of them; on Linux /proc adds the parent, the user and the command
/// line, on macOS ps adds the parent and the user.
/// </summary>
public sealed class ProcessSampler
{
    Dictionary<int, TimeSpan> _cpu = [];
    DateTime _at;

    public IReadOnlyList<ProcessInfo> Sample()
    {
        var now = DateTime.UtcNow;
        var elapsed = _at == default ? TimeSpan.Zero : now - _at;
        var cores = Environment.ProcessorCount;
        var extra = OperatingSystem.IsMacOS() ? FromPs() : [];
        var users = OperatingSystem.IsLinux() ? LinuxUsers() : [];
        Dictionary<int, TimeSpan> cpu = [];
        List<ProcessInfo> processes = [];
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                int pid;
                string name;
                long memory;
                TimeSpan used;
                try
                {
                    pid = process.Id;
                    name = NameOf(process.ProcessName);
                    memory = process.WorkingSet64;
                    used = process.TotalProcessorTime;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // Gone meanwhile, or not ours to read.
                    continue;
                }
                cpu[pid] = used;
                var share = elapsed > TimeSpan.Zero && _cpu.TryGetValue(pid, out var before)
                    ? Math.Clamp((used - before).TotalMilliseconds / elapsed.TotalMilliseconds / cores * 100, 0, 100)
                    : 0;
                int? parent = null;
                string? command = null, user = null;
                if (OperatingSystem.IsLinux())
                    (parent, command, user) = FromProc(pid, users);
                else if (extra.TryGetValue(pid, out var known))
                    (parent, user) = known;
                processes.Add(new ProcessInfo(pid, parent, name, command, user, memory, share));
            }
        }
        _cpu = cpu;
        _at = now;
        return processes;
    }

    /// <summary>
    /// A process's name: a program that writes its whole command line where its name goes (Chromium's and Electron's
    /// helpers) is named by its first word, the file's name.
    /// </summary>
    internal static string NameOf(string name)
    {
        var first = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? name;
        return first.Contains('/') ? Path.GetFileName(first.TrimEnd('/')) : first;
    }

    /// <summary>A Linux process's parent (stat's fourth field, after its name in brackets), command line and user.</summary>
    static (int? Parent, string? Command, string? User) FromProc(int pid, Dictionary<int, string> users)
    {
        var dir = Path.Combine("/proc", pid.ToString(CultureInfo.InvariantCulture));
        int? parent = null;
        string? user = null;
        if (Read(Path.Combine(dir, "stat")) is { } stat && stat.LastIndexOf(')') is > 0 and var close
            && stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries) is [_, var ppid, ..]
            && int.TryParse(ppid, CultureInfo.InvariantCulture, out var p))
            parent = p;
        if (Read(Path.Combine(dir, "status")) is { } status
            && status.Split('\n').FirstOrDefault(l => l.StartsWith("Uid:", StringComparison.Ordinal)) is { } line
            && line.Split('\t', ' ', StringSplitOptions.RemoveEmptyEntries) is [_, var uid, ..]
            && int.TryParse(uid, CultureInfo.InvariantCulture, out var id))
            user = users.GetValueOrDefault(id, uid);
        var command = Read(Path.Combine(dir, "cmdline"))?.Replace('\0', ' ').Trim();
        return (parent, command is { Length: > 0 } ? command : null, user);
    }

    static Dictionary<int, string> LinuxUsers()
    {
        Dictionary<int, string> users = [];
        foreach (var line in (Read("/etc/passwd") ?? "").Split('\n'))
            if (line.Split(':') is [var name, _, var id, ..] && int.TryParse(id, CultureInfo.InvariantCulture, out var uid))
                users.TryAdd(uid, name);
        return users;
    }

    /// <summary>macOS's parents and users: ps -axo pid=,ppid=,user=.</summary>
    static Dictionary<int, (int? Parent, string? User)> FromPs()
    {
        Dictionary<int, (int?, string?)> known = [];
        try
        {
            using var ps = Process.Start(new ProcessStartInfo("ps", ["-axo", "pid=,ppid=,user="]) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
            var output = ps.StandardOutput.ReadToEnd();
            ps.WaitForExit(5000);
            foreach (var line in output.Split('\n'))
                if (line.Split(' ', StringSplitOptions.RemoveEmptyEntries) is [var pid, var ppid, var user]
                    && int.TryParse(pid, CultureInfo.InvariantCulture, out var p) && int.TryParse(ppid, CultureInfo.InvariantCulture, out var pp))
                    known[p] = (pp, user);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
        return known;
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
}
