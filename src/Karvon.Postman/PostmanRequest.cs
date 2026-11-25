using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Karvon.Postman;

/// <summary>A row of a request's: a query parameter, a path variable, a header, a form's field; a file's field has its path in <see cref="Value"/>.</summary>
public sealed record PostmanPair(string Key, string Value)
{
    public bool Enabled { get; init; } = true;

    public string Description { get; init; } = "";

    /// <summary>A form-data field that is a file.</summary>
    public bool IsFile { get; init; }
}

/// <summary>A request's body: its mode (none, raw, urlencoded, formdata, file, graphql) and what that mode has.</summary>
public sealed record PostmanBody(string Mode)
{
    public const string None = "none", RawMode = "raw", UrlEncoded = "urlencoded", FormData = "formdata", FileMode = "file", GraphQl = "graphql";

    public static IReadOnlyList<string> Modes { get; } = [None, FormData, UrlEncoded, RawMode, FileMode, GraphQl];

    public string Raw { get; init; } = "";

    /// <summary>What a raw body is (json, text, xml, html, javascript): its Content-Type when no header says one.</summary>
    public string Language { get; init; } = "text";

    public IReadOnlyList<PostmanPair> Pairs { get; init; } = [];

    public string File { get; init; } = "";

    public string Query { get; init; } = "";

    public string Variables { get; init; } = "";
}

/// <summary>How a request signs in: its type (noauth, bearer, basic, apikey, oauth2, or one Karvon does not send) and its values.</summary>
public sealed record PostmanAuth(string Type, IReadOnlyDictionary<string, string> Values)
{
    public const string NoAuth = "noauth", Bearer = "bearer", Basic = "basic", ApiKey = "apikey", OAuth2 = "oauth2";

    public string this[string name] => Values.GetValueOrDefault(name, "");

    /// <summary>An auth of a request, a folder or a collection (v2.1's rows, v2.0's object); null when it has none of its own (it inherits).</summary>
    public static PostmanAuth? Read(JsonNode? node)
    {
        if (node is not JsonObject auth || auth.Text("type") is not { } type)
            return null;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (auth[type] is JsonArray rows)
        {
            foreach (var row in rows.OfType<JsonObject>())
                if (row.Text("key") is { } key)
                    values[key] = row.Text("value") ?? "";
        }
        else if (auth[type] is JsonObject fields)
            foreach (var (key, value) in fields)
                values[key] = value.Text() ?? "";
        return new PostmanAuth(type, values);
    }

    public JsonObject ToJson() => Type == NoAuth ? new JsonObject { ["type"] = Type } : new JsonObject
    {
        ["type"] = Type,
        [Type] = new JsonArray([.. Values.Select(v => new JsonObject { ["key"] = v.Key, ["value"] = v.Value, ["type"] = "string" })]),
    };
}

/// <summary>A request as a collection has it, its variables unresolved: read from its item, changed, and put back.</summary>
public sealed partial record PostmanRequest
{
    public string Method { get; init; } = "GET";

    /// <summary>The URL as typed, its enabled query in it.</summary>
    public string Url { get; init; } = "";

    /// <summary>Its query's rows, those that are off too (which are not in <see cref="Url"/>).</summary>
    public IReadOnlyList<PostmanPair> Query { get; init; } = [];

    /// <summary>The values of its path's :names.</summary>
    public IReadOnlyList<PostmanPair> Path { get; init; } = [];

    public IReadOnlyList<PostmanPair> Headers { get; init; } = [];

    public PostmanBody Body { get; init; } = new(PostmanBody.None);

    /// <summary>Its own sign-in; null when it is its folder's or its collection's.</summary>
    public PostmanAuth? Auth { get; init; }

    public string PreRequest { get; init; } = "";

    public string Tests { get; init; } = "";

    public string Description { get; init; } = "";

