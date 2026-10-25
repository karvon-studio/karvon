using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Karvon.Http;

/// <summary>What a request sent got back, or why it got nothing.</summary>
public sealed record HttpExchange(string Method, string Url, IReadOnlyList<(string Name, string Value)> RequestHeaders, string? RequestBody)
{
    public int Status { get; init; }

    public string Reason { get; init; } = "";

    public IReadOnlyList<(string Name, string Value)> Headers { get; init; } = [];

    /// <summary>The body as text (JSON indented), or what it is when it is not text.</summary>
    public string Body { get; init; } = "";

    public string? ContentType { get; init; }

    public long Size { get; init; }

    public TimeSpan Elapsed { get; init; }

    /// <summary>Why nothing came back (the server not reached, a timeout, a variable not known).</summary>
    public string? Error { get; init; }

    public DateTimeOffset At { get; init; } = DateTimeOffset.Now;

    public string? Name { get; init; }

    public bool Succeeded => Error is null && Status is >= 200 and < 400;

    public string StatusText => Error ?? $"{Status} {Reason}";

    /// <summary>The raw JSON body, for request variables ({{login.response.body.$.token}}).</summary>
    public string RawBody { get; init; } = "";

    public string Summary => Error is not null ? Error : $"{Status} {Reason} · {Elapsed.TotalMilliseconds:0} ms · {Size} B";
}

/// <summary>A variable that has no value, or a request variable whose request was not sent yet.</summary>
public sealed class HttpVariableException(string message) : Exception(message);

/// <summary>
/// Where a .http file's variables come from: the file's own (@name = value, first), its environment's (http-client.env.json
/// and its .user beside it or above), the responses of the named requests sent before, and the dynamic ones ($guid,
/// $randomInt, $timestamp, $datetime, $localDatetime, $processEnv, $dotenv).
/// </summary>
public sealed partial class HttpVariables
{
    readonly Dictionary<string, string> _file = new(StringComparer.Ordinal);
    readonly IReadOnlyDictionary<string, string> _environment;
    readonly IReadOnlyDictionary<string, HttpExchange> _responses;
    readonly string? _folder;

    public HttpVariables(IEnumerable<HttpVariable> file, IReadOnlyDictionary<string, string> environment, IReadOnlyDictionary<string, HttpExchange> responses, string? folder)
    {
        foreach (var v in file)
            _file[v.Name] = v.Value;
        _environment = environment;
        _responses = responses;
        _folder = folder;
    }

    /// <summary>The text with every {{...}} put in (a variable's own variables too).</summary>
    public string Resolve(string text, int depth = 0)
    {
        if (depth > 10)
            throw new HttpVariableException("A variable refers to itself.");
        return Reference().Replace(text, m => Value(m.Groups[1].Value.Trim(), depth));
    }

    string Value(string name, int depth)
    {
        if (name.StartsWith('$'))
            return Dynamic(name);
        if (_file.TryGetValue(name, out var own))
            return Resolve(own, depth + 1);
        if (_environment.TryGetValue(name, out var env))
            return Resolve(env, depth + 1);
        if (name.Split('.') is [var request, "response" or "request", ..] && _responses.TryGetValue(request, out var exchange))
            return FromExchange(exchange, name.Split('.')[1..]);
        if (name.Contains(".response.", StringComparison.Ordinal))
            throw new HttpVariableException($"{{{{{name}}}}} needs the request {name.Split('.')[0]} sent first.");
        throw new HttpVariableException($"{{{{{name}}}}} has no value: define @{name} in the file or in http-client.env.json.");
    }

    static string FromExchange(HttpExchange exchange, string[] path)
    {
        // response.body.$.a.b, response.body.*, response.headers.Name; request.body..., request.headers...
        var response = path[0] == "response";
        if (path.Length >= 3 && path[1] == "headers")
        {
            var headers = response ? exchange.Headers : exchange.RequestHeaders;
            return headers.FirstOrDefault(h => h.Name.Equals(path[2], StringComparison.OrdinalIgnoreCase)).Value ?? "";
        }
        if (path.Length >= 2 && path[1] == "body")
        {
            var body = response ? exchange.RawBody : exchange.RequestBody ?? "";
            if (path.Length == 2 || path[2] == "*")
                return body;
            return JsonPath(body, string.Join('.', path[2..]));
        }
        throw new HttpVariableException($"{string.Join('.', path)} is not a part of a request Karvon knows.");
    }

