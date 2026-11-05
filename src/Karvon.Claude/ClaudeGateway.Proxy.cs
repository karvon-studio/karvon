using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;

namespace Karvon.Claude;

/// <summary>
/// The gateway as Claude Code's HTTPS proxy, set up as a company's inspecting proxy is (HTTPS_PROXY, NODE_EXTRA_CA_CERTS).
/// Claude Code then still talks to api.anthropic.com, so what it offers only there stays on (Remote Control); a base URL of
/// any other host turns those off. Only that host is opened here, with a certificate of this session's that only this CLI
/// trusts and whose issuer's key is gone once it is made; every other host is a tunnel, its bytes passed on unread.
/// </summary>
public sealed partial class ClaudeGateway
{
    const string ApiHost = "api.anthropic.com";

    /// <summary>Set by the user, these say how Claude Code reaches the API: Karvon does not take their place.</summary>
    static readonly string[] OwnWays =
    [
        "ANTHROPIC_BASE_URL", "CLAUDE_CODE_API_BASE_URL", "ANTHROPIC_UNIX_SOCKET", "HTTPS_PROXY", "https_proxy", "HTTP_PROXY", "http_proxy", "ALL_PROXY", "all_proxy", "NO_PROXY", "no_proxy",
    ];

    TcpListener? _proxy;
    X509Certificate2? _certificate;
    string? _folder;
    Func<string, int, CancellationToken, Task<Stream>> _connect = ConnectTlsAsync;

    /// <summary>What Claude Code is given as its HTTPS_PROXY; null when it has a base URL instead.</summary>
    public string? ProxyUrl { get; private set; }

    /// <summary>The certificates Claude Code trusts (its NODE_EXTRA_CA_CERTS): this session's, and the user's own.</summary>
    public string? CertificateFile { get; private set; }

    /// <summary>
    /// What Claude Code runs before each Bash command (its CLAUDE_ENV_FILE): the proxy is for Claude Code alone, not for
    /// what runs in its shell, which may outlive the session.
    /// </summary>
    public string? ShellFile { get; private set; }

    /// <summary>The gateway for a CLI with this environment: the proxy, or a base URL when the user has their own way to the API.</summary>
    public static ClaudeGateway StartFor(IDictionary<string, string?> environment)
    {
        string? Value(string name) => environment.TryGetValue(name, out var value) && value is { Length: > 0 } ? value : null;
        return OwnWays.Any(name => Value(name) is not null)
            ? Start(Value("ANTHROPIC_BASE_URL"))
            : StartProxy(Value("NODE_EXTRA_CA_CERTS"), Value("CLAUDE_ENV_FILE"));
    }

    /// <summary>A gateway listening as a proxy to the API.</summary>
    public static ClaudeGateway StartProxy(string? ownCertificates = null, string? ownShellFile = null, HttpMessageHandler? handler = null,
        Func<string, int, CancellationToken, Task<Stream>>? connect = null)
    {
        var gateway = new ClaudeGateway(new Uri("https://" + ApiHost + "/"), handler, listen: false);
        gateway._connect = connect ?? ConnectTlsAsync;
        gateway._folder = Directory.CreateTempSubdirectory("karvon-gateway-").FullName;
        var (certificate, pem) = Certificates();
        gateway._certificate = certificate;
        if (ownCertificates is not null && File.Exists(ownCertificates))
            pem += "\n" + File.ReadAllText(ownCertificates);
        gateway.CertificateFile = Path.Combine(gateway._folder, "ca.pem");
        File.WriteAllText(gateway.CertificateFile, pem);
        gateway.ShellFile = Path.Combine(gateway._folder, "shell-env.sh");
        File.WriteAllText(gateway.ShellFile, string.Join('\n',
        [
            .. ownShellFile is not null ? new[] { $". {Quoted(ownShellFile)}" } : [],
            ownCertificates is not null ? $"export NODE_EXTRA_CA_CERTS={Quoted(ownCertificates)}" : "unset NODE_EXTRA_CA_CERTS",
            "unset HTTPS_PROXY NO_PROXY CLAUDE_ENV_FILE",
            "",
        ]));
        gateway._proxy = new TcpListener(IPAddress.Loopback, 0);
        gateway._proxy.Start();
        gateway.ProxyUrl = $"http://127.0.0.1:{((IPEndPoint)gateway._proxy.LocalEndpoint).Port}";
        _ = gateway.ServeProxyAsync();
        return gateway;
    }

