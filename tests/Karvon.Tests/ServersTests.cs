using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Karvon.Claude;
using Karvon.Claude.Tools;
using Karvon.Docker.ViewModels;
using Karvon.Servers;
using Karvon.Servers.ViewModels;
using Karvon.Servers.Views;
using Karvon.Ui;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>What the Servers explorer asked of Karvon.</summary>
sealed class TestServersHost : IServersHost
{
    public string ConfigDirectory { get; } = Path.Combine(Path.GetTempPath(), "karvon-servers-" + Guid.NewGuid().ToString("N"));

    public string? Workspace { get; set; }

    public List<ServerDocument> Open { get; } = [];

    public List<string> Opened { get; } = [];

    public List<string> Prompts { get; } = [];

    public List<string> Statuses { get; } = [];

    public IReadOnlyList<string> FilesToPick { get; set; } = [];

    public IEnumerable<ServerDocument> Documents => Open;

    public void ShowDocument(ServerDocument document)
    {
        if (!Open.Contains(document))
            Open.Add(document);
    }

    public void CloseDocument(ServerDocument document)
    {
        Open.Remove(document);
        document.Dispose();
    }

    public void OpenFile(string localPath) => Opened.Add(localPath);

    public Task<IReadOnlyList<string>> PickFilesAsync(string title) => Task.FromResult(FilesToPick);

    public void AskClaude(string prompt) => Prompts.Add(prompt);

    public void Status(string text) => Statuses.Add(text);

    public List<(ServerHost Host, string Path)> RemoteFolders { get; } = [];

    public Task OpenRemoteFolderAsync(ServerHost host, string path)
    {
        RemoteFolders.Add((host, path));
        return Task.CompletedTask;
    }
}

/// <summary>The Servers explorer: ~/.ssh/config as OpenSSH reads it, known host keys, what the machines print, the tree and pages, Claude's tools.</summary>
public class ServersTests
{
    static string Ssh => Path.Combine(SshConfig.Home, ".ssh");

