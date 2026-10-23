using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Karvon.Services;

/// <summary>
/// A process's descendants and ending them: on Linux from /proc, on macOS from ps; on Windows Karvon has none to read
/// yet (killing a process there ends its tree anyway). Roslyn's language server starts MSBuild build hosts that outlive
/// it when it exits by itself (or when Karvon is killed): they are reparented and wait for good, tens of MB each, so
/// Karvon ends them itself.
/// </summary>
public static partial class ProcessTree
{
    [GeneratedRegex(@"^\s*(\d+)\s+(\d+)\s+(\w{3} \w{3} +\d{1,2} \d{2}:\d{2}:\d{2} \d{4})\s+(\S)\S*\s*(.*)$")]
    private static partial Regex PsLine();

    /// <summary>A process, with its start time: the same pid later may be another process.</summary>
    public readonly record struct Member(int Pid, ulong StartTime);

    /// <summary>Every process under <paramref name="pid"/>, children first, theirs after; empty where the processes cannot be read.</summary>
    public static IReadOnlyList<Member> Descendants(int pid)
    {
        var children = new Dictionary<int, List<Member>>();
        foreach (var (child, parent, start) in All())
        {
            if (!children.TryGetValue(parent, out var list))
                children[parent] = list = [];
            list.Add(new Member(child, start));
        }
        var found = new List<Member>();
        var next = new Queue<int>([pid]);
        while (next.TryDequeue(out var at))
            foreach (var member in children.GetValueOrDefault(at) ?? [])
            {
                found.Add(member);
                next.Enqueue(member.Pid);
            }
        return found;
    }

    /// <summary>Ends those still running (the very ones: a pid that is another process now is left).</summary>
    public static int Kill(IEnumerable<Member> members)
    {
        var running = Ps.IsUsed ? Ps.Read().ToDictionary(p => p.Pid) : null;
        var killed = 0;
        foreach (var member in members)
        {
            var now = running is null ? Stat(member.Pid) : running.TryGetValue(member.Pid, out var p) ? (p.State, p.Parent, p.Start) : null;
            if (now is { State: not 'Z' } alive && alive.Start == member.StartTime && TryKill(member.Pid))
                killed++;
        }
        return killed;
    }

    /// <summary>
    /// The processes started from under <paramref name="root"/> whose command line has <paramref name="marker"/> and with
    /// no <paramref name="owner"/> above them any more: left behind by a server that is gone. A live server's are left,
    /// however deep (a build host may start another), another Karvon's too.
    /// </summary>
    public static int KillOrphans(string root, string marker, string owner)
    {
        var commands = Ps.IsUsed ? Ps.Read().ToDictionary(p => p.Pid, p => p.Command) : null;
        string? CommandOf(int pid) => commands is null ? CommandLine(pid) : commands.GetValueOrDefault(pid);
        var parents = All().ToDictionary(p => p.Pid, p => p.Parent);
        var killed = 0;
        foreach (var (pid, _) in parents)
        {
            var command = CommandOf(pid);
            if (command is null || !command.Contains(root, StringComparison.Ordinal) || !command.Contains(marker, StringComparison.Ordinal))
                continue;
            if (!HasAncestor(pid, owner, parents, CommandOf) && TryKill(pid))
                killed++;
        }
        return killed;
    }

    static bool HasAncestor(int pid, string owner, Dictionary<int, int> parents, Func<int, string?> commandOf)
    {
        for (var at = parents.GetValueOrDefault(pid); at > 1 && parents.ContainsKey(at); at = parents[at])
            if (commandOf(at)?.Contains(owner, StringComparison.Ordinal) == true)
                return true;
        return false;
    }

    static bool TryKill(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill();
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Gone meanwhile, or not the user's.
            return false;
        }
    }

    static IEnumerable<(int Pid, int Parent, ulong Start)> All()
    {
        if (Ps.IsUsed)
        {
            foreach (var p in Ps.Read())
                yield return (p.Pid, p.Parent, p.Start);
            yield break;
        }
        if (!Directory.Exists("/proc"))
            yield break;
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
            if (int.TryParse(Path.GetFileName(dir), out var pid) && Stat(pid) is { } stat)
                yield return (pid, stat.Parent, stat.Start);
    }

    /// <summary>
    /// macOS's processes, from ps (there is no /proc): each one's parent, start time (lstart, to the second, which tells a
    /// pid used again apart), state and command line.
    /// </summary>
    internal static class Ps
    {
        public static bool IsUsed => OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD();

        public readonly record struct Row(int Pid, int Parent, ulong Start, char State, string Command);

        public static IReadOnlyList<Row> Read()
        {
            try
            {
                using var ps = Process.Start(new ProcessStartInfo("ps", ["-axww", "-o", "pid=,ppid=,lstart=,state=,command="])
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    Environment = { ["LC_ALL"] = "C" },
                });
                if (ps is null)
                    return [];
                var text = ps.StandardOutput.ReadToEnd();
                ps.WaitForExit();
                return Parse(text);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                return [];
            }
        }

        /// <summary>ps's lines: "  412     1 Fri Oct 23 10:02:41 2026 Ss /usr/bin/dotnet BuildHost.dll".</summary>
        internal static IReadOnlyList<Row> Parse(string text)
        {
            var rows = new List<Row>();
            foreach (var line in text.Split('\n'))
            {
                if (PsLine().Match(line) is not { Success: true } m
                    || !DateTime.TryParseExact(Regex.Replace(m.Groups[3].Value, " +", " "), "ddd MMM d HH:mm:ss yyyy",
                        System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var started))
                    continue;
                rows.Add(new Row(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), (ulong)started.Ticks, m.Groups[4].Value[0], m.Groups[5].Value.TrimEnd('\r', ' ')));
            }
            return rows;
        }
    }

    /// <summary>/proc/pid/stat's state, parent and start time (fields 3, 4 and 22; the name before them may hold spaces).</summary>
    static (char State, int Parent, ulong Start)? Stat(int pid)
    {
        try
        {
            var text = File.ReadAllText($"/proc/{pid}/stat");
            var fields = text[(text.LastIndexOf(')') + 2)..].Split(' ');
            return (fields[0][0], int.Parse(fields[1], System.Globalization.CultureInfo.InvariantCulture), ulong.Parse(fields[19], System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    static string? CommandLine(int pid)
    {
        try
        {
            return File.ReadAllText($"/proc/{pid}/cmdline").Replace('\0', ' ');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
