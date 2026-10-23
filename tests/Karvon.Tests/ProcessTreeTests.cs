using System.Diagnostics;
using Karvon.Services;

namespace Karvon.Tests;

/// <summary>The processes a language server leaves behind (its build hosts) are ended, and only those.</summary>
public class ProcessTreeTests
{
    static string Command(int pid)
    {
        try
        {
            return File.ReadAllText($"/proc/{pid}/cmdline");
        }
        catch (IOException)
        {
            return "";
        }
    }

    static bool Alive(int pid)
    {
        try
        {
            return !File.ReadAllText($"/proc/{pid}/stat").Contains(") Z ", StringComparison.Ordinal);
        }
        catch (IOException)
        {
            // Gone, also between looking for its folder and reading it.
            return false;
        }
    }

    static async Task<bool> GoneAsync(int pid)
    {
        for (var i = 0; i < 50 && Alive(pid); i++)
            await Task.Delay(100);
        return !Alive(pid);
    }

    /// <summary>A "server" with two children; it exits by itself when its input closes, leaving them.</summary>
    static Process Server(string script, string? host = null)
    {
        var psi = new ProcessStartInfo("bash", ["-c", script]) { RedirectStandardInput = true, RedirectStandardOutput = true };
        if (host is not null)
            psi.Environment["HOST"] = host;
        var process = Process.Start(psi)!;
        process.StandardOutput.ReadLine();
        return process;
    }

    [Fact]
    public async Task What_a_server_started_is_ended_after_it_exits_by_itself()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "/proc is Linux's.");
        var server = Server("sleep 300 & sleep 301 & echo started; read line");
        var tree = ProcessTree.Descendants(server.Id);
        var sleeps = tree.Where(m => Command(m.Pid).StartsWith("sleep", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, sleeps.Count);

        server.StandardInput.Close();
        await server.WaitForExitAsync();
        Assert.All(sleeps, s => Assert.True(Alive(s.Pid)));
        Assert.Equal(2, ProcessTree.Kill(tree));
        foreach (var sleep in sleeps)
            Assert.True(await GoneAsync(sleep.Pid));
        // Gone, or another process by now: nothing more is killed.
        Assert.Equal(0, ProcessTree.Kill(tree));
    }

    [Fact]
    public async Task Build_hosts_whose_server_is_gone_are_ended_and_a_live_servers_are_left()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "/proc is Linux's.");
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "karvon-roslyn-" + Guid.NewGuid().ToString("N"))).FullName;
        var host = Path.Combine(root, "BuildHost.sh");
        // It keeps its name (as a build host's dotnet keeps its command line) while it waits.
        File.WriteAllText(host, "#!/bin/bash\necho started\nexec -a \"$0\" sleep 300\n");
        File.SetUnixFileMode(host, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        // A live server (its command line says what it is) with its build host; a server that went, leaving one.
        // The build host is started by a process the server started (as dotnet's host does): not the server's child, but its.
        var middle = Path.Combine(root, "middle.sh");
        File.WriteAllText(middle, "#!/bin/bash\nbash -c '\"$HOST\" & wait' &\nread line\n");
        var live = Server($"exec -a Microsoft.CodeAnalysis.LanguageServer bash {middle}", host);
        var gone = Server("\"$HOST\" & read line", host);
        try
        {
            var kept = ProcessTree.Descendants(live.Id).Single(m => Command(m.Pid).Contains("BuildHost", StringComparison.Ordinal));
            Assert.NotEqual(live.Id, int.Parse(File.ReadAllText($"/proc/{kept.Pid}/stat").Split(' ')[3], System.Globalization.CultureInfo.InvariantCulture));
            var orphan = ProcessTree.Descendants(gone.Id).Single(m => Command(m.Pid).Contains("BuildHost", StringComparison.Ordinal));
            gone.StandardInput.Close();
            await gone.WaitForExitAsync();

            Assert.Equal(1, ProcessTree.KillOrphans(root, "BuildHost", "Microsoft.CodeAnalysis.LanguageServer"));
            Assert.True(await GoneAsync(orphan.Pid));
            Assert.True(Alive(kept.Pid));
        }
        finally
        {
            live.Kill(entireProcessTree: true);
            ProcessTree.KillOrphans(root, "BuildHost", "Microsoft.CodeAnalysis.LanguageServer");
        }
    }

    [Fact]
    public void Ps_reads_the_processes_where_there_is_no_proc()
    {
        // macOS's ps, one-digit days padded; a line that is not a process is left out.
        var rows = ProcessTree.Ps.Parse("""
              412     1 Fri Oct 23 10:02:41 2026 S    /usr/local/share/dotnet/dotnet exec BuildHost.dll  --pipe x
             9001   412 Sat Oct  3 09:00:00 2026 Z <defunct>
            PID PPID STARTED
            """);
        Assert.Equal([(412, 1, 'S', "/usr/local/share/dotnet/dotnet exec BuildHost.dll  --pipe x"), (9001, 412, 'Z', "<defunct>")],
            rows.Select(r => (r.Pid, r.Parent, r.State, r.Command)));
        Assert.Equal((ulong)new DateTime(2026, 10, 3, 9, 0, 0).Ticks, rows[1].Start);

        // The real ps (Linux's takes the same options) knows this process and its parent (as /proc has it, on Linux).
        if (OperatingSystem.IsWindows())
            return;
        var self = ProcessTree.Ps.Read().Single(r => r.Pid == Environment.ProcessId);
        Assert.Equal(System.Diagnostics.Process.GetCurrentProcess().ProcessName.Length > 0, self.Command.Length > 0);
        if (!OperatingSystem.IsLinux())
            return;
        Assert.Equal(int.Parse(File.ReadAllText("/proc/self/stat").Split(") ")[1].Split(' ')[1]), self.Parent);
        Assert.Equal(File.ReadAllText("/proc/self/cmdline").Replace('\0', ' ').Trim(), self.Command);
    }
}
