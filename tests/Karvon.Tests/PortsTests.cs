using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Karvon.Claude;
using Karvon.Ports;
using Karvon.Ports.Jprq;
using Karvon.Ports.ViewModels;
using Karvon.Ports.Views;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>What the Ports window asked of Karvon, and the running program's addresses it is told.</summary>
sealed class TestPortsHost : IPortsHost
{
    public string ConfigDirectory { get; } = Path.Combine(Path.GetTempPath(), "karvon-ports-" + Guid.NewGuid().ToString("N"));

    public List<string> Listening { get; } = [];

    public IReadOnlyList<string> ListeningUrls => Listening;

    public event Action? ListeningChanged;

    public void Say(params string[] urls)
    {
        Listening.Clear();
        Listening.AddRange(urls);
        ListeningChanged?.Invoke();
    }

    public List<string> Opened { get; } = [];

    public List<string> Statuses { get; } = [];

    public void OpenUrl(string url) => Opened.Add(url);

    public void Status(string text) => Statuses.Add(text);
}

/// <summary>Ports: jprq tunnels through a fake server (tulki.uz's protocol), the window, its dialogs and Claude's tools.</summary>
public class PortsTests
{
    static PortsViewModel NewPorts(FakeJprq server, TestPortsHost host) => new(host, keyring: null, (_, _) => Task.FromResult(server.Endpoint));

