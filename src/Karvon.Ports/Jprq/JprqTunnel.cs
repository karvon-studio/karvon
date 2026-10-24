using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace Karvon.Ports.Jprq;

/// <summary>A tunnel the server refused (its own words: a busy subdomain, a key it does not accept, a limit).</summary>
public sealed class JprqException(string message) : Exception(message);

/// <summary>
/// One open jprq tunnel: its connection to the server's event port, and a connection to the local port for each visitor
/// of its public address (their bytes as the stream's frames, in order each way). As jprq's own client: TCP keepalive
/// every 15 s (NATs drop a quiet connection), the server's pings let through; a connection quiet for longer than the
/// server's pings come is taken as lost.
/// </summary>
public sealed class JprqTunnel : IAsyncDisposable
{
    readonly Socket _socket;
    readonly NetworkStream _server;
    readonly SemaphoreSlim _write = new(1, 1);
    readonly ConcurrentDictionary<uint, Visitor> _visitors = new();
    readonly CancellationTokenSource _stop = new();
    readonly string _localHost;
    readonly int _localPort;
    readonly TimeSpan _silence;
    Task _pump = Task.CompletedTask;
    long _lastFrame = Environment.TickCount64;
    int _closed;

    JprqTunnel(Socket socket, TunnelOpened opened, string localHost, int localPort, TimeSpan silence)
    {
        _silence = silence;
        _socket = socket;
        _server = new NetworkStream(socket, ownsSocket: true);
        Opened = opened;
        _localHost = localHost;
        _localPort = localPort;
    }

    public TunnelOpened Opened { get; private set; }

    /// <summary>Where the internet reaches it: https://name.domain for HTTP (the server's TLS), host:port for TCP.</summary>
    public string PublicAddress => Opened.Protocol == "http" ? $"https://{Opened.Hostname}" : $"{Opened.Hostname}:{Opened.PublicServer}";

    /// <summary>The visitors connected now.</summary>
    public int Connections => _visitors.Count;

    /// <summary>The visitors connected since it opened.</summary>
    public int TotalConnections { get; private set; }

    /// <summary>A visitor came or went (on the tunnel's own thread).</summary>
    public event Action? ConnectionsChanged;

    /// <summary>The server turned a visitor away: the tunnel has as many as it takes.</summary>
    public event Action<IPAddress?>? LimitReached;

    /// <summary>The tunnel is closed: why, or null when it was closed here.</summary>
    public event Action<string?>? Closed;