    /// <summary>Points a CLI's environment at this gateway.</summary>
    public void Apply(IDictionary<string, string?> environment)
    {
        if (BaseUrl is not null)
        {
            environment["ANTHROPIC_BASE_URL"] = BaseUrl;
            return;
        }
        environment["HTTPS_PROXY"] = ProxyUrl;
        environment["NO_PROXY"] = "localhost,127.0.0.1,::1";
        environment["NODE_EXTRA_CA_CERTS"] = CertificateFile;
        environment["CLAUDE_ENV_FILE"] = ShellFile;
    }

    static string Quoted(string text) => "'" + text.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    /// <summary>The API's certificate for this session, and its issuer's (as PEM). The issuer's key is not kept: it issues nothing else.</summary>
    static (X509Certificate2 Certificate, string IssuerPem) Certificates()
    {
        var now = DateTimeOffset.UtcNow;
        using var issuerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var issuerRequest = new CertificateRequest("CN=Karvon gateway (this session only)", issuerKey, HashAlgorithmName.SHA256);
        issuerRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        issuerRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        issuerRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(issuerRequest.PublicKey, false));
        using var issuer = issuerRequest.CreateSelfSigned(now.AddDays(-1), now.AddYears(1));

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=" + ApiHost, key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(ApiHost);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(issuer, true, false));
        using var issued = request.Create(issuer, now.AddDays(-1), now.AddYears(1), RandomNumberGenerator.GetBytes(16));
        using var withKey = issued.CopyWithPrivateKey(key);
        // Windows' TLS wants the key as a loaded PFX gives it.
        return (X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null), issuer.ExportCertificatePem());
    }

    void StopProxy()
    {
        _proxy?.Stop();
        _certificate?.Dispose();
        if (_folder is null)
            return;
        try
        {
            Directory.Delete(_folder, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Public certificates and a shell line: nothing to keep from anyone.
        }
    }

    async Task ServeProxyAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _proxy!.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }
            _ = ServeConnectionAsync(client);
        }
    }

    async Task ServeConnectionAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                var stream = client.GetStream();
                var reader = new HttpReader(stream);
                var head = await reader.ReadHeadAsync(_stop.Token);
                if (head is null)
                    return;
                var colon = head.Target.LastIndexOf(':');
                if (head.Method != "CONNECT" || colon <= 0 || !int.TryParse(head.Target[(colon + 1)..], out var port))
                {
                    await WriteAsync(stream, "HTTP/1.1 405 Method Not Allowed\r\nConnection: close\r\nContent-Length: 0\r\n\r\n");
                    return;
                }
                var host = head.Target[..colon].Trim('[', ']');
                if (port == 443 && host.Equals(ApiHost, StringComparison.OrdinalIgnoreCase))
                {
                    await WriteAsync(stream, "HTTP/1.1 200 Connection Established\r\n\r\n");
                    await ServeApiAsync(stream);
                }
                else
                {
                    await TunnelAsync(stream, reader, host, port);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or AuthenticationException or InvalidOperationException)
            {
                // The client went away, or the connection could not go on: it sees the connection close.
            }
        }
    }

    async Task TunnelAsync(NetworkStream client, HttpReader reader, string host, int port)
    {
        using var target = new TcpClient { NoDelay = true };
        try
        {
            await target.ConnectAsync(host, port, _stop.Token);
        }
        catch (SocketException)
        {
            await WriteAsync(client, "HTTP/1.1 502 Bad Gateway\r\nConnection: close\r\nContent-Length: 0\r\n\r\n");
            return;
        }
        await WriteAsync(client, "HTTP/1.1 200 Connection Established\r\n\r\n");
        var stream = target.GetStream();
        await stream.WriteAsync(reader.TakeBuffered(), _stop.Token);
        await PipeAsync(client, stream);
    }

    /// <summary>Both ways until either side ends, by closing or by a reset: each is how a tunnel ends, and the other side sees the connection close.</summary>
    async Task PipeAsync(Stream a, Stream b)
    {
        using var done = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        Task[] copies = [a.CopyToAsync(b, done.Token), b.CopyToAsync(a, done.Token)];
        await Task.WhenAny(copies);
        await done.CancelAsync();
        try
        {
            await Task.WhenAll(copies);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    static async Task<Stream> ConnectTlsAsync(string host, int port, CancellationToken cancellationToken)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(host, port, cancellationToken);
            var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host, ApplicationProtocols = [SslApplicationProtocol.Http11] }, cancellationToken);
            return tls;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>The API's connection, opened: requests one after another, as the listener's are, until the client ends it.</summary>
    async Task ServeApiAsync(Stream connection)
    {
        await using var client = new SslStream(connection, leaveInnerStreamOpen: true);
        await client.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = _certificate,
            ApplicationProtocols = [SslApplicationProtocol.Http11],
        }, _stop.Token);
        var reader = new HttpReader(client);
        while (await reader.ReadHeadAsync(_stop.Token) is { } head)
        {
            if (head.Header("Upgrade") is not null)
            {
                // A WebSocket (Remote Control's): its handshake goes on as it is, then its frames both ways.
                await using var upstream = await _connect(Upstream.Host, Upstream.Port, _stop.Token);
                await upstream.WriteAsync(head.Raw, _stop.Token);
                await upstream.WriteAsync(reader.TakeBuffered(), _stop.Token);
                await PipeAsync(client, upstream);
                return;
            }
            if (head.Header("Expect")?.Contains("100-continue", StringComparison.OrdinalIgnoreCase) == true)
                await WriteAsync(client, "HTTP/1.1 100 Continue\r\n\r\n");
            var bytes = await reader.ReadBodyAsync(head, _stop.Token);
            await AnswerAsync(client, head, bytes);
            if (head.Header("Connection")?.Contains("close", StringComparison.OrdinalIgnoreCase) == true)
                return;
        }
    }

    async Task AnswerAsync(Stream client, HttpHead head, byte[] bytes)
    {
        var target = new Uri(Upstream, head.Target);
        if (head.Method == "POST" && target.AbsolutePath.StartsWith("/v1/messages", StringComparison.Ordinal))
            bytes = Scan(bytes);
        using var outgoing = new HttpRequestMessage(new HttpMethod(head.Method), target);
        if (bytes.Length > 0 || head.Method is "POST" or "PUT" or "PATCH")
            outgoing.Content = new ByteArrayContent(bytes);
        foreach (var (name, value) in head.Headers)
        {
            if (NotForwarded.Contains(name))
                continue;
            if (!outgoing.Headers.TryAddWithoutValidation(name, value))
                outgoing.Content?.Headers.TryAddWithoutValidation(name, value);
        }
        HttpResponseMessage answer;
        try
        {
            answer = await _client.SendAsync(outgoing, HttpCompletionOption.ResponseHeadersRead, _stop.Token);
        }
        catch (HttpRequestException ex)
        {
            var text = Encoding.UTF8.GetBytes(new JsonObject
            {
                ["type"] = "error",
                ["error"] = new JsonObject { ["type"] = "api_error", ["message"] = $"Karvon's gateway could not reach {Upstream.Host}: {ex.Message}" },
            }.ToJsonString());
            await WriteAsync(client, $"HTTP/1.1 502 Bad Gateway\r\nContent-Type: application/json\r\nContent-Length: {text.Length}\r\n\r\n");
            await client.WriteAsync(text, _stop.Token);
            return;
        }
        using (answer)
        {
            var response = new StringBuilder($"HTTP/1.1 {(int)answer.StatusCode} {answer.ReasonPhrase}\r\n");
            AddHeaders(response, answer.Headers);
            AddHeaders(response, answer.Content.Headers);
            var noBody = head.Method == "HEAD" || answer.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotModified;
            var length = answer.Content.Headers.ContentLength;
            if (length is not null)
                response.Append($"Content-Length: {length}\r\n");
            else if (!noBody)
                response.Append("Transfer-Encoding: chunked\r\n");
            response.Append("\r\n");
            await WriteAsync(client, response.ToString());
            if (noBody)
                return;
            // Streamed as it comes (server-sent events): each read goes out at once.
            await using var stream = await answer.Content.ReadAsStreamAsync(_stop.Token);
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, _stop.Token)) > 0)
            {
                if (length is null)
                    await WriteAsync(client, $"{read:x}\r\n");
                await client.WriteAsync(buffer.AsMemory(0, read), _stop.Token);
                if (length is null)
                    await WriteAsync(client, "\r\n");
                await client.FlushAsync(_stop.Token);
            }
            if (length is null)
                await WriteAsync(client, "0\r\n\r\n");
            await client.FlushAsync(_stop.Token);
        }
    }

    static void AddHeaders(StringBuilder response, HttpHeaders headers)
    {
        foreach (var (name, values) in headers)
            if (!NotForwarded.Contains(name))
                foreach (var value in values)
                    response.Append(name).Append(": ").Append(value).Append("\r\n");
    }

    async Task WriteAsync(Stream stream, string text) => await stream.WriteAsync(Encoding.Latin1.GetBytes(text), _stop.Token);

    /// <summary>A request's line and headers, as they came.</summary>
    sealed record HttpHead(string Method, string Target, IReadOnlyList<(string Name, string Value)> Headers, byte[] Raw)
    {
        public string? Header(string name) => Headers.Where(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value).FirstOrDefault();
    }

    /// <summary>HTTP/1.1 requests off a stream: what Claude Code's client sends, no more.</summary>
    sealed class HttpReader(Stream stream)
    {
        const int MaxHead = 256 * 1024;

        byte[] _buffer = new byte[32 * 1024];
        int _start;
        int _end;

        /// <summary>What was read past the last head.</summary>
        public byte[] TakeBuffered()
        {
            var rest = _buffer[_start.._end];
            _start = _end;
            return rest;
        }

        async Task<bool> FillAsync(CancellationToken cancellationToken)
        {
            if (_start > 0)
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }
            if (_end == _buffer.Length)
                Array.Resize(ref _buffer, _buffer.Length * 2);
            var read = await stream.ReadAsync(_buffer.AsMemory(_end), cancellationToken);
            _end += read;
            return read > 0;
        }

        /// <summary>The next request's head; null when the client ended the connection.</summary>
        public async Task<HttpHead?> ReadHeadAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                var at = _buffer.AsSpan(_start, _end - _start).IndexOf("\r\n\r\n"u8);
                if (at >= 0)
                {
                    var raw = _buffer[_start..(_start + at + 4)];
                    _start += at + 4;
                    return Parse(raw);
                }
                if (_end - _start > MaxHead)
                    throw new IOException("A request's head is too long.");
                if (!await FillAsync(cancellationToken))
                    return null;
            }
        }

        static HttpHead Parse(byte[] raw)
        {
            var lines = Encoding.Latin1.GetString(raw).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            var first = lines[0].Split(' ');
            if (first.Length != 3)
                throw new IOException("Not an HTTP request.");
            List<(string, string)> headers = [];
            foreach (var line in lines.Skip(1))
                if (line.IndexOf(':') is var colon and > 0)
                    headers.Add((line[..colon].Trim(), line[(colon + 1)..].Trim()));
            return new HttpHead(first[0], first[1], headers, raw);
        }

        public async Task<byte[]> ReadBodyAsync(HttpHead head, CancellationToken cancellationToken)
        {
            if (head.Header("Transfer-Encoding")?.Contains("chunked", StringComparison.OrdinalIgnoreCase) == true)
                return await ReadChunkedAsync(cancellationToken);
            return long.TryParse(head.Header("Content-Length"), out var length) && length > 0 ? await ReadExactlyAsync(checked((int)length), cancellationToken) : [];
        }

        async Task<byte[]> ReadExactlyAsync(int length, CancellationToken cancellationToken)
        {
            var bytes = new byte[length];
            var have = 0;
            while (have < length)
            {
                if (_start == _end && !await FillAsync(cancellationToken))
                    throw new IOException("The client ended the connection in a request's body.");
                var take = Math.Min(length - have, _end - _start);
                Buffer.BlockCopy(_buffer, _start, bytes, have, take);
                _start += take;
                have += take;
            }
            return bytes;
        }

        async Task<string> ReadLineAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                var at = _buffer.AsSpan(_start, _end - _start).IndexOf("\r\n"u8);
                if (at >= 0)
                {
                    var line = Encoding.Latin1.GetString(_buffer, _start, at);
                    _start += at + 2;
                    return line;
                }
                if (!await FillAsync(cancellationToken))
                    throw new IOException("The client ended the connection in a request's body.");
            }
        }

        async Task<byte[]> ReadChunkedAsync(CancellationToken cancellationToken)
        {
            using var body = new MemoryStream();
            while (true)
            {
                var size = Convert.ToInt32((await ReadLineAsync(cancellationToken)).Split(';')[0].Trim(), 16);
                if (size == 0)
                    break;
                body.Write(await ReadExactlyAsync(size, cancellationToken));
                await ReadLineAsync(cancellationToken);
            }
            // Trailers, if any, up to the empty line.
            while ((await ReadLineAsync(cancellationToken)).Length > 0)
            {
            }
            return body.ToArray();
        }
    }
}
