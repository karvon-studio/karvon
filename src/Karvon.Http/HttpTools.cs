using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using Karvon.Claude;
using Karvon.Claude.Tools;
using static Karvon.Claude.Tools.ToolInput;

namespace Karvon.Http;

/// <summary>
/// Claude Code's HTTP tools: the solution's endpoints, and a request sent (a .http file's, with its environment and the
/// user's values in it that Claude never sees, or one of its own); to localhost at once, anywhere else asking first.
/// </summary>
public static class HttpClaudeTools
{
    internal const int MaxBody = 20_000;

    public static IEnumerable<IClaudeTool> Create(HttpModule module) => [new HttpEndpointsTool(module), new HttpSendTool(module)];

    internal static string Text(HttpExchange e)
    {
        if (e.Error is not null)
            return e.Error;
        var text = new StringBuilder($"{e.Status} {e.Reason} · {e.Elapsed.TotalMilliseconds:0} ms · {e.Size} bytes\n");
        foreach (var (name, value) in e.Headers)
            text.Append($"{name}: {value}\n");
        text.Append('\n').Append(e.Body.Length > MaxBody ? e.Body[..MaxBody] + $"\n… ({e.Body.Length - MaxBody} more characters)" : e.Body);
        return Redaction.Text(text.ToString());
    }
}

/// <summary><c>http_endpoints</c>: the solution's ASP.NET Core endpoints.</summary>
sealed class HttpEndpointsTool(HttpModule module) : IClaudeTool
{
    public string Name => "http_endpoints";

    public string Description =>
        "Lists the open solution's ASP.NET Core endpoints, as Visual Studio's Endpoints Explorer does: method, route, project, and where it "
        + "is written (controllers' actions, minimal APIs' MapGet and the like, Razor pages' and Blazor's @page).";

    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject { ["filter"] = new JsonObject { ["type"] = "string", ["description"] = "Only routes with this in them." } },
    };

    public ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Read, "Endpoints", Str(input, "filter"), "");

    public async Task<ClaudeToolOutput> CallAsync(JsonElement input, CancellationToken cancellationToken) => await Dispatcher.UIThread.InvokeAsync(async () =>
    {
        var endpoints = module.EnsureEndpoints();
        await endpoints.RefreshAsync();
        var filter = Str(input, "filter") ?? "";
        var list = endpoints.Endpoints.Where(e => e.Path.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        var root = module.Workbench.Workspace ?? "";
        return new ClaudeToolOutput(list.Count == 0 ? endpoints.Message ?? "No endpoints." : string.Join("\n", list.Select(e =>
            $"{e.Method} {e.Path} ({Path.GetFileNameWithoutExtension(e.Project)}, {Path.GetRelativePath(root, e.File).Replace('\\', '/')}:{e.Line})"
            + (EndpointScanner.HostAddress(e.Project) is { } host ? $" at {host}" : ""))));
    });
}

/// <summary><c>http_send</c>: a request sent, a .http file's or one given.</summary>
sealed class HttpSendTool(HttpModule module) : IClaudeTool
{
    public string Name => "http_send";

    public string Description =>
        "Sends an HTTP request and returns what came back (status, time, headers, body): a request of a .http file (by its # @name or its "
        + "line), with the file's variables and its environment's (http-client.env.json) put in, or one given by method, url, headers and "
        + "body. The response shows in the .http file's pane when it is open. To localhost it is sent at once; anywhere else the user approves it.";

    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["file"] = new JsonObject { ["type"] = "string", ["description"] = "A .http file's path." },
            ["request"] = new JsonObject { ["type"] = "string", ["description"] = "Its request: its @name, or its line number." },
            ["environment"] = new JsonObject { ["type"] = "string", ["description"] = "http-client.env.json's environment (default: the one chosen in its pane)." },
            ["method"] = new JsonObject { ["type"] = "string", ["description"] = "Without a file: GET, POST..." },
            ["url"] = new JsonObject { ["type"] = "string" },
            ["headers"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = new JsonObject { ["type"] = "string" } },
            ["body"] = new JsonObject { ["type"] = "string" },
        },
    };

    /// <summary>Where it goes, as far as it can be told before it is sent.</summary>
    string? Target(JsonElement input)
    {
        if (Str(input, "file") is not { Length: > 0 })
            return Str(input, "url");
        try
        {
            var (block, variables) = module.Request(Str(input, "file")!, Str(input, "request"), Str(input, "environment"));
            return HttpRunner.Resolve(block, variables).Url;
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpVariableException or IOException)
        {
            return null;
        }
    }

    public ClaudeToolPlan Plan(JsonElement input)
    {
        var url = Target(input);
        var method = Str(input, "method")?.ToUpperInvariant() ?? (Str(input, "file") is { Length: > 0 } ? "the request" : "GET");
        return url is not null && HttpRunner.IsLocalUrl(url)
            ? new(ToolAccess.Run, "Send Request", url, "")
            : new(ToolAccess.Write, "Send Request", url, $"Allow Claude to send {method} {url ?? Str(input, "request") ?? "a request"}?")
            {
                Warning = "It goes to a server outside this machine, with the file's values (a token among them) in it.",
            };
    }

    public async Task<ClaudeToolOutput> CallAsync(JsonElement input, CancellationToken cancellationToken) => await Dispatcher.UIThread.InvokeAsync(async () =>
    {
        try
        {
            HttpExchange exchange;
            if (Str(input, "file") is { Length: > 0 } file)
                exchange = await module.SendAsync(file, Str(input, "request"), Str(input, "environment"), cancellationToken);
            else
            {
                var url = Str(input, "url") ?? throw new InvalidOperationException("'url' is required without a file.");
                var headers = input.TryGetProperty("headers", out var h) && h.ValueKind == JsonValueKind.Object
                    ? h.EnumerateObject().Select(p => (p.Name, p.Value.ToString())).ToList()
                    : [];
                exchange = await HttpRunner.SendAsync(Str(input, "method")?.ToUpperInvariant() ?? "GET", url, headers, Str(input, "body"), cancellationToken);
            }
            var text = HttpClaudeTools.Text(exchange);
            return exchange.Error is null ? new ClaudeToolOutput(text) { Outside = "the HTTP response" } : ClaudeToolOutput.Error(text);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or HttpVariableException)
        {
            return ClaudeToolOutput.Error(ex.Message);
        }
    });
}
