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
}

/// <summary>
/// Sends a collection's requests as Postman does: the collection's, the folders' and the request's pre-request scripts, its
/// variables put in, its sign-in, its body; then their tests over what came back.
/// </summary>
/// <param name="mayReach">Whether a request (a script's too) may go to a host; all of them when null (the user's own Send).</param>
/// <param name="mayRead">Whether a body may be a file of this machine's; any when null.</param>
public sealed class PostmanSender(PostmanEnvironment? environment, PostmanEnvironment? globals, Func<Uri, bool>? mayReach = null, Func<string, string?>? mayRead = null)
{
    /// <summary>Requests a run sends at most (setNextRequest may go round).</summary>
    internal const int MaxRequests = 2000;

    /// <summary>The folder a relative file of a body's is in (the workspace).</summary>
    public string? Folder { get; init; }

    /// <summary>Values given for this call, over every other.</summary>
    public IReadOnlyDictionary<string, string>? Local { get; init; }

    /// <summary>A request sent: <paramref name="request"/> in place of the item's own when it is given (edited, not saved yet).</summary>
    public async Task<PostmanResult> SendAsync(PostmanItem item, PostmanRequest? request = null, CancellationToken ct = default, int iteration = 0, int iterations = 1)
    {
        request ??= item.Read();
        var scope = new PostmanScope(item.Collection, environment, globals, Local);
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
            Send = (method, url, headers, body) => SendOwn(method, url, headers, body, ct),
        };

        HttpExchange Failed(string error) => new(request.Method, scope.Resolve(request.Url), [], null) { Error = error };