    /// <summary>$.a.b[0].c of a JSON text.</summary>
    internal static string JsonPath(string json, string path)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            throw new HttpVariableException("The response's body is not JSON.");
        }
        foreach (Match part in PathPart().Matches(path.TrimStart('$')))
        {
            if (node is null)
                break;
            node = part.Groups["name"].Success ? (node as JsonObject)?[part.Groups["name"].Value] : (node as JsonArray)?.ElementAtOrDefault(int.Parse(part.Groups["index"].Value));
        }
        return node switch
        {
            null => "",
            JsonValue value when value.TryGetValue<string>(out var s) => s,
            _ => node.ToJsonString(),
        };
    }

    string Dynamic(string expression)
    {
        var parts = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (parts[0])
        {
            case "$guid":
                return Guid.NewGuid().ToString();
            case "$randomInt":
                var min = parts.Length > 1 && int.TryParse(parts[1], out var a) ? a : 0;
                var max = parts.Length > 2 && int.TryParse(parts[2], out var b) ? b : 1000;
                return Random.Shared.Next(min, Math.Max(min + 1, max)).ToString(CultureInfo.InvariantCulture);
            case "$timestamp":
                return Offset(DateTimeOffset.UtcNow, parts.Skip(1)).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            case "$datetime" or "$localDatetime":
                var at = Offset(parts[0] == "$datetime" ? DateTimeOffset.UtcNow : DateTimeOffset.Now, parts.Skip(2));
                return (parts.ElementAtOrDefault(1) ?? "iso8601") switch
                {
                    "rfc1123" => at.ToString("r", CultureInfo.InvariantCulture),
                    "iso8601" => at.ToString(parts[0] == "$datetime" ? "yyyy-MM-ddTHH:mm:ss.fffZ" : "yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture),
                    var format => at.ToString(format.Trim('"', '\''), CultureInfo.InvariantCulture),
                };
            case "$processEnv" when parts.Length > 1:
                return Environment.GetEnvironmentVariable(parts[1].TrimStart('%')) ?? "";
            case "$dotenv" when parts.Length > 1:
                return DotEnv(parts[1]) ?? "";
            default:
                throw new HttpVariableException($"{{{{{expression}}}}} is not a variable Karvon knows.");
        }
    }

    static DateTimeOffset Offset(DateTimeOffset at, IEnumerable<string> offset)
    {
        var o = offset.ToArray();
        if (o.Length < 2 || !int.TryParse(o[0], out var n))
            return at;
        return o[1] switch
        {
            "s" => at.AddSeconds(n),
            "m" => at.AddMinutes(n),
            "h" => at.AddHours(n),
            "d" => at.AddDays(n),
            "w" => at.AddDays(n * 7),
            "M" => at.AddMonths(n),
            "y" => at.AddYears(n),
            _ => at,
        };
    }

    /// <summary>A variable of the .env file beside the .http file (or above it).</summary>
    string? DotEnv(string name)
    {
        for (var dir = _folder; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            var file = Path.Combine(dir, ".env");
            if (!File.Exists(file))
                continue;
            foreach (var line in File.ReadLines(file))
                if (line.Split('=', 2) is [var key, var value] && key.Trim() == name)
                    return value.Trim().Trim('"');
            return null;
        }
        return null;
    }

    [GeneratedRegex(@"\{\{(.+?)\}\}")]
    private static partial Regex Reference();

    [GeneratedRegex(@"\.(?<name>[^.\[]+)|\[(?<index>\d+)\]")]
    private static partial Regex PathPart();
}

/// <summary>
/// http-client.env.json (and its .user, the user's own values over the shared ones) nearest a .http file, as Visual
/// Studio reads them: environments by name, each with $shared's variables under its own.
/// </summary>
public static class HttpEnvironments
{
    public const string FileName = "http-client.env.json";

    /// <summary>The environments' names and variables; none when there is no file.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Load(string httpFile, string? root = null)
    {
        var found = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        for (var dir = Path.GetDirectoryName(Path.GetFullPath(httpFile)); dir is not null; dir = Path.GetDirectoryName(dir))
        {
            var file = Path.Combine(dir, FileName);
            if (File.Exists(file))
            {
                var shared = new Dictionary<string, Dictionary<string, string>>();
                Read(file, shared);
                Read(file + ".user", shared);
                var common = shared.GetValueOrDefault("$shared") ?? [];
                foreach (var (name, values) in shared.Where(e => e.Key != "$shared"))
                {
                    var merged = new Dictionary<string, string>(common, StringComparer.Ordinal);
                    foreach (var (k, v) in values)
                        merged[k] = v;
                    found[name] = merged;
                }
                break;
            }
            if (root is not null && Path.GetFullPath(dir).TrimEnd('/', '\\') == Path.GetFullPath(root).TrimEnd('/', '\\'))
                break;
        }
        return found;
    }

    static void Read(string file, Dictionary<string, Dictionary<string, string>> into)
    {
        if (!File.Exists(file))
            return;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(file), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) is not JsonObject root)
                return;
            foreach (var (name, env) in root)
            {
                if (env is not JsonObject values)
                    continue;
                var target = into.TryGetValue(name, out var existing) ? existing : into[name] = [];
                foreach (var (key, value) in values)
                    if (value is JsonValue v && (v.TryGetValue<string>(out var s) ? s : v.ToJsonString()) is { } text)
                        target[key] = text;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
        }
    }
}

