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
    internal sealed class Shop : IDisposable
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

            // The steps written as a Playwright test: each control by its role and name, the password from the environment.
            var steps = (await Call("browser_test", new { })).Text;
            Assert.Contains("type \"socks\" into textbox \"Search\"", steps);
            Assert.Contains("sign in to " + shop.Url + " as nbk@example.com", steps);
            var spec = Path.Combine(profile + "-tests", "shop.spec.ts");
            Assert.Equal((ToolAccess.Write, "Allow Claude to write a Playwright test into shop.spec.ts?"),
                (tools["browser_test"].Plan(Input(new { path = spec })).Access, tools["browser_test"].Plan(Input(new { path = spec })).Question));
            await Call("browser_test", new { path = spec, name = "signs in and adds to the cart", expect = new[] { "password ok" } });
            var written = File.ReadAllText(spec);
            Assert.StartsWith("import { test, expect } from '@playwright/test';\n\ntest(\"signs in and adds to the cart\", async ({ page }) => {\n  await page.goto(\"" + shop.Url + "/\");", written);
            Assert.Contains("  await page.getByRole(\"combobox\", { name: \"Size\" }).selectOption([\"M\"]);", written);
            Assert.Contains("  await page.getByRole(\"button\", { name: \"Add to cart\" }).click();", written);
            Assert.Contains("  await expect(page.getByText(\"1 item\").first()).toBeVisible();", written);
            Assert.Contains("  await page.getByRole(\"textbox\", { name: \"Email\" }).fill(\"nbk@example.com\");", written);
            Assert.Contains($"process.env[\"{BrowserTestWriter.PasswordVariable(shop.Url)}\"]", written);
            Assert.DoesNotContain("s3cret!", written);
            Assert.Equal("No steps yet.", (await Call("browser_test", new { })).Text);
            Directory.Delete(profile + "-tests", recursive: true);

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
    public async Task Karvons_Chrome_is_driven_through_its_pipe_with_no_port_another_program_could_reach_and_closes_with_Karvon()
    {
        var found = ChromeLocator.Find();
        Assert.SkipUnless(found is not null, "Chrome (or Chromium, or Edge) is not installed.");
        Assert.SkipUnless(OperatingSystem.IsLinux(), "It reads what Chrome listens on from Linux's /proc (on Windows Karvon's Chrome has a port).");
        var ct = TestContext.Current.CancellationToken;
        var profile = Directory.CreateTempSubdirectory("karvon-browser-").FullName;
        var portFile = Path.Combine(profile, "DevToolsActivePort");
        // The Chrome an older Karvon started with a debugging port, left open after it.
        var start = new System.Diagnostics.ProcessStartInfo(found!) { UseShellExecute = false, RedirectStandardError = true };
        foreach (var argument in new[] { $"--user-data-dir={profile}", "--remote-debugging-port=0", "--headless=new", "--no-first-run", "about:blank" })
            start.ArgumentList.Add(argument);
        using var old = System.Diagnostics.Process.Start(start)!;
        old.ErrorDataReceived += (_, _) => { };
        old.BeginErrorReadLine();
        var browser = new ManagedBrowser(profile, ChromeLocator.Find, headless: true);
        try
        {
            await WaitUntil(() => File.Exists(portFile), TimeSpan.FromSeconds(30));
            // Any program on the machine could reach its port.
            Assert.NotEmpty(ListeningSockets(old.Id));
            var fifos = Directory.GetDirectories(Path.GetTempPath(), "karvon-cdp-*").ToHashSet();

            await browser.StartAsync(ct);
            Assert.True(browser.IsRunning);
            // The old one is closed, its port with it; the new one wrote none.
            Assert.True(await Exits(old, ct));
            Assert.False(File.Exists(portFile));
            var pid = browser.ProcessId!.Value;
            Assert.DoesNotContain("--remote-debugging-port", File.ReadAllText($"/proc/{pid}/cmdline").Replace('\0', ' '));
            Assert.Empty(ListeningSockets(pid));
            // The FIFOs that handed Chrome its ends are gone.
            Assert.Subset(fifos, Directory.GetDirectories(Path.GetTempPath(), "karvon-cdp-*").ToHashSet());
            Assert.Equal("about:blank", browser.Current?.Url);

            // Karvon lets go of it (it closes, or exits): Chrome closes.
            using var chrome = System.Diagnostics.Process.GetProcessById(pid);
            await browser.DisposeAsync();
            Assert.True(await Exits(chrome, ct));
            Assert.False(browser.IsRunning);
        }
        finally
        {
            if (!old.HasExited)
                old.Kill(entireProcessTree: true);
            await browser.StopAsync();
            Delete(profile);
        }

        static async Task<bool> Exits(System.Diagnostics.Process process, CancellationToken ct)
        {
            try
            {
                await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(15), ct);
                return true;
            }
            catch (TimeoutException)
            {
                return false;
            }
        }

        // The TCP sockets the process listens on (Linux's /proc): its descriptors' sockets that /proc/net/tcp says listen.
        static List<string> ListeningSockets(int pid)
        {
            var sockets = new HashSet<string>();
            foreach (var fd in Directory.EnumerateFileSystemEntries($"/proc/{pid}/fd"))
                if (new FileInfo(fd).LinkTarget is { } target && target.StartsWith("socket:[", StringComparison.Ordinal))
                    sockets.Add(target[8..^1]);
            return [.. new[] { "/proc/net/tcp", "/proc/net/tcp6" }.Where(File.Exists).SelectMany(File.ReadLines).Skip(1)
                .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Where(f => f.Length > 9 && f[3] == "0A" && sockets.Contains(f[9]))
                .Select(f => f[1])];
        }
    }

    [Fact]
    public async Task A_program_that_never_takes_its_pipe_is_let_go_and_nothing_is_left_waiting()
    {
        Assert.SkipUnless(CdpPipe.IsSupported && File.Exists("/bin/true"), "On Windows Karvon's Chrome has a port.");
        var before = Directory.GetDirectories(Path.GetTempPath(), "karvon-cdp-*").ToHashSet();
        // A shell that ends at once, Chrome's ends never opened: Karvon's own opens, which wait for them, are let go.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CdpPipe.StartAsync("/opt/google/chrome/chrome", ["--remote-debugging-pipe"], TimeSpan.FromSeconds(30), "/bin/true", TestContext.Current.CancellationToken))
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal("chrome closed as it started.", error.Message);
        Assert.Subset(before, Directory.GetDirectories(Path.GetTempPath(), "karvon-cdp-*").ToHashSet());
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

    [Fact]
    public void Steps_are_written_as_a_Playwright_test_in_TypeScript_Python_or_CSharp()
    {
        BrowserStep[] steps =
        [
            new("goto", Text: "https://shop.example/"),
            new("fill", "textbox", "Search", "socks"),
            new("select", "combobox", "Size", Values: ["M"]),
            new("click", "button", "Add to cart"),
            new("expect", Text: "1 item"),
            new("login", "textbox", "Email", "nbk@example.com") { Site = "https://shop.example" },
        ];
        Assert.Equal("SHOP_EXAMPLE_PASSWORD", BrowserTestWriter.PasswordVariable("https://shop.example"));
        Assert.Equal("HOST_127_0_0_1_5000_PASSWORD", BrowserTestWriter.PasswordVariable("http://127.0.0.1:5000"));

        Assert.Equal("""
            import os

            from playwright.sync_api import Page, expect


            def test_adds_to_the_cart(page: Page):
                page.goto("https://shop.example/")
                page.get_by_role("textbox", name="Search").fill("socks")
                page.get_by_role("combobox", name="Size").select_option(["M"])
                page.get_by_role("button", name="Add to cart").click()
                expect(page.get_by_text("1 item").first).to_be_visible()
                # The sign-in Karvon keeps for https://shop.example: its password from SHOP_EXAMPLE_PASSWORD, never in the test.
                page.get_by_role("textbox", name="Email").fill("nbk@example.com")
                page.locator('input[type="password"]').first.fill(os.environ.get("SHOP_EXAMPLE_PASSWORD", ""))
                page.locator('input[type="password"]').first.press("Enter")

            """, BrowserTestWriter.Write("test_shop.py", "adds to the cart", steps, null));

        var csharp = BrowserTestWriter.Write("tests/Shop.Tests.cs", "adds to the cart", steps, null);
        Assert.Contains("public class ShopTests : PageTest", csharp);
        Assert.Contains("    public async Task AddsToTheCart()", csharp);
        Assert.Contains("        await Page.GetByRole(AriaRole.Combobox, new() { Name = \"Size\" }).SelectOptionAsync(new[] { \"M\" });", csharp);
        Assert.Contains("        await Expect(Page.GetByText(\"1 item\").First).ToBeVisibleAsync();", csharp);
        Assert.Contains("Environment.GetEnvironmentVariable(\"SHOP_EXAMPLE_PASSWORD\")", csharp);
        Assert.Throws<InvalidOperationException>(() => BrowserTestWriter.Write("Shop.cs", "x", steps, "class A {}"));

        // A file that is there gets the test after what it has (its imports once).
        var first = BrowserTestWriter.Write("shop.spec.ts", "one", steps[..1], null);
        var both = BrowserTestWriter.Write("shop.spec.ts", "two", steps[..1], first);
        Assert.Equal(1, Regex.Count(both, "import \\{ test"));
        Assert.Contains("test(\"two\"", both);
        Assert.Throws<InvalidOperationException>(() => BrowserTestWriter.Write("shop.rb", "x", steps, null));
    }
}
