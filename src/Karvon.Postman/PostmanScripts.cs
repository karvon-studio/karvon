using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jint;
using Jint.Native;
using Jint.Runtime;
using Karvon.Http;

namespace Karvon.Postman;

/// <summary>A test of a script's (pm.test): whether it passed, and why not; one skipped is neither.</summary>
public sealed record PostmanTest(string Name, bool Passed, string? Error = null)
{
    public bool IsSkipped { get; init; }

    public bool Failed => !Passed && !IsSkipped;
}

/// <summary>What a script sees and leaves: the request (which a pre-request script may change), the response, the tests, what it printed.</summary>
sealed class PostmanScriptRun
{
    public required PostmanScope Scope { get; init; }

    public required string Name { get; init; }

    /// <summary>"prerequest" or "test".</summary>
    public required string Event { get; init; }

    public required PostmanOutgoing Request { get; set; }

    public HttpExchange? Response { get; init; }

    public int Iteration { get; init; }

    public int Iterations { get; init; } = 1;

    /// <summary>A request of the script's own (pm.sendRequest), sent if it may go there.</summary>
    public required Func<string, string, IReadOnlyList<(string Name, string Value)>, string?, HttpExchange> Send { get; init; }

    public List<PostmanTest> Tests { get; } = [];

    public List<string> Console { get; } = [];

    /// <summary>What runs next in a run (pm.execution.setNextRequest): a request's name, or null for nothing more.</summary>
    public (bool Set, string? Name) Next { get; set; }

    /// <summary>The script asked that its request is not sent.</summary>
    public bool Skip { get; set; }
}

/// <summary>A request as it goes out, before its scripts see it or after they changed it.</summary>
public sealed record PostmanOutgoing(string Method, string Url, IReadOnlyList<(string Name, string Value)> Headers, string Mode, string? Body);

/// <summary>
/// The scripts of a collection, as Postman runs them, in an interpreter inside Karvon: no file, no network but
/// pm.sendRequest, no .NET, a few seconds and some memory each. They are a repository's code as its requests are.
/// </summary>
static class PostmanScripts
{
    /// <summary>A script's time, its own requests' in it.</summary>
    internal static TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    const int MaxLogLines = 500;

    static readonly string Api = ReadApi();

