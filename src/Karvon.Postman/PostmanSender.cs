using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Karvon.Http;

namespace Karvon.Postman;

/// <summary>What a request sent got back: the exchange, its tests, what its scripts printed, and what it lacked.</summary>
public sealed record PostmanResult(string Path, HttpExchange Exchange)
{
    public IReadOnlyList<PostmanTest> Tests { get; init; } = [];

    public IReadOnlyList<string> Console { get; init; } = [];

    /// <summary>Why a script stopped; null when none did.</summary>
    public string? ScriptError { get; init; }

    /// <summary>The variables that had no value, left as they are written.</summary>
    public IReadOnlyList<string> Missing { get; init; } = [];

    /// <summary>The request was not sent: its pre-request script skipped it.</summary>
    public bool IsSkipped { get; init; }

    internal (bool Set, string? Name) Next { get; init; }

    public int Passed => Tests.Count(t => t.Passed);

    public int Failed => Tests.Count(t => t.Failed);

    /// <summary>What came back as the one who asked may have it: an agent's call has the user's values hidden in it.</summary>
    public HttpExchange Shown { get; init; } = Exchange;

    /// <summary>Which of a run's data rows it was sent with (from 1); 0 without data.</summary>
    public int Row { get; init; }

    /// <summary>It came back, its scripts ran and no test failed.</summary>
    public bool Succeeded => IsSkipped || (Exchange.Error is null && ScriptError is null && Failed == 0);

    public string TestsText => Tests.Count == 0 ? "" : $"{Passed}/{Tests.Count(t => !t.IsSkipped)} tests passed";
}

/// <summary>What a run of requests sends and how.</summary>
public sealed record PostmanRunOptions
{
    public int Iterations { get; init; } = 1;

    public TimeSpan Delay { get; init; }

    public bool StopOnFailure { get; init; }

    /// <summary>A data file's rows: an iteration has one, the last again when there are more iterations.</summary>
    public IReadOnlyList<IReadOnlyDictionary<string, object?>> Data { get; init; } = [];
}

/// <summary>The connection Postman's requests go over: redirects and cookies are Karvon's to follow and keep.</summary>
static class PostmanTransport
{
    public static HttpClient Client { get; } = New();

    /// <summary>One connection of its own (NTLM signs in a connection).</summary>
    public static HttpClient New(bool single = false) => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        SslOptions = HttpRunner.Tls(),
        MaxConnectionsPerServer = single ? 1 : int.MaxValue,
    }) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
}

/// <summary>
/// Sends a collection's requests as Postman does: the collection's, the folders' and the request's pre-request scripts, its
/// variables put in, its sign-in, its body, its cookies; then their tests over what came back. The environment's values go
/// only to its own hosts: elsewhere the user is asked, and an agent's call is refused.
/// </summary>
/// <param name="mayReach">Whether a request (a script's too) may go to a host; all of them when null (the user's own Send).</param>
/// <param name="mayRead">Why a body may not be a file of this machine's; any may when null.</param>
public sealed class PostmanSender(PostmanEnvironment? environment, PostmanEnvironment? globals, Func<Uri, bool>? mayReach = null, Func<string, string?>? mayRead = null)
{
    /// <summary>Requests a run sends at most (setNextRequest may go round).</summary>
    internal const int MaxRequests = 2000;

    const int MaxRedirects = 10;

    /// <summary>The folder a relative file of a body's is in (the workspace).</summary>
    public string? Folder { get; init; }

    /// <summary>Values given for this call, over every other.</summary>
    public IReadOnlyDictionary<string, string>? Local { get; init; }

    /// <summary>An agent's call: what its scripts set is there, and it gets none of the user's values.</summary>
    public PostmanAgentLayer? Agent { get; init; }

    /// <summary>The cookies servers set (the user's; an agent's are its layer's).</summary>
    public PostmanCookies? Cookies { get; init; }

    /// <summary>OAuth 2.0's tokens.</summary>
    public PostmanOAuth? OAuth { get; init; }

    /// <summary>The user is asked whether their values (named) may go to a host that is not the environment's; any may when null.</summary>
    public Func<Uri, IReadOnlyCollection<string>, Task<bool>>? AskHost { get; init; }