/// <summary>Sends a request of a .http file, its variables put in; what came back, as an exchange (an error one when nothing did).</summary>
public static class HttpRunner
{
    internal static TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(100);

    static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        // A development server's own certificate (https://localhost) is taken; any other must be valid.
        SslOptions = new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (sender, _, _, errors) =>
                errors == SslPolicyErrors.None || sender is SslStream { TargetHostName: var host } && IsLocal(host),
        },
    }) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    public static bool IsLocal(string? host) =>
        host is not null && (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host == "127.0.0.1" || host == "::1" || host == "[::1]"
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase));

    public static bool IsLocalUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsLocal(uri.Host);

    /// <summary>The block resolved: its method, URL, headers and body with their variables put in.</summary>
    public static (string Url, List<(string Name, string Value)> Headers, string? Body) Resolve(HttpRequestBlock block, HttpVariables variables) =>
        (variables.Resolve(block.Url).Trim(), [.. block.Headers.Select(h => (h.Name, variables.Resolve(h.Value)))], block.Body is { } b ? variables.Resolve(b) : null);

    public static async Task<HttpExchange> SendAsync(HttpRequestBlock block, HttpVariables variables, CancellationToken ct = default)
    {
        string url;
        List<(string Name, string Value)> headers;
        string? body;
        try
        {
            (url, headers, body) = Resolve(block, variables);
        }
        catch (HttpVariableException ex)
        {
            return new HttpExchange(block.Method, block.Url, block.Headers, block.Body) { Error = ex.Message, Name = block.Name };
        }
        return await SendAsync(block.Method, url, headers, body, ct) with { Name = block.Name };
    }

    public static async Task<HttpExchange> SendAsync(string method, string url, IReadOnlyList<(string Name, string Value)> headers, string? body, CancellationToken ct = default)
    {
        var exchange = new HttpExchange(method, url, headers, body);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return exchange with { Error = $"The URL is not an absolute http(s) one: {url}" };
        using var request = new HttpRequestMessage(new HttpMethod(method), uri);
        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8);
        foreach (var (name, value) in headers)
        {
            if (!request.Headers.TryAddWithoutValidation(name, value) && request.Content is { } content)
            {
                content.Headers.Remove(name);
                content.Headers.TryAddWithoutValidation(name, value);
            }
        }
        if (request.Content is { Headers.ContentType: { } type } && !headers.Any(h => h.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)))
            type.CharSet = "utf-8";
        var watch = Stopwatch.StartNew();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(Timeout);
        try
        {
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseContentRead, limit.Token);
            var bytes = await response.Content.ReadAsByteArrayAsync(limit.Token);
            watch.Stop();
            var contentType = response.Content.Headers.ContentType;
            var raw = IsText(contentType) || bytes.Length == 0 ? Decode(bytes, contentType) : "";
            return exchange with
            {
                Status = (int)response.StatusCode,
                Reason = response.ReasonPhrase ?? "",
                Headers = [.. response.Headers.Concat(response.Content.Headers).SelectMany(h => h.Value.Select(v => (h.Key, v)))],
                ContentType = contentType?.MediaType,
                RawBody = raw,
                Body = IsText(contentType) || bytes.Length == 0 ? Pretty(raw, contentType) : $"({bytes.Length} bytes of {contentType?.MediaType ?? "binary"})",
                Size = bytes.Length,
                Elapsed = watch.Elapsed,
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return exchange with { Error = $"No answer in {Timeout.TotalSeconds:0} s.", Elapsed = watch.Elapsed };
        }
        catch (HttpRequestException ex)
        {
            return exchange with { Error = ex.InnerException is { } inner ? $"{ex.Message} ({inner.Message})" : ex.Message, Elapsed = watch.Elapsed };
        }
    }

    static bool IsText(MediaTypeHeaderValue? type) => type?.MediaType is null
        || type.MediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
        || type.MediaType.Contains("json", StringComparison.OrdinalIgnoreCase) || type.MediaType.Contains("xml", StringComparison.OrdinalIgnoreCase)
        || type.MediaType.Contains("javascript", StringComparison.OrdinalIgnoreCase) || type.MediaType.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)
        || type.MediaType.Contains("yaml", StringComparison.OrdinalIgnoreCase);

    static string Decode(byte[] bytes, MediaTypeHeaderValue? type)
    {
        try
        {
            return (type?.CharSet is { Length: > 0 } charset ? Encoding.GetEncoding(charset.Trim('"')) : Encoding.UTF8).GetString(bytes);
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8.GetString(bytes);
        }
    }

    /// <summary>JSON indented, as Visual Studio shows a response; the rest as it is.</summary>
    static string Pretty(string text, MediaTypeHeaderValue? type)
    {
        if (type?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) != true && !(text.TrimStart().StartsWith('{') || text.TrimStart().StartsWith('[')))
            return text;
        try
        {
            return JsonNode.Parse(text)?.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) ?? text;
        }
        catch (JsonException)
        {
            return text;
        }
    }
}
