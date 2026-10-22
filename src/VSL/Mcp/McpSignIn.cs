using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bengine.Introspection.Servers;
using ModelContextProtocol.Authentication;

namespace VSL.Mcp;

/// <summary>Signing in to MCP servers: where the browser goes, where the tokens are kept.</summary>
public sealed class McpSignIn(Action<Uri> openBrowser, ICredentialStore? keyring)
{
    public Action<Uri> OpenBrowser { get; } = openBrowser;

    /// <summary>The system keyring; without one, tokens last as long as VSL runs.</summary>
    public ICredentialStore? Keyring { get; } = keyring;

    readonly Dictionary<string, McpTokens> _tokens = new(StringComparer.Ordinal);

    /// <summary>A server's tokens, by its URL (two entries with one URL share them).</summary>
    public McpTokens TokensFor(string url)
    {
        lock (_tokens)
        {
            if (!_tokens.TryGetValue(url, out var tokens))
                _tokens[url] = tokens = new McpTokens(Keyring, "vsl-mcp/oauth:" + url, url);
            return tokens;
        }
    }
}

/// <summary>
/// A server's OAuth tokens (with the client they were issued to, so a refresh works after a restart): in the system
/// keyring when there is one, else for this session only.
/// </summary>
public sealed class McpTokens(ICredentialStore? keyring, string key, string url) : ITokenCache
{
    TokenContainer? _tokens;
    bool _read;

    public bool HasTokens => Read() is not null;

    TokenContainer? Read()
    {
        if (_read)
            return _tokens;
        _read = true;
        try
        {
            if (keyring?.Get(key) is { Length: > 0 } json)
                _tokens = JsonSerializer.Deserialize<TokenContainer>(json);
        }
        catch (JsonException)
        {
        }
        return _tokens;
    }

    public ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Read());

    public ValueTask StoreTokensAsync(TokenContainer tokens, CancellationToken cancellationToken)
    {
        (_tokens, _read) = (tokens, true);
        keyring?.Set(key, $"VSL: MCP server {url}", JsonSerializer.Serialize(tokens));
        return ValueTask.CompletedTask;
    }

    /// <summary>Signed out: the tokens go, here and in the keyring.</summary>
    public void Forget()
    {
        (_tokens, _read) = (null, true);
        keyring?.Delete(key);
    }
}

/// <summary>
/// One sign-in in the browser: the authorization page opens, and the server's answer comes back to a listener of
/// VSL's on 127.0.0.1 (a free port), which tells the browser it can be closed.
/// </summary>
sealed class LoopbackSignIn(string server, Action<Uri> openBrowser, int? port = null)
{
    public int Port { get; } = port ?? FreePort();

    /// <summary>A client given out for a port is registered at localhost (as Claude Code's); VSL's own at 127.0.0.1.</summary>
    public Uri RedirectUri => new($"http://{(port is null ? "127.0.0.1" : "localhost")}:{Port}/callback");

    static int FreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public async Task<AuthorizationResult> AuthorizeAsync(AuthorizationCallbackContext context, CancellationToken cancellationToken)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        if (port is not null)
            listener.Prefixes.Add($"http://localhost:{Port}/");
        listener.Start();
        openBrowser(context.AuthorizationUri);
        while (true)
        {
            var request = await listener.GetContextAsync().WaitAsync(cancellationToken);
            if (request.Request.Url?.AbsolutePath != "/callback")
            {
                request.Response.StatusCode = 404;
                request.Response.Close();
                continue;
            }
            var query = System.Web.HttpUtility.ParseQueryString(request.Request.Url.Query);
            var error = query["error"] is { } code ? query["error_description"] ?? code : null;
            await RespondAsync(request.Response, error is null ? $"Signed in to {server}. You can close this tab and go back to VSL." : $"{server} did not sign you in: {error}");
            if (error is not null)
                throw new InvalidOperationException($"{server} did not sign you in: {error}");
            return new AuthorizationResult { Code = query["code"], State = query["state"], Iss = query["iss"] };
        }
    }

    static async Task RespondAsync(HttpListenerResponse response, string text)
    {
        var html = Encoding.UTF8.GetBytes($"<!doctype html><meta charset=utf-8><title>VSL</title><body style=\"font:15px system-ui;margin:3em;background:#1e1e1e;color:#ddd\"><p>{WebUtility.HtmlEncode(text)}</p>");
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = html.Length;
        await response.OutputStream.WriteAsync(html);
        response.Close();
    }
}

/// <summary>
/// What a server's sign-in goes through, read before it: its authorization servers' issuers as their metadata writes
/// them. RFC 8414 wants the same text in both places; a server whose resource metadata lists
/// <c>https://id.example.com</c> while its metadata says <c>https://id.example.com/</c> is the same server (Claude Code
/// signs in to it), so the issuer is chosen as the metadata writes it. Any other difference still fails.
/// </summary>
static class McpDiscovery
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static async Task<IReadOnlyList<string>> IssuersAsync(Uri server, CancellationToken cancellationToken)
    {
        try
        {
            var origin = server.GetLeftPart(UriPartial.Authority);
            var path = server.AbsolutePath.TrimEnd('/');
            JsonNode? resource = null;
            foreach (var url in new[] { origin + "/.well-known/oauth-protected-resource" + path, origin + "/.well-known/oauth-protected-resource" })
                if ((resource = await JsonAsync(url, cancellationToken)) is not null)
                    break;
            var issuers = new List<string>();
            foreach (var at in (resource?["authorization_servers"] as JsonArray ?? []).Select(s => (string?)s).OfType<string>())
            {
                if (!Uri.TryCreate(at, UriKind.Absolute, out var authority))
                    continue;
                var root = authority.GetLeftPart(UriPartial.Authority);
                var tail = authority.AbsolutePath.TrimEnd('/');
                foreach (var url in new[] { root + "/.well-known/oauth-authorization-server" + tail, root + "/.well-known/openid-configuration" + tail, root + tail + "/.well-known/openid-configuration" })
                    if (await JsonAsync(url, cancellationToken) is { } metadata && (string?)metadata["issuer"] is { } issuer)
                    {
                        issuers.Add(issuer);
                        break;
                    }
            }
            return issuers;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or UriFormatException or InvalidOperationException)
        {
            return [];
        }
    }

    static async Task<JsonNode?> JsonAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(url, cancellationToken);
        return response.IsSuccessStatusCode ? JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken)) : null;
    }

    /// <summary>The first authorization server, as its metadata writes its issuer when that differs only by a trailing slash.</summary>
    public static Uri? Select(IReadOnlyList<Uri> servers, IReadOnlyList<string> issuers) =>
        servers.Select(s => issuers.FirstOrDefault(i => Uri.TryCreate(i, UriKind.Absolute, out var issuer) && issuer == s && i.TrimEnd('/') == s.OriginalString.TrimEnd('/')) is { } exact
            ? new Uri(exact) : s).FirstOrDefault();
}