    PostmanCookies? Jar => Agent is null ? Cookies : Agent.Cookies;

    /// <summary>A request sent: <paramref name="request"/> in place of the item's own when it is given (edited, not saved yet).</summary>
    public async Task<PostmanResult> SendAsync(PostmanItem item, PostmanRequest? request = null, CancellationToken ct = default, int iteration = 0, int iterations = 1,
        IReadOnlyDictionary<string, object?>? data = null, int row = 0)
    {
        request ??= item.Read();
        var scope = new PostmanScope(item.Collection, environment, globals, Local, data, Agent);
        var path = item.Path;
        var console = new List<string>();
        var tests = new List<PostmanTest>();
        (bool, string?) next = default;
        var own = request;
        // Read here, on the window's thread, where the collection is changed.
        List<string> Scripts(string listen) => (item.Parent?.Scripts(listen) ?? [PostmanRequest.Script(item.Collection.Root, listen)])
            .Append(listen == "test" ? own.Tests : own.PreRequest).Where(s => s.Trim().Length > 0).ToList();

        PostmanScriptRun Run(string name, PostmanOutgoing outgoing, HttpExchange? response) => new()
        {
            Scope = scope,
            Name = item.Name,
            Event = name,
            Request = outgoing,
            Response = response,
            Iteration = iteration,
            Iterations = iterations,
            Send = asked => SendOwnAsync(asked, scope, ct).GetAwaiter().GetResult(),
            Jar = Jar,
            Address = () => Uri.TryCreate(UrlOf(own, t => scope.Put(t)), UriKind.Absolute, out var at) ? at : null,
            MayReadCookies = uri => Agent is not null || Uri.TryCreate(outgoing.Url, UriKind.Absolute, out var to) && Related(to.Host, uri.Host) || environment?.Reaches(uri) == true,
        };

        PostmanResult Failed(string error) => new PostmanResult(path, new(request.Method, request.Url, [], null) { Error = error }) { Row = row };

        try
        {
            // Before: scripts see the request as it is written, and may change its variables or it.
            var unresolved = new PostmanOutgoing(request.Method, request.Url, [.. request.Headers.Where(h => h.Enabled).Select(h => (h.Key, h.Value))], request.Body.Mode,
                request.Body.Mode == PostmanBody.RawMode ? request.Body.Raw : null);
            var before = Run("prerequest", unresolved, null);
            var preRequest = Scripts("prerequest");
            if (await Task.Run(() => PostmanScripts.Run(preRequest, before, ct), ct) is { } broken)
                return Failed("The pre-request script stopped: " + broken) with { Console = before.Console, Tests = before.Tests, ScriptError = broken };
            console.AddRange(before.Console);
            tests.AddRange(before.Tests);
            next = before.Next;
            if (before.Skip)
                return Failed("Skipped by its pre-request script.") with { Console = console, Tests = tests, IsSkipped = true, Next = next };
            if (before.Request != unresolved)
                request = Changed(request, unresolved, before.Request);

            var missing = new List<string>();
            var auth = request.Auth ?? Inherited(item);
            var profile = item.Node["protocolProfileBehavior"];
            // As it goes out, and as its scripts (and an agent) may have it.
            var built = await BuildAsync(request, auth, scope, scope.Put, missing, ct);
            var shown = Agent is null ? built.Request : Shown(request, auth, scope);
            if (missing.FirstOrDefault(m => built.Request.Url.Contains("{{" + m, StringComparison.Ordinal)) is { } inUrl)
                return Failed($"{{{{{inUrl}}}}} has no value: choose an environment that has it, or set it in the collection's variables.") with { Console = console, Tests = tests, Missing = missing };
            if (await RefusedAsync(built.Request.Url, scope) is { } refused)
                return Failed(refused) with { Console = console, Tests = tests };

            var sent = await TransportAsync(built, auth, scope, profile, ct);
            var exchange = sent.Exchange with { Name = item.Name };
            var seen = Seen(exchange, shown, scope, sent.Derived);
            string? error = null;
            if (exchange.Error is null)
            {
                var after = Run("test", shown, seen);
                var scripts = Scripts("test");
                error = await Task.Run(() => PostmanScripts.Run(scripts, after, ct), ct);
                console.AddRange(after.Console);
                tests.AddRange(after.Tests);
                if (after.Next.Set)
                    next = after.Next;
            }
            return new PostmanResult(path, exchange) { Shown = seen, Tests = tests, Console = console, ScriptError = error, Missing = missing, Next = next, Row = row };
        }
        catch (PostmanException ex)
        {
            return Failed(Agent is null ? ex.Message : PostmanMasks.Hide(ex.Message, PostmanMasks.Values(scope.Used, []))) with { Console = console, Tests = tests };
        }
        finally
        {
            scope.Apply();
        }
    }