    public static PostmanRequest Read(JsonObject item)
    {
        var (preRequest, tests) = (Script(item, "prerequest"), Script(item, "test"));
        // A request may be its URL alone.
        if (item["request"] is not JsonObject request)
            return new PostmanRequest { Url = item["request"].Text() ?? "", Query = QueryOf(item["request"].Text() ?? ""), PreRequest = preRequest, Tests = tests };
        var url = request["url"];
        var raw = url is JsonObject ? url.Text("raw") ?? FromParts((JsonObject)url) : url.Text() ?? "";
        return new PostmanRequest
        {
            Method = request.Text("method") is { Length: > 0 } method ? method.ToUpperInvariant() : "GET",
            Url = raw,
            Query = url is JsonObject parts && parts["query"] is JsonArray ? [.. Pairs(parts, "query")] : QueryOf(raw),
            Path = [.. Pairs(url, "variable")],
            Headers = request["header"] is JsonArray ? [.. Pairs(request, "header")] : HeaderLines(request["header"].Text() ?? ""),
            Body = ReadBody(request["body"] as JsonObject),
            Auth = PostmanAuth.Read(request["auth"]),
            PreRequest = preRequest,
            Tests = tests,
            Description = request.Description(),
        };
    }

    /// <summary>Put in an item, what it does not know left as it is (examples, protocol options, ids).</summary>
    public void Write(JsonObject item)
    {
        if (item["request"] is not JsonObject request)
            item["request"] = request = [];
        request["method"] = Method;
        WriteAuth(request, Auth);
        request["header"] = new JsonArray([.. Headers.Select(h => ToJson(h))]);
        if (Body.Mode == PostmanBody.None)
            request.Remove("body");
        else
            request["body"] = WriteBody(request["body"] as JsonObject);
        request["url"] = UrlJson();
        request.Put("description", Description);
        PutScript(item, "prerequest", PreRequest);
        PutScript(item, "test", Tests);
        if (item["response"] is null)
            item["response"] = new JsonArray();
    }

    /// <summary>The same request (its rows and texts), whatever lists hold it.</summary>
    public bool Same(PostmanRequest other)
    {
        static string Text(PostmanRequest request)
        {
            var item = new JsonObject();
            request.Write(item);
            return item.ToJsonString();
        }
        return Text(this) == Text(other);
    }

    static void WriteAuth(JsonObject owner, PostmanAuth? auth)
    {
        if (auth is null)
            owner.Remove("auth");
        else if (PostmanAuth.Read(owner["auth"]) is not { } before || before.Type != auth.Type || !before.Values.OrderBy(v => v.Key).SequenceEqual(auth.Values.OrderBy(v => v.Key)))
            owner["auth"] = auth.ToJson();
    }

    // ---- Rows -------------------------------------------------------------------------------------------------

    internal static IEnumerable<PostmanPair> Pairs(JsonNode? owner, string name) => owner.Objects(name).Select(row => new PostmanPair(row.Text("key") ?? "", row.Text("value") ?? "")
    {
        Enabled = !row.Flag("disabled"),
        Description = row.Description(),
    });

    internal static JsonObject ToJson(PostmanPair pair)
    {
        var row = new JsonObject { ["key"] = pair.Key, ["value"] = pair.Value };
        row.Put("description", pair.Description);
        if (!pair.Enabled)
            row["disabled"] = true;
        return row;
    }

    /// <summary>Headers written as lines (an old export's).</summary>
    static IReadOnlyList<PostmanPair> HeaderLines(string text) =>
        [.. text.Split('\n').Select(l => l.Split(':', 2)).Where(l => l.Length == 2 && l[0].Trim().Length > 0).Select(l => new PostmanPair(l[0].Trim(), l[1].Trim()))];

    // ---- The URL ----------------------------------------------------------------------------------------------

    /// <summary>A URL's query, as rows; its values as they are written.</summary>
    public static IReadOnlyList<PostmanPair> QueryOf(string url)
    {
        var query = UrlParts().Match(url).Groups["query"];
        return !query.Success || query.Value.Length == 0 ? [] : [.. query.Value.Split('&').Select(p => p.Split('=', 2)).Select(p => new PostmanPair(p[0], p.ElementAtOrDefault(1) ?? ""))];
    }

    /// <summary>A URL with these rows' enabled ones as its query.</summary>
    public static string WithQuery(string url, IEnumerable<PostmanPair> query)
    {
        var match = UrlParts().Match(url);
        var rows = query.Where(p => p.Enabled && (p.Key.Length > 0 || p.Value.Length > 0)).Select(p => p.Value.Length > 0 ? $"{p.Key}={p.Value}" : p.Key).ToList();
        return match.Groups["base"].Value + (rows.Count > 0 ? "?" + string.Join('&', rows) : "") + (match.Groups["hash"].Success ? "#" + match.Groups["hash"].Value : "");
    }

