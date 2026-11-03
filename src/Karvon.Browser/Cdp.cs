using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Karvon.Browser;

/// <summary>
/// A connection to Chrome's DevTools protocol (its browser endpoint's WebSocket): commands answered by id, a page's
/// through its session (attached flat), and the events each sends.
/// </summary>
public sealed class CdpConnection : IAsyncDisposable
{
    readonly ClientWebSocket _socket = new();
    readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> _pending = new();
    readonly SemaphoreSlim _send = new(1, 1);
    readonly CancellationTokenSource _closing = new();
    Task? _reading;
    int _next;

    /// <summary>An event: its method, its params, the session it came from (null for the browser's own).</summary>
    public event Action<string, JsonObject, string?>? Event;

    /// <summary>The connection closed (Chrome quit, or it was closed).</summary>
    public event Action? Closed;

    public bool IsOpen => _socket.State == WebSocketState.Open;

    public static async Task<CdpConnection> ConnectAsync(Uri endpoint, CancellationToken ct)
    {
        var connection = new CdpConnection();
        connection._socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        await connection._socket.ConnectAsync(endpoint, ct);
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
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
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
        var buffer = new byte[1 << 16];
        using var message = new MemoryStream();
        try
        {
            while (_socket.State == WebSocketState.Open && !_closing.IsCancellationRequested)
            {
                var received = await _socket.ReceiveAsync(buffer, _closing.Token);
                if (received.MessageType == WebSocketMessageType.Close)
                    break;
                message.Write(buffer, 0, received.Count);
                if (!received.EndOfMessage)
                    continue;
                var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                message.SetLength(0);
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
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or System.Text.Json.JsonException or ObjectDisposedException)
        {
        }
        foreach (var waiting in _pending.Values)
            waiting.TrySetException(new InvalidOperationException("Chrome closed the connection."));
        _pending.Clear();
        Closed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        _closing.Cancel();
        try
        {
            if (_socket.State == WebSocketState.Open)
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) when (ex is WebSocketException or TimeoutException or ObjectDisposedException or OperationCanceledException)
        {
        }
        if (_reading is not null)
            await _reading.WaitAsync(TimeSpan.FromSeconds(2)).ContinueWith(_ => { });
        _socket.Dispose();
    }
}