    [Fact]
    public async Task A_tunnel_carries_visitors_to_the_local_port_and_back()
    {
        await using var server = new FakeJprq();
        using var app = new LocalWebApp();
        await using var tunnel = await JprqTunnel.OpenAsync("127.0.0.1", server.EventPort, new TunnelRequest("http", "demo", "good", "karvon-test"), app.Port, default);
        Assert.Equal("https://demo.tunnel.test", tunnel.PublicAddress);
        var request = Assert.Single(server.Requests);
        Assert.Equal(("http", "demo", "good", "karvon-test"), ((string)request["Protocol"], (string)request["Subdomain"], (string)request["AuthToken"], (string)request["CliVersion"]));

        // Many at once, each its own stream.
        var bodies = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => server.GetAsync("demo.tunnel.test", $"/page/{i}")));
        Assert.Equal(Enumerable.Range(0, 12).Select(i => $"/page/{i} from demo.tunnel.test"), bodies);
        Assert.Equal(12, tunnel.TotalConnections);
        for (var clock = System.Diagnostics.Stopwatch.StartNew(); tunnel.Connections > 0 && clock.Elapsed < TimeSpan.FromSeconds(5);)
            await Task.Delay(50);
        Assert.Equal(0, tunnel.Connections);
    }

    [Fact]
    public async Task The_servers_refusal_is_said_in_its_own_words()
    {
        await using var server = new FakeJprq();
        var key = await Assert.ThrowsAsync<JprqException>(() => JprqTunnel.OpenAsync("127.0.0.1", server.EventPort, new TunnelRequest("http", "", "wrong", "t"), 1, default));
        Assert.Equal("authentication failed obtain auth token from https://keys.example.invalid/", key.Message);
        var busy = await Assert.ThrowsAsync<JprqException>(() => JprqTunnel.OpenAsync("127.0.0.1", server.EventPort, new TunnelRequest("http", "busy", "good", "t"), 1, default));
        Assert.Equal("subdomain is busy: busy, try another one", busy.Message);
        await Assert.ThrowsAsync<IOException>(() => JprqTunnel.OpenAsync("127.0.0.1", 1, new TunnelRequest("http", "", "good", "t"), 1, default));
    }

    [Fact]
    public async Task A_lost_or_silent_connection_says_why_it_closed()
    {
        await using var server = new FakeJprq();
        var dropped = await JprqTunnel.OpenAsync("127.0.0.1", server.EventPort, new TunnelRequest("tcp", "", "good", "t"), 1, default);
        Assert.Equal($"tester.tunnel.test:{server.PublicPort}", dropped.PublicAddress);
        var reason = new TaskCompletionSource<string?>();
        dropped.Closed += r => reason.TrySetResult(r);
        server.Drop();
        Assert.Equal("The tunnel's server closed the connection.", await reason.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        // The fake does not ping: quiet past the limit, it is taken as lost.
        await using var quiet = await JprqTunnel.OpenAsync("127.0.0.1", server.EventPort, new TunnelRequest("tcp", "", "good", "t"), 1, default, silence: TimeSpan.FromSeconds(1.5));
        var silent = new TaskCompletionSource<string?>();
        quiet.Closed += r => silent.TrySetResult(r);
        Assert.Equal("The connection to the tunnel's server was lost (no answer).", await silent.Task.WaitAsync(TimeSpan.FromSeconds(6)));

        // Closed here: no reason. (The lost one's name is the server's until it sees that connection go: a moment.)
        JprqTunnel? closed = null;
        for (var attempt = 0; closed is null; attempt++)
        {
            try
            {
                closed = await JprqTunnel.OpenAsync("127.0.0.1", server.EventPort, new TunnelRequest("tcp", "", "good", "t"), 1, default);
            }
            catch (JprqException ex) when (ex.Message.StartsWith("subdomain is busy", StringComparison.Ordinal) && attempt < 50)
            {
                await Task.Delay(100);
            }
        }
        var mine = new TaskCompletionSource<string?>();
        closed.Closed += r => mine.TrySetResult(r);
        await closed.DisposeAsync();
        Assert.Null(await mine.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [AvaloniaFact]
    public async Task Forward_a_Port_asks_for_the_key_once_and_shows_the_address()
    {
        await using var server = new FakeJprq();
        using var app = new LocalWebApp();
        var host = new TestPortsHost();
        using var ports = NewPorts(server, host);
        var window = new Window { Width = 1100, Height = 420, Content = new Grid { Children = { new PortsView { DataContext = ports }, new PortsOverlay { DataContext = ports } } } };
        window.Show();
        Assert.True(ports.IsEmpty);
        Assert.Equal("Through tulki.uz: no API key yet", ports.ServiceText);
        Assert.Contains("https://name.tulki.uz", ports.EmptyText);

        // The running program's ports are offered, an HTTPS one as TCP.
        host.Say($"http://localhost:{app.Port}", "https://localhost:7001");
        await WaitUntil(() => ports.Detected.Count == 2, TimeSpan.FromSeconds(5));
        Assert.Equal(["http", "tcp"], ports.Detected.Select(d => d.Protocol));

        // Forward…: the port given, a name; then the key, which the service's site says where to get.
        var forwarding = ports.ForwardPortCommand.ExecuteAsync(ports.Detected[0]);
        var form = Assert.IsType<ForwardPortDialog>(ports.Dialog);
        Assert.Equal(app.Port.ToString(), form.PortText);
        Assert.Equal("https://<your key's name>.tulki.uz", form.Preview);
        form.Subdomain = "Demo";
        Assert.Equal("https://demo.tulki.uz", form.Preview);
        Snapshot(window, "ports-forward");
        form.Subdomain = "x";
        Assert.Equal("The name: 3 to 38 letters, digits and hyphens.", form.Problem);
        form.Subdomain = "Demo";
        form.Confirm();
        await WaitUntil(() => ports.Dialog is PortsServiceDialog, TimeSpan.FromSeconds(5));
        var service = Assert.IsType<PortsServiceDialog>(ports.Dialog);
        Assert.Equal(("https://tulki.uz/config.json", "https://tulki.uz/"), (service.Service, service.Site));
        Assert.False(service.CanConfirm);
        service.ShowSiteCommand.Execute(null);
        Assert.Equal(["https://tulki.uz/"], host.Opened);
        Snapshot(window, "ports-key");
        service.Key = "good";
        service.Confirm();
        await forwarding;

        var forward = Assert.Single(ports.Forwards);
        Assert.Equal((PortState.Online, "https://demo.tunnel.test", "demo"), (forward.State, forward.Address, forward.Subdomain));
        Assert.Equal("Through tulki.uz", ports.ServiceText);
        Assert.Equal([7001], ports.Detected.Select(d => d.Port));
        Assert.Equal($"/ from demo.tunnel.test", await server.GetAsync("demo.tunnel.test"));
        await WaitUntil(() => forward.Connections == 0, TimeSpan.FromSeconds(5));
        Assert.Contains($"Ports: localhost:{app.Port} is at https://demo.tunnel.test.", host.Statuses);
        forward.OpenCommand.Execute(null);
        Assert.Equal("https://demo.tunnel.test", host.Opened[^1]);
        await WaitUntil(() => window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "https://demo.tunnel.test" && t.IsEffectivelyVisible), TimeSpan.FromSeconds(5));
        Snapshot(window, "ports");

        // The next one needs no key. Without a name each has the key's (TCP's too), so one of ours taking it, the next is it and its port.
        var tcp = await ports.ForwardAsync(7001, "tcp", "", 0, ask: false);
        Assert.Equal((PortState.Online, $"tester.tunnel.test:{server.PublicPort}"), (tcp.State, tcp.Address));
        Assert.Empty(ports.Detected);
        var third = await ports.ForwardAsync(8080, "http", "", 0, ask: false);
        Assert.Equal((PortState.Online, "https://tester-8080.tunnel.test"), (third.State, third.Address));
        await ports.StopAllAsync();
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_lost_tunnel_comes_back_and_a_refused_one_says_why()
    {
        var retries = PortsViewModel.Retries;
        PortsViewModel.Retries = [TimeSpan.FromMilliseconds(100)];
        try
        {
            await using var server = new FakeJprq();
            using var app = new LocalWebApp();
            var host = new TestPortsHost();
            using var ports = NewPorts(server, host);
            ports.Secrets.Set(ports.Service, "good", keep: false);

            var forward = await ports.ForwardAsync(app.Port, "http", "demo", 0, ask: false);
            Assert.Equal(PortState.Online, forward.State);
            server.Drop();
            await WaitUntil(() => forward.State == PortState.Reconnecting, TimeSpan.FromSeconds(5));
            Assert.Equal("The tunnel's server closed the connection.", forward.Error);
            await WaitUntil(() => forward.State == PortState.Online, TimeSpan.FromSeconds(5));
            Assert.Equal((PortState.Online, null), (forward.State, forward.Error));
            Assert.Equal(2, server.Requests.Count);
            Assert.Equal("/again from demo.tunnel.test", await server.GetAsync("demo.tunnel.test", "/again"));

            // Stopped: the server loses it; started again, back.
            await forward.StopCommand.ExecuteAsync(null);
            Assert.Equal((PortState.Stopped, "Stopped"), (forward.State, forward.StateText));
            await WaitUntil(() => !server.HasTunnel, TimeSpan.FromSeconds(5));
            Assert.False(server.HasTunnel);
            await forward.StartCommand.ExecuteAsync(null);
            Assert.Equal(PortState.Online, forward.State);

            // Another name for the same port: the old one goes, the new one is refused (busy).
            var busy = await ports.ForwardAsync(app.Port, "http", "busy", 0, ask: false);
            Assert.Equal((PortState.Failed, "subdomain is busy: busy, try another one"), (busy.State, busy.Error));
            Assert.Same(busy, Assert.Single(ports.Forwards));

            // What the solution keeps, and gets back stopped.
            Assert.Equal([new SavedForward(app.Port, "http", "busy", 0)], ports.Saved);
            await ports.RestoreAsync([new SavedForward(3000, "http", "", 0), new SavedForward(22, "tcp", "", 33001)]);
            Assert.Equal([(3000, PortState.Stopped), (22, PortState.Stopped)], ports.Forwards.Select(f => (f.Port, f.State)));
            await ports.Forwards[0].RemoveCommand.ExecuteAsync(null);
            Assert.Equal([22], ports.Forwards.Select(f => f.Port));
        }
        finally
        {
            PortsViewModel.Retries = retries;
        }
    }

    [AvaloniaFact]
    public async Task Claude_forwards_a_port_through_Karvon_never_seeing_the_key()
    {
        await using var server = new FakeJprq();
        using var app = new LocalWebApp();
        var host = new TestPortsHost();
        using var ports = NewPorts(server, host);
        var tools = KarvonTools.Create(PortsClaudeTools.Create(() => ports));

        static JsonElement Input(object value) => JsonSerializer.SerializeToElement(value);
        async Task<ClaudeToolOutput> Call(string name, object input) =>
            await tools.Tools.Single(t => t.Name == name).CallAsync(Input(input), default);

        // Making a port public asks the user; listing and stopping do not.
        var plan = tools.Plan(ClaudeToolServer.Prefix + "tunnel_open", Input(new { port = app.Port }))!;
        Assert.Equal((ToolAccess.Write, $"Allow Claude to make localhost:{app.Port} public?"), (plan.Access, plan.Question));
        Assert.Contains("tulki.uz", plan.Warning);
        Assert.Equal(ToolAccess.Read, tools.Plan(ClaudeToolServer.Prefix + "tunnel_list", Input(new { }))!.Access);
        Assert.Equal(ToolAccess.Run, tools.Plan(ClaudeToolServer.Prefix + "tunnel_close", Input(new { port = 1 }))!.Access);

        // Without a key Claude is told where the user sets it (no dialog for it).
        var noKey = await Call("tunnel_open", new { port = app.Port });
        Assert.True(noKey.IsError);
        Assert.Equal($"localhost:{app.Port} is not forwarded: Ports has no API key for tulki.uz: the user sets it in View > Ports (the service button).", noKey.Text);
        Assert.Null(ports.Dialog);

        ports.Secrets.Set(ports.Service, "good", keep: false);
        var opened = await Call("tunnel_open", new { port = app.Port, subdomain = "Shop" });
        Assert.Equal($"localhost:{app.Port} is forwarded (HTTP) at https://shop.tunnel.test.", opened.Text);
        host.Say("http://localhost:5173");
        await WaitUntil(() => ports.HasDetected, TimeSpan.FromSeconds(5));
        var list = await Call("tunnel_list", new { });
        Assert.Equal($"Tunnel service: tulki.uz.\nlocalhost:{app.Port} HTTP: Online at https://shop.tunnel.test, 0 connection(s)\n"
            + "The running program listens on (not forwarded): http://localhost:5173.", list.Text);
        Assert.DoesNotContain("good", list.Text);
        Assert.Equal($"localhost:{app.Port} is no longer forwarded.", (await Call("tunnel_close", new { port = app.Port })).Text);
        Assert.True((await Call("tunnel_close", new { port = 9 })).IsError);
    }

    [AvaloniaFact]
    public async Task The_Ports_window_sits_at_the_bottom_and_offers_what_the_program_listens_on()
    {
        var vm = NewViewModel();
        var window = new Karvon.Views.MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        window.Show();
        var module = vm.Module<PortsModule>()!;
        Assert.Null(module.Ports);
        vm.ShowToolWindow(PortsModule.ToolWindowId);
        await WaitUntil(() => window.GetVisualDescendants().OfType<PortsView>().Any(v => v.IsEffectivelyVisible), TimeSpan.FromSeconds(5));
        Assert.True(window.IsToolWindowShowing(PortsModule.ToolWindowId));
        Assert.Same(module.Ports, module.Overlay);

        // The program says where it listens: offered (and gone when it stops).
        vm.IsRunning = true;
        vm.DebugOutput.Append("info: Microsoft.Hosting.Lifetime[14]");
        vm.DebugOutput.Append("      Now listening on: http://localhost:5055");
        await WaitUntil(() => module.Ports!.HasDetected, TimeSpan.FromSeconds(5));
        Assert.Equal(["http://localhost:5055"], vm.ListeningUrls);
        Assert.Equal([5055], module.Ports!.Detected.Select(d => d.Port));
        Snapshot(window, "ports-window");
        vm.IsRunning = false;
        await WaitUntil(() => !module.Ports.HasDetected, TimeSpan.FromSeconds(5));
        Assert.Empty(module.Ports.Detected);
        window.Close();
    }

    [Theory]
    [InlineData("jprq http 3000 -s demo", true)]
    [InlineData("ngrok http 8080", true)]
    [InlineData("cloudflared tunnel --url http://localhost:3000", true)]
    [InlineData("jprq auth abc", false)]
    [InlineData("cloudflared --version", false)]
    public void A_tunnel_CLI_goes_to_the_Ports_tools(string command, bool redirected) =>
        Assert.Equal(redirected, new PortsModule().ClaudeShellRules().Any(r => r.Matches(command)));

    [Fact]
    public async Task The_service_is_its_config_page_or_its_event_port()
    {
        Assert.Equal(("tulki.uz", "https://tulki.uz/"), (PortsSettings.NameOf(PortsSettings.DefaultService), PortsSettings.SiteOf(PortsSettings.DefaultService)));
        Assert.Equal(("tunnels.example.com", null), (PortsSettings.NameOf("tunnels.example.com:4321"), PortsSettings.SiteOf("tunnels.example.com:4321")));
        Assert.Equal(new JprqEndpoint("tunnels.example.com", "tunnels.example.com", 4321), await JprqEndpoint.ResolveAsync("tunnels.example.com:4321", default));
        await Assert.ThrowsAsync<IOException>(() => JprqEndpoint.ResolveAsync("not a service", default));

        // A config page, as tulki.uz serves it.
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            await new StreamReader(stream).ReadLineAsync();
            const string body = """{"domain":"tunnel.test","events":"event.tunnel.test:4321"}""";
            await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}"));
        });
        Assert.Equal(new JprqEndpoint("tunnel.test", "event.tunnel.test", 4321), await JprqEndpoint.ResolveAsync($"http://127.0.0.1:{port}/config.json", default));
        listener.Stop();

        // The setting and the key: the file has the service only.
        var host = new TestPortsHost();
        var settings = new PortsSettings(Path.Combine(host.ConfigDirectory, "ports.json"));
        Assert.Equal(PortsSettings.DefaultService, settings.Service);
        settings.Service = "tunnels.example.com:4321";
        Assert.Equal("tunnels.example.com:4321", new PortsSettings(settings.File).Service);
        var secrets = new PortsSecrets(null);
        secrets.Set(settings.Service, "k", keep: true);
        Assert.Equal("k", secrets.Get(settings.Service));
        Assert.DoesNotContain("\"k\"", File.ReadAllText(settings.File));
        secrets.Forget(settings.Service);
        Assert.Null(secrets.Get(settings.Service));
    }
}
