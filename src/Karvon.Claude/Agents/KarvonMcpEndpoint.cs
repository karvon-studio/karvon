using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Karvon.Claude;

/// <summary>
/// Karvon's tools as an MCP server over HTTP on this computer only (127.0.0.1), for an agent that cannot be given them inside
/// Karvon as Claude Code is (an Agent Client Protocol agent): each request carries a token only that agent was given, and
/// every call goes through the same <see cref="ClaudeToolServer"/> (the user's rules, the cards, the audit) as Claude's.
/// Streamable HTTP without a stream of its own: a JSON-RPC message (or a batch) in, its answer out.
/// </summary>
public sealed class KarvonMcpEndpoint : IAsyncDisposable
{
    readonly HttpListener _listener = new();
    readonly ClaudeToolServer _tools;
    readonly object? _caller;
    readonly CancellationTokenSource _stop = new();
    readonly Task _loop;

    KarvonMcpEndpoint(ClaudeToolServer tools, object? caller, int port)
    {
        _tools = tools;
        _caller = caller;
        Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        Url = $"http://127.0.0.1:{port}/mcp";
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/mcp/");
        _listener.Start();
        _loop = Task.Run(LoopAsync);
    }

    public string Url { get; }

    /// <summary>What the agent sends as "Authorization: Bearer ...".</summary>
    public string Token { get; }

    /// <summary>Starts it on a free port; <paramref name="caller"/> is the session its calls come from (where the user is asked).</summary>
    public static KarvonMcpEndpoint Start(ClaudeToolServer tools, object? caller)
    {
        for (var attempt = 0; ; attempt++)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            try
            {
                return new KarvonMcpEndpoint(tools, caller, port);
            }
            // Taken between the probe and the listener: another port.
            catch (HttpListenerException) when (attempt < 5)
            {
            }
        }
    }

    async Task LoopAsync()
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
            _ = Task.Run(() => AnswerAsync(context));
        }
    }

    async Task AnswerAsync(HttpListenerContext context)
    {
        var response = context.Response;
        try
        {
            var token = context.Request.Headers["Authorization"];
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token ?? ""), Encoding.UTF8.GetBytes("Bearer " + Token)))
            {
                response.StatusCode = 401;
                response.Close();
                return;
            }
            if (context.Request.HttpMethod != "POST")
            {
                // No stream of its own to open (GET), nothing to end (DELETE).
                response.StatusCode = context.Request.HttpMethod == "DELETE" ? 200 : 405;
                response.Close();
                return;
            }
            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            JsonElement message;
            try
            {
                using var document = JsonDocument.Parse(await reader.ReadToEndAsync(_stop.Token));
                message = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                await WriteAsync(response, ClaudeToolServer.Failure(null, -32700, "Not JSON."));
                return;
            }
            if (message.ValueKind == JsonValueKind.Array)
            {
                var answers = new JsonArray();
                foreach (var one in message.EnumerateArray())
                    if (await AnswerOneAsync(one) is { } answer)
                        answers.Add(answer);
                if (answers.Count == 0)
                    Accepted(response);
                else
                    await WriteAsync(response, answers);
            }
            else if (await AnswerOneAsync(message) is { } answer)
                await WriteAsync(response, answer);
            else
                Accepted(response);
        }
        catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }

    /// <summary>A request's answer; null for a notification (or a client's answer), which has none.</summary>
    async Task<JsonNode?> AnswerOneAsync(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("method", out _))
            return null;
        var answer = await _tools.HandleAsync(message, _stop.Token, _caller);
        return message.TryGetProperty("id", out var id) && id.ValueKind != JsonValueKind.Null ? answer : null;
    }

    static void Accepted(HttpListenerResponse response)
    {
        response.StatusCode = 202;
        response.Close();
    }

    static async Task WriteAsync(HttpListenerResponse response, JsonNode body)
    {
        var bytes = Encoding.UTF8.GetBytes(body.ToJsonString());
        response.ContentType = "application/json";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
        }
        try
        {
            await _loop;
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
        {
        }
        _stop.Dispose();
    }
}