    /// <summary>A ~/.ssh/config of the tests' home (KARVON_SSH_HOME), never the user's.</summary>
    static void WriteConfig(string text, params (string Name, string Text)[] includes)
    {
        Assert.StartsWith(Path.GetTempPath(), SshConfig.Home);
        Directory.CreateDirectory(Path.Combine(Ssh, "conf.d"));
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Ssh, "conf.d")))
            File.Delete(file);
        foreach (var (name, content) in includes)
            File.WriteAllText(Path.Combine(Ssh, "conf.d", name), content);
        File.WriteAllText(SshConfig.ConfigFile, text);
    }

    [Fact]
    public void Ssh_config_is_read_as_openssh_reads_it()
    {
        WriteConfig("""
            # Work machines
            Host web-1 web-2
                HostName %h.example.test
                User deploy
                IdentityFile ~/.ssh/work_ed25519

            Host db-prod
              HostName 10.0.0.5
              Port 2222
              ProxyJump bastion

            Match host *.internal
              User nobody

            Include conf.d/*.conf

            Host * !web-2
              User fallback
              IdentityFile %d/.ssh/id_shared
            """, ("bastion.conf", "Host bastion\n  HostName bastion.example.test\n  User jump\n"));
        var hosts = SshConfig.Read();
        Assert.Equal(["web-1", "web-2", "db-prod", "bastion"], hosts.Select(h => h.Name));
        var web1 = hosts[0];
        Assert.Equal(("web-1.example.test", 22, "deploy"), (web1.Address, web1.Port, web1.User));
        Assert.Equal([Path.Combine(Ssh, "work_ed25519"), Path.Combine(Ssh, "id_shared")], web1.IdentityFiles);
        // The negated pattern keeps web-2 out of the last block.
        Assert.Equal([Path.Combine(Ssh, "work_ed25519")], hosts[1].IdentityFiles);
        var db = hosts[2];
        Assert.Equal(("10.0.0.5", 2222, "fallback", "bastion"), (db.Address, db.Port, db.User, db.ProxyJump));
        Assert.Equal("fallback@10.0.0.5:2222", db.Target);
        Assert.Equal(("bastion.example.test", "jump"), (hosts[3].Address, hosts[3].User));
        Assert.True(SshConfig.Matches(["*.example.test", "!secret.example.test"], "a.example.test"));
        Assert.False(SshConfig.Matches(["*.example.test", "!secret.example.test"], "secret.example.test"));
    }

    [Fact]
    public void Known_hosts_find_plain_bracketed_and_hashed_entries()
    {
        var file = Path.Combine(Path.GetTempPath(), "karvon-known-" + Guid.NewGuid().ToString("N"));
        var salt = RandomNumberGenerator.GetBytes(20);
        using var hmac = new HMACSHA1(salt);
        var hashed = $"|1|{Convert.ToBase64String(salt)}|{Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes("[hidden.test]:2200")))}";
        File.WriteAllLines(file,
        [
            "# comment",
            "plain.test,10.0.0.9 ssh-ed25519 AAAAPLAIN",
            "[ported.test]:2222 ssh-ed25519 AAAAPORTED",
            $"{hashed} ssh-ed25519 AAAAHASHED",
            "@revoked plain.test ssh-ed25519 AAAAREVOKED",
            "*.wild.test ssh-rsa AAAAWILD",
        ]);
        Assert.Equal(["AAAAPLAIN"], KnownHosts.Find(file, "plain.test", 22, "ssh-ed25519"));
        Assert.Equal(["AAAAPLAIN"], KnownHosts.Find(file, "10.0.0.9", 22, "ssh-ed25519"));
        Assert.Empty(KnownHosts.Find(file, "plain.test", 22, "ssh-rsa"));
        Assert.Equal(["AAAAPORTED"], KnownHosts.Find(file, "ported.test", 2222, "ssh-ed25519"));
        Assert.Empty(KnownHosts.Find(file, "ported.test", 22, "ssh-ed25519"));
        Assert.Equal(["AAAAHASHED"], KnownHosts.Find(file, "hidden.test", 2200, "ssh-ed25519"));
        Assert.Equal(["AAAAWILD"], KnownHosts.Find(file, "a.wild.test", 22, "ssh-rsa"));
    }

    [Fact]
    public void What_a_machine_prints_is_read()
    {
        var metrics = RemoteCommands.ParseMetrics("""
            @os
            Ubuntu 24.04.1 LTS
            @uptime
            93784.21 180000.10
            @load
            0.52 0.58 0.59 1/402 12345
            @cpus
            4
            @mem
            MemTotal:        8000000 kB
            MemAvailable:    6000000 kB
            SwapTotal:       2000000 kB
            SwapFree:        2000000 kB
            @disk
            /dev/sda1        100000000  40000000  60000000      40% /
            /dev/sdb1        200000000 150000000  50000000      75% /data
            @cpu
            cpu  1000 0 1000 8000 0 0 0 0 0 0
            cpu  1100 0 1100 8800 0 0 0 0 0 0
            """);
        Assert.Equal("Ubuntu 24.04.1 LTS", metrics.System);
        Assert.Equal(TimeSpan.FromSeconds(93784.21), metrics.Uptime);
        Assert.Equal([0.52, 0.58, 0.59], metrics.Load);
        Assert.Equal(4, metrics.Cpus);
        Assert.Equal(20, metrics.CpuPercent!.Value, 3);
        Assert.Equal(2_048_000_000L, metrics.MemoryUsed);
        Assert.Equal(["/", "/data"], metrics.Disks.Select(d => d.Mount));
        Assert.Equal(0.75, metrics.Disks[1].UsedRatio, 3);
        Assert.Equal("1 d 2 h", RemoteCommands.Duration(metrics.Uptime!.Value));

        var services = RemoteCommands.ParseServices("""
              cron.service              loaded active   running Regular background program processing daemon
            ● nginx.service             loaded failed   failed  A high performance web server
              ssh.service               loaded active   running OpenBSD Secure Shell server
              apt-daily.service         loaded inactive dead    Daily apt download activities
            """);
        Assert.Equal(["nginx", "cron", "ssh", "apt-daily"], services.Select(s => s.Name));
        Assert.True(services[0].IsFailed);
        Assert.Equal("active (running)", services[1].State);
        Assert.Equal("OpenBSD Secure Shell server", services[2].Description);

        Assert.Equal("journalctl -u 'my api' -n 50 --no-pager -f", RemoteCommands.Logs("my api", null, 50, follow: true));
        Assert.Equal("tail -n 200 '/var/log/it'\\''s.log'", RemoteCommands.Logs(null, "/var/log/it's.log", 200));
        Assert.Equal("journalctl -n 10000 --no-pager", RemoteCommands.Logs(null, null, 99_999));
        Assert.Equal("sudo -n systemctl restart 'nginx.service'", RemoteCommands.ServiceAction("restart", "nginx.service"));
    }

    [AvaloniaFact]
    public void An_ssh_command_to_a_known_machine_is_one_for_Karvons_server_tools()
    {
        WriteConfig("Host lab\n  HostName 10.0.0.5\n  User tester\n");
        var servers = new ServersExplorerViewModel(new TestServersHost()).Servers;
        Assert.Equal("lab", ServersModule.ServerOf("ssh lab uptime", servers)?.Name);
        Assert.Equal("lab", ServersModule.ServerOf("ssh -p 22 -i ~/.ssh/id tester@10.0.0.5 'df -h'", servers)?.Name);
        Assert.Equal("lab", ServersModule.ServerOf("cd /tmp && ssh -t lab", servers)?.Name);
        Assert.Null(ServersModule.ServerOf("ssh other.example.com", servers));
        Assert.Null(ServersModule.ServerOf("ssh-keygen -t ed25519", servers));
        Assert.Null(ServersModule.ServerOf("git push lab main", servers));
    }

    [AvaloniaFact]
    public async Task The_tree_groups_the_machines_and_a_page_says_why_it_cannot_connect()
    {
        WriteConfig("Host lab\n  HostName 127.0.0.1\n  Port 1\n  User tester\n\nHost web-1\n  HostName 127.0.0.1\n  Port 1\n");
        var host = new TestServersHost();
        var vm = new ServersExplorerViewModel(host);
        var window = new Window { Width = 1300, Height = 820, Content = new Grid { ColumnDefinitions = new("320,*"), Children = { new ServersExplorerView { DataContext = vm }, new ServersOverlay { DataContext = vm } } } };
        Grid.SetColumnSpan(((Grid)window.Content).Children[1], 2);
        window.Show();
        Assert.Equal(["SSH config"], vm.Groups.Select(g => g.Name));
        Assert.Equal(["lab", "web-1"], vm.Groups[0].Servers.Select(s => s.Name));

        // Add Server: the form, then the machine in its group, kept in Karvon's list (no secret in it).
        var adding = vm.AddServerCommand.ExecuteAsync(null);
        var form = Assert.IsType<ServerFormDialog>(vm.Dialog);
        Assert.Equal("Give it a name.", form.Problem);
        (form.Name, form.Address, form.Port, form.User, form.Group) = ("box", "127.0.0.1", "1", "deploy", "Work");
        Assert.Null(form.Problem);
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(150));
        Snapshot(window, "servers-add");
        form.Confirm();
        await adding;
        Assert.Equal(["Work", "SSH config"], vm.Groups.Select(g => g.Name));
        var box = vm.Find("box")!;
        Assert.Equal(("deploy@127.0.0.1:1", "Karvon"), (box.Target, box.Host.Source));
        var saved = JsonNode.Parse(File.ReadAllText(Path.Combine(host.ConfigDirectory, "servers.json")))!;
        Assert.Equal("127.0.0.1", saved["box"]!["Address"]!.GetValue<string>());
        Assert.DoesNotContain("assword", saved.ToJsonString());

        // Production: marked in the tree, kept for a ~/.ssh/config host too.
        var lab = vm.Find("lab")!;
        lab.ToggleProductionCommand.Execute(null);
        Assert.True(vm.Find("lab")!.IsProduction);
        Assert.True(new ServerList(Path.Combine(host.ConfigDirectory, "servers.json")).Hosts().Single(h => h.Name == "lab").Production);

        // Opening a machine that does not answer: its page and the tree say why.
        var document = vm.Open(box);
        Assert.Same(document, Assert.Single(host.Open));
        Assert.Equal(("box", "server:box"), (document.Title, document.Key));
        await WaitUntil(() => box.Status == ServerStatus.Failed, TimeSpan.FromSeconds(20));
        Assert.Contains("Could not connect to box (deploy@127.0.0.1:1)", document.Page.Problem);
        Assert.Equal(["-p", "1", "deploy@127.0.0.1"], document.Page.SshArguments);
        Assert.Equal(["-F", SshConfig.ConfigFile, "lab"], new ServerPageViewModel(vm, lab).SshArguments);
        Assert.Same(document, vm.Open(box, "logs"));
        Assert.Equal("logs", document.Page.Tab);
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(150));
        Snapshot(window, "servers-tree");

        // Removing asks, and takes its page with it; a ~/.ssh/config host is not Karvon's to remove.
        var removing = box.RemoveCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.Dialog is ConfirmDialog, TimeSpan.FromSeconds(5));
        vm.Dialog!.Confirm();
        await removing;
        Assert.Null(vm.Find("box"));
        Assert.Empty(host.Open);
        await lab.RemoveCommand.ExecuteAsync(null);
        Assert.Contains("is in ~/.ssh/config", vm.Error);
        vm.Dispose();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Claude_lists_the_servers_and_a_production_command_is_destructive()
    {
        WriteConfig("Host lab\n  HostName 127.0.0.1\n  Port 1\n  User tester\n\nHost prod-1\n  HostName 127.0.0.1\n  Port 1\n");
        var shop = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "karvon-shop-" + Guid.NewGuid().ToString("N"))).FullName;
        var host = new TestServersHost { Workspace = shop };
        var explorer = new ServersExplorerViewModel(host);
        explorer.SetProduction(explorer.Find("prod-1")!, true);
        var (docker, _, _) = FakeDocker.Create();
        var server = new TestToolHost(null, new Docker.DockerEngine(docker)).Tools(() => explorer);
        async Task<(string Text, bool IsError)> Call(string tool, object arguments)
        {
            var message = ClaudeIdeToolsTests.Allowed(server, tool, arguments);
            var result = (await server.HandleAsync(message, CancellationToken.None))["result"]!;
            return (result["content"]![0]!["text"]!.GetValue<string>(), result["isError"]!.GetValue<bool>());
        }

        // Not every host of ~/.ssh/config: only those the user lets Claude reach in this folder.
        Assert.Equal("No server is Claude's to reach in this folder (the user has 2). The user lets Claude reach a server in the Servers "
            + "window (right-click > Let Claude Reach It Here): ask them.", (await Call("server_list", new { })).Text);
        var (refused, isError) = await Call("server_logs", new { server = "lab", unit = "nginx" });
        Assert.True(isError);
        Assert.StartsWith("lab is not one of the servers Claude may reach in this folder.", refused);
        explorer.SetForClaude(explorer.Find("lab")!, true);
        explorer.SetForClaude(explorer.Find("prod-1")!, true);
        Assert.True(explorer.Find("lab")!.IsForClaude);
        Assert.Equal("lab: tester@127.0.0.1:1\nprod-1: " + Environment.UserName + "@127.0.0.1:1, production (every command asks the user)",
            (await Call("server_list", new { })).Text);
        // Another folder has its own; kept away again, it is not Claude's here.
        host.Workspace = Path.GetTempPath();
        explorer.Refresh();
        Assert.False(explorer.Find("lab")!.IsForClaude);
        host.Workspace = shop;
        explorer.Refresh();
        explorer.SetForClaude(explorer.Find("prod-1")!, false);
        Assert.Equal("lab: tester@127.0.0.1:1", (await Call("server_list", new { })).Text);

        ClaudeToolPlan Plan(string tool, object input) => server.Plan(ClaudeToolServer.Prefix + tool, JsonSerializer.SerializeToElement(input))!;
        Assert.Equal(ToolAccess.Write, Plan("server_exec", new { server = "lab", command = "uptime" }).Access);
        var production = Plan("server_exec", new { server = "prod-1", command = "rm -rf /tmp/app.log" });
        Assert.Equal((ToolAccess.Destructive, "Allow Claude to run this on prod-1 (production)?", "rm -rf /tmp/app.log"), (production.Access, production.Question, production.Code));
        Assert.Equal(ToolAccess.Read, Plan("server_logs", new { server = "lab", unit = "nginx" }).Access);
        explorer.Dispose();
    }
}