    /// <summary>The names of a URL's path (/users/:id).</summary>
    public static IReadOnlyList<string> PathNames(string url) =>
        [.. PathOf(UrlParts().Match(url).Groups["base"].Value).Split('/').Where(s => s.Length > 1 && s[0] == ':').Select(s => s[1..]).Distinct(StringComparer.Ordinal)];

    static string PathOf(string url)
    {
        var start = url.IndexOf("://", StringComparison.Ordinal) is >= 0 and var scheme ? scheme + 3 : 0;
        return url.IndexOf('/', start) is >= 0 and var slash ? url[slash..] : "";
    }

    /// <summary>The URL as a collection keeps it: raw, and in parts.</summary>
    JsonObject UrlJson()
    {
        var match = UrlParts().Match(Url);
        var address = match.Groups["base"].Value;
        var url = new JsonObject { ["raw"] = Url };
        if (address.IndexOf("://", StringComparison.Ordinal) is > 0 and var scheme)
        {
            url["protocol"] = address[..scheme];
            address = address[(scheme + 3)..];
        }
        var path = address.IndexOf('/') is >= 0 and var slash ? address[(slash + 1)..] : null;
        var host = path is null ? address : address[..address.IndexOf('/')];
        if (Port().Match(host) is { Success: true } port)
        {
            url["port"] = port.Groups[1].Value;
            host = host[..port.Index];
        }
        if (host.Length > 0)
            url["host"] = new JsonArray([.. OutsideBraces().Split(host).Select(h => (JsonNode)h)]);
        if (path is not null)
            url["path"] = new JsonArray([.. path.Split('/').Select(p => (JsonNode)p)]);
        if (match.Groups["hash"].Success)
            url["hash"] = match.Groups["hash"].Value;
        if (Query.Count > 0)
            url["query"] = new JsonArray([.. Query.Select(q => ToJson(q))]);
        if (Path.Count > 0)
            url["variable"] = new JsonArray([.. Path.Select(p => ToJson(p))]);
        return url;
    }

    /// <summary>A URL kept in parts alone (an old export's).</summary>
    static string FromParts(JsonObject url)
    {
        static string Joined(JsonNode? part, string separator) => part is JsonArray many ? string.Join(separator, many.Select(p => p.Text() ?? p.Text("value") ?? "")) : part.Text() ?? "";
        var text = (url.Text("protocol") is { Length: > 0 } protocol ? protocol.TrimEnd(':', '/') + "://" : "") + Joined(url["host"], ".")
            + (url.Text("port") is { Length: > 0 } port ? ":" + port : "");
        if (Joined(url["path"], "/") is { Length: > 0 } path)
            text += "/" + path.TrimStart('/');
        return WithQuery(text, Pairs(url, "query"));
    }

    [GeneratedRegex(@"^(?<base>(?:\{\{[^}]*\}\}|[^?#])*)(?:\?(?<query>(?:\{\{[^}]*\}\}|[^#])*))?(?:#(?<hash>.*))?$", RegexOptions.Singleline)]
    private static partial Regex UrlParts();

    [GeneratedRegex(@":(\d+|\{\{[^}]*\}\})$")]
    private static partial Regex Port();

    [GeneratedRegex(@"\.(?![^{]*\}\})")]
    private static partial Regex OutsideBraces();

    // ---- The body ---------------------------------------------------------------------------------------------

