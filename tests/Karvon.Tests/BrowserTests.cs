using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Bengine.Introspection.Servers;
using Karvon.Browser;
using Karvon.Browser.Views;
using Karvon.Claude;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>Karvon's browser: its own Chrome driven by Claude's tools, its tool window, the sign-ins Karvon fills in.</summary>
public class BrowserTests
{
    sealed class MemoryKeyring : ICredentialStore
    {
        public Dictionary<string, string> Entries { get; } = [];

        public string Name => "memory";

        public string? Get(string key) => Entries.GetValueOrDefault(key);

        public bool Set(string key, string label, string secret)
        {
            Entries[key] = secret;
            return true;
        }

        public void Delete(string key) => Entries.Remove(key);
    }

    /// <summary>A shop of this machine's: a page to search, choose and add to the cart; a sign-in that greets whoever signs in.</summary>
    sealed class Shop : IDisposable
    {
        readonly HttpListener _listener = new();

        public string Url { get; }

        public Shop()
        {
            var port = FreePort();
            Url = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(Url + "/");
            _listener.Start();
            _ = Task.Run(ServeAsync);
        }

        static int FreePort()
        {
            var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            var port = ((IPEndPoint)socket.LocalEndpoint).Port;
            socket.Stop();
            return port;
        }

        const string Home = """
            <!doctype html><title>Shop</title>
            <h1>Shop</h1>
            <a href="/login">Sign in</a>
            <p>Socks and hats, made by hand.</p>
            <label>Search <input id="q"></label>
            <label>Size <select id="size"><option>S</option><option>M</option><option>L</option></select></label>
            <button type="button" id="add" onclick="add()">Add to cart</button>
            <div id="cart">empty</div>
            <script>
              async function add() {
                const size = document.getElementById('size').value;
                console.log('added ' + size);
                const answer = await (await fetch('/api/cart')).json();
                document.getElementById('cart').textContent = answer.items + ' item';
              }
            </script>
            """;

        const string Login = """
            <!doctype html><title>Sign in</title>
            <h1>Sign in</h1>
            <form method="post" action="/welcome">
              <label>Email <input name="email" type="email"></label>
              <label>Password <input name="password" type="password"></label>
              <button>Sign in</button>
            </form>
            """;

        async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
                {
                    return;
                }
                var path = context.Request.Url!.AbsolutePath;
                string body, type = "text/html";
                if (path == "/api/cart")
                    (body, type) = ("""{"items":1}""", "application/json");
                else if (path == "/login")
                    body = Login;
                else if (path == "/welcome")
                {
                    using var reader = new StreamReader(context.Request.InputStream);
                    var form = System.Web.HttpUtility.ParseQueryString(await reader.ReadToEndAsync());
                    body = $"<!doctype html><title>Welcome</title><h1>Welcome, {WebUtility.HtmlEncode(form["email"])}</h1>"
                        + $"<p>{(form["password"] == "s3cret!" ? "password ok" : "wrong password")}</p>";
                }
                else
                    body = Home;
                var bytes = Encoding.UTF8.GetBytes(body);
                context.Response.ContentType = type + "; charset=utf-8";
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
        }

