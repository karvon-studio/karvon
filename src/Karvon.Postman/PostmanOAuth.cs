using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Karvon.Claude.Tools;
using Karvon.Http;
using Karvon.Services;

namespace Karvon.Postman;

/// <summary>An access token a server gave, kept as an environment's values are.</summary>
public sealed record PostmanToken(string AccessToken)
{
    public string? RefreshToken { get; init; }

    public DateTimeOffset? Expires { get; init; }

    public string? Scope { get; init; }

    /// <summary>Not for a request sent now (half a minute is for the way).</summary>
    public bool IsExpired => Expires is { } at && at <= DateTimeOffset.UtcNow.AddSeconds(30);
}

/// <summary>
/// OAuth 2.0's access tokens, asked of the server as Postman does: the authorization code (with PKCE or without) and the
/// implicit grant in the user's browser, the password's and the client's own. Tokens are kept in the vault's sealed part
/// (or this session), refreshed when they end. Only the user's calls ask for them.
/// </summary>
public sealed class PostmanOAuth(SealedStore store, Action<Uri> openBrowser, Func<string, string, Task<string?>> ask)
{
    public const string Code = "authorization_code", Pkce = "authorization_code_with_pkce", Implicit = "implicit", PasswordGrant = "password_credentials", Client = "client_credentials";

    public static IReadOnlyList<(string Type, string Name)> Grants { get; } =
        [(Code, "Authorization Code"), (Pkce, "Authorization Code (with PKCE)"), (Implicit, "Implicit"), (PasswordGrant, "Password Credentials"), (Client, "Client Credentials")];

    /// <summary>The browser's answer is waited for this long.</summary>
    internal static TimeSpan Patience { get; set; } = TimeSpan.FromMinutes(5);

    public event Action? Changed;

    // ---- Kept -------------------------------------------------------------------------------------------------