    static PostmanBody ReadBody(JsonObject? body)
    {
        if (body is null || body.Flag("disabled"))
            return new PostmanBody(PostmanBody.None);
        switch (body.Text("mode"))
        {
            case PostmanBody.RawMode:
                var raw = body.Text("raw") ?? "";
                return new PostmanBody(PostmanBody.RawMode) { Raw = raw, Language = body["options"]?["raw"].Text("language") ?? "text" };
            case PostmanBody.UrlEncoded:
                return new PostmanBody(PostmanBody.UrlEncoded) { Pairs = [.. Pairs(body, PostmanBody.UrlEncoded)] };
            case PostmanBody.FormData:
                return new PostmanBody(PostmanBody.FormData)
                {
                    Pairs = [.. body.Objects(PostmanBody.FormData).Zip(Pairs(body, PostmanBody.FormData), (row, pair) => row.Text("type") == "file"
                        ? pair with { IsFile = true, Value = row["src"] is JsonArray many ? many.FirstOrDefault().Text() ?? "" : row.Text("src") ?? "" } : pair)],
                };
            case PostmanBody.FileMode:
                return new PostmanBody(PostmanBody.FileMode) { File = body["file"].Text("src") ?? body.Text("file") ?? "" };
            case PostmanBody.GraphQl:
                return new PostmanBody(PostmanBody.GraphQl) { Query = body["graphql"].Text("query") ?? "", Variables = body["graphql"].Text("variables") ?? "" };
            default:
                return new PostmanBody(PostmanBody.None);
        }
    }

    JsonObject WriteBody(JsonObject? before)
    {
        // A body that stays in its mode keeps its other options.
        var body = before?.Text("mode") == Body.Mode ? (JsonObject)before.DeepClone() : new JsonObject();
        body["mode"] = Body.Mode;
        body.Remove("disabled");
        switch (Body.Mode)
        {
            case PostmanBody.RawMode:
                body["raw"] = Body.Raw;
                if (Body.Language != "text" || body["options"]?["raw"] is not null)
                {
                    if (body["options"] is not JsonObject options)
                        body["options"] = options = [];
                    if (options["raw"] is not JsonObject raw)
                        options["raw"] = raw = [];
                    raw["language"] = Body.Language;
                }
                break;
            case PostmanBody.UrlEncoded:
                body[PostmanBody.UrlEncoded] = new JsonArray([.. Body.Pairs.Select(p =>
                {
                    var row = ToJson(p);
                    row.Insert(2, "type", "text");
                    return row;
                })]);
                break;
            case PostmanBody.FormData:
                body[PostmanBody.FormData] = new JsonArray([.. Body.Pairs.Select(p =>
                {
                    var row = ToJson(p);
                    if (p.IsFile)
                    {
                        row.Remove("value");
                        row.Insert(1, "src", p.Value);
                    }
                    row.Insert(2, "type", p.IsFile ? "file" : "text");
                    return row;
                })]);
                break;
            case PostmanBody.FileMode:
                body["file"] = new JsonObject { ["src"] = Body.File };
                break;
            case PostmanBody.GraphQl:
                body["graphql"] = new JsonObject { ["query"] = Body.Query, ["variables"] = Body.Variables };
                break;
        }
        return body;
    }

    // ---- Scripts ----------------------------------------------------------------------------------------------

    /// <summary>An item's (a folder's, a collection's) script: "prerequest" or "test".</summary>
    internal static string Script(JsonObject owner, string listen) => string.Join('\n', owner.Objects("event")
        .Where(e => e.Text("listen") == listen && !e.Flag("disabled")).Select(e => e["script"]?["exec"] switch
        {
            JsonArray lines => string.Join('\n', lines.Select(l => l.Text() ?? "")),
            var text => text.Text() ?? "",
        }).Where(s => s.Length > 0));

    internal static void PutScript(JsonObject owner, string listen, string script)
    {
        if (Script(owner, listen) == script)
            return;
        if (owner["event"] is not JsonArray events)
            owner["event"] = events = [];
        var first = events.OfType<JsonObject>().FirstOrDefault(e => e.Text("listen") == listen);
        foreach (var other in events.OfType<JsonObject>().Where(e => e.Text("listen") == listen && e != first).ToList())
            events.Remove(other);
        if (script.Length == 0)
        {
            if (first is not null)
                events.Remove(first);
            if (events.Count == 0)
                owner.Remove("event");
            return;
        }
        if (first is null)
            events.Add(first = new JsonObject { ["listen"] = listen });
        first.Remove("disabled");
        if (first["script"] is not JsonObject body)
            first["script"] = body = new JsonObject { ["type"] = "text/javascript" };
        body["exec"] = new JsonArray([.. script.Replace("\r\n", "\n").Split('\n').Select(l => (JsonNode)l)]);
    }
}