        public void Dispose() => _listener.Close();
    }

    static JsonElement Input(object value) => JsonSerializer.SerializeToElement(value);

    /// <summary>A profile's folder gone (Chrome may write a last file as it exits).</summary>
    static void Delete(string folder)
    {
        for (var i = 0; i < 20 && Directory.Exists(folder); i++)
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
                Thread.Sleep(250);
            }
        }
    }

    /// <summary>A control's ref in an outline, by its role and name.</summary>
    static string RefOf(string outline, string role, string name)
    {
        var match = Regex.Match(outline, $@"- {role} ""{Regex.Escape(name)}""[^\n]*\[ref=(e\d+)\]");
        Assert.True(match.Success, $"no {role} \"{name}\" in:\n{outline}");
        return match.Groups[1].Value;
    }

    [Fact]
    public async Task Claude_opens_reads_clicks_types_and_signs_in_on_a_site_in_Karvons_Chrome()
    {
        Assert.SkipUnless(ChromeLocator.Find() is not null, "Chrome (or Chromium, or Edge) is not installed.");
        using var shop = new Shop();
        var profile = Directory.CreateTempSubdirectory("karvon-browser-").FullName;
        var browser = new ManagedBrowser(profile, ChromeLocator.Find, headless: true);
        var keyring = new MemoryKeyring();
        var logins = new BrowserLogins(Path.Combine(profile, "..", Path.GetFileName(profile) + "-logins.json"), keyring);
        var tools = BrowserClaudeTools.Create(_ => Task.FromResult(browser), logins).ToDictionary(t => t.Name);
        async Task<ClaudeToolOutput> Call(string tool, object input)
        {
            var output = await tools[tool].CallAsync(Input(input), default);
            Assert.False(output.IsError, output.Text);
            return output;
        }
        try
        {
            // This machine's site opens at once; one not open in the browser yet asks.
            Assert.Equal(ToolAccess.Run, tools["browser_open"].Plan(Input(new { url = shop.Url })).Access);
            var away = tools["browser_open"].Plan(Input(new { url = "example.com" }));
            Assert.Equal((ToolAccess.Write, "Allow Claude to open example.com in Karvon's browser?"), (away.Access, away.Question));
            Assert.Equal("example.com", tools["browser_open"].Target(Input(new { url = "https://example.com/a" })));

            // Opened, and read as an outline: its headings, text and controls, each control with a ref.
            Assert.StartsWith("Tab: Shop — " + shop.Url, (await Call("browser_open", new { url = shop.Url })).Text);
            var page = await Call("browser_snapshot", new { });
            Assert.Equal($"the page 127.0.0.1:{new Uri(shop.Url).Port}", page.Outside);
            Assert.Contains("- heading \"Shop\" [level=1]", page.Text);
            Assert.Contains("- text: \"Socks and hats, made by hand.\"", page.Text);
            Assert.Matches(@"- link ""Sign in"" \[ref=e\d+\] -> /login", page.Text);
            Assert.Matches(@"- combobox ""Size"" \[ref=e\d+\] value=""S""", page.Text);

            // Typed into, chosen from, clicked: the page's console and its requests say what it did.
            await Call("browser_type", new { @ref = RefOf(page.Text, "textbox", "Search"), text = "socks" });
            await Call("browser_select", new { @ref = RefOf(page.Text, "combobox", "Size"), values = new[] { "M" } });
            var add = RefOf(page.Text, "button", "Add to cart");
            Assert.Equal(ToolAccess.Run, tools["browser_click"].Plan(Input(new { @ref = add })).Access);
            var clicked = await Call("browser_click", new { @ref = add, snapshot = true });
            Assert.StartsWith("Clicked button \"Add to cart\"", clicked.Text);
            Assert.Contains("value=\"socks\"", clicked.Text);
            await Call("browser_wait", new { text = "1 item" });
            Assert.Contains("[log] added M", (await Call("browser_logs", new { kind = "console" })).Text);
            Assert.Matches(@"200 http://127\.0\.0\.1:\d+/api/cart", (await Call("browser_logs", new { kind = "network" })).Text);

            // A picture of it; a script of Claude's asks first, and gives what it returns.
            var shot = await Call("browser_screenshot", new { });
            Assert.StartsWith("iVBOR", Assert.Single(shot.Images).Data);
            File.WriteAllBytes(Path.Combine(SampleSolution.ArtifactsDir, "browser-page.png"), Convert.FromBase64String(shot.Images[0].Data));
            Assert.Equal(ToolAccess.Write, tools["browser_eval"].Plan(Input(new { expression = "1" })).Access);
            Assert.Equal("\"1 item\"", (await Call("browser_eval", new { expression = "document.getElementById('cart').textContent" })).Text);

            // The sign-in page: a password is never typed by Claude, and sending the form asks.
            await Call("browser_click", new { @ref = RefOf(page.Text, "link", "Sign in") });
            var signIn = (await Call("browser_snapshot", new { })).Text;
            Assert.Contains("[password]", signIn);
            Assert.Contains("password", tools["browser_type"].Refusal(Input(new { @ref = RefOf(signIn, "textbox", "Password"), text = "x" })));
            Assert.Equal(ToolAccess.Write, tools["browser_click"].Plan(Input(new { @ref = RefOf(signIn, "button", "Sign in") })).Access);

            // A sign-in Karvon keeps: filled in and sent by Karvon, its password never in what Claude gets.
            Assert.Contains("keeps no sign-in", tools["browser_login"].Refusal(Input(new { })));
            logins.Save(shop.Url, "nbk@example.com", "s3cret!");
            Assert.Equal($"Allow Claude to sign in to 127.0.0.1:{new Uri(shop.Url).Port} as nbk@example.com with the password Karvon keeps?",
                tools["browser_login"].Plan(Input(new { })).Question);
            var signedIn = await Call("browser_login", new { snapshot = true });
            Assert.Contains("- heading \"Welcome, nbk@example.com\" [level=1]", signedIn.Text);
            Assert.Contains("password ok", signedIn.Text);
            Assert.DoesNotContain("s3cret!", signedIn.Text);

            // Tabs: another opened, chosen, closed; back where it was.
            await Call("browser_open", new { url = shop.Url, new_tab = true });
            Assert.Equal(2, browser.Tabs.Count);
            Assert.Contains("1. * Welcome", (await Call("browser_tabs", new { action = "select", tab = 1 })).Text);
            await Call("browser_tabs", new { action = "close", tab = 2 });
            await WaitUntil(() => browser.Tabs.Count == 1, TimeSpan.FromSeconds(5));
            // Back from the greeting: the sign-in page (from the back-forward cache, without loading).
            Assert.StartsWith("Tab: Sign in", (await Call("browser_open", new { action = "back" })).Text);
        }
        finally
        {
            await browser.StopAsync();
            Delete(profile);
        }
    }

    [AvaloniaFact]
    public async Task The_Browser_window_starts_Karvons_Chrome_shows_its_tabs_and_keeps_sign_ins()
    {
        Assert.SkipUnless(ChromeLocator.Find() is not null, "Chrome (or Chromium, or Edge) is not installed.");
        using var shop = new Shop();
        var profile = Directory.CreateTempSubdirectory("karvon-browser-").FullName;
        var vm = NewViewModel();
        var module = vm.Module<BrowserModule>()!;
        module.CreateBrowser = () => new ManagedBrowser(profile, ChromeLocator.Find, headless: true);
        module.Keyring = new MemoryKeyring();
        module.LoginsFile = Path.Combine(profile, "..", Path.GetFileName(profile) + "-logins.json");
        var window = new Karvon.Views.MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        window.Show();
        try
        {
            vm.ShowToolWindow(BrowserModule.ToolWindowId);
            await WaitUntil(() => window.GetVisualDescendants().OfType<BrowserView>().Any(v => v.IsEffectivelyVisible), TimeSpan.FromSeconds(5));
            var view = module.EnsureView();
            Assert.False(view.IsRunning);

            // An address: Chrome starts with Karvon's profile and opens it; its tabs show.
            view.Address = shop.Url;
            await view.GoCommand.ExecuteAsync(null);
            Assert.True(view.IsRunning, view.StatusText);
            await WaitUntil(() => view.Tabs.Any(t => t.Title == "Shop"), TimeSpan.FromSeconds(10));
            Assert.Equal("Running · 1 tab(s)", view.StatusText);

            // A sign-in kept: its password in the vault, the site and name in the list.
            module.Logins.Save(shop.Url + "/login", "nbk@example.com", "s3cret!");
            await WaitUntil(() => view.Logins.Count == 1, TimeSpan.FromSeconds(5));
            Assert.Equal(("127.0.0.1:" + new Uri(shop.Url).Port, "nbk@example.com"), (view.Logins[0].Host, view.Logins[0].Username));
            Assert.DoesNotContain("s3cret!", File.ReadAllText(module.LoginsFile));
            Snapshot(window, "browser-window");

            // Closed from the window: nothing runs.
            await view.StopCommand.ExecuteAsync(null);
            Assert.False(view.IsRunning);
        }
        finally
        {
            await module.Browser.StopAsync();
            window.Close();
            Delete(profile);
            File.Delete(module.LoginsFile);
        }
    }

    [Fact]
    public void A_site_is_kept_by_its_origin_and_its_sign_ins_found_by_its_host()
    {
        var folder = Directory.CreateTempSubdirectory("karvon-logins-").FullName;
        try
        {
            var logins = new BrowserLogins(Path.Combine(folder, "logins.json"), new MemoryKeyring());
            Assert.Equal("https://github.com", BrowserLogins.OriginOf("github.com/login"));
            Assert.Equal("http://localhost:5000", BrowserLogins.OriginOf("http://localhost:5000/a?b"));
            Assert.Null(BrowserLogins.OriginOf("ftp://x"));
            logins.Save("github.com", "nbk", "pw1");
            logins.Save("http://github.com", "other", "pw2");
            Assert.Equal(["nbk", "other"], logins.For("https://github.com").Select(l => l.Username));
            Assert.Equal(["other", "nbk"], logins.For("http://github.com").Select(l => l.Username));
            Assert.Equal("pw1", logins.Password(logins.All[0]));
            logins.Remove(logins.All[0]);
            Assert.Equal(["other"], new BrowserLogins(Path.Combine(folder, "logins.json"), null).All.Select(l => l.Username));
            Assert.Throws<InvalidOperationException>(() => new BrowserLogins(Path.Combine(folder, "x.json"), null).Save("a.com", "n", "p"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