    /// <summary>A sign-in's token is by what it is asked with: the same for every request of it.</summary>
    static string KeyOf(PostmanAuth auth, Func<string, string> put) => "postman/oauth/" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join('\n', new[] { "grant_type", "authUrl", "accessTokenUrl", "refreshTokenUrl", "clientId", "scope", "username", "audience", "resource" }.Select(n => put(auth[n]))))))[..24];

    /// <summary>Whether it says how a token is asked for (else its access token is typed in).</summary>
    public static bool CanAsk(PostmanAuth auth) => auth["accessTokenUrl"].Length > 0 || (auth["grant_type"] == Implicit && auth["authUrl"].Length > 0);

    public PostmanToken? Token(PostmanAuth auth, Func<string, string> put)
    {
        try
        {
            var token = store.Get(KeyOf(auth, put)) is { Length: > 0 } kept ? JsonSerializer.Deserialize<PostmanToken>(kept) : null;
            Hold(token);
            return token;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Forget(PostmanAuth auth, Func<string, string> put)
    {
        store.Delete(KeyOf(auth, put));
        Changed?.Invoke();
    }

    PostmanToken Keep(PostmanAuth auth, Func<string, string> put, PostmanToken token)
    {
        Hold(token);
        store.Set(KeyOf(auth, put), "Karvon: an access token (Postman)", JsonSerializer.Serialize(token));
        Changed?.Invoke();
        return token;
    }

    static void Hold(PostmanToken? token)
    {
        KnownSecrets.Seal("Postman access token", token?.AccessToken);
        KnownSecrets.Seal("Postman refresh token", token?.RefreshToken);
    }

    // ---- Asked for --------------------------------------------------------------------------------------------

    /// <summary>The token a request is sent with: the one kept, refreshed or (where no one is asked anything) asked for again when it ended.</summary>
    /// <param name="mayGo">Why a request may not go to an address; null when it may.</param>
    public async Task<string> ForAsync(PostmanAuth auth, Func<string, string> put, Func<string, Task<string?>> mayGo, CancellationToken ct)
    {
        if (Token(auth, put) is { } kept)
        {
            if (!kept.IsExpired)
                return kept.AccessToken;
            if (kept.RefreshToken is { Length: > 0 } refresh)
                return (await RefreshAsync(auth, put, refresh, kept, mayGo, ct)).AccessToken;
        }
        if (auth["grant_type"] is Client or PasswordGrant && CanAsk(auth))
            return (await GetAsync(auth, put, mayGo, ct)).AccessToken;
        throw new PostmanException(CanAsk(auth)
            ? "It has no access token yet, or it ended: get one (Authorization > Get New Access Token)."
            : "Its OAuth 2.0 sign-in has no access token: put one in, or say where it is asked for.");
    }

    async Task<PostmanToken> RefreshAsync(PostmanAuth auth, Func<string, string> put, string refresh, PostmanToken old, Func<string, Task<string?>> mayGo, CancellationToken ct)
    {
        var url = put(auth["refreshTokenUrl"]) is { Length: > 0 } own ? own : put(auth["accessTokenUrl"]);
        var token = await PostAsync(auth, put, url, [("grant_type", "refresh_token"), ("refresh_token", refresh)], "refreshRequestParams", mayGo, ct);
        // A server that gives no new refresh token leaves the old one good.
        return Keep(auth, put, token with { RefreshToken = token.RefreshToken ?? old.RefreshToken });
    }

    /// <summary>A new token: from the server, or with the user in their browser.</summary>
    public async Task<PostmanToken> GetAsync(PostmanAuth auth, Func<string, string> put, Func<string, Task<string?>> mayGo, CancellationToken ct)
    {
        var grant = auth["grant_type"] is { Length: > 0 } said ? said : Code;
        var tokenUrl = put(auth["accessTokenUrl"]).Trim();
        var scope = put(auth["scope"]);
        List<(string Name, string Value)> More(params (string Name, string Value)[] fields)
        {
            (string Name, string Value)[] also = [("scope", scope), ("audience", put(auth["audience"])), ("resource", put(auth["resource"]))];
            return [.. fields.Concat(also).Where(f => f.Value.Length > 0)];
        }
        switch (grant)
        {
            case Client:
                return Keep(auth, put, await PostAsync(auth, put, tokenUrl, More(("grant_type", "client_credentials")), "tokenRequestParams", mayGo, ct));
            case PasswordGrant:
                return Keep(auth, put, await PostAsync(auth, put, tokenUrl, More(("grant_type", "password"), ("username", put(auth["username"])), ("password", put(auth["password"]))), "tokenRequestParams", mayGo, ct));
        }
        var verifier = put(auth["code_verifier"]) is { Length: >= 43 } typed ? typed : Url(RandomNumberGenerator.GetBytes(48));
        var plain = auth["challengeAlgorithm"].Equals("plain", StringComparison.OrdinalIgnoreCase);
        var state = put(auth["state"]) is { Length: > 0 } given ? given : Url(RandomNumberGenerator.GetBytes(16));
        var redirect = put(auth["redirect_uri"]).Trim();
        var own = redirect.Length == 0;
        if (own)
            redirect = $"http://127.0.0.1:{FreePort()}/callback";
        var query = new List<(string Name, string Value)>
        {
            ("response_type", grant == Implicit ? "token" : "code"), ("client_id", put(auth["clientId"])), ("redirect_uri", redirect), ("scope", scope), ("state", state),
            ("audience", put(auth["audience"])), ("resource", put(auth["resource"])),
        };
        if (grant == Pkce)
        {
            query.Add(("code_challenge", plain ? verifier : Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))));
            query.Add(("code_challenge_method", plain ? "plain" : "S256"));
        }
        query.AddRange(Extra(auth, put, "authRequestParams").Select(p => (p.Key, p.Value)));
        var address = put(auth["authUrl"]).Trim();
        if (!Uri.TryCreate(PostmanRequest.WithQuery(address, [.. PostmanRequest.QueryOf(address), .. query.Where(q => q.Value.Length > 0).Select(q => new PostmanPair(q.Name, Uri.EscapeDataString(q.Value)))]), UriKind.Absolute, out var open)
            || open.Scheme is not ("http" or "https"))
            throw new PostmanException("Its authorization URL is not an http(s) address.");
        if (await mayGo(open.AbsoluteUri) is { } refused)
            throw new PostmanException(refused);
        var answer = await InBrowserAsync(open, redirect, ct);
        if (answer.GetValueOrDefault("error") is { Length: > 0 } error)
            throw new PostmanException($"The server did not sign you in: {answer.GetValueOrDefault("error_description") ?? error}");
        if (answer.GetValueOrDefault("state") is { } back && back != state)
            throw new PostmanException("What came back is not the answer to this sign-in (its state is another).");
        if (grant == Implicit)
            return Keep(auth, put, Read(answer.GetValueOrDefault("access_token") is { Length: > 0 } token ? token : throw new PostmanException("The server gave no access token."),
                null, answer.GetValueOrDefault("expires_in"), answer.GetValueOrDefault("scope")));
        if (answer.GetValueOrDefault("code") is not { Length: > 0 } code)
            throw new PostmanException("The server gave no code.");
        var fields = new List<(string Name, string Value)> { ("grant_type", "authorization_code"), ("code", code), ("redirect_uri", redirect) };
        if (grant == Pkce)
            fields.Add(("code_verifier", verifier));
        return Keep(auth, put, await PostAsync(auth, put, tokenUrl, fields, "tokenRequestParams", mayGo, ct));
    }

    static string Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    static IEnumerable<PostmanPair> Extra(PostmanAuth auth, Func<string, string> put, string name) =>
        auth.Lists.GetValueOrDefault(name, []).Where(p => p.Enabled && p.Key.Length > 0).Select(p => p with { Key = put(p.Key), Value = put(p.Value) });

    async Task<PostmanToken> PostAsync(PostmanAuth auth, Func<string, string> put, string url, List<(string Name, string Value)> fields, string extra, Func<string, Task<string?>> mayGo, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new PostmanException("Its access token URL is not an http(s) address.");
        var headers = new List<(string Name, string Value)> { ("Content-Type", "application/x-www-form-urlencoded"), ("Accept", "application/json") };
        var (id, secret) = (put(auth["clientId"]), put(auth["clientSecret"]));
        if (auth["client_authentication"] == "body")
        {
            fields.Add(("client_id", id));
            if (secret.Length > 0)
                fields.Add(("client_secret", secret));
        }
        else if (secret.Length > 0)
            headers.Add(("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Uri.EscapeDataString(id)}:{Uri.EscapeDataString(secret)}"))));
        else
            // A public client says who it is.
            fields.Add(("client_id", id));
        foreach (var pair in Extra(auth, put, extra))
            switch (pair.Description)
            {
                case "request_header":
                    headers.Add((pair.Key, pair.Value));
                    break;
                case "request_url":
                    url = PostmanRequest.WithQuery(url, [.. PostmanRequest.QueryOf(url), new PostmanPair(Uri.EscapeDataString(pair.Key), Uri.EscapeDataString(pair.Value))]);
                    break;
                default:
                    fields.Add((pair.Key, pair.Value));
                    break;
            }
        // After what it says was put in: the user's values are in it.
        if (await mayGo(url) is { } refused)
            throw new PostmanException(refused);
        var form = string.Join('&', fields.Select(f => $"{Uri.EscapeDataString(f.Name)}={Uri.EscapeDataString(f.Value)}"));
        var exchange = await HttpRunner.SendAsync(PostmanTransport.Client, "POST", url, headers, new ByteArrayContent(Encoding.UTF8.GetBytes(form)), null, ct);
        if (exchange.Error is { } error)
            throw new PostmanException($"The access token was not got: {error}");
        Dictionary<string, string> answer;
        try
        {
            answer = JsonNode.Parse(exchange.RawBody) is JsonObject json ? json.ToDictionary(p => p.Key, p => p.Value.Text() ?? "") : [];
        }
        catch (JsonException)
        {
            answer = Parsed(exchange.RawBody);
        }
        if (answer.GetValueOrDefault("access_token") is not { Length: > 0 } token)
            throw new PostmanException($"The server gave no access token: {exchange.Status} {answer.GetValueOrDefault("error_description") ?? answer.GetValueOrDefault("error") ?? exchange.Reason}");
        return Read(token, answer.GetValueOrDefault("refresh_token"), answer.GetValueOrDefault("expires_in"), answer.GetValueOrDefault("scope"));
    }

    static PostmanToken Read(string token, string? refresh, string? expires, string? scope) => new(token)
    {
        RefreshToken = refresh is { Length: > 0 } ? refresh : null,
        Expires = double.TryParse(expires, System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds > 0 ? DateTimeOffset.UtcNow.AddSeconds(Math.Min(seconds, 1e9)) : null,
        Scope = scope is { Length: > 0 } ? scope : null,
    };

    static Dictionary<string, string> Parsed(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?', '#').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2)))
            values[WebUtility.UrlDecode(pair[0])] = WebUtility.UrlDecode(pair.ElementAtOrDefault(1) ?? "");
        return values;
    }

    // ---- The browser ------------------------------------------------------------------------------------------

    static int FreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>What the browser came back with: to a listener of Karvon's on this machine, or pasted by the user where the sign-in goes back elsewhere.</summary>
    async Task<Dictionary<string, string>> InBrowserAsync(Uri open, string redirect, CancellationToken ct)
    {
        if (!Uri.TryCreate(redirect, UriKind.Absolute, out var back))
            throw new PostmanException("Its callback URL is not an address.");
        if (back.Scheme != "http" || !back.IsLoopback)
        {
            openBrowser(open);
            var pasted = await ask("Sign In", $"Sign in in the browser. It goes back to {back.Host}: paste the address it ends at here.");
            if (pasted is not { Length: > 0 } || !Uri.TryCreate(pasted.Trim(), UriKind.Absolute, out var end))
                throw new PostmanException("The sign-in was not finished.");
            return Parsed(end.Query.Length > 1 ? end.Query : end.Fragment);
        }
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://{(back.Host is "localhost" ? "localhost" : "127.0.0.1")}:{back.Port}/");
        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            throw new PostmanException($"The sign-in's answer cannot be waited for at {back.Authority}: {ex.Message}");
        }
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(Patience);
        openBrowser(open);
        try
        {
            while (true)
            {
                var context = await listener.GetContextAsync().WaitAsync(limit.Token);
                var url = context.Request.Url!;
                if (url.AbsolutePath.TrimEnd('/') != back.AbsolutePath.TrimEnd('/'))
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    continue;
                }
                // The implicit grant's answer is after a #, which only the page itself sees: it comes again with it as a query.
                if (url.Query.Length <= 1)
                {
                    await AnswerAsync(context.Response, "<script>if(location.hash.length>1)location.replace(location.pathname+'?'+location.hash.slice(1));else document.body.append('Nothing came back.')</script>");
                    continue;
                }
                await AnswerAsync(context.Response, "<p>Done. You can close this tab and go back to Karvon.</p>");
                return Parsed(url.Query);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new PostmanException($"The sign-in was not finished in {Patience.TotalMinutes:0} minutes.");
        }
    }

    static async Task AnswerAsync(HttpListenerResponse response, string body)
    {
        var html = Encoding.UTF8.GetBytes("<!doctype html><meta charset=utf-8><title>Karvon</title><body style=\"font:15px system-ui;margin:3em;background:#1e1e1e;color:#ddd\">" + body);
        response.ContentType = "text/html; charset=utf-8";
        response.Headers["Cache-Control"] = "no-store";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.ContentLength64 = html.Length;
        await response.OutputStream.WriteAsync(html);
        response.Close();
    }
}