    /// <summary>
    /// A tunnel from the server at <paramref name="host"/>:<paramref name="port"/> (its event port) to the local port:
    /// opened, or the server's reason it is not (<see cref="JprqException"/>), or why it could not be reached. Without a
    /// frame for <paramref name="silence"/> (35 s; the server pings every 10 s) the connection counts as lost.
    /// </summary>
    public static async Task<JprqTunnel> OpenAsync(string host, int port, TunnelRequest request, int localPort, CancellationToken ct,
        string localHost = "localhost", TimeSpan? silence = null)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        try
        {
            KeepAlive(socket, 15);
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connect.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    await socket.ConnectAsync(new DnsEndPoint(host, port), connect.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new IOException($"{host}:{port} does not answer.");
                }
                catch (SocketException ex)
                {
                    throw new IOException($"Cannot reach the tunnel service at {host}:{port}: {ex.Message}", ex);
                }
            }
            var tunnel = new JprqTunnel(socket, null!, localHost, localPort, silence ?? TimeSpan.FromSeconds(35));
            await tunnel.SendAsync(new JprqFrame(JprqMessage.TunnelRequested, 0, request.Encode()), ct);
            JprqFrame? first;
            using (var answer = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                answer.CancelAfter(TimeSpan.FromSeconds(30));
                try
                {
                    first = await JprqFrame.ReadAsync(tunnel._server, answer.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new IOException($"{host}:{port} did not answer the request.");
                }
            }
            if (first is not { Type: JprqMessage.TunnelOpened } opened)
                throw new IOException(first is null ? $"{host}:{port} closed the connection." : $"{host}:{port} answered with a frame of type {first.Value.Type}.");
            tunnel.Opened = TunnelOpened.Decode(opened.Payload);
            if (tunnel.Opened.ErrorMessage is { Length: > 0 } refused)
                throw new JprqException(string.Join(" ", refused.Split(['\n', '\t'], StringSplitOptions.RemoveEmptyEntries)));
            tunnel._pump = Task.Run(tunnel.PumpAsync);
            return tunnel;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    static void KeepAlive(Socket socket, int seconds)
    {
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, seconds);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, seconds);
        }
        catch (SocketException)
        {
            // A system without these options keeps its own defaults.
        }
    }

    async Task SendAsync(JprqFrame frame, CancellationToken ct)
    {
        var bytes = frame.ToBytes();
        await _write.WaitAsync(ct);
        try
        {
            await _server.WriteAsync(bytes, ct);
        }
        finally
        {
            _write.Release();
        }
    }

    /// <summary>The server's frames, until the connection ends (or goes quiet longer than its pings allow).</summary>
    async Task PumpAsync()
    {
        string? reason = null;
        using var watchdog = new Timer(_ =>
        {
            if (Environment.TickCount64 - Interlocked.Read(ref _lastFrame) > _silence.TotalMilliseconds)
            {
                reason = "The connection to the tunnel's server was lost (no answer).";
                _socket.Dispose();
            }
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        try
        {
            while (await JprqFrame.ReadAsync(_server, _stop.Token) is { } frame)
            {
                Interlocked.Exchange(ref _lastFrame, Environment.TickCount64);
                switch (frame.Type)
                {
                    case JprqMessage.ConnectionOpened:
                        Open(frame.Stream);
                        break;
                    case JprqMessage.ConnectionData when _visitors.TryGetValue(frame.Stream, out var visitor):
                        visitor.Incoming.Writer.TryWrite(frame.Payload);
                        break;
                    case JprqMessage.ConnectionClose:
                        Close(frame.Stream, tellServer: false);
                        break;
                    case JprqMessage.ConnectionLimit:
                        LimitReached?.Invoke(Decode(frame.Payload));
                        break;
                }
            }
            reason ??= "The tunnel's server closed the connection.";
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            reason ??= _stop.IsCancellationRequested ? null : "The connection to the tunnel's server was lost: " + ex.Message;
        }
        Shut(_stop.IsCancellationRequested ? null : reason);
    }

    static IPAddress? Decode(byte[] payload)
    {
        try
        {
            return ConnectionOpened.Ip(Gob.Decode(payload));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>A visitor: its stream now (the bytes that come before the local port answers wait), the local port dialed.</summary>
    void Open(uint stream)
    {
        var visitor = new Visitor();
        if (!_visitors.TryAdd(stream, visitor))
            return;
        TotalConnections++;
        ConnectionsChanged?.Invoke();
        _ = Task.Run(() => ServeAsync(stream, visitor));
    }

    async Task ServeAsync(uint stream, Visitor visitor)
    {
        var local = new Socket(SocketType.Stream, ProtocolType.Tcp);
        visitor.Local = local;
        try
        {
            await local.ConnectAsync(new DnsEndPoint(_localHost, _localPort), visitor.Stop.Token);
            KeepAlive(local, 30);
            var toLocal = Task.Run(async () =>
            {
                await foreach (var bytes in visitor.Incoming.Reader.ReadAllAsync(visitor.Stop.Token))
                    await local.SendAsync(bytes, visitor.Stop.Token);
            });
            var buffer = new byte[JprqFrame.MaxData];
            while (true)
            {
                var n = await local.ReceiveAsync(buffer, visitor.Stop.Token);
                if (n == 0)
                    break;
                await SendAsync(new JprqFrame(JprqMessage.ConnectionData, stream, buffer[..n]), _stop.Token);
            }
            // What the visitor sent last may still be on its way to the local port.
            visitor.Incoming.Writer.TryComplete();
            await Task.WhenAny(toLocal, Task.Delay(TimeSpan.FromSeconds(1)));
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // The local port refused it, or one side closed: the visitor's connection closes.
        }
        Close(stream, tellServer: true);
    }

    void Close(uint stream, bool tellServer)
    {
        if (!_visitors.TryRemove(stream, out var visitor))
            return;
        visitor.Incoming.Writer.TryComplete();
        if (!tellServer)
        {
            // The visitor left: what it sent goes to the local port first, then the connection ends.
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200));
                visitor.Dispose();
            });
        }
        else
        {
            visitor.Dispose();
            if (_closed == 0)
                _ = SendAsync(new JprqFrame(JprqMessage.ConnectionClose, stream, []), _stop.Token).ContinueWith(_ => { }, TaskScheduler.Default);
        }
        ConnectionsChanged?.Invoke();
    }

    void Shut(string? reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
            return;
        foreach (var stream in _visitors.Keys)
            if (_visitors.TryRemove(stream, out var visitor))
                visitor.Dispose();
        _socket.Dispose();
        ConnectionsChanged?.Invoke();
        Closed?.Invoke(reason);
    }

    int _disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await _stop.CancelAsync();
        Shut(null);
        await _pump.ContinueWith(_ => { }, TaskScheduler.Default);
        _stop.Dispose();
    }

    sealed class Visitor : IDisposable
    {
        public Channel<byte[]> Incoming { get; } = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });

        public CancellationTokenSource Stop { get; } = new();

        public Socket? Local { get; set; }

        public void Dispose()
        {
            Stop.Cancel();
            Local?.Dispose();
        }
    }
}
