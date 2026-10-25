using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Karvon.Claude;
using Karvon.Http;
using Karvon.Http.ViewModels;
using Karvon.Http.Views;
using Karvon.Views;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>An HTTP server on 127.0.0.1 that says what it was sent (method, path, an Authorization header, the body); /login gives a token.</summary>
sealed class EchoServer : IDisposable
{
    readonly HttpListener _listener = new();

    public EchoServer()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        Port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _ = ServeAsync();
    }

    public int Port { get; }

    public string Url => $"http://127.0.0.1:{Port}";

    async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }
            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            var answer = context.Request.Url!.AbsolutePath == "/login"
                ? """{"token":"abc123","user":{"name":"ada"}}"""
                : new JsonObject
                {
                    ["method"] = context.Request.HttpMethod, ["path"] = context.Request.Url.PathAndQuery, ["auth"] = context.Request.Headers["Authorization"],
                    ["type"] = context.Request.ContentType, ["body"] = body,
                }.ToJsonString();
            var bytes = Encoding.UTF8.GetBytes(answer);
            context.Response.StatusCode = context.Request.Url.AbsolutePath == "/missing" ? 404 : 200;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.Headers["X-Echo"] = "yes";
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
    }

    public void Dispose() => _listener.Close();
}

/// <summary>The HTTP client: .http files read, their variables put in, sent from the editor's pane; the endpoints found; Claude's tools.</summary>
public class HttpClientTests
{
    const string File1 = """
        @host = {{baseUrl}}/api
        @json = application/json

        ### Sign in
        # @name login
        POST {{baseUrl}}/login
        Content-Type: {{json}}

        {"user": "ada"}

        ###
        // Orders of the day
        GET {{host}}/orders
            ?day=today
            &limit=5
        Authorization: Bearer {{login.response.body.$.token}}
        Accept: {{json}}

        ### Nothing there
        GET {{baseUrl}}/missing
        """;

    [Fact]
    public void A_http_file_reads_as_its_requests_and_variables()
    {
        var (requests, variables) = HttpFile.Parse(File1);
        Assert.Equal([("host", "{{baseUrl}}/api", 1), ("json", "application/json", 2)], variables.Select(v => (v.Name, v.Value, v.Line)));
        Assert.Equal(3, requests.Count);
        var login = requests[0];
        Assert.Equal(("POST", "{{baseUrl}}/login", "login", "Sign in", 6), (login.Method, login.Url, login.Name, login.Comment, login.Line));
        Assert.Equal([("Content-Type", "{{json}}")], login.Headers);
        Assert.Equal("""{"user": "ada"}""", login.Body);
        var orders = requests[1];
        Assert.Equal(("GET", "{{host}}/orders?day=today&limit=5", null), (orders.Method, orders.Url, orders.Body));
        Assert.Equal(["Authorization", "Accept"], orders.Headers.Select(h => h.Name));
        Assert.Equal("Nothing there", requests[2].Title);
        Assert.Same(orders, HttpFile.At(requests, 16));
        Assert.Null(HttpFile.At(requests, 2));
        // A bare URL is a GET.
        Assert.Equal(("GET", "https://example.com/x"), (HttpFile.Parse("https://example.com/x").Requests[0].Method, HttpFile.Parse("https://example.com/x").Requests[0].Url));
    }

