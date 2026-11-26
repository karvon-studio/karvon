using System.Diagnostics;
using System.Globalization;
using System.Net;

namespace Karvon.Machine;

/// <summary>
/// A port this machine listens on: TCP (listening) or UDP (bound), the address it is bound to, and the process that holds
/// it, when it can be told (another user's, the system's, are not, without their rights).
/// </summary>
public sealed record ListeningPort(string Protocol, string Address, int Port, int? Pid, string? Process, string? CommandLine, string? User)
{
    /// <summary>Bound to this machine's loopback alone: only programs here reach it.</summary>
    public bool IsLocalOnly => Address is "::1" || Address.StartsWith("127.", StringComparison.Ordinal);

    /// <summary>Bound to every address: reached from the network too (unless a firewall says no).</summary>
    public bool IsEverywhere => Address is "0.0.0.0" or "::" or "*";

    /// <summary>Where a browser reaches it.</summary>
    public string Url => $"http://{(IsEverywhere || IsLocalOnly ? "localhost" : Address.Contains(':') ? $"[{Address}]" : Address)}:{Port}";
}

/// <summary>
/// The ports this machine listens on, with their processes: Linux's /proc (the sockets, and each process's descriptors that
/// hold them), macOS's lsof, Windows's netstat.
/// </summary>
public static class Listening
{
    public static IReadOnlyList<ListeningPort> All()
    {
        var ports = OperatingSystem.IsLinux() ? FromProc("/proc")
            : OperatingSystem.IsMacOS() ? ParseLsof(Run("lsof", "-nP", "-iTCP", "-sTCP:LISTEN", "-iUDP", "-FpcLPn"))
            : OperatingSystem.IsWindows() ? ParseNetstat(Run("netstat", "-ano"), WindowsProcess)
            : [];
        return [.. ports.DistinctBy(p => (p.Protocol, p.Address, p.Port)).OrderBy(p => p.Port).ThenBy(p => p.Protocol, StringComparer.Ordinal).ThenBy(p => p.Address, StringComparer.Ordinal)];
    }

    // ---- Linux --------------------------------------------------------------------------------------------------------

    /// <summary>The listening sockets in <paramref name="proc"/>/net, each with the process whose descriptor holds it.</summary>
    internal static IReadOnlyList<ListeningPort> FromProc(string proc)
    {
        var sockets = Sockets(proc);
        var owners = Owners(proc, sockets.Select(s => s.Inode).ToHashSet());
        var users = Users();
        return
        [
            .. sockets.Select(s =>
            {
                var pid = owners.TryGetValue(s.Inode, out var owner) ? owner : (int?)null;
                return new ListeningPort(s.Protocol, s.Address, s.Port, pid,
                    pid is { } p ? ReadText(Path.Combine(proc, p.ToString(CultureInfo.InvariantCulture), "comm"))?.Trim() : null,
                    pid is { } q ? ReadText(Path.Combine(proc, q.ToString(CultureInfo.InvariantCulture), "cmdline"))?.Replace('\0', ' ').Trim() : null,
                    users.GetValueOrDefault(s.Uid, s.Uid.ToString(CultureInfo.InvariantCulture)));
            }),
        ];
    }

    internal sealed record Socket(string Protocol, string Address, int Port, long Inode, int Uid);

