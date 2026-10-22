using System.Diagnostics;

namespace Karvon.Services;

/// <summary>
/// A process's descendants (Linux, from /proc) and ending them. Roslyn's language server starts MSBuild build hosts
/// that outlive it when it exits by itself (or when Karvon is killed): they are reparented and wait for good, tens of MB
/// each, so Karvon ends them itself.
/// </summary>
public static class ProcessTree
{
    /// <summary>A process, with its start time: the same pid later may be another process.</summary>
    public readonly record struct Member(int Pid, ulong StartTime);

    /// <summary>Every process under <paramref name="pid"/>, children first, theirs after; empty where there is no /proc.</summary>
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
        var killed = 0;
        foreach (var member in members)
            if (Stat(member.Pid) is { State: not 'Z' } now && now.Start == member.StartTime && TryKill(member.Pid))
                killed++;
        return killed;
    }

    /// <summary>
    /// The processes started from under <paramref name="root"/> whose command line has <paramref name="marker"/> and with
    /// no <paramref name="owner"/> above them any more: left behind by a server that is gone. A live server's are left,
    /// however deep (a build host may start another), another Karvon's too.
    /// </summary>
    public static int KillOrphans(string root, string marker, string owner)
    {
        var parents = All().ToDictionary(p => p.Pid, p => p.Parent);
        var killed = 0;
        foreach (var (pid, _) in parents)
        {
            var command = CommandLine(pid);
            if (command is null || !command.Contains(root, StringComparison.Ordinal) || !command.Contains(marker, StringComparison.Ordinal))
                continue;
            if (!HasAncestor(pid, owner, parents) && TryKill(pid))
                killed++;
        }
        return killed;
    }

    static bool HasAncestor(int pid, string owner, Dictionary<int, int> parents)
    {
        for (var at = parents.GetValueOrDefault(pid); at > 1 && parents.ContainsKey(at); at = parents[at])
            if (CommandLine(at)?.Contains(owner, StringComparison.Ordinal) == true)
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
        if (!Directory.Exists("/proc"))
            yield break;
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
            if (int.TryParse(Path.GetFileName(dir), out var pid) && Stat(pid) is { } stat)
                yield return (pid, stat.Parent, stat.Start);
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
