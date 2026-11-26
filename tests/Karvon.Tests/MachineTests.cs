using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Karvon.Machine;
using Karvon.Machine.ViewModels;
using Karvon.Machine.Views;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>This Computer: the ports it listens on and the processes that hold them.</summary>
public class MachineTests
{
    sealed class Host : IMachineHost
    {
        public List<string> Opened { get; } = [];

        public bool? Answer { get; set; } = true;

        public List<string> Asked { get; } = [];

        public List<Karvon.ViewModels.DocumentTab> Shown { get; } = [];

        public Task<bool?> ConfirmAsync(string title, string message)
        {
            Asked.Add(message);
            return Task.FromResult(Answer);
        }

        public void OpenUrl(string url) => Opened.Add(url);

        public bool IsShown => false;

        public IEnumerable<Karvon.ViewModels.DocumentTab> Documents => Shown;

        public void ShowDocument(Karvon.ViewModels.DocumentTab document)
        {
            if (!Shown.Contains(document))
                Shown.Add(document);
        }

        public List<string> Edited { get; } = [];

        public void OpenFile(string path) => Edited.Add(path);
    }

    /// <summary>A program of the test's own listening on a port of this computer: what is stopped.</summary>
    static async Task<(Process Child, int Port)> ListenerAsync()
    {
        var child = Process.Start(new ProcessStartInfo("python3", ["-c",
            "import socket,time\ns=socket.socket()\ns.bind(('127.0.0.1',0))\ns.listen()\nprint(s.getsockname()[1],flush=True)\ntime.sleep(120)"])
        { RedirectStandardOutput = true })!;
        return (child, int.Parse((await child.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken))!));
    }

    [Fact]
    public void Linux_tables_give_listening_TCP_and_bound_UDP_sockets_with_their_addresses()
    {
        var proc = Directory.CreateTempSubdirectory("karvon-proc-").FullName;
        Directory.CreateDirectory(Path.Combine(proc, "net"));
        const string head = "  sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode\n";
        File.WriteAllText(Path.Combine(proc, "net", "tcp"), head
            + "   0: 0100007F:1F90 00000000:0000 0A 00000000:00000000 00:00000000 00000000  1000        0 11111 1 0 100 0 0 10 0\n"
            + "   1: 00000000:0016 00000000:0000 0A 00000000:00000000 00:00000000 00000000     0        0 22222 1 0 100 0 0 10 0\n"
            // Connected, not listening.
            + "   2: 0100007F:1F90 0100007F:C350 01 00000000:00000000 00:00000000 00000000  1000        0 33333 1 0 100 0 0 10 0\n");
        File.WriteAllText(Path.Combine(proc, "net", "tcp6"), head
            + "   0: 00000000000000000000000001000000:0050 00000000000000000000000000000000:0000 0A 00000000:00000000 00:00000000 00000000  1000        0 44444 1 0\n"
            + "   1: 0000000000000000FFFF00000100007F:0051 00000000000000000000000000000000:0000 0A 00000000:00000000 00:00000000 00000000  1000        0 55555 1 0\n");
        File.WriteAllText(Path.Combine(proc, "net", "udp"), head
            + "   0: 00000000:14E9 00000000:0000 07 00000000:00000000 00:00000000 00000000   100        0 66666 2 0\n");

        Assert.Equal(
        [
            ("TCP", "127.0.0.1", 8080),
            ("TCP", "0.0.0.0", 22),
            ("TCP", "::1", 80),
            ("TCP", "127.0.0.1", 81),
            ("UDP", "0.0.0.0", 5353),
        ], Listening.Sockets(proc).Select(s => (s.Protocol, s.Address, s.Port)));
        var ports = Listening.FromProc(proc);
        Assert.All(ports, p => Assert.Null(p.Pid));
        Assert.Equal(["127.0.0.1:8080 local", "0.0.0.0:22 everywhere"], ports.Take(2).Select(p => $"{p.Address}:{p.Port} {(p.IsLocalOnly ? "local" : p.IsEverywhere ? "everywhere" : "")}"));
        Assert.Equal("http://localhost:8080", ports[0].Url);
    }

    [Fact]
    public void Lsof_and_netstat_are_read_as_macOS_and_Windows_give_them()
    {
        var lsof = Listening.ParseLsof("p501\ncnode\nLnodir\nPTCP\nn*:3000\nPTCP\nn[::1]:3001\np88\ncmDNSResponder\nL_mdnsresponder\nPUDP\nn*:5353\nPUDP\nn10.0.0.2:5000->10.0.0.9:6000\n");
        Assert.Equal(
        [
            ("TCP", "*", 3000, 501, "node", "nodir"),
            ("TCP", "::1", 3001, 501, "node", "nodir"),
            ("UDP", "*", 5353, 88, "mDNSResponder", "_mdnsresponder"),
        ], lsof.Select(p => (p.Protocol, p.Address, p.Port, p.Pid!.Value, p.Process!, p.User!)));

        var netstat = Listening.ParseNetstat("""

            Active Connections

              Proto  Local Address          Foreign Address        State           PID
              TCP    0.0.0.0:135            0.0.0.0:0              LISTENING       1012
              TCP    [::]:5000              [::]:0                 ABHÖREN         4321
              TCP    127.0.0.1:49700        127.0.0.1:49701        ESTABLISHED     4321
              UDP    0.0.0.0:500            *:*                                    4460
              UDP    192.168.1.5:137        *:*                                    4
            """, pid => pid == 4321 ? "dotnet" : null);
        Assert.Equal(
        [
            ("TCP", "0.0.0.0", 135, 1012, null),
            ("TCP", "::", 5000, 4321, "dotnet"),
            ("UDP", "0.0.0.0", 500, 4460, null),
            ("UDP", "192.168.1.5", 137, 4, null),
        ], netstat.Select(p => (p.Protocol, p.Address, p.Port, p.Pid!.Value, p.Process)));
    }

    [Fact]
    public async Task This_processes_own_ports_are_found_with_it_and_Claude_is_told_whether_a_port_is_taken()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux's /proc");
        using var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        var tcpPort = ((IPEndPoint)tcp.LocalEndpoint).Port;
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var udpPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;

        var all = Listening.All();
        var mine = all.Single(p => p.Protocol == "TCP" && p.Port == tcpPort);
        Assert.Equal((Environment.ProcessId, "127.0.0.1", Environment.UserName), (mine.Pid, mine.Address, mine.User));
        Assert.False(string.IsNullOrEmpty(mine.Process));
        Assert.Contains(all, p => p.Protocol == "UDP" && p.Port == udpPort && p.Pid == Environment.ProcessId);

        var tool = new MachinePortsTool(Listening.All);
        var taken = await tool.CallAsync(JsonSerializer.SerializeToElement(new { port = tcpPort }), TestContext.Current.CancellationToken);
        Assert.StartsWith($"Port {tcpPort} is taken:\nTCP 127.0.0.1:{tcpPort} (this computer only): {mine.Process}, pid {Environment.ProcessId}", taken.Text);
        tcp.Stop();
        var free = await tool.CallAsync(JsonSerializer.SerializeToElement(new { port = tcpPort }), TestContext.Current.CancellationToken);
        Assert.Equal($"Nothing listens on port {tcpPort}: it is free.", free.Text);
    }

    [AvaloniaFact]
    public async Task Its_explorer_opens_each_section_as_a_page_and_the_ports_page_puts_a_process_s_addresses_together()
    {
        var host = new Host();
        using var explorer = new MachineExplorerViewModel(host)
        {
            ListPorts = () =>
            [
                new("UDP", "192.168.1.5", 3702, 47865, "python3", "python3 /usr/bin/wsdd", Environment.UserName),
                new("UDP", "172.17.0.1", 3702, 47865, "python3", "python3 /usr/bin/wsdd", Environment.UserName),
                new("UDP", "fe80::1", 3702, 47865, "python3", "python3 /usr/bin/wsdd", Environment.UserName),
                new("TCP", "0.0.0.0", 5000, 4242, "dotnet", "dotnet run --password=hunter2hunter2", Environment.UserName),
                new("TCP", "::", 5000, 4242, "dotnet", "dotnet run --password=hunter2hunter2", Environment.UserName),
                new("TCP", "127.0.0.53", 53, null, null, null, "systemd-resolve"),
            ],
        };
        await explorer.RefreshAsync();
        Assert.Equal(["Ports", "Processes", "Resources", "Disks"], explorer.Sections.Select(s => s.Title));
        Assert.Equal("3", explorer.Sections[0].Detail);

        // A section chosen: its page in front; chosen again, the same page.
        explorer.Sections[0].Open();
        explorer.Sections[0].Open();
        var page = Assert.IsType<MachineDocument>(Assert.Single(host.Shown));
        Assert.Same(explorer.Ports, page.Content);
        Assert.Equal(("machine:ports", "Ports"), (page.Key, page.Title));

        Assert.Equal(["53 TCP This computer only Another user's", "3702 UDP 172.17.0.1 +2 python3", "5000 TCP All addresses dotnet"],
            explorer.Ports.Rows.Select(r => $"{r.PortText} {r.Protocol} {r.AddressText} {r.ProcessText}"));
        Assert.Equal("172.17.0.1\n192.168.1.5\nfe80::1", explorer.Ports.Rows[1].AddressTip);
        Assert.Equal(("3", "2", "2"), (explorer.Ports.ListeningText, explorer.Ports.MineText, explorer.Ports.NetworkText));
        Assert.DoesNotContain("hunter2", explorer.Ports.Rows[2].CommandText);
        explorer.Ports.MineOnly = true;
        Assert.Equal(["3702", "5000"], explorer.Ports.Rows.Select(r => r.PortText));
        explorer.Ports.Search = "dotnet";
        Assert.Equal("Showing 1 of 3", explorer.Ports.CountText);
        explorer.Ports.Search = "";
        explorer.Ports.MineOnly = false;

        var window = new Avalonia.Controls.Window { Width = 1300, Height = 560, Content = new MachineDocumentView { DataContext = page } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Snapshot(window, "machine-ports");
        window.Close();
        var side = new Avalonia.Controls.Window { Width = 320, Height = 400, Content = new MachineExplorerView { DataContext = explorer } };
        side.Show();
        Dispatcher.UIThread.RunJobs();
        Snapshot(side, "machine-explorer");
        side.Close();
    }

    [AvaloniaFact]
    public async Task The_ports_and_processes_pages_stop_a_process_only_when_asked()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux's /proc");
        var (child, port) = await ListenerAsync();
        using var _ = child;
        var host = new Host();
        using var explorer = new MachineExplorerViewModel(host);
        await explorer.RefreshAsync();
        var row = explorer.Ports.Rows.Single(r => r.Port.Port == port);
        Assert.Equal(("python3", child.Id.ToString(), "TCP", "This computer only"), (row.ProcessText, row.PidText, row.Protocol, row.AddressText));
        Assert.True(row.CanStop && row.IsMine);
        explorer.Ports.OpenCommand.Execute(row);
        Assert.Equal([$"http://localhost:{port}"], host.Opened);

        // The process, with the port it listens on, among the processes.
        var process = explorer.Processes.Rows.Single(r => r.Pid == child.Id);
        Assert.Equal(("python3", port.ToString(), Environment.UserName), (process.Name, process.PortsText, process.UserText));
        Assert.Contains(explorer.Processes.Rows, r => r.IsKarvon);
        explorer.Processes.SortCommand.Execute("pid");
        Assert.Equal(explorer.Processes.Rows.Select(r => r.Pid).Order(), explorer.Processes.Rows.Select(r => r.Pid));
        Assert.Equal("↑", explorer.Processes.PidSort);
        explorer.Processes.SortCommand.Execute("memory");
        Assert.True(explorer.Processes.Rows[0].Info.Memory >= explorer.Processes.Rows[^1].Info.Memory);
        explorer.Processes.Search = "python3 " + port;
        Assert.Equal([child.Id], explorer.Processes.Rows.Select(r => r.Pid));

        host.Answer = false;
        await explorer.Processes.StopCommand.ExecuteAsync(process);
        Assert.False(child.HasExited);
        Assert.Contains($"It listens on {port}.", host.Asked[^1]);
        host.Answer = true;
        await explorer.Ports.StopCommand.ExecuteAsync(row);
        Assert.True(child.WaitForExit(5000));
        Assert.DoesNotContain(explorer.Ports.Rows, r => r.Port.Port == port);
        Assert.Empty(explorer.Processes.Rows);
    }

    [AvaloniaFact]
    public async Task Processes_and_resources_are_read_with_their_CPU_over_time_and_shown_on_their_page()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux's /proc");
        var processes = new ProcessSampler();
        var resources = new ResourceSampler();
        resources.Sample(processes.Sample());
        await Task.Delay(300, TestContext.Current.CancellationToken);
        var all = processes.Sample();
        var usage = resources.Sample(all);
        var me = all.Single(p => p.Pid == Environment.ProcessId);
        Assert.Equal(Environment.UserName, me.User);
        Assert.NotNull(me.ParentPid);
        Assert.True(me.Memory > 0);
        Assert.InRange(usage.Cpu.Total, 0, 100);
        Assert.Equal(Environment.ProcessorCount, usage.Cpu.Cores.Count);
        Assert.True(usage.Memory.Total > usage.Memory.Used && usage.Memory.Used > 0);
        Assert.Contains(usage.Disks, d => d.Mount == "/" && d.Total > 0);
        Assert.DoesNotContain(usage.Disks, d => d.Format is "tmpfs" or "squashfs" or "overlay");
        Assert.Equal(50, ResourceSampler.Busy([10, 0, 10, 80, 0], [20, 0, 20, 100, 0]), 1);

        var host = new Host();
        using var explorer = new MachineExplorerViewModel(host);
        await explorer.RefreshAsync();
        await explorer.RefreshAsync();
        explorer.Sections[2].Open();
        var page = explorer.Resources;
        Assert.Equal(Environment.ProcessorCount, page.Cores.Count);
        Assert.Contains(page.Disks, d => d.Name == "/");
        Assert.Equal(2, page.CpuHistory.Count);
        Assert.StartsWith("CPU ", explorer.CpuText);
        var window = new Avalonia.Controls.Window { Width = 1300, Height = 900, Content = new MachineDocumentView { DataContext = host.Shown.Single() } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Snapshot(window, "machine-resources");
        explorer.Sections[1].Open();
        var processesWindow = new Avalonia.Controls.Window { Width = 1300, Height = 600, Content = new MachineDocumentView { DataContext = host.Shown.Last() } };
        processesWindow.Show();
        Dispatcher.UIThread.RunJobs();
        Snapshot(processesWindow, "machine-processes");
        window.Close();
        processesWindow.Close();
    }

    [Fact]
    public async Task Claude_lists_processes_and_resources_and_stops_a_process_with_the_user_asked()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux's /proc");
        var (child, port) = await ListenerAsync();
        using var _ = child;
        var listed = await new MachineProcessesTool().CallAsync(JsonSerializer.SerializeToElement(new { name = "python3", limit = 200 }), TestContext.Current.CancellationToken);
        Assert.Contains($"\npid {child.Id} python3 (user {Environment.UserName}): CPU ", listed.Text);
        Assert.Contains($", listens on {port}: python3 -c", listed.Text);

        var resources = await new MachineResourcesTool().CallAsync(JsonSerializer.SerializeToElement(new { }), TestContext.Current.CancellationToken);
        Assert.Matches(@"^CPU [\d.]+% of \d+ CPUs \(each: ", resources.Text);
        Assert.Contains("\n  / (", resources.Text);

        var stop = new MachineStopProcessTool();
        Assert.Equal("That is Karvon itself: it is not stopped from a tool.", stop.Refusal(JsonSerializer.SerializeToElement(new { pid = Environment.ProcessId })));
        var input = JsonSerializer.SerializeToElement(new { pid = child.Id });
        Assert.Null(stop.Refusal(input));
        var plan = stop.Plan(input);
        Assert.Equal((Karvon.Claude.ToolAccess.Destructive, $"Allow Claude to stop python3 (process {child.Id})?"), (plan.Access, plan.Question));
        Assert.StartsWith("python3 -c", plan.Code);
        Assert.Equal($"Process {child.Id} (python3) is stopped.", (await stop.CallAsync(input, TestContext.Current.CancellationToken)).Text);
        Assert.True(child.WaitForExit(5000));
        Assert.Equal($"No process {child.Id} runs.", stop.Refusal(input));
    }

    static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    [AvaloniaFact]
    public async Task The_disks_page_browses_a_folder_measures_what_takes_its_space_and_opens_its_files()
    {
        var root = Directory.CreateTempSubdirectory("karvon-disks-").FullName;
        Directory.CreateDirectory(Path.Combine(root, "big"));
        Directory.CreateDirectory(Path.Combine(root, "small", "deeper"));
        File.WriteAllBytes(Path.Combine(root, "big", "data.bin"), new byte[300_000]);
        File.WriteAllBytes(Path.Combine(root, "small", "deeper", "a.bin"), new byte[1_000]);
        File.WriteAllText(Path.Combine(root, "notes.txt"), "hello");
        File.WriteAllText(Path.Combine(root, ".secret"), "hidden");
        File.WriteAllBytes(Path.Combine(root, "a.png"), Png);
        File.WriteAllBytes(Path.Combine(root, "b.png"), Png);
        var host = new Host();
        using var explorer = new MachineExplorerViewModel(host);
        await explorer.RefreshAsync();
        Assert.Contains(explorer.Disks.Disks, d => d.Mount == "/");
        var disks = explorer.Disks;
        disks.Browse(root);
        Assert.Equal(["big", "small", "a.png", "b.png", "notes.txt"], disks.Rows.Select(r => r.Name));
        Assert.Equal(["Folder", "Folder", "Picture", "Picture", "TXT file"], disks.Rows.Select(r => r.KindText));
        Assert.Equal(root, disks.Crumbs[^1].Path);
        disks.ShowHidden = true;
        Assert.Contains(disks.Rows, r => r.Name == ".secret");
        disks.ShowHidden = false;

        // Measured: each folder's size, the largest first.
        await disks.MeasureCommand.ExecuteAsync(null);
        Assert.Equal("size", disks.SortBy);
        Assert.Equal(["big", "small"], disks.Rows.Take(2).Select(r => r.Name));
        Assert.Equal((300_000L, 1_000L), (disks.Rows[0].Size!.Value, disks.Rows[1].Size!.Value));
        Assert.Equal(100, disks.Rows[0].Share, 1);

        // A folder browsed into, and back up; a text file in the editor, a picture in a page of its own.
        disks.Open(disks.Rows.Single(r => r.Name == "small"));
        Assert.Equal(["deeper"], disks.Rows.Select(r => r.Name));
        disks.UpCommand.Execute(null);
        Assert.Equal(root, disks.Folder);
        disks.Open(disks.Rows.Single(r => r.Name == "notes.txt"));
        Assert.Equal([Path.Combine(root, "notes.txt")], host.Edited);
        disks.Open(disks.Rows.Single(r => r.Name == "a.png"));
        var picture = Assert.IsType<MachineFileDocument>(host.Shown.Last());
        var image = Assert.IsType<ImagePage>(picture.Content);
        Assert.Equal(("a.png", "1 × 1 · 70 B", "1 of 2"), (picture.Title, image.Info, image.Place));
        image.NextCommand.Execute(null);
        Assert.Equal(("b.png", "2 of 2"), (picture.Title, image.Place));

        explorer.Sections[3].Open();
        var window = new Avalonia.Controls.Window { Width = 1300, Height = 760, Content = new MachineDocumentView { DataContext = host.Shown.Last() } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Snapshot(window, "machine-disks");
        window.Close();
        picture.Dispose();
    }

    [AvaloniaFact]
    public async Task A_video_plays_in_its_page_pauses_and_seeks()
    {
        Assert.SkipUnless(Karvon.Machine.Media.GstPlayer.IsAvailable, "GStreamer is not on this computer");
        var file = MediaTests.Video(90);
        MediaPage.AudioSink = "fakesink sync=true";
        try
        {
            var host = new Host();
            using var explorer = new MachineExplorerViewModel(host);
            explorer.OpenFile(file);
            var document = Assert.IsType<MachineFileDocument>(Assert.Single(host.Shown));
            var media = Assert.IsType<MediaPage>(document.Content);
            Assert.Equal(("clip one.webm", true), (document.Title, media.IsVideo));
            var window = new Avalonia.Controls.Window { Width = 900, Height = 600, Content = new MachineFileView { DataContext = document } };
            window.Show();
            await WaitUntil(() => media.Frame is not null && media.Position > 0.3, TimeSpan.FromSeconds(10));
            Assert.Equal((64, 48), (media.Frame!.PixelSize.Width, media.Frame.PixelSize.Height));
            Assert.True(media.IsPlaying);
            Assert.InRange(media.Duration, 2.8, 3.2);
            Snapshot(window, "machine-video");
            media.PlayPauseCommand.Execute(null);
            Assert.False(media.IsPlaying);
            media.Position = 2.0;
            await WaitUntil(() => media.Position >= 1.9 && media.Position < 2.5, TimeSpan.FromSeconds(5));
            Assert.InRange(media.Position, 1.9, 2.5);
            Assert.Null(media.Problem);

            // The mini player: over every other window; its page closed, it plays on there.
            media.Position = 0;
            media.PlayPauseCommand.Execute(null);
            media.MiniCommand.Execute(null);
            var mini = Assert.IsType<MiniPlayerWindow>(MiniPlayerWindow.Current);
            Assert.True(mini.Topmost);
            Assert.Same(media, mini.Page);
            Assert.True(media.IsMini);
            // Shown there alone: its page says so.
            Assert.Equal((false, true), (media.ShowsHere, media.ShowsInMini));
            await WaitUntil(() => media.Position > 0.3, TimeSpan.FromSeconds(10));
            Snapshot(mini, "machine-mini-player");
            Dispatcher.UIThread.RunJobs();
            Snapshot(window, "machine-video-in-mini");
            window.Close();
            document.Dispose();
            Assert.True(media.IsPlaying);
            Assert.True(media.IsPageClosed);
            // Back to its page: opened again, on the same player.
            media.ShowPageCommand.Execute(null);
            var again = Assert.IsType<MachineFileDocument>(host.Shown.Last());
            Assert.NotSame(document, again);
            Assert.Same(media, again.Content);
            Assert.False(media.IsPageClosed);
            // Its page open again: the mini player closes, and it plays on there.
            mini.Close();
            Assert.False(media.IsMini);
            Assert.True(media.IsPlaying);
            Assert.True(media.ShowsHere);
            Assert.Null(MiniPlayerWindow.Current);
            // In the mini player again, its page closed, the mini player closed: nothing plays it any more.
            media.MiniCommand.Execute(null);
            Assert.Null(MiniPlayerWindow.Current);
            MiniPlayerWindow.Open(media, null);
            again.Dispose();
            MiniPlayerWindow.Current!.Close();
            Assert.False(media.IsPlaying);
        }
        finally
        {
            MediaPage.AudioSink = null;
        }
    }
}
