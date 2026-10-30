using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Karvon.Claude.Tools;

namespace Karvon.Claude;

/// <summary>
/// A gateway inside Karvon for one Claude Code session (its <c>ANTHROPIC_BASE_URL</c>): what Claude Code sends the model,
/// what its own tools read among it (a file's contents, a command's output), goes through it, and the secrets Karvon
/// knows, the tokens told by their form and the passwords told by their place are marked in the conversation's messages
/// before they go on (<see cref="SecretMasks"/>), the same each time, so the prompt cache holds. The answers come back as they are, streamed. It listens on 127.0.0.1 only, at a path
/// of its own no other process knows; a Claude subscription's sign-in goes through it as an API key does (Claude Code
/// 2.1.312 sends its OAuth bearer to the base URL it is given).
/// </summary>
public sealed class ClaudeGateway : IAsyncDisposable
{
    /// <summary>Where the requests go on: the API, or the base URL the user had set (their own gateway).</summary>
    public Uri Upstream { get; }

    /// <summary>What Claude Code is given as its ANTHROPIC_BASE_URL.</summary>
    public string BaseUrl { get; }

    /// <summary>What was hidden of a request (the kinds, «secret: …» as Claude got them), as it went.</summary>
    public event Action<IReadOnlyList<string>>? Hidden;

    readonly HttpListener _listener = new();
    readonly HttpClient _client;
    readonly string _prefix;
    readonly CancellationTokenSource _stop = new();

    ClaudeGateway(Uri upstream, HttpMessageHandler? handler)
    {
        Upstream = upstream;
        var port = FreePort();
        _prefix = "/" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        BaseUrl = $"http://127.0.0.1:{port}{_prefix}";
        _listener.Prefixes.Add(BaseUrl + "/");
        _client = new HttpClient(handler ?? new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.None }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>A gateway to <paramref name="upstream"/> (the API when null), listening.</summary>
    public static ClaudeGateway Start(string? upstream = null, HttpMessageHandler? handler = null)
    {
        var gateway = new ClaudeGateway(new Uri((upstream is { Length: > 0 } ? upstream : "https://api.anthropic.com").TrimEnd('/') + "/"), handler);
        gateway._listener.Start();
        _ = gateway.ServeAsync();
        return gateway;
    }

    static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }
            _ = ForwardAsync(context);
        }
    }

    static readonly HashSet<string> NotForwarded = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Connection", "Content-Length", "Transfer-Encoding", "Keep-Alive", "Proxy-Connection", "Upgrade", "Expect",
    };

    async Task ForwardAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        try
        {
            var path = request.Url!.AbsolutePath;
            if (!path.StartsWith(_prefix + "/", StringComparison.Ordinal))
            {
                response.StatusCode = 404;
                response.Close();
                return;
            }
            var target = new Uri(Upstream, path[(_prefix.Length + 1)..] + request.Url.Query);
            using var body = new MemoryStream();
            await request.InputStream.CopyToAsync(body, _stop.Token);
            var bytes = body.ToArray();
            if (request.HttpMethod == "POST" && target.AbsolutePath.StartsWith("/v1/messages", StringComparison.Ordinal))
                bytes = Scan(bytes);
            using var outgoing = new HttpRequestMessage(new HttpMethod(request.HttpMethod), target);
            if (bytes.Length > 0 || request.HttpMethod is "POST" or "PUT" or "PATCH")
                outgoing.Content = new ByteArrayContent(bytes);
            foreach (var name in request.Headers.AllKeys.OfType<string>())
            {
                if (NotForwarded.Contains(name))
                    continue;
                var values = request.Headers.GetValues(name) ?? [];
                if (!outgoing.Headers.TryAddWithoutValidation(name, values))
                    outgoing.Content?.Headers.TryAddWithoutValidation(name, values);
            }
            using var answer = await _client.SendAsync(outgoing, HttpCompletionOption.ResponseHeadersRead, _stop.Token);
            response.StatusCode = (int)answer.StatusCode;
            foreach (var (name, values) in answer.Headers.Concat(answer.Content.Headers))
                if (!NotForwarded.Contains(name))
                    response.Headers[name] = string.Join(", ", values);
            response.SendChunked = true;
            // Streamed as it comes (server-sent events): each read goes out at once.
            await using var stream = await answer.Content.ReadAsStreamAsync(_stop.Token);
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, _stop.Token)) > 0)
            {
                await response.OutputStream.WriteAsync(buffer.AsMemory(0, read), _stop.Token);
                await response.OutputStream.FlushAsync(_stop.Token);
            }
            response.Close();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or HttpListenerException or OperationCanceledException or ObjectDisposedException)
        {
            try
            {
                if (response.OutputStream.CanWrite && response.StatusCode == 200 && !response.SendChunked)
                {
                    response.StatusCode = 502;
                    var text = Encoding.UTF8.GetBytes(new JsonObject
                    {
                        ["type"] = "error",
                        ["error"] = new JsonObject { ["type"] = "api_error", ["message"] = $"Karvon's gateway could not reach {Upstream.Host}: {ex.Message}" },
                    }.ToJsonString());
                    response.ContentType = "application/json";
                    await response.OutputStream.WriteAsync(text);
                }
                response.Close();
            }
            catch (Exception closing) when (closing is IOException or HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
            }
        }
    }

    /// <summary>A request's body with the secrets hidden in its messages (text, tool results); as it was when there are none.</summary>
    byte[] Scan(byte[] body)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(body);
        }
        catch (System.Text.Json.JsonException)
        {
            return body;
        }
        if (root?["messages"] is not JsonArray messages)
            return body;
        List<string> hidden = [];
        foreach (var message in messages)
            if (message?["content"] is { } content)
                Hide(message.AsObject(), "content", content, hidden);
        if (hidden.Count == 0)
            return body;
        Hidden?.Invoke(hidden);
        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    /// <summary>A content (a string, or blocks of text and tool results, whose own content is either) with its secrets hidden.</summary>
    static void Hide(JsonObject owner, string name, JsonNode content, List<string> hidden)
    {
        if (content is JsonValue value && value.TryGetValue<string>(out var text))
        {
            // Marked as Claude Code's own tools' outputs are (SecretMasks): what Claude writes back with a mark gets the value.
            var safe = SecretMasks.Hide(text);
            if (safe != text)
            {
                hidden.AddRange(Kinds(text, safe));
                owner[name] = safe;
            }
            return;
        }
        if (content is not JsonArray blocks)
            return;
        foreach (var block in blocks.OfType<JsonObject>())
            switch ((string?)block["type"])
            {
                case "text" when block["text"] is { } blockText:
                    Hide(block, "text", blockText, hidden);
                    break;
                case "tool_result" when block["content"] is { } result:
                    Hide(block, "content", result, hidden);
                    break;
            }
    }

    static IEnumerable<string> Kinds(string before, string after)
    {
        var was = Marks(before);
        foreach (var kind in Marks(after))
            if (!was.Remove(kind))
                yield return System.Text.RegularExpressions.Regex.Replace(kind, " #\\d+$", "");
    }

    static List<string> Marks(string text)
    {
        List<string> marks = [];
        for (var at = text.IndexOf("«secret: ", StringComparison.Ordinal); at >= 0; at = text.IndexOf("«secret: ", at + 1, StringComparison.Ordinal))
            if (text.IndexOf('»', at) is var end and > 0)
                marks.Add(text[(at + "«secret: ".Length)..end]);
        return marks;
    }

    public ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
        }
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}