    /// <summary>What came back as the caller may have it: an agent gets the request as it was written, and none of the user's values in the answer.</summary>
    HttpExchange Seen(HttpExchange exchange, PostmanOutgoing shown, PostmanScope scope, IEnumerable<string> derived) => Agent is null ? exchange
        : PostmanMasks.Hide(exchange, scope.Used, derived) with { Url = shown.Url, RequestHeaders = shown.Headers, RequestBody = shown.Body };

    /// <summary>Requests run in their order (or as setNextRequest says), as many times as asked; each result as it comes.</summary>
    public async Task<IReadOnlyList<PostmanResult>> RunAsync(IReadOnlyList<PostmanItem> requests, PostmanRunOptions options, Action<PostmanResult>? done = null, CancellationToken ct = default)
    {
        var results = new List<PostmanResult>();
        for (var iteration = 0; iteration < options.Iterations; iteration++)
        {
            var at = options.Data.Count == 0 ? 0 : Math.Min(iteration, options.Data.Count - 1) + 1;
            for (var i = 0; i < requests.Count;)
            {
                ct.ThrowIfCancellationRequested();
                if (results.Count == MaxRequests)
                    return results;
                var result = await SendAsync(requests[i], ct: ct, iteration: iteration, iterations: options.Iterations, data: at == 0 ? null : options.Data[at - 1], row: at);
                results.Add(result);
                done?.Invoke(result);
                if (!result.Succeeded && options.StopOnFailure)
                    return results;
                if (result.Next.Set)
                {
                    if (result.Next.Name is not { } name)
                        break;
                    var to = Enumerable.Range(0, requests.Count).FirstOrDefault(r => requests[r].Name == name || requests[r].Node.Text("id") == name, -1);
                    // A request that is not in the run ends it, as in Postman.
                    i = to < 0 ? requests.Count : to;
                }
                else
                    i++;
                if (options.Delay > TimeSpan.Zero && i < requests.Count)
                    await Task.Delay(options.Delay, ct);
            }
        }
        return results;
    }

    // ---- Where it may go --------------------------------------------------------------------------------------