        try
        {
            // Before: scripts see the request as it is written, and may change its variables or it.
            var unresolved = new PostmanOutgoing(request.Method, request.Url, [.. request.Headers.Where(h => h.Enabled).Select(h => (h.Key, h.Value))], request.Body.Mode,
                request.Body.Mode == PostmanBody.RawMode ? request.Body.Raw : null);
            var before = Run("prerequest", unresolved, null);
            var preRequest = Scripts("prerequest");
            if (await Task.Run(() => PostmanScripts.Run(preRequest, before, ct), ct) is { } broken)
                return new PostmanResult(path, Failed("The pre-request script stopped: " + broken)) { Console = before.Console, Tests = before.Tests, ScriptError = broken };
            console.AddRange(before.Console);
            tests.AddRange(before.Tests);
            next = before.Next;
            if (before.Skip)
                return new PostmanResult(path, Failed("Skipped by its pre-request script.")) { Console = console, Tests = tests, IsSkipped = true, Next = next };
            if (before.Request != unresolved)
                request = Changed(request, unresolved, before.Request);

            var missing = new List<string>();
            var (outgoing, content) = Build(item, request, scope, missing);
            if (missing.FirstOrDefault(m => outgoing.Url.Contains("{{" + m, StringComparison.Ordinal)) is { } inUrl)
                return new PostmanResult(path, new HttpExchange(outgoing.Method, outgoing.Url, outgoing.Headers, outgoing.Body)
                {
                    Error = $"{{{{{inUrl}}}}} has no value: choose an environment that has it, or set it in the collection's variables.",
                }) { Console = console, Tests = tests, Missing = missing };
            if (Refused(outgoing.Url) is { } refused)
                return new PostmanResult(path, new HttpExchange(outgoing.Method, outgoing.Url, outgoing.Headers, outgoing.Body) { Error = refused }) { Console = console, Tests = tests };

            HttpExchange exchange;
            using (content)
                exchange = await HttpRunner.SendAsync(outgoing.Method, outgoing.Url, outgoing.Headers, content, outgoing.Body, ct) with { Name = item.Name };
            string? error = null;
            if (exchange.Error is null)
            {
                var after = Run("test", outgoing, exchange);
                var scripts = Scripts("test");
                error = await Task.Run(() => PostmanScripts.Run(scripts, after, ct), ct);
                console.AddRange(after.Console);
                tests.AddRange(after.Tests);
                if (after.Next.Set)
                    next = after.Next;
            }
            return new PostmanResult(path, exchange) { Tests = tests, Console = console, ScriptError = error, Missing = missing, Next = next };
        }
        catch (PostmanException ex)
        {
            return new PostmanResult(path, Failed(ex.Message)) { Console = console, Tests = tests };
        }
        finally
        {
            scope.Apply();
        }
    }

    /// <summary>Requests run in their order (or as setNextRequest says), as many times as asked; each result as it comes.</summary>
    public async Task<IReadOnlyList<PostmanResult>> RunAsync(IReadOnlyList<PostmanItem> requests, PostmanRunOptions options, Action<PostmanResult>? done = null, CancellationToken ct = default)
    {
        var results = new List<PostmanResult>();
        for (var iteration = 0; iteration < options.Iterations; iteration++)
            for (var i = 0; i < requests.Count;)
            {
                ct.ThrowIfCancellationRequested();
                if (results.Count == MaxRequests)
                    return results;
                var result = await SendAsync(requests[i], ct: ct, iteration: iteration, iterations: options.Iterations);
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
        return results;
    }

    // ---- Where it may go --------------------------------------------------------------------------------------

    string? Refused(string url) => !Uri.TryCreate(url, UriKind.Absolute, out var uri) || mayReach is null || mayReach(uri) ? null
        : $"Not sent: {uri.Host} is not where this call was to go (a script or a variable changed it).";

    HttpExchange SendOwn(string method, string url, IReadOnlyList<(string Name, string Value)> headers, string? body, CancellationToken ct) =>
        Refused(url) is { } refused
            ? new HttpExchange(method, url, headers, body) { Error = refused }
            : HttpRunner.SendAsync(method, url, headers, body, ct).GetAwaiter().GetResult();

    /// <summary>Where a request goes, as far as it can be told before its scripts run; null when a variable of it has no value yet.</summary>
    public static Uri? HostOf(PostmanItem item, PostmanEnvironment? environment, PostmanEnvironment? globals, IReadOnlyDictionary<string, string>? local = null)
    {
        var url = Absolute(new PostmanScope(item.Collection, environment, globals, local).Resolve(item.Read().Url).Trim());
        var authority = url.Length == 0 ? "" : url[(url.IndexOf("://", StringComparison.Ordinal) + 3)..].Split('/', '?', '#')[0];
        return url.Length > 0 && !authority.Contains("{{", StringComparison.Ordinal) && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri is { Scheme: "http" or "https", Host.Length: > 0 }
            ? uri : null;
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

    (PostmanOutgoing Request, HttpContent? Content) Build(PostmanItem item, PostmanRequest request, PostmanScope scope, List<string> missing)
    {
        string Put(string text) => scope.Resolve(text, missing);
        var url = request.Url;
        // /users/:id, its value put in.
        foreach (var name in PostmanRequest.PathNames(url))
            if (request.Path.FirstOrDefault(p => p.Key == name) is { Value.Length: > 0 } value)
                url = System.Text.RegularExpressions.Regex.Replace(url, $"(?<=/):{System.Text.RegularExpressions.Regex.Escape(name)}(?=[/?#]|$)", value.Value.Replace("$", "$$"));
        url = Absolute(Put(url).Trim());

        var headers = request.Headers.Where(h => h.Enabled && h.Key.Trim().Length > 0).Select(h => (Name: Put(h.Key).Trim(), Value: Put(h.Value))).ToList();
        bool Has(string name) => headers.Any(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        url = SignIn(request.Auth ?? Inherited(item), Put, headers, Has, url);

        var (content, text) = Content(request.Body, Put, headers, Has);
        return (new PostmanOutgoing(request.Method, url, headers, request.Body.Mode, text), content);
    }

    static PostmanAuth? Inherited(PostmanItem item) => PostmanAuth.Read(item.Node["auth"])
        ?? (item.Parent is { } folder ? folder.EffectiveAuth() : PostmanAuth.Read(item.Collection.Root["auth"]));

    /// <summary>The sign-in, as a header (or a query parameter, an API key's); the URL with it.</summary>
    static string SignIn(PostmanAuth? auth, Func<string, string> put, List<(string Name, string Value)> headers, Func<string, bool> has, string url)
    {
        switch (auth?.Type)
        {
            case null or PostmanAuth.NoAuth:
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
                if (put(auth["accessToken"]) is not { Length: > 0 } access)
                    throw new PostmanException("Its OAuth 2.0 sign-in has no access token: put one in (Karvon does not ask the server for it).");
                if (auth["addTokenTo"] == "queryParams")
                    return PostmanRequest.WithQuery(url, [.. PostmanRequest.QueryOf(url), new PostmanPair("access_token", Uri.EscapeDataString(access))]);
                if (!has("Authorization"))
                    headers.Add(("Authorization", (auth.Values.TryGetValue("headerPrefix", out var prefix) ? prefix.Trim() : "Bearer") + " " + access));
                return url;
            default:
                throw new PostmanException($"Its sign-in ({auth.Type}) is not one Karvon sends: bearer, basic, API key and OAuth 2.0 are.");
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
