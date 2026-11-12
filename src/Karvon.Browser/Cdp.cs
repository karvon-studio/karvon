using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Karvon.Browser;

/// <summary>
/// A connection to Chrome's DevTools protocol: commands answered by id, a page's through its session (attached flat), and
/// the events each sends. Over the pipe Chrome was started with (<see cref="CdpPipe"/>), which nothing else can reach;
/// on Windows over its browser endpoint's WebSocket.
/// </summary>
public sealed class CdpConnection : IAsyncDisposable
{
    readonly ICdpTransport _transport;
    readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> _pending = new();
    readonly SemaphoreSlim _send = new(1, 1);
    readonly CancellationTokenSource _closing = new();
    Task? _reading;
    int _next;

    CdpConnection(ICdpTransport transport) => _transport = transport;

    /// <summary>An event: its method, its params, the session it came from (null for the browser's own).</summary>
    public event Action<string, JsonObject, string?>? Event;

    /// <summary>The connection closed (Chrome quit, or it was closed).</summary>
    public event Action? Closed;

    public bool IsOpen => _transport.IsOpen;

    /// <summary>Over a WebSocket (a browser endpoint's address).</summary>
    public static async Task<CdpConnection> ConnectAsync(Uri endpoint, CancellationToken ct) =>
        Start(await WebSocketTransport.ConnectAsync(endpoint, ct));

    /// <summary>Over the pipe of the Chrome Karvon started.</summary>
    public static CdpConnection Over(CdpPipe pipe) => Start(pipe);

    static CdpConnection Start(ICdpTransport transport)
    {
        var connection = new CdpConnection(transport);
        connection._reading = Task.Run(connection.ReadAsync);
        return connection;
    }

    /// <summary>A command (to a page's session when one is given): its result; an error Chrome gives is thrown.</summary>
    public async Task<JsonObject> SendAsync(string method, JsonObject? parameters = null, string? session = null, CancellationToken ct = default, TimeSpan? timeout = null)
    {
        var id = Interlocked.Increment(ref _next);
        var done = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = done;
        var message = new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters ?? new JsonObject() };
        if (session is not null)
            message["sessionId"] = session;
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        await _send.WaitAsync(ct);
        try
        {
            await _transport.SendAsync(bytes, ct);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _pending.TryRemove(id, out _);
            throw new InvalidOperationException("Chrome closed the connection.", ex);
        }
        catch
        {
            _pending.TryRemove(id, out _);
            throw;
        }
        finally
        {
            _send.Release();
        }
        try
        {
            return await done.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(60), ct);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException($"Chrome did not answer {method}.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    async Task ReadAsync()
    {
        try
        {
            while (!_closing.IsCancellationRequested && await _transport.ReceiveAsync(_closing.Token) is { } text)
            {
                if (JsonNode.Parse(text) is not JsonObject json)
                    continue;
                if (json["id"]?.GetValue<int>() is { } id)
                {
                    if (!_pending.TryRemove(id, out var waiting))
                        continue;
                    if (json["error"] is JsonObject error)
                        waiting.TrySetException(new InvalidOperationException(error["message"]?.GetValue<string>() ?? error.ToJsonString()));
                    else
                        waiting.TrySetResult(json["result"] as JsonObject ?? new JsonObject());
                }
                else if (json["method"]?.GetValue<string>() is { } method)
                {
                    try
                    {
                        Event?.Invoke(method, json["params"] as JsonObject ?? new JsonObject(), json["sessionId"]?.GetValue<string>());
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException)
                    {
                    }
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException or System.Text.Json.JsonException or ObjectDisposedException)
        {
        }
        _transport.Lost();
        foreach (var waiting in _pending.Values)
            waiting.TrySetException(new InvalidOperationException("Chrome closed the connection."));
        _pending.Clear();
        Closed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        _closing.Cancel();
        await _transport.DisposeAsync();
        if (_reading is not null)
            await _reading.WaitAsync(TimeSpan.FromSeconds(2)).ContinueWith(_ => { });
    }
}

/// <summary>How the protocol's messages go to Chrome and come back.</summary>
interface ICdpTransport : IAsyncDisposable
{
    bool IsOpen { get; }

    Task SendAsync(byte[] message, CancellationToken ct);

    /// <summary>The next message; null when Chrome closed its end.</summary>
    Task<string?> ReceiveAsync(CancellationToken ct);

    /// <summary>The reading ended (Chrome closed it, or it failed): it is not open any more.</summary>
    void Lost();
}

sealed class WebSocketTransport : ICdpTransport
{
    readonly ClientWebSocket _socket = new();
    readonly byte[] _buffer = new byte[1 << 16];
    readonly MemoryStream _message = new();

    public static async Task<WebSocketTransport> ConnectAsync(Uri endpoint, CancellationToken ct)
    {
        var transport = new WebSocketTransport();
        transport._socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        try
        {
            await transport._socket.ConnectAsync(endpoint, ct);
        }
        catch
        {
            transport._socket.Dispose();
            throw;
        }
        return transport;
    }

    public bool IsOpen => _socket.State == WebSocketState.Open;

    public Task SendAsync(byte[] message, CancellationToken ct) => _socket.SendAsync(message, WebSocketMessageType.Text, true, ct);

    public async Task<string?> ReceiveAsync(CancellationToken ct)
    {
        while (_socket.State == WebSocketState.Open)
        {
            var received = await _socket.ReceiveAsync(_buffer, ct);
            if (received.MessageType == WebSocketMessageType.Close)
                return null;
            _message.Write(_buffer, 0, received.Count);
            if (!received.EndOfMessage)
                continue;
            var text = Encoding.UTF8.GetString(_message.GetBuffer(), 0, (int)_message.Length);
            _message.SetLength(0);
            return text;
        }
        return null;
    }

    public void Lost()
    {
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_socket.State == WebSocketState.Open)
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) when (ex is WebSocketException or TimeoutException or ObjectDisposedException or OperationCanceledException)
        {
        }
        _socket.Dispose();
    }
}