    static string ReadApi()
    {
        using var stream = typeof(PostmanScripts).Assembly.GetManifestResourceStream("Karvon.Postman.pm.js")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>The scripts run in their order, each on its own; the first error (its text) stops them.</summary>
    public static string? Run(IEnumerable<string> scripts, PostmanScriptRun run, CancellationToken ct)
    {
        foreach (var script in scripts)
            if (Run(script, run, ct) is { } error)
                return error;
        return null;
    }

    static string? Run(string script, PostmanScriptRun run, CancellationToken ct)
    {
        try
        {
            using var engine = new Engine(options => options
                .LimitMemory(128_000_000)
                .LimitRecursion(512)
                .TimeoutInterval(Timeout)
                .RegexTimeoutInterval(TimeSpan.FromSeconds(2))
                .CancellationToken(ct));
            engine.SetValue("__host", Host(engine, run));
            engine.Execute(Api);
            engine.Evaluate("(async () => {\n" + script + "\n})()").UnwrapIfPromise();
            if (engine.Evaluate("__finish()").UnwrapIfPromise() is { } left && run.Event == "prerequest" && JsonNode.Parse(left.AsString()) is JsonObject request)
                run.Request = new PostmanOutgoing(request.Text("method") ?? run.Request.Method, request.Text("url") ?? run.Request.Url,
                    [.. request.Objects("headers").Select(h => (h.Text("key") ?? "", h.Text("value") ?? ""))], run.Request.Mode, request.Text("body"));
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (PromiseRejectedException ex)
        {
            return Message(ex.RejectedValue);
        }
        catch (JavaScriptException ex)
        {
            return Message(ex.Error) is var message && ex.Location.Start.Line > 1 ? $"{message} (line {ex.Location.Start.Line - 1})" : message;
        }
        catch (TimeoutException)
        {
            return $"The script did not end in {Timeout.TotalSeconds:0} s.";
        }
        catch (MemoryLimitExceededException)
        {
            return "The script takes too much memory.";
        }
        catch (RecursionDepthOverflowException)
        {
            return "The script calls itself too deep.";
        }
        // A syntax error (the parser's own), or a limit of the interpreter's.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return ex.Message;
        }
    }

    static string Message(JsValue error) => error.IsObject() && error.Get("message") is { } message && !message.IsUndefined()
        ? (error.Get("name") is { } name && !name.IsUndefined() ? name + ": " : "") + message
        : error.ToString();

    /// <summary>What pm.js has of Karvon: nothing but these.</summary>
    static JsObject Host(Engine engine, PostmanScriptRun run)
    {
        var host = new JsObject(engine);
        void Function(string name, Func<JsValue[], JsValue> body) =>
            host.Set(name, new Jint.Runtime.Interop.ClrFunction(engine, name, (_, arguments) => body(arguments)));
        static string At(JsValue[] arguments, int i) => i < arguments.Length ? arguments[i].ToString() : "";

        host.Set("state", State(run).ToJsonString());
        Function("get", a => run.Scope.TryGet(At(a, 0), At(a, 1), out var value) ? JsValue.FromObject(engine, value) : JsValue.Undefined);
        Function("has", a => run.Scope.TryGet(At(a, 0), At(a, 1), out _) ? JsBoolean.True : JsBoolean.False);
        Function("set", a =>
        {
            var value = a.Length > 2 ? a[2] : JsValue.Undefined;
            run.Scope.Set(At(a, 0), At(a, 1), value.IsNumber() ? value.AsNumber() : value.IsBoolean() ? value.AsBoolean() : value.IsNull() || value.IsUndefined() ? null : value.ToString());
            return JsValue.Undefined;
        });
        Function("unset", a =>
        {
            run.Scope.Unset(At(a, 0), At(a, 1));
            return JsValue.Undefined;
        });
        Function("all", a => JsonSerializer.Serialize(run.Scope.All(At(a, 0)).ToDictionary(v => v.Key, v => v.Value)));
        Function("replace", a => run.Scope.Resolve(At(a, 0)));
        Function("test", a =>
        {
            run.Tests.Add(new PostmanTest(At(a, 0), a.Length > 1 && a[1].AsBoolean(), At(a, 2) is { Length: > 0 } error ? error : null));
            return JsValue.Undefined;
        });
        Function("skip", a =>
        {
            run.Tests.Add(new PostmanTest(At(a, 0), false) { IsSkipped = true });
            return JsValue.Undefined;
        });
        Function("count", _ => run.Tests.Count);
        Function("log", a =>
        {
            if (run.Console.Count < MaxLogLines)
                run.Console.Add(At(a, 0) is "warn" or "error" ? $"{At(a, 0)}: {At(a, 1)}" : At(a, 1));
            return JsValue.Undefined;
        });
        Function("next", a =>
        {
            run.Next = (true, a.Length > 0 && !a[0].IsNull() ? a[0].ToString() : null);
            return JsValue.Undefined;
        });
        Function("skipRequest", _ =>
        {
            run.Skip = true;
            return JsValue.Undefined;
        });
        Function("btoa", a => Convert.ToBase64String(Encoding.Latin1.GetBytes(At(a, 0))));
        Function("atob", a => Convert.TryFromBase64String(At(a, 0), new byte[At(a, 0).Length], out var n) && n >= 0 ? Encoding.Latin1.GetString(Convert.FromBase64String(At(a, 0))) : "");
        Function("send", a => Sent(run, At(a, 0)).ToJsonString());
        return host;
    }

    static JsonObject State(PostmanScriptRun run) => new()
    {
        ["event"] = run.Event,
        ["name"] = run.Name,
        ["environment"] = run.Scope.EnvironmentTitle,
        ["iteration"] = run.Iteration,
        ["iterations"] = run.Iterations,
        ["request"] = new JsonObject
        {
            ["method"] = run.Request.Method,
            ["url"] = run.Request.Url,
            ["headers"] = Headers(run.Request.Headers),
            ["mode"] = run.Request.Mode,
            ["body"] = run.Request.Body,
        },
        ["response"] = run.Response is { } response ? Response(response) : null,
    };

    static JsonArray Headers(IEnumerable<(string Name, string Value)> headers) => new([.. headers.Select(h => new JsonObject { ["key"] = h.Name, ["value"] = h.Value })]);

    static JsonObject Response(HttpExchange e) => new()
    {
        ["code"] = e.Status,
        ["status"] = e.Reason,
        ["time"] = Math.Round(e.Elapsed.TotalMilliseconds),
        ["size"] = e.Size,
        ["headers"] = Headers(e.Headers),
        ["body"] = e.RawBody,
    };

    /// <summary>pm.sendRequest's request, sent: the response, or why there is none.</summary>
    static JsonObject Sent(PostmanScriptRun run, string json)
    {
        var item = new JsonObject { ["request"] = Normal(JsonNode.Parse(json) as JsonObject ?? []) };
        var request = PostmanRequest.Read(item);
        var headers = request.Headers.Where(h => h.Enabled).Select(h => (h.Key, run.Scope.Resolve(h.Value))).ToList();
        string? body = null;
        switch (request.Body.Mode)
        {
            case PostmanBody.RawMode:
                body = run.Scope.Resolve(request.Body.Raw);
                break;
            case PostmanBody.UrlEncoded:
                body = string.Join('&', request.Body.Pairs.Where(p => p.Enabled).Select(p => $"{Uri.EscapeDataString(run.Scope.Resolve(p.Key))}={Uri.EscapeDataString(run.Scope.Resolve(p.Value))}"));
                if (!headers.Any(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)))
                    headers.Add(("Content-Type", "application/x-www-form-urlencoded"));
                break;
            case PostmanBody.None:
                break;
            default:
                return new JsonObject { ["error"] = $"pm.sendRequest does not send a {request.Body.Mode} body in Karvon." };
        }
        var exchange = run.Send(request.Method, run.Scope.Resolve(request.Url), headers, body);
        return exchange.Error is { } error ? new JsonObject { ["error"] = error } : Response(exchange);
    }

    /// <summary>A request as scripts write it (header an object, body a text), as a collection has it.</summary>
    static JsonObject Normal(JsonObject request)
    {
        var normal = (JsonObject)request.DeepClone();
        if ((normal["header"] ?? normal["headers"]) is JsonObject headers)
            normal["header"] = new JsonArray([.. headers.Select(h => new JsonObject { ["key"] = h.Key, ["value"] = h.Value.Text() ?? "" })]);
        else if (normal["header"] is JsonArray rows)
            normal["header"] = new JsonArray([.. rows.Select(r => r is JsonObject row ? (JsonNode)row.DeepClone()
                : r.Text()?.Split(':', 2) is [var key, var value] ? new JsonObject { ["key"] = key.Trim(), ["value"] = value.Trim() } : new JsonObject())]);
        if (normal["body"].Text() is { } text)
            normal["body"] = new JsonObject { ["mode"] = PostmanBody.RawMode, ["raw"] = text };
        else if (normal["body"]?["raw"] is JsonObject or JsonArray)
            normal["body"]!["raw"] = normal["body"]!["raw"]!.ToJsonString();
        return normal;
    }
}
