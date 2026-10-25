using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Karvon.Tests;

/// <summary>
/// A gh CLI of the tests' own (TestAssets/FakeGh): `auth status` signed in or not, `api` answered by the routes given
/// ("GET repos/o/r/actions/runs?..."), every call logged. A POST with no route goes through.
/// </summary>
sealed class FakeGh
{
    readonly Dictionary<string, object> _routes = [];
    bool _signedIn = true;

    public FakeGh()
    {
        Dir = Path.Combine(Path.GetTempPath(), "karvon-fake-gh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Dir);
        var script = Path.Combine(Dir, "gh.py");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "TestAssets", "FakeGh", "gh.py"), script);
        Command = TestTools.Command(script, Dir, new Dictionary<string, string> { ["FAKE_GH_DATA"] = Dir });
        Write();
    }

    public string Dir { get; }

    public string Command { get; }

    public bool SignedIn
    {
        set
        {
            _signedIn = value;
            Write();
        }
    }

    public void Route(string method, string path, object body, int code = 0, string stderr = "")
    {
        _routes[$"{method} {path}"] = new { body = body is string s ? s : JsonSerializer.Serialize(body), code, stderr };
        Write();
    }

    void Write() => File.WriteAllText(Path.Combine(Dir, "routes.json"), JsonSerializer.Serialize(new { signed_in = _signedIn, routes = _routes }));

    public IReadOnlyList<string> Calls => File.Exists(Path.Combine(Dir, "calls.txt")) ? File.ReadAllLines(Path.Combine(Dir, "calls.txt")) : [];
}

/// <summary>A GitLab's REST v4 on 127.0.0.1, its answers by route ("GET /api/v4/projects/a%2Fb/pipelines?..."); what it was asked, with its token.</summary>
sealed class FakeGitLab : IAsyncDisposable
{
    readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource _stop = new();
    readonly ConcurrentDictionary<string, (int Status, string Body)> _routes = new();

    public FakeGitLab()
    {
        _listener.Start();
        _ = ServeAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>The token it takes (PRIVATE-TOKEN); another is refused (401).</summary>
    public string Token { get; set; } = "glpat-good";

    public ConcurrentQueue<(string Request, string? Token)> Requests { get; } = new();

    /// <summary>The bodies it was sent, by request ("PUT /api/v4/...").</summary>
    public ConcurrentQueue<(string Request, string Body)> Bodies { get; } = new();

    public void Route(string method, string pathAndQuery, object body, int status = 200) =>
        _routes[$"{method} {pathAndQuery}"] = (status, body is string s ? s : JsonSerializer.Serialize(body));

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
                    var reader = new StreamReader(stream, Encoding.ASCII);
                    var line = await reader.ReadLineAsync() ?? "";
                    string? token = null;
                    var length = 0;
                    for (string? header; (header = await reader.ReadLineAsync()) is { Length: > 0 };)
                    {
                        if (header.StartsWith("PRIVATE-TOKEN:", StringComparison.OrdinalIgnoreCase))
                            token = header[14..].Trim();
                        if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                            length = int.Parse(header[15..].Trim());
                    }
                    var sent = new char[length];
                    if (length > 0)
                        await reader.ReadBlockAsync(sent);
                    var parts = line.Split(' ');
                    var request = $"{parts[0]} {parts.ElementAtOrDefault(1)}";
                    Requests.Enqueue((request, token));
                    if (length > 0)
                        Bodies.Enqueue((request, new string(sent)));
                    var (status, body) = token != Token ? (401, """{"message":"401 Unauthorized"}""")
                        : _routes.TryGetValue(request, out var route) ? route
                        : parts[0] == "POST" ? (201, "{}") : parts[0] is "PUT" or "DELETE" ? (200, "{}") : (404, """{"message":"404 Project Not Found"}""");
                    var bytes = Encoding.UTF8.GetBytes(body);
                    var head = $"HTTP/1.1 {status} {(status < 300 ? "OK" : "Error")}\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                    await stream.WriteAsync(bytes);
                }
            });
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
    }
}
