using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Karvon.Ports;
using Karvon.Ports.Jprq;

namespace Karvon.Tests;

/// <summary>
/// A jprq server as tulki.uz's multiplexes its tunnels, on 127.0.0.1: an event port, and one public port that goes to the
/// last tunnel opened (HTTP's or TCP's). The key "good" is accepted (its name "tester", a tunnel's name when it gives
/// none); a name is refused while a tunnel of either protocol has it, and "busy" always.
/// </summary>
sealed class FakeJprq : IAsyncDisposable
{
    public const string Domain = "tunnel.test";

    readonly TcpListener _events = new(IPAddress.Loopback, 0);
    readonly TcpListener _public = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource _stop = new();
    readonly ConcurrentDictionary<uint, Socket> _visitors = new();
    readonly ConcurrentDictionary<string, byte> _names = new();
    readonly SemaphoreSlim _write = new(1, 1);
    NetworkStream? _tunnel;
    uint _next;

    public FakeJprq()
    {
        _events.Start();
        _public.Start();
        _ = AcceptTunnelsAsync();
        _ = AcceptVisitorsAsync();
    }

    public int EventPort => ((IPEndPoint)_events.LocalEndpoint).Port;

    public int PublicPort => ((IPEndPoint)_public.LocalEndpoint).Port;

    public JprqEndpoint Endpoint => new(Domain, "127.0.0.1", EventPort);

    /// <summary>The requests it was sent, as the server decodes them.</summary>
    public ConcurrentQueue<IReadOnlyDictionary<string, object>> Requests { get; } = new();

    /// <summary>The tunnel's connection is up.</summary>
    public bool HasTunnel => _tunnel is not null;

    /// <summary>A visitor is turned away (MaxConsPerTunnel) when this many are connected.</summary>
    public int MaxConnections { get; set; } = 24;

    async Task AcceptTunnelsAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await _events.AcceptSocketAsync(_stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }
            _ = ServeTunnelAsync(new NetworkStream(socket, ownsSocket: true));
        }
    }

    async Task ServeTunnelAsync(NetworkStream stream)
    {
        string? name = null;
        try
        {
            if (await JprqFrame.ReadAsync(stream, _stop.Token) is not { Type: JprqMessage.TunnelRequested } first)
                return;
            var request = Gob.Decode(first.Payload);
            Requests.Enqueue(request);
            var subdomain = request.TryGetValue("Subdomain", out var s) ? (string)s : "tester";
            var protocol = (string)request["Protocol"];
            string? error = request.GetValueOrDefault("AuthToken") as string != "good"
                ? "authentication failed\n\tobtain auth token from https://keys.example.invalid/\n"
                : subdomain == "busy" || !_names.TryAdd(subdomain, 0) ? $"subdomain is busy: {subdomain}, try another one" : null;
            name = error is null ? subdomain : null;
            var opened = Gob.Encode("TunnelOpened",
            [
                ("Hostname", Gob.String, error is null ? $"{subdomain}.{Domain}" : ""),
                ("Protocol", Gob.String, error is null ? protocol : ""),
                ("PublicServer", Gob.Uint, error is null ? (protocol == "http" ? 443 : PublicPort) : 0),
                ("ErrorMessage", Gob.String, error ?? ""),
            ]);
            await stream.WriteAsync(new JprqFrame(JprqMessage.TunnelOpened, 0, opened).ToBytes(), _stop.Token);
            if (error is not null)
                return;
            _tunnel = stream;
            while (await JprqFrame.ReadAsync(stream, _stop.Token) is { } frame)
            {
                if (frame.Type == JprqMessage.ConnectionData && _visitors.TryGetValue(frame.Stream, out var visitor))
                    await visitor.SendAsync(frame.Payload);
                else if (frame.Type == JprqMessage.ConnectionClose && _visitors.TryRemove(frame.Stream, out var closed))
                    closed.Shutdown(SocketShutdown.Both);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            if (name is not null)
                _names.TryRemove(name, out _);
            if (_tunnel == stream)
                _tunnel = null;
            await stream.DisposeAsync();
        }
    }

    async Task SendAsync(NetworkStream tunnel, JprqFrame frame)
    {
        await _write.WaitAsync();
        try
        {
            await tunnel.WriteAsync(frame.ToBytes());
        }
        finally
        {
            _write.Release();
        }
    }

    async Task AcceptVisitorsAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            Socket visitor;
            try
            {
                visitor = await _public.AcceptSocketAsync(_stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }
            _ = ServeVisitorAsync(visitor);
        }
    }

    async Task ServeVisitorAsync(Socket visitor)
    {
        if (_tunnel is not { } tunnel)
        {
            visitor.Dispose();
            return;
        }
        var endpoint = (IPEndPoint)visitor.RemoteEndPoint!;
        if (_visitors.Count >= MaxConnections)
        {
            await SendAsync(tunnel, new JprqFrame(JprqMessage.ConnectionLimit, 0, Gob.Encode("ConnectionLimit", [("ClientIP", Gob.Bytes, endpoint.Address.MapToIPv6().GetAddressBytes())])));
            visitor.Dispose();
            return;
        }
        var id = Interlocked.Increment(ref _next);
        _visitors[id] = visitor;
        try
        {
            await SendAsync(tunnel, new JprqFrame(JprqMessage.ConnectionOpened, id, Gob.Encode("ConnectionOpened",
                [("ClientIP", Gob.Bytes, endpoint.Address.MapToIPv6().GetAddressBytes()), ("ClientPort", Gob.Uint, endpoint.Port)])));
            var buffer = new byte[8192];
            while (true)
            {
                var n = await visitor.ReceiveAsync(buffer);
                if (n == 0)
                    break;
                await SendAsync(tunnel, new JprqFrame(JprqMessage.ConnectionData, id, buffer[..n]));
            }
            if (_visitors.TryRemove(id, out _))
                await SendAsync(tunnel, new JprqFrame(JprqMessage.ConnectionClose, id, []));
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            _visitors.TryRemove(id, out _);
        }
    }

    /// <summary>The tunnel's connection dropped (a NAT forgot it, the server restarted).</summary>
    public void Drop() => _tunnel?.Dispose();

    /// <summary>A visitor's HTTP request through the public port: the response's body.</summary>
    public async Task<string> GetAsync(string host, string path = "/")
    {
        using var visitor = new TcpClient();
        await visitor.ConnectAsync(IPAddress.Loopback, PublicPort);
        var stream = visitor.GetStream();
        await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: {host}\r\nConnection: close\r\n\r\n"));
        var response = await new StreamReader(stream).ReadToEndAsync();
        return response[(response.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..];
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _events.Stop();
        _public.Stop();
        _tunnel?.Dispose();
        foreach (var visitor in _visitors.Values)
            visitor.Dispose();
    }
}

/// <summary>A local web app: it answers every request with its path and the Host it was sent with.</summary>
sealed class LocalWebApp : IDisposable
{
    readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource _stop = new();

    public LocalWebApp()
    {
        _listener.Start();
        _ = ServeAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }
            _ = Task.Run(async () =>
            {
                using (client)
                {
                    var stream = client.GetStream();
                    var reader = new StreamReader(stream);
                    var line = await reader.ReadLineAsync() ?? "";
                    var host = "";
                    for (string? header; (header = await reader.ReadLineAsync()) is { Length: > 0 };)
                        if (header.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
                            host = header[5..].Trim();
                    var body = $"{line.Split(' ').ElementAtOrDefault(1)} from {host}";
                    await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}"));
                }
            });
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }
}