    /// <summary>The TCP sockets that listen and the UDP ones bound, from /proc/net's tables.</summary>
    internal static List<Socket> Sockets(string proc)
    {
        List<Socket> sockets = [];
        foreach (var (file, protocol) in new[] { ("tcp", "TCP"), ("tcp6", "TCP"), ("udp", "UDP"), ("udp6", "UDP") })
        {
            if (ReadText(Path.Combine(proc, "net", file)) is not { } table)
                continue;
            foreach (var line in table.Split('\n').Skip(1))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 10 || parts[1].Split(':') is not [var address, var port])
                    continue;
                // TCP: 0A is LISTEN. UDP: 07 is a socket with no peer, which is what is bound for others to send to.
                if (parts[3] != (protocol == "TCP" ? "0A" : "07") || !parts[2].EndsWith(":0000", StringComparison.Ordinal))
                    continue;
                if (Address(address) is not { } ip || !int.TryParse(port, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var number) || number == 0)
                    continue;
                if (long.TryParse(parts[9], CultureInfo.InvariantCulture, out var inode) && int.TryParse(parts[7], CultureInfo.InvariantCulture, out var uid))
                    sockets.Add(new Socket(protocol, ip, number, inode, uid));
            }
        }
        return sockets;
    }

    /// <summary>An address as /proc/net writes it: hex, each 32-bit word in the machine's (little-endian) order.</summary>
    internal static string? Address(string hex)
    {
        if (hex.Length is not (8 or 32))
            return null;
        try
        {
            var bytes = Convert.FromHexString(hex);
            for (var i = 0; i < bytes.Length; i += 4)
                Array.Reverse(bytes, i, 4);
            var ip = new IPAddress(bytes);
            return ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4().ToString() : ip.ToString();
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Which process holds each socket: the descriptors /proc lets this user read (its own processes').</summary>
    static Dictionary<long, int> Owners(string proc, HashSet<long> inodes)
    {
        Dictionary<long, int> owners = [];
        if (inodes.Count == 0)
            return owners;
        IEnumerable<string> processes;
        try
        {
            processes = Directory.EnumerateDirectories(proc).Where(d => int.TryParse(Path.GetFileName(d), out _)).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return owners;
        }
        foreach (var dir in processes)
        {
            var pid = int.Parse(Path.GetFileName(dir), CultureInfo.InvariantCulture);
            try
            {
                foreach (var fd in Directory.EnumerateFiles(Path.Combine(dir, "fd")))
                {
                    var target = new FileInfo(fd).LinkTarget;
                    if (target is not null && target.StartsWith("socket:[", StringComparison.Ordinal)
                        && long.TryParse(target.AsSpan(8, target.Length - 9), CultureInfo.InvariantCulture, out var inode)
                        && inodes.Contains(inode))
                        owners.TryAdd(inode, pid);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return owners;
    }

    /// <summary>User ids to names, from /etc/passwd.</summary>
    static Dictionary<int, string> Users()
    {
        Dictionary<int, string> users = [];
        foreach (var line in (ReadText("/etc/passwd") ?? "").Split('\n'))
            if (line.Split(':') is [var name, _, var id, ..] && int.TryParse(id, CultureInfo.InvariantCulture, out var uid))
                users.TryAdd(uid, name);
        return users;
    }

    static string? ReadText(string path)
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

    // ---- macOS --------------------------------------------------------------------------------------------------------

    /// <summary>
    /// lsof's fields (-F pcLPn): a process's p (pid), c (command), L (login), then each of its files' P (protocol) and n
    /// (name: "*:3000", "127.0.0.1:5432", "[::1]:8080"; a connected UDP socket's has "->").
    /// </summary>
    internal static IReadOnlyList<ListeningPort> ParseLsof(string output)
    {
        List<ListeningPort> ports = [];
        int? pid = null;
        string? command = null, login = null, protocol = null;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var value = line[1..].TrimEnd('\r');
            switch (line[0])
            {
                case 'p':
                    pid = int.TryParse(value, CultureInfo.InvariantCulture, out var p) ? p : null;
                    command = login = protocol = null;
                    break;
                case 'c':
                    command = value;
                    break;
                case 'L':
                    login = value;
                    break;
                case 'P':
                    protocol = value.ToUpperInvariant();
                    break;
                case 'n' when protocol is "TCP" or "UDP" && !value.Contains("->", StringComparison.Ordinal):
                    var colon = value.LastIndexOf(':');
                    if (colon > 0 && int.TryParse(value[(colon + 1)..], CultureInfo.InvariantCulture, out var port) && port > 0)
                        ports.Add(new ListeningPort(protocol, value[..colon].Trim('[', ']'), port, pid, command, null, login));
                    break;
            }
        }
        return ports;
    }

    // ---- Windows ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// netstat -ano's rows: a TCP one listens when its foreign address is any (0.0.0.0:0, [::]:0), whatever its state is
    /// called in the system's language; a UDP one is bound (*:*). Its last column is the process's id.
    /// </summary>
    internal static IReadOnlyList<ListeningPort> ParseNetstat(string output, Func<int, string?> process)
    {
        List<ListeningPort> ports = [];
        foreach (var line in output.Split('\n'))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4 || parts[0] is not ("TCP" or "UDP") || !int.TryParse(parts[^1], CultureInfo.InvariantCulture, out var pid))
                continue;
            var listens = parts[0] == "TCP" ? parts[2] is "0.0.0.0:0" or "[::]:0" : parts[2] == "*:*";
            var colon = parts[1].LastIndexOf(':');
            if (!listens || colon <= 0 || !int.TryParse(parts[1][(colon + 1)..], CultureInfo.InvariantCulture, out var port) || port == 0)
                continue;
            ports.Add(new ListeningPort(parts[0], parts[1][..colon].Trim('[', ']'), port, pid, process(pid), null, null));
        }
        return ports;
    }

    static string? WindowsProcess(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    static string Run(string program, params string[] arguments)
    {
        try
        {
            using var process = new Process { StartInfo = new ProcessStartInfo(program, arguments) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true } };
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromSeconds(10)))
            {
                process.Kill();
                return "";
            }
            return output.Result;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return "";
        }
    }
}
