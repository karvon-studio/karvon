using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Karvon.Tests;

/// <summary>A server of a test's: each request answered by its function, and kept.</summary>
sealed class PostmanServer : IDisposable
{
    public sealed record Seen(string Method, string Path, string Body, IReadOnlyDictionary<string, string> Headers, IPEndPoint From);

    public sealed record Answer(string Body, int Status = 200, string Type = "application/json", params (string Name, string Value)[] Headers);

    readonly HttpListener _listener = new();
    readonly Func<Seen, Answer> _answer;
    readonly string _host;

    public PostmanServer(Func<Seen, Answer> answer, string host = "127.0.0.1")
    {
        (_answer, _host) = (answer, host);
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        Port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _listener.Prefixes.Add($"http://{host}:{Port}/");
        _listener.Start();
        _ = ServeAsync();
    }

    public int Port { get; }

    public string Url => $"http://{_host}:{Port}";

    public List<Seen> Requests { get; } = [];

    async Task ServeAsync()
    {
        while (_listener.IsListening)
            try
            {
                var context = await _listener.GetContextAsync();
                using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                var seen = new Seen(context.Request.HttpMethod, context.Request.Url!.PathAndQuery, await reader.ReadToEndAsync(),
                    context.Request.Headers.AllKeys.OfType<string>().ToDictionary(k => k, k => context.Request.Headers[k]!, StringComparer.OrdinalIgnoreCase), context.Request.RemoteEndPoint);
                lock (Requests)
                    Requests.Add(seen);
                var answer = _answer(seen);
                context.Response.StatusCode = answer.Status;
                context.Response.ContentType = answer.Type;
                foreach (var (name, value) in answer.Headers)
                    context.Response.Headers.Add(name, value);
                var bytes = Encoding.UTF8.GetBytes(answer.Body);
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                if (!_listener.IsListening)
                    return;
            }
    }

    public void Dispose() => _listener.Close();
}
