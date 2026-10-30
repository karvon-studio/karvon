using System.Diagnostics;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Karvon.Claude;
using Karvon.Claude.Tools;
using Karvon.Docker.ViewModels;
using Karvon.Servers;
using Karvon.Servers.ViewModels;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>
/// The Servers explorer against a real sshd: a throwaway container of the test's own (karvon-live-ssh-..., from the local
/// redis:7-alpine image, with openssh added), removed at the end; nothing else is touched. It shares the host's network
/// (Docker's own may not reach the internet for apk) and its sshd listens on 127.0.0.1 only, on a free port. Skipped
/// unless KARVON_SSH_LIVE=1 (it needs Docker, that image, and the network for apk).
/// </summary>
public class ServersLiveTests
{
    static string Run(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return output.Trim();
    }

    [AvaloniaFact]
    public async Task The_explorer_works_with_a_real_ssh_server()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("KARVON_SSH_LIVE") == "1", "Set KARVON_SSH_LIVE=1 to try the Servers explorer against a throwaway sshd container.");
        var name = "karvon-live-ssh-" + Guid.NewGuid().ToString("N")[..8];
        var ssh = Path.Combine(SshConfig.Home, ".ssh");
        var key = Path.Combine(ssh, "live_ed25519");
        File.Delete(key);
        File.Delete(key + ".pub");
        Run("ssh-keygen", "-q", "-t", "ed25519", "-N", "", "-f", key);
        var publicKey = File.ReadAllText(key + ".pub").Trim();
        var free = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        free.Start();
        var port = ((System.Net.IPEndPoint)free.LocalEndpoint).Port;
        free.Stop();
        var setup = string.Join(" && ",
            "apk add --no-cache openssh-server openssh-sftp-server",
            "ssh-keygen -A",
            "adduser -D tester",
            "echo 'tester:not-a-real-password' | chpasswd",
            "mkdir -p /home/tester/.ssh",
            $"echo '{publicKey}' > /home/tester/.ssh/authorized_keys",
            "chown -R tester /home/tester/.ssh && chmod 700 /home/tester/.ssh && chmod 600 /home/tester/.ssh/authorized_keys",
            "echo 'line one' > /tmp/app.log && chown tester /tmp/app.log",
            "exec /usr/sbin/sshd -D -e -o PasswordAuthentication=yes -o ListenAddress=127.0.0.1 -o Port=" + port);
        Run("docker", "run", "-d", "--name", name, "--network", "host", "--entrypoint", "sh", "redis:7-alpine", "-c", setup);
        var explorer = default(ServersExplorerViewModel);
        try
        {
            File.WriteAllText(SshConfig.ConfigFile,
                $"Host live\n  HostName 127.0.0.1\n  Port {port}\n  User tester\n  IdentityFile {key}\n\nHost live-password\n  HostName 127.0.0.1\n  Port {port}\n  User tester\n  IdentityFile {ssh}/none\n");
            var host = new TestServersHost();
            explorer = new ServersExplorerViewModel(host);
            var live = explorer.Find("live")!;

            // sshd starts after apk: the first connection shows its host key, which is trusted once and kept.
            var connected = false;
            for (var attempt = 0; attempt < 60 && !connected; attempt++)
            {
                // The setup failed (apk without the network, say): its output says why, instead of two minutes of refusals.
                var state = Run("docker", "inspect", "-f", "{{.State.Status}} {{.State.ExitCode}}", name);
                Assert.True(state.StartsWith("running", StringComparison.Ordinal), $"{name} is {state}:\n{Run("docker", "logs", "--tail", "40", name)}");
                var connecting = explorer.ConnectAsync(live);
                await WaitUntil(() => connecting.IsCompleted || explorer.Dialog is TrustDialog, TimeSpan.FromSeconds(20));
                if (explorer.Dialog is TrustDialog trust)
                {
                    Assert.StartsWith("SHA256:", trust.Fingerprint);
                    trust.Confirm();
                }
                connected = await connecting;
                if (!connected)
                    await WaitUntil(() => false, TimeSpan.FromSeconds(2));
            }
            Assert.True(connected, $"{live.Error}\n{Run("docker", "logs", "--tail", "40", name)}");
            Assert.Contains($"127.0.0.1:{port}", File.ReadAllText(Path.Combine(host.ConfigDirectory, "ssh_known_hosts.json")));
            Assert.NotNull(live.Latency);

            // Its page: the state, its files, its logs.
            var page = explorer.Open(live).Page;
            await WaitUntil(() => page.Metrics is not null, TimeSpan.FromSeconds(30));
            Assert.Contains("Alpine", page.Metrics!.System);
            Assert.True(page.Metrics.MemoryTotal > 0 && page.Metrics.Cpus > 0, page.Metrics.ToString());
            page.Tab = "files";
            await WaitUntil(() => page.CurrentPath == "/home/tester", TimeSpan.FromSeconds(10));
            var local = Path.Combine(Path.GetTempPath(), "karvon-upload-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(local, "hello from Karvon\n");
            await page.UploadAsync([local]);
            var uploaded = Assert.Single(page.Entries, e => e.Name == Path.GetFileName(local));
            await page.OpenEntryCommand.ExecuteAsync(uploaded);
            var copy = Assert.Single(host.Opened);
            Assert.Equal("hello from Karvon\n", File.ReadAllText(copy));
            // Saved in the editor: back on the machine.
            File.WriteAllText(copy, "changed in Karvon\n");
            await explorer.OnFileSavedAsync(copy);
            Assert.Equal("changed in Karvon", (await explorer.Connections.RunAsync(live.Host, $"cat {uploaded.Path}")).Output);

            page.Tab = "services";
            await WaitUntil(() => page.ServicesProblem is not null, TimeSpan.FromSeconds(15));
            Assert.StartsWith("No systemd here", page.ServicesProblem);
            (page.LogFile, page.LogLines) = ("/tmp/app.log", 50);
            page.Tab = "logs";
            await WaitUntil(() => page.Logs.Document.Text.Contains("line one", StringComparison.Ordinal), TimeSpan.FromSeconds(15));
            page.FollowLogs = true;
            await WaitUntil(() => false, TimeSpan.FromSeconds(1));
            var appended = await explorer.Connections.RunAsync(live.Host, "echo 'password=hunter2 line two' >> /tmp/app.log");
            Assert.True(appended.Succeeded, appended.Combined);
            await WaitUntil(() => page.Logs.Document.Text.Contains("line two", StringComparison.Ordinal), TimeSpan.FromSeconds(15));
            Assert.Contains("password=*** line two", page.Logs.Document.Text);
            page.FollowLogs = false;

            // Claude: a command through Karvon's connection; on a production machine it is destructive.
            var (docker, _, _) = FakeDocker.Create();
            var tools = new TestToolHost(null, new Docker.DockerEngine(docker)).Tools(() => explorer);
            var message = ClaudeIdeToolsTests.Allowed(tools, "server_exec", new { server = "live", command = "uname -s" });
            Assert.Equal("Exit code 0.\nLinux", (await tools.HandleAsync(message, CancellationToken.None))["result"]!["content"]![0]!["text"]!.GetValue<string>());

            // A password, asked once (no key gets in), then kept for the session.
            var byPassword = explorer.Find("live-password")!;
            var signingIn = explorer.ConnectAsync(byPassword);
            await WaitUntil(() => explorer.Dialog is SecretDialog || signingIn.IsCompleted, TimeSpan.FromSeconds(20));
            var secret = Assert.IsType<SecretDialog>(explorer.Dialog);
            secret.Secret = "not-a-real-password";
            secret.Confirm();
            Assert.True(await signingIn, byPassword.Error);
            explorer.Disconnect(byPassword);
            Assert.True(await explorer.ConnectAsync(byPassword));
            Assert.Null(explorer.Dialog);
        }
        finally
        {
            explorer?.Dispose();
            Run("docker", "rm", "-f", name);
            File.Delete(SshConfig.ConfigFile);
        }
    }
}