    [Fact]
    public void Variables_come_from_the_file_its_environment_the_responses_and_the_dynamic_ones()
    {
        var dir = Path.Combine(Path.GetTempPath(), "karvon-http-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "api"));
        File.WriteAllText(Path.Combine(dir, HttpEnvironments.FileName), """
            {
              // Shared by every environment.
              "$shared": { "apiVersion": "v2" },
              "dev": { "baseUrl": "http://localhost:5280", "token": "from-the-shared-file" },
              "prod": { "baseUrl": "https://shop.example" }
            }
            """);
        File.WriteAllText(Path.Combine(dir, HttpEnvironments.FileName + ".user"), """{ "dev": { "token": "mine" } }""");
        File.WriteAllText(Path.Combine(dir, ".env"), "API_KEY=\"k-123\"\n");
        var environments = HttpEnvironments.Load(Path.Combine(dir, "api", "shop.http"));
        Assert.Equal(["dev", "prod"], environments.Keys.Order());
        Assert.Equal(("http://localhost:5280", "mine", "v2"), (environments["dev"]["baseUrl"], environments["dev"]["token"], environments["dev"]["apiVersion"]));

        var login = new HttpExchange("POST", "x", [], null) { Status = 200, RawBody = """{"token":"abc123","items":[{"id":7}]}""", Headers = [("Location", "/me")] };
        var variables = new HttpVariables([new HttpVariable("host", "{{baseUrl}}/{{apiVersion}}", 1)], environments["dev"],
            new Dictionary<string, HttpExchange> { ["login"] = login }, Path.Combine(dir, "api"));
        Assert.Equal("http://localhost:5280/v2/orders", variables.Resolve("{{host}}/orders"));
        Assert.Equal("Bearer abc123 7 /me", variables.Resolve("Bearer {{login.response.body.$.token}} {{login.response.body.$.items[0].id}} {{login.response.headers.Location}}"));
        Assert.Matches("^[0-9a-f-]{36}$", variables.Resolve("{{$guid}}"));
        Assert.InRange(int.Parse(variables.Resolve("{{$randomInt 5 10}}")), 5, 9);
        Assert.InRange(long.Parse(variables.Resolve("{{$timestamp}}")), DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 5, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 5);
        Assert.Equal("k-123", variables.Resolve("{{$dotenv API_KEY}}"));
        Assert.Equal("{{signup.response.body.$.id}} needs the request signup sent first.",
            Assert.Throws<HttpVariableException>(() => variables.Resolve("{{signup.response.body.$.id}}")).Message);
        Assert.StartsWith("{{nope}} has no value", Assert.Throws<HttpVariableException>(() => variables.Resolve("{{nope}}")).Message);
    }

    [Fact]
    public void The_endpoints_of_controllers_minimal_APIs_and_pages_are_found()
    {
        const string code = """
            [ApiController]
            [Route("api/[controller]")]
            public class OrdersController : ControllerBase
            {
                // [HttpDelete("commented")] is no endpoint.
                [HttpGet]
                public IActionResult List() => Ok();

                [HttpGet("{id:int}")]
                public async Task<IActionResult> Get(int id) => Ok();

                [HttpPost("[action]")]
                public IActionResult Import([FromBody] string body) => Ok();

                [HttpPut("/absolute/{id}")]
                public IActionResult Replace(int id) => Ok();
            }

            var api = app.MapGroup("/api/v2");
            var todos = api.MapGroup("/todos");
            todos.MapGet("/{id}", (int id) => id);
            app.MapGroup("/admin").MapPost("/reset", () => "");
            app.MapDelete("/cache", () => "");
            """;
        var found = EndpointScanner.InCode(EndpointScanner.StripComments(code), "Orders.cs", "Web.csproj").ToList();
        Assert.Equal([("GET", "/api/Orders", 7), ("GET", "/api/Orders/{id}", 10), ("POST", "/api/Orders/Import", 13), ("PUT", "/absolute/{id}", 16),
                ("GET", "/api/v2/todos/{id}", 21), ("POST", "/admin/reset", 22), ("DELETE", "/cache", 23)],
            found.Select(e => (e.Method, e.Path, e.Line)));
        Assert.Equal("/api/Orders/{id:int}", found[1].Route);
    }

    [AvaloniaFact]
    public async Task A_http_file_is_sent_from_its_editor_and_the_endpoints_become_requests()
    {
        using var server = new EchoServer();
        var sln = SolutionExplorerTests.CopySample();
        var dir = Path.GetDirectoryName(sln)!;
        File.WriteAllText(Path.Combine(dir, HttpEnvironments.FileName), $$"""{ "dev": { "baseUrl": "{{server.Url}}" }, "prod": { "baseUrl": "https://shop.example" } }""");
        var http = Path.Combine(dir, "shop.http");
        File.WriteAllText(http, File1);
        Directory.CreateDirectory(Path.Combine(dir, "Web", "Controllers"));
        File.WriteAllText(Path.Combine(dir, "Web", "Controllers", "OrdersController.cs"),
            "[ApiController]\n[Route(\"api/orders\")]\npublic class OrdersController : ControllerBase\n{\n    [HttpPost]\n    public IActionResult Create() => Ok();\n}\n");

        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        window.Show();
        await vm.OpenSolutionAsync(sln);

        // The .http file's editor has its pane, ▶ at each request, the environments of http-client.env.json.
        var doc = vm.OpenDocument(http)!;
        var pane = Assert.IsType<HttpPane>(doc.Host.Pane).Model;
        Assert.Equal(["No Environment", "dev", "prod"], pane.Environments.Select(e => e.ToString()));
        Assert.Equal("dev", pane.Environment?.Name);
        Assert.Contains(doc.Host.Editor.TextArea.LeftMargins, m => m is HttpRequestMargin);
        await WaitUntil(() => window.GetVisualDescendants().OfType<HttpPaneView>().Any(v => v.IsEffectivelyVisible), TimeSpan.FromSeconds(5));

        // The second request needs the first's token: said, until the first is sent.
        var orders = await pane.SendAtAsync(16);
        Assert.Equal("{{login.response.body.$.token}} needs the request login sent first.", orders!.Error);
        Assert.True(pane.IsFailure);
        var login = await pane.SendAtAsync(6);
        Assert.Equal((200, "application/json"), (login!.Status, login.ContentType));
        orders = await pane.SendAtAsync(17);
        Assert.Equal(200, orders!.Status);
        var echo = JsonNode.Parse(orders.RawBody)!;
        Assert.Equal(("GET", "/api/orders?day=today&limit=5", "Bearer abc123"), (echo["method"]!.GetValue<string>(), echo["path"]!.GetValue<string>(), echo["auth"]!.GetValue<string>()));
        Assert.Contains("\n  \"method\": \"GET\"", orders.Body);
        Assert.Contains(("X-Echo", "yes"), orders.Headers);
        Assert.Equal($"GET {server.Url}/api/orders?day=today&limit=5\nAuthorization: Bearer abc123\nAccept: application/json", pane.RequestText);
        Assert.Equal(3, pane.History.Count);
        Assert.Equal("404 Not Found", (await pane.SendAtAsync(21))!.StatusText);
        pane.Exchange = orders;
        Snapshot(window, "http-client");

        // The Endpoints Explorer: the sample's, and the controller's; one becomes a request of the project's .http file.
        var module = vm.Module<HttpModule>()!;
        var endpoints = module.EnsureEndpoints();
        await WaitUntil(() => endpoints.Groups.Count > 0, TimeSpan.FromSeconds(10));
        var web = endpoints.Groups.Single(g => g.Title == "Web");
        Assert.Equal([("GET", "/"), ("POST", "/api/orders")], web.Items.Select(r => (r.Method, r.Path)));
        var file = endpoints.AddRequest(web.Items.Single(r => r.Method == "POST"));
        Assert.Equal(Path.Combine(dir, "Web", "Web.http"), file);
        Assert.Equal("@Web_HostAddress = http://localhost:5280\n\n### POST /api/orders\n\nPOST {{Web_HostAddress}}/api/orders\nAccept: application/json\nContent-Type: application/json\n\n{\n}\n",
            File.ReadAllText(file));
        Assert.Equal(file, vm.ActiveEditor?.FilePath);
        await WaitUntil(() => vm.ActiveEditor!.Host.CaretPosition.Line == 5, TimeSpan.FromSeconds(5));

        // Claude: the endpoints, and a request of the file sent (to localhost at once, elsewhere asked first).
        var tools = KarvonTools.Create(HttpClaudeTools.Create(module));
        static JsonElement Input(object value) => JsonSerializer.SerializeToElement(value);
        Assert.Contains("POST /api/orders (Web, Web/Controllers/OrdersController.cs:6) at http://localhost:5280",
            (await tools.Tools.Single(t => t.Name == "http_endpoints").CallAsync(Input(new { }), default)).Text);
        Assert.Equal(ToolAccess.Run, tools.Plan(ClaudeToolServer.Prefix + "http_send", Input(new { file = http, request = "login" }))!.Access);
        var outside = tools.Plan(ClaudeToolServer.Prefix + "http_send", Input(new { method = "delete", url = "https://shop.example/api/orders/1" }))!;
        Assert.Equal((ToolAccess.Write, "Allow Claude to send DELETE https://shop.example/api/orders/1?"), (outside.Access, outside.Question));
        var sent = await tools.Tools.Single(t => t.Name == "http_send").CallAsync(Input(new { file = http, request = "login" }), default);
        Assert.StartsWith("200 OK · ", sent.Text);
        Assert.Contains("\"token\": \"abc123\"", sent.Text);
        // It shows in the file's pane, after the four sent there.
        Assert.Equal(5, pane.History.Count);
        window.Close();
    }
}