    /// <summary>Why a request may not go to an address, with the user's values that were used so far; null when it may.</summary>
    async Task<string?> RefusedAsync(string url, PostmanScope scope)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;
        if (mayReach is not null && !mayReach(uri))
            return "Not sent: its host is not where this call was to go (a script or a variable changed it).";
        if (scope.Used.Count == 0)
            return null;
        if (Agent is not null)
            return AgentRefusal(uri, scope);
        if (AskHost is null || environment?.Reaches(uri) == true || globals?.Reaches(uri) == true)
            return null;
        return await AskHost(uri, [.. scope.Used.Keys.Order(StringComparer.Ordinal)]) ? null
            : $"Not sent: {uri.Authority} is not one of {environment?.Name ?? globals?.Name ?? "the environment"}'s hosts, and its values were not let go there.";
    }

    /// <summary>An agent's call takes the user's values only where they let agents send with them, and only to the environment's own hosts.</summary>
    string? AgentRefusal(Uri uri, PostmanScope scope)
    {
        bool Lets(PostmanEnvironment? of) => of is { AgentsMaySend: true } && of.Reaches(uri);
        var fromGlobals = scope.Used.Values.All(u => u.Scope == PostmanScope.GlobalsName);
        if (fromGlobals ? Lets(globals) : Lets(environment) && (scope.Used.Values.All(u => u.Scope != PostmanScope.GlobalsName) || globals is { AgentsMaySend: true }))
            return null;
        return environment is { AgentsMaySend: true } || fromGlobals && globals is { AgentsMaySend: true }
            ? "Not sent: its host is not one of the environment's, so the environment's values do not go there in an agent's call."
            : NotLet(scope.Used.Keys, environment?.Name ?? globals?.Name ?? "the environment");
    }

    /// <summary>What an agent is told of an environment its user keeps from agents.</summary>
    public static string NotLet(IEnumerable<string> names, string environment) =>
        $"Not sent: it uses {string.Join(", ", names.Order(StringComparer.Ordinal).Take(8).Select(n => $"{{{{{n}}}}}"))} of {environment}, which the user keeps from agents. Give your own values with variables, "
        + $"or ask the user to send it, or to let agents send with {environment} (Postman > {environment}); its values are never shown either way.";

    static bool Related(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase) || b.EndsWith("." + a, StringComparison.OrdinalIgnoreCase) || a.EndsWith("." + b, StringComparison.OrdinalIgnoreCase);

    /// <summary>A script's own request (pm.sendRequest).</summary>
    async Task<HttpExchange> SendOwnAsync(PostmanRequest request, PostmanScope scope, CancellationToken ct)
    {
        try
        {
            var built = await BuildAsync(request, request.Auth, scope, scope.Put, [], ct);
            if (await RefusedAsync(built.Request.Url, scope) is { } refused)
                return new HttpExchange(request.Method, request.Url, [], null) { Error = refused };
            var sent = await TransportAsync(built, request.Auth, scope, null, ct);
            return Seen(sent.Exchange, Agent is null ? built.Request : Shown(request, request.Auth, scope), scope, sent.Derived);
        }
        catch (PostmanException ex)
        {
            return new HttpExchange(request.Method, request.Url, [], null) { Error = ex.Message };
        }
    }

    /// <summary>Where a request goes, as far as it can be told before its scripts run; null when a variable of it has no value yet.</summary>
    public static Uri? HostOf(PostmanItem item, PostmanEnvironment? environment, PostmanEnvironment? globals, IReadOnlyDictionary<string, string>? local = null, PostmanAgentLayer? agent = null)
    {
        var url = Absolute(new PostmanScope(item.Collection, environment, globals, local, agent: agent).Put(item.Read().Url).Trim());
        var authority = url.Length == 0 ? "" : url[(url.IndexOf("://", StringComparison.Ordinal) + 3)..].Split('/', '?', '#')[0];
        return url.Length > 0 && !authority.Contains("{{", StringComparison.Ordinal) && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri is { Scheme: "http" or "https", Host.Length: > 0 }
            ? uri : null;
    }

    /// <summary>An access token asked of its server, with the user.</summary>
    public async Task<PostmanToken> GetTokenAsync(PostmanCollection collection, PostmanAuth auth, CancellationToken ct)
    {
        var scope = new PostmanScope(collection, environment, globals, Local, agent: Agent);
        return await (OAuth ?? throw new PostmanException("No sign-in can be asked for here.")).GetAsync(auth, t => scope.Put(t), to => RefusedAsync(to, scope), ct);
    }

    // ---- Over the wire ----------------------------------------------------------------------------------------

    /// <summary>A request built: what it is, its body's bytes, and what of it is a sign-in.</summary>
    sealed record Built(PostmanOutgoing Request, byte[]? Body, List<string> Derived, bool SignInIsTheirs);

    const string AccessToken = "access token";

    sealed record Sent(HttpExchange Exchange, IReadOnlyList<string> Derived);

    async Task<Sent> TransportAsync(Built built, PostmanAuth? auth, PostmanScope scope, JsonNode? profile, CancellationToken ct)
    {
        var (request, derived) = (built.Request, new List<string>(built.Derived));
        var (method, url, body, text) = (request.Method, request.Url, built.Body, request.Body);
        var headers = request.Headers.ToList();
        var follows = profile?["followRedirects"] is null || profile.Flag("followRedirects");
        var cookies = profile.Flag("disableCookies") ? null : Jar;
        HttpClient? single = auth?.Type == PostmanAuth.Ntlm ? PostmanTransport.New(single: true) : null;
        try
        {
            for (var hop = 0; ; hop++)
            {
                var exchange = await SignedAsync(single ?? PostmanTransport.Client, method, url, headers, body, text, hop == 0 ? auth : null, scope, cookies, built.SignInIsTheirs ? derived : [], ct);
                // The headers as they went out, and the URL as it was asked for.
                exchange = exchange with { Url = request.Url };
                if (!follows || exchange.Error is not null || exchange.Status is not (301 or 302 or 303 or 307 or 308) || hop == MaxRedirects
                    || exchange.Headers.FirstOrDefault(h => h.Name.Equals("Location", StringComparison.OrdinalIgnoreCase)).Value is not { Length: > 0 } location
                    || !Uri.TryCreate(new Uri(url), location, out var to) || to.Scheme is not ("http" or "https"))
                    return new Sent(hop == MaxRedirects && exchange.Status is >= 300 and < 400 ? exchange with { Error = $"It was redirected {MaxRedirects} times." } : exchange, derived);
                if (await RefusedAsync(to.AbsoluteUri, scope) is { } refused)
                    return new Sent(exchange with { Error = "It was redirected. " + refused }, derived);
                if (!to.Authority.Equals(new Uri(url).Authority, StringComparison.OrdinalIgnoreCase))
                    headers.RemoveAll(h => h.Name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) || h.Name.Equals("Cookie", StringComparison.OrdinalIgnoreCase));
                if (exchange.Status == 303 || (exchange.Status is 301 or 302 && method == "POST" && !profile.Flag("followOriginalHttpMethod")))
                {
                    if (method != "HEAD")
                        method = "GET";
                    (body, text) = (null, null);
                    headers.RemoveAll(h => h.Name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase));
                }
                url = to.AbsoluteUri;
            }
        }
        finally
        {
            single?.Dispose();
        }
    }

    /// <summary>One request, its cookies in it, and the answer to the challenge its sign-in gets (a digest's, NTLM's).</summary>
    async Task<HttpExchange> SignedAsync(HttpClient client, string method, string url, List<(string Name, string Value)> headers, byte[]? body, string? text, PostmanAuth? auth, PostmanScope scope,
        PostmanCookies? cookies, List<string> derived, CancellationToken ct)
    {
        var uri = new Uri(url);

        async Task<HttpExchange> Send(string? authorization)
        {
            var all = headers.ToList();
            if (authorization is not null)
            {
                all.RemoveAll(h => h.Name.Equals("Authorization", StringComparison.OrdinalIgnoreCase));
                all.Add(("Authorization", authorization));
                derived.Add(authorization);
            }
            if (cookies?.For(uri) is { Count: > 0 } kept)
            {
                var typed = all.FirstOrDefault(h => h.Name.Equals("Cookie", StringComparison.OrdinalIgnoreCase));
                var own = kept.Where(c => typed.Value?.Contains(c.Name + "=", StringComparison.Ordinal) != true).Select(c => $"{c.Name}={c.Value}").ToList();
                if (own.Count > 0)
                {
                    all.Remove(typed);
                    all.Add(("Cookie", string.Join("; ", own.Prepend(typed.Value).Where(v => v is { Length: > 0 }))));
                }
            }
            var exchange = await HttpRunner.SendAsync(client, method, url, all, body is null ? null : new ByteArrayContent(body), text, ct);
            cookies?.Read(uri, exchange.Headers.Where(h => h.Name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase)).Select(h => h.Value));
            return exchange;
        }

        static IEnumerable<string> Challenges(HttpExchange exchange) => exchange.Headers.Where(h => h.Name.Equals("WWW-Authenticate", StringComparison.OrdinalIgnoreCase)).Select(h => h.Value);

        var retries = auth is not null && auth["disableRetryRequest"] != "true";
        switch (auth?.Type)
        {
            case PostmanAuth.Digest:
            {
                var (username, password) = (scope.Put(auth["username"]), scope.Put(auth["password"]));
                string Answer(IReadOnlyDictionary<string, string> challenge, int count) => PostmanDigest.Header(username, password, method, uri.PathAndQuery, challenge, body ?? [],
                    scope.Put(auth["clientNonce"]) is { Length: > 0 } cnonce ? cnonce : null, count);
                if (scope.Put(auth["nonce"]) is { Length: > 0 } nonce && scope.Put(auth["realm"]) is { Length: > 0 } realm)
                    return await Send(Answer(new Dictionary<string, string>
                    {
                        ["realm"] = realm, ["nonce"] = nonce, ["qop"] = scope.Put(auth["qop"]), ["algorithm"] = auth["algorithm"], ["opaque"] = scope.Put(auth["opaque"]),
                    }, int.TryParse(scope.Put(auth["nonceCount"]), out var n) ? n : 1));
                var first = await Send(null);
                return retries && first is { Error: null, Status: 401 } && PostmanDigest.Challenge(Challenges(first)) is { } asked ? await Send(Answer(asked, 1)) : first;
            }
            case PostmanAuth.Ntlm:
            {
                var first = await Send(null);
                if (!retries || first is not { Error: null, Status: 401 } || !Challenges(first).Any(c => c.StartsWith("NTLM", StringComparison.OrdinalIgnoreCase) || c.StartsWith("Negotiate", StringComparison.OrdinalIgnoreCase)))
                    return first;
                var second = await Send(PostmanNtlm.Negotiate());
                return PostmanNtlm.Challenge(Challenges(second)) is { } challenge
                    ? await Send(PostmanNtlm.Authenticate(challenge, scope.Put(auth["username"]), scope.Put(auth["password"]), scope.Put(auth["domain"]), scope.Put(auth["workstation"])))
                    : second;
            }
            default:
                return await Send(null);
        }
    }

    // ---- The request as it goes out ---------------------------------------------------------------------------

    /// <summary>A script's changes put in the request: its method, URL, headers and raw body.</summary>
    static PostmanRequest Changed(PostmanRequest request, PostmanOutgoing before, PostmanOutgoing after) => request with
    {
        Method = after.Method.ToUpperInvariant(),
        Url = after.Url,
        Query = after.Url == before.Url ? request.Query : PostmanRequest.QueryOf(after.Url),
        Headers = after.Headers.SequenceEqual(before.Headers) ? request.Headers : [.. after.Headers.Select(h => new PostmanPair(h.Name, h.Value))],
        Body = after.Body == before.Body || request.Body.Mode != PostmanBody.RawMode ? request.Body : request.Body with { Raw = after.Body ?? "" },
    };

    /// <summary>http:// before an address without a scheme, as Postman has it.</summary>
    static string Absolute(string url) => url.Contains("://", StringComparison.Ordinal) || url.Length == 0 ? url : "http://" + url;

    static string UrlOf(PostmanRequest request, Func<string, string> put)
    {
        var url = request.Url;
        // /users/:id, its value put in.
        foreach (var name in PostmanRequest.PathNames(url))
            if (request.Path.FirstOrDefault(p => p.Key == name) is { Value.Length: > 0 } value)
                url = System.Text.RegularExpressions.Regex.Replace(url, $"(?<=/):{System.Text.RegularExpressions.Regex.Escape(name)}(?=[/?#]|$)", value.Value.Replace("$", "$$"));
        return Absolute(put(url).Trim());
    }

    async Task<Built> BuildAsync(PostmanRequest request, PostmanAuth? auth, PostmanScope scope, Func<string, ICollection<string>?, string> resolve, List<string> missing, CancellationToken ct)
    {
        string Put(string text) => resolve(text, missing);
        var url = UrlOf(request, Put);
        var headers = request.Headers.Where(h => h.Enabled && h.Key.Trim().Length > 0).Select(h => (Name: Put(h.Key).Trim(), Value: Put(h.Value))).ToList();
        bool Has(string name) => headers.Any(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var (content, text) = Content(request.Body, Put, headers, Has);
        byte[]? body = null;
        using (content)
            if (content is not null)
            {
                body = await content.ReadAsByteArrayAsync(ct);
                if (content is MultipartFormDataContent { Headers.ContentType: { } type })
                    headers.Add(("Content-Type", type.ToString()));
            }
        var before = headers.Count;
        url = await SignInAsync(auth, scope, Put, request.Method, headers, Has, url, body ?? [], ct);
        // A sign-in worked out of the user's values is theirs as they are.
        var theirs = auth is not null && (scope.Used.ContainsKey(AccessToken) || auth.Values.Values.Any(v => scope.Resolve(v) != scope.Put(v)));
        return new Built(new PostmanOutgoing(request.Method, url, headers, request.Body.Mode, text), body, theirs ? [.. headers.Skip(before).Select(h => h.Value)] : [], theirs);
    }

    /// <summary>The request as an agent (and its scripts) may have it: the user's values by their names, its sign-in not worked out.</summary>
    static PostmanOutgoing Shown(PostmanRequest request, PostmanAuth? auth, PostmanScope scope)
    {
        string Put(string text) => scope.Resolve(text);
        var headers = request.Headers.Where(h => h.Enabled && h.Key.Trim().Length > 0).Select(h => (Put(h.Key).Trim(), Put(h.Value))).ToList();
        if (auth is { Type: not PostmanAuth.NoAuth })
            headers.Add(("Authorization", $"({auth.Type})"));
        var body = request.Body.Mode switch
        {
            PostmanBody.RawMode => Put(request.Body.Raw),
            PostmanBody.UrlEncoded or PostmanBody.FormData => string.Join('&', request.Body.Pairs.Where(p => p.Enabled && p.Key.Length > 0).Select(p => $"{Put(p.Key)}={Put(p.Value)}")),
            PostmanBody.GraphQl => Put(request.Body.Query),
            _ => null,
        };
        return new PostmanOutgoing(request.Method, UrlOf(request, Put), headers, request.Body.Mode, body);
    }

    internal static PostmanAuth? Inherited(PostmanItem item) => PostmanAuth.Read(item.Node["auth"])
        ?? (item.Parent is { } folder ? folder.EffectiveAuth() : PostmanAuth.Read(item.Collection.Root["auth"]));

    /// <summary>The sign-in, as a header (or the query); the URL with it. A digest's and NTLM's wait for the server's challenge.</summary>
    async Task<string> SignInAsync(PostmanAuth? auth, PostmanScope scope, Func<string, string> put, string method, List<(string Name, string Value)> headers, Func<string, bool> has, string url, byte[] body, CancellationToken ct)
    {
        switch (auth?.Type)
        {
            case null or PostmanAuth.NoAuth:
                return url;
            case PostmanAuth.Digest or PostmanAuth.Ntlm:
                // Answered when the server asks, but the user's values in it are known before it is let go.
                foreach (var part in auth.Values.Values)
                    put(part);
                return url;
            case PostmanAuth.Bearer:
                if (!has("Authorization") && put(auth["token"]) is { Length: > 0 } token)
                    headers.Add(("Authorization", "Bearer " + token));
                return url;
            case PostmanAuth.Basic:
                if (!has("Authorization"))
                    headers.Add(("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{put(auth["username"])}:{put(auth["password"])}"))));
                return url;
            case PostmanAuth.ApiKey:
                var (key, value) = (put(auth["key"]), put(auth["value"]));
                if (key.Length == 0)
                    return url;
                if (auth["in"] == "query")
                    return PostmanRequest.WithQuery(url, [.. PostmanRequest.QueryOf(url), new PostmanPair(Uri.EscapeDataString(key), Uri.EscapeDataString(value))]);
                if (!has(key))
                    headers.Add((key, value));
                return url;
            case PostmanAuth.OAuth2:
                var access = put(auth["accessToken"]);
                if (access.Length == 0)
                {
                    if (OAuth is null)
                        throw new PostmanException("Its OAuth 2.0 sign-in has no access token.");
                    // An agent's call sends with the token there is: what asks the server for one is the user's.
                    access = Agent is null ? await OAuth.ForAsync(auth, put, to => RefusedAsync(to, scope), ct)
                        : OAuth.Token(auth, put) is { IsExpired: false } kept ? kept.AccessToken
                        : throw new PostmanException("Its OAuth 2.0 sign-in has no access token (or it ended): the user gets one in Karvon.");
                    scope.Use(AccessToken, access);
                }
                if (auth["addTokenTo"] == "queryParams")
                    return PostmanRequest.WithQuery(url, [.. PostmanRequest.QueryOf(url), new PostmanPair("access_token", Uri.EscapeDataString(access))]);
                if (!has("Authorization"))
                    headers.Add(("Authorization", ((auth.Values.TryGetValue("headerPrefix", out var prefix) ? prefix.Trim() : "Bearer") + " " + access).Trim()));
                return url;
            case PostmanAuth.Aws:
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                    return url;
                var keys = PostmanAws.Of(name => put(auth[name]), uri);
                if (keys.AccessKey.Length == 0 || keys.SecretKey.Length == 0)
                    throw new PostmanException("Its AWS sign-in has no access key or secret key.");
                return PostmanAws.Sign(keys, method, url, headers, body, auth["addAuthDataToQuery"] == "true", DateTimeOffset.UtcNow);
            default:
                throw new PostmanException($"Its sign-in ({auth.Type}) is not one Karvon sends: bearer, basic, API key, OAuth 2.0, AWS, digest and NTLM are.");
        }
    }

    (HttpContent? Content, string? Text) Content(PostmanBody body, Func<string, string> put, List<(string Name, string Value)> headers, Func<string, bool> has)
    {
        void Type(string type)
        {
            if (!has("Content-Type"))
                headers.Add(("Content-Type", type));
        }
        switch (body.Mode)
        {
            case PostmanBody.RawMode:
                var raw = put(body.Raw);
                Type(body.Language switch
                {
                    "json" => "application/json",
                    "xml" => "application/xml",
                    "html" => "text/html",
                    "javascript" => "application/javascript",
                    _ => "text/plain",
                });
                return (new StringContent(raw, Encoding.UTF8), raw);
            case PostmanBody.UrlEncoded:
                var form = string.Join('&', body.Pairs.Where(p => p.Enabled && p.Key.Length > 0).Select(p => $"{Encode(put(p.Key))}={Encode(put(p.Value))}"));
                Type("application/x-www-form-urlencoded");
                return (new StringContent(form, Encoding.UTF8), form);
            case PostmanBody.GraphQl:
                var query = new JsonObject { ["query"] = put(body.Query) };
                if (put(body.Variables).Trim() is { Length: > 0 } variables)
                    try
                    {
                        query["variables"] = JsonNode.Parse(variables);
                    }
                    catch (System.Text.Json.JsonException)
                    {
                        throw new PostmanException("Its GraphQL variables are not JSON.");
                    }
                Type("application/json");
                return (new StringContent(query.ToJsonString(), Encoding.UTF8), query.ToJsonString());
            case PostmanBody.FormData:
                var multipart = new MultipartFormDataContent();
                var shown = new StringBuilder();
                foreach (var pair in body.Pairs.Where(p => p.Enabled && p.Key.Length > 0))
                {
                    var (name, value) = (put(pair.Key), put(pair.Value));
                    if (pair.IsFile)
                    {
                        var file = FileOf(value);
                        var part = new ByteArrayContent(File.ReadAllBytes(file));
                        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                        multipart.Add(part, Quoted(name), Quoted(Path.GetFileName(file)));
                        shown.Append(CultureInfo.InvariantCulture, $"{name}: (file {file})\n");
                    }
                    else
                    {
                        multipart.Add(new StringContent(value, Encoding.UTF8), Quoted(name));
                        shown.Append(CultureInfo.InvariantCulture, $"{name}: {value}\n");
                    }
                }
                // Its own Content-Type has the boundary.
                headers.RemoveAll(h => h.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase));
                return (multipart, shown.ToString().TrimEnd());
            case PostmanBody.FileMode:
                var source = FileOf(put(body.File));
                Type("application/octet-stream");
                return (new ByteArrayContent(File.ReadAllBytes(source)), $"(file {source})");
            default:
                return (null, null);
        }
    }

    static string Encode(string text) => Uri.EscapeDataString(text).Replace("%20", "+", StringComparison.Ordinal);

    static string Quoted(string name) => "\"" + name.Replace("\"", "%22") + "\"";

    /// <summary>A file of a body's, if it may be read (one Claude sends: the workspace's, and not one that holds secrets).</summary>
    string FileOf(string path)
    {
        if (path.Trim().Length == 0)
            throw new PostmanException("Its body's file is not chosen.");
        var full = Path.GetFullPath(path, Folder ?? Directory.GetCurrentDirectory());
        if (mayRead?.Invoke(full) is { } refused)
            throw new PostmanException(refused);
        return File.Exists(full) ? full : throw new PostmanException($"There is no {full} (its body's file).");
    }
}
