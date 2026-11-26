using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bengine.Introspection.Servers;
using Karvon.Claude;
using Karvon.Claude.Tools;
using Karvon.Postman;
using Karvon.Postman.ViewModels;
using Karvon.Services;
using Karvon.Views;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>Postman: its files read and written as they are, variables, scripts and tests, requests sent and run, Claude's tools and the windows.</summary>
public class PostmanTests
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

    /// <summary>Values kept for the session alone (no vault).</summary>
    internal static SealedStore Session() => new(() => null);

    /// <summary>A vault with a passphrase, open.</summary>
    internal static SealedStore Sealed(out SecretVault vault)
    {
        var own = SecretVault.Open(new MemoryKeyring(), Path.Combine(Folder(), "vault.db"))!;
        own.SetPassphrase("correct horse battery");
        vault = own;
        return new SealedStore(() => own);
    }

    internal static string Folder() => Directory.CreateTempSubdirectory("karvon-postman-").FullName;

    internal static string Write(string folder, string name, string json)
    {
        var file = Path.Combine(folder, name);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, json.ReplaceLineEndings("\n"));
        return file;
    }

    /// <summary>A shop's collection: a sign-in whose token its test keeps, then orders with it.</summary>
    internal static string Shop(string folder, string name = "shop") => Write(folder, $"api/{name}.postman_collection.json", """
        {
        	"info": {
        		"_postman_id": "11111111-2222-3333-4444-555555555555",
        		"name": "Shop",
        		"schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json",
        		"_exporter_id": "42"
        	},
        	"item": [
        		{
        			"name": "Sign in",
        			"event": [{ "listen": "test", "script": { "id": "keep-me", "exec": ["pm.test('signed in', () => pm.response.to.have.status(200));", "pm.environment.set('token', pm.response.json().token);"], "type": "text/javascript" } }],
        			"request": {
        				"auth": { "type": "noauth" },
        				"method": "POST",
        				"header": [],
        				"body": { "mode": "raw", "raw": "{\"user\": \"{{user}}\"}", "options": { "raw": { "language": "json" } } },
        				"url": { "raw": "{{baseUrl}}/login", "host": ["{{baseUrl}}"], "path": ["login"] }
        			},
        			"response": [{ "name": "An example", "code": 200 }]
        		},
        		{
        			"name": "Orders",
        			"item": [
        				{
        					"name": "List",
        					"event": [{ "listen": "test", "script": { "exec": ["pm.test('its path', function () { pm.expect(pm.response.json().path).to.include('/orders/7'); });", "pm.test('signed', () => pm.expect(pm.response.json().auth).to.eql('Bearer ' + pm.environment.get('token')));"] } }],
        					"protocolProfileBehavior": { "followRedirects": false },
        					"request": {
        						"method": "GET",
        						"header": [{ "key": "X-Trace", "value": "{{$guid}}" }, { "key": "X-Off", "value": "1", "disabled": true }],
        						"url": {
        							"raw": "{{baseUrl}}/orders/:id?day=today&limit=5",
        							"host": ["{{baseUrl}}"],
        							"path": ["orders", ":id"],
        							"query": [{ "key": "day", "value": "today", "description": "Which day" }, { "key": "limit", "value": "5" }, { "key": "all", "value": "1", "disabled": true }],
        							"variable": [{ "key": "id", "value": "7" }]
        						}
        					},
        					"response": []
        				}
        			],
        			"auth": { "type": "bearer", "bearer": [{ "key": "token", "value": "{{token}}", "type": "string" }] }
        		}
        	],
        	"variable": [{ "key": "user", "value": "ada" }]
        }
        """);

    internal static string Local(string folder, string baseUrl, string extra = "") => Write(folder, "api/local.postman_environment.json", $$"""
        {
        	"id": "env-1",
        	"name": "Local",
        	"values": [
        		{ "key": "baseUrl", "value": "{{baseUrl}}", "type": "default", "enabled": true },
        		{ "key": "apiSecret", "value": "s3cr3t-value-in-file", "type": "secret", "enabled": true }{{extra}}
        	],
        	"_postman_variable_scope": "environment"
        }
        """);

    // ---- The files --------------------------------------------------------------------------------------------

    [Fact]
    public void A_collection_is_read_and_what_is_not_changed_stays_as_it_was()
    {
        var file = Shop(Folder());
        var before = File.ReadAllText(file);
        var collection = PostmanCollection.Load(file);
        Assert.Equal("Shop", collection.Name);
        Assert.Equal(["Sign in", "Orders/List"], collection.Requests.Select(r => r.Path));
        var list = collection.Find("orders/list")!;
        Assert.Same(list.Node, collection.Find("List")!.Node);
        var request = list.Read();
        Assert.Equal(("GET", "{{baseUrl}}/orders/:id?day=today&limit=5"), (request.Method, request.Url));
        Assert.Equal([("day", true), ("limit", true), ("all", false)], request.Query.Select(q => (q.Key, q.Enabled)));
        Assert.Equal(("id", "7"), (request.Path[0].Key, request.Path[0].Value));
        Assert.Equal("bearer", list.EffectiveAuth()!.Type);
        Assert.Null(request.Auth);
        Assert.Equal(PostmanAuth.NoAuth, collection.Find("Sign in")!.EffectiveAuth()!.Type);
        Assert.Contains("signed in", collection.Find("Sign in")!.Read().Tests);

        // Saved as it was read: the same JSON, indented as it was.
        request.Write(list.Node);
        collection.Save();
        var after = File.ReadAllText(file);
        Assert.Contains("\n\t\"info\": {\n\t\t\"_postman_id\"", after);
        Assert.Equal(before.EndsWith('\n'), after.EndsWith('\n'));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(before), JsonNode.Parse(after)), after);

        // One request changed: its own part alone; its example, its options and the script's id stay.
        var signIn = collection.Find("Sign in")!;
        (signIn.Read() with { Url = "https://api.{{domain}}:8443/v1/login?x=1#top", Query = PostmanRequest.QueryOf("?x=1"), Tests = "pm.test('ok', () => {});" }).Write(signIn.Node);
        collection.Save();
        var read = PostmanCollection.Load(file).Find("Sign in")!;
        Assert.Equal("""{"raw":"https://api.{{domain}}:8443/v1/login?x=1#top","protocol":"https","port":"8443","host":["api","{{domain}}"],"path":["v1","login"],"hash":"top","query":[{"key":"x","value":"1"}]}""",
            read.Node["request"]!["url"]!.ToJsonString());
        Assert.Equal("keep-me", read.Node["event"]![0]!["script"]!["id"]!.GetValue<string>());
        Assert.Equal("An example", read.Node["response"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("42", collection.Root["info"]!["_exporter_id"]!.GetValue<string>());
        // A script emptied goes.
        (read.Read() with { Tests = "" }).Write(read.Node);
        Assert.False(read.Node.ContainsKey("event"));
    }

    [Fact]
    public void Other_exports_are_read_too_and_what_is_no_collection_is_said()
    {
        var folder = Folder();
        // v2.0: auth's values an object, the URL a text, headers as lines, a request that is its URL alone.
        var old = PostmanCollection.Load(Write(folder, "old.postman_collection.json", """
            {"info":{"name":"Old","schema":"https://schema.getpostman.com/json/collection/v2.0.0/collection.json"},
             "item":[{"name":"A","request":{"method":"get","url":"http://x/a?b=1","header":"X-A: 1\nX-B: two","auth":{"type":"basic","basic":{"username":"u","password":"p"}}}},
                     {"name":"B","request":"http://x/b"},
                     {"name":"C","request":{"url":{"protocol":"http","host":["x","y"],"port":"81","path":["c","d"],"query":[{"key":"q","value":"1"}]},"body":{"mode":"file","file":{"src":"a.bin"}}}}]}
            """));
        var a = old.Find("A")!.Read();
        Assert.Equal(("GET", "X-B", "two", "u"), (a.Method, a.Headers[1].Key, a.Headers[1].Value, a.Auth!["username"]));
        Assert.Equal("http://x/b", old.Find("B")!.Read().Url);
        Assert.Equal(("http://x.y:81/c/d?q=1", "a.bin"), (old.Find("C")!.Read().Url, old.Find("C")!.Read().Body.File));

        Assert.Contains("v1", Assert.Throws<PostmanException>(() => PostmanCollection.Load(Write(folder, "v1.json", """{"id":"1","order":[],"requests":[]}"""))).Message);
        Assert.Throws<PostmanException>(() => PostmanCollection.Load(Write(folder, "no.json", "{}")));
        Assert.Contains("not JSON", Assert.Throws<PostmanException>(() => PostmanCollection.Load(Write(folder, "bad.json", "{"))).Message);

        // Items added, copied, told apart by their place, removed.
        var made = PostmanCollection.Create(Path.Combine(folder, "new.postman_collection.json"), "New");
        var orders = made.Add(null, "Orders", isFolder: true);
        var get = made.Add(orders, "Get", isFolder: false);
        (get.Read() with { Url = "http://x/1" }).Write(get.Node);
        var copy = made.Duplicate(get);
        copy.Name = "Get";
        Assert.Equal(["Orders/Get", "Orders/Get#2"], made.Requests.Select(made.PathOf));
        Assert.Same(copy.Node, made.Find("Orders/Get#2")!.Node);
        made.Remove(get);
        made.Save();
        Assert.Equal(["Orders/Get"], PostmanCollection.Load(made.File).Requests.Select(r => r.Path));
    }

    [Fact]
    public void Bodies_are_kept_in_their_modes()
    {
        foreach (var body in new[]
        {
            new PostmanBody(PostmanBody.None),
            new PostmanBody(PostmanBody.RawMode) { Raw = "<a/>", Language = "xml" },
            new PostmanBody(PostmanBody.UrlEncoded) { Pairs = [new("a", "1"), new("b", "2") { Enabled = false, Description = "why" }] },
            new PostmanBody(PostmanBody.FormData) { Pairs = [new("a", "1"), new("f", "/tmp/x.png") { IsFile = true }] },
            new PostmanBody(PostmanBody.FileMode) { File = "x.bin" },
            new PostmanBody(PostmanBody.GraphQl) { Query = "{ a }", Variables = "{}" },
        })
        {
            var item = new JsonObject();
            var request = new PostmanRequest { Method = "POST", Url = "http://x", Body = body };
            request.Write(item);
            Assert.True(request.Same(PostmanRequest.Read((JsonObject)JsonNode.Parse(item.ToJsonString())!)), body.Mode);
        }
        Assert.Equal("http://x/a?b=1&c#h", PostmanRequest.WithQuery("http://x/a?old=1#h", [new("b", "1"), new("c", ""), new("d", "2") { Enabled = false }]));
        Assert.Equal([("a", "{{x?y}}"), ("b", "1=2")], PostmanRequest.QueryOf("http://x?a={{x?y}}&b=1=2").Select(q => (q.Key, q.Value)));
        Assert.Equal(["id", "line"], PostmanRequest.PathNames("{{base}}/orders/:id/lines/:line?x=:no"));
        Assert.Empty(PostmanRequest.PathNames("http://localhost:5000/a"));
    }

    [Fact]
    public async Task An_environments_values_go_to_the_vaults_sealed_part_never_to_its_file()
    {
        var store = Sealed(out var vault);
        var file = Local(Folder(), "http://localhost:1");
        var before = File.ReadAllText(file);
        var environment = PostmanEnvironment.Load(file, store);
        Assert.Equal(["baseUrl", "apiSecret"], environment.Variables.Select(v => v.Key));
        Assert.True(environment.Variables[1].HoldsSecret);
        environment.Set("token", "tok-0123456789-ABC");
        environment.Set("baseUrl", "http://localhost:2");
        await store.Written;
        Assert.Equal(before, File.ReadAllText(file));
        Assert.Contains("tok-0123456789-ABC", vault.GetSealed("postman/environment/env-1"));
        // Read again (another session): the vault's, over the file's.
        var again = PostmanEnvironment.Load(file, store);
        Assert.Equal([("baseUrl", "http://localhost:2"), ("apiSecret", "s3cr3t-value-in-file"), ("token", "tok-0123456789-ABC")], again.Variables.Select(v => (v.Key, v.Value)));
        // Its secrets are hidden in what goes to an agent, with no way back.
        Assert.Equal("a «hidden: Postman Local/apiSecret» b «hidden: Postman Local/token»", Redaction.Secrets("a s3cr3t-value-in-file b tok-0123456789-ABC"));
        again.Unset("token");
        again.Unset("baseUrl");
        await store.Written;
        Assert.Null(vault.GetSealed("postman/environment/env-1"));

        // Edited: the file gets names, never a value; a value it came with stays until it is moved.
        again.Put("Here", [new("baseUrl", "") { Current = "http://localhost:3" }, new("apiSecret", "") { IsSecret = true, Current = "mine-9876543-XYZ" }, new("new", "") { Current = "n" }]);
        await store.Written;
        var written = File.ReadAllText(file);
        Assert.Contains("\"name\": \"Here\"", written);
        Assert.Contains("\"key\": \"new\"", written);
        Assert.DoesNotContain("mine-9876543-XYZ", written);
        Assert.DoesNotContain("localhost:3", written);
        Assert.Contains("mine-9876543-XYZ", vault.GetSealed("postman/environment/env-1"));
        Assert.True(again.HasFileValues);
        await again.MoveToVaultAsync();
        Assert.False(again.HasFileValues);
        Assert.DoesNotContain("s3cr3t-value-in-file", File.ReadAllText(file));
        Assert.DoesNotContain("localhost:1", File.ReadAllText(file));
        Assert.Equal("mine-9876543-XYZ", PostmanEnvironment.Load(file, store).Variables.Single(v => v.Key == "apiSecret").Value);

        // Without a passphrase: this session's, and the file's values are not moved (they would be lost).
        var other = Local(Folder(), "http://localhost:1");
        var loose = Session();
        var kept = PostmanEnvironment.Load(other, loose);
        kept.Set("token", "t");
        Assert.Equal("t", PostmanEnvironment.Load(other, loose).Variables.Single(v => v.Key == "token").Value);
        Assert.DoesNotContain(PostmanEnvironment.Load(other, Session()).Variables, v => v.Key == "token");
        await Assert.ThrowsAsync<PostmanException>(kept.MoveToVaultAsync);
        Assert.True(kept.HasFileValues);
    }

    // ---- Variables and scripts --------------------------------------------------------------------------------

    [Fact]
    public void Variables_are_found_the_nearest_first()
    {
        var folder = Folder();
        var collection = PostmanCollection.Load(Write(folder, "c.postman_collection.json", """{"info":{"name":"C"},"item":[],"variable":[{"key":"a","value":"collection"},{"key":"b","value":"{{a}}-{{c}}"},{"key":"loop","value":"{{loop}}"}]}"""));
        var environment = PostmanEnvironment.Load(Write(folder, "e.postman_environment.json", """{"id":"e","name":"E","values":[{"key":"c","value":"env"},{"key":"off","value":"x","enabled":false}]}"""), Session());
        var globals = PostmanEnvironment.Load(Write(folder, "g.postman_globals.json", """{"id":"g","name":"Globals","values":[{"key":"a","value":"global"},{"key":"g","value":"1"}],"_postman_variable_scope":"globals"}"""), Session());
        var scope = new PostmanScope(collection, environment, globals);
        var missing = new List<string>();
        Assert.Equal("collection collection-env 1 {{off}} {{nope}}", scope.Resolve("{{a}} {{ b }} {{g}} {{off}} {{nope}}", missing));
        Assert.Equal(["off", "nope"], missing);
        Assert.Contains("{{loop}}", scope.Resolve("{{loop}}"));
        Assert.True(Guid.TryParse(scope.Resolve("{{$guid}}"), out _));
        Assert.Matches(@"^\d{10}$", scope.Resolve("{{$timestamp}}"));
        Assert.Matches(@"^[\w.]+@example\.com$", scope.Resolve("{{$randomEmail}}"));
        Assert.Equal("{{$nothing}}", scope.Resolve("{{$nothing}}"));
        Assert.Equal("call", new PostmanScope(collection, environment, globals, new Dictionary<string, string> { ["a"] = "call" }).Resolve("{{a}}"));
        // What a script sets is put where it belongs, once it is done.
        scope.Set(PostmanScope.EnvironmentName, "c", "new");
        Assert.Equal("env", environment.Variables[0].Value);
        scope.Apply();
        Assert.Equal("new", environment.Variables.First(v => v.Key == "c").Value);
        Assert.Equal("", File.ReadAllText(environment.File).Contains("new", StringComparison.Ordinal) ? "in the file" : "");
    }

    /// <summary>A script run on a response, its tests and what it printed.</summary>
    internal static (IReadOnlyList<PostmanTest> Tests, IReadOnlyList<string> Console, string? Error, PostmanScope Scope) Script(string script, string body = """{"id":7,"tags":["a","b"],"user":{"name":"ada"},"ok":true,"none":null}""")
    {
        var collection = PostmanCollection.Load(Write(Folder(), "c.postman_collection.json", """{"info":{"name":"C"},"item":[]}"""));
        var scope = new PostmanScope(collection, null, null);
        var run = new PostmanScriptRun
        {
            Scope = scope, Name = "R", Event = "test", Request = new PostmanOutgoing("GET", "http://localhost/a?b=1", [("X-A", "1")], "none", null),
            Response = new Karvon.Http.HttpExchange("GET", "http://localhost/a", [], null)
            {
                Status = 200, Reason = "OK", RawBody = body, Body = body, Headers = [("Content-Type", "application/json")], Elapsed = TimeSpan.FromMilliseconds(12), Size = body.Length,
            },
            Send = r => r.Url.EndsWith("/bad", StringComparison.Ordinal) ? new("GET", r.Url, [], null) { Error = "refused" } : new("GET", r.Url, [], null) { Status = 201, Reason = "Created", RawBody = """{"sent":true}""" },
        };
        var error = PostmanScripts.Run([script], run, CancellationToken.None);
        return (run.Tests, run.Console, error, scope);
    }

    [Fact]
    public void Tests_pass_and_fail_as_postmans_do()
    {
        var (tests, console, error, _) = Script("""
            const json = pm.response.json();
            pm.test("status", () => { pm.response.to.have.status(200); pm.response.to.have.status('OK'); pm.response.to.be.ok; pm.response.to.be.json; pm.response.to.not.be.error; });
            pm.test("headers", () => { pm.response.to.have.header('content-type'); pm.expect(pm.response.headers.get('Content-Type')).to.include('json'); });
            pm.test("body", () => { pm.response.to.have.jsonBody('user.name', 'ada'); pm.response.to.have.jsonBody('tags[1]', 'b'); pm.expect(pm.response.text()).to.match(/ada/); });
            pm.test("chai", function () {
              pm.expect(json.id).to.equal(7); pm.expect(json.id).to.be.a('number').that.is.above(6).and.below(8); pm.expect(json.tags).to.be.an('array').that.includes('a');
              pm.expect(json.tags).to.have.lengthOf(2); pm.expect(json.tags).to.have.length.above(1); pm.expect(json).to.have.property('user').with.property('name', 'ada');
              pm.expect(json.user).to.eql({ name: 'ada' }); pm.expect(json).to.have.all.keys('id', 'tags', 'user', 'ok', 'none'); pm.expect(json).to.include.keys('id');
              pm.expect(json.ok).to.be.true; pm.expect(json.none).to.be.null; pm.expect(json.nope).to.be.undefined; pm.expect('').to.be.empty; pm.expect([1, 2]).to.have.members([2, 1]);
              pm.expect(json.id).to.be.oneOf([1, 7]); pm.expect(json.id).to.not.equal(8); pm.expect(() => { throw new Error('x'); }).to.throw('x'); pm.expect(pm.response.responseTime).to.be.below(200);
              pm.expect(json).to.have.nested.property('user.name'); pm.expect([{ a: 1 }]).to.deep.include({ a: 1 }); pm.expect(5).to.be.within(1, 5);
            });
            pm.test("fails", () => pm.expect(json.id).to.equal(8));
            pm.test("fails by status", () => pm.response.to.have.status(404));
            pm.test("throws", () => json.nope.deeper);
            pm.test.skip("later", () => {});
            pm.test("async", async () => { const r = await pm.sendRequest('http://localhost/x'); pm.expect(r.code).to.equal(201); pm.expect(r.json().sent).to.be.true; });
            pm.test("done", done => setTimeout(() => done(), 50));
            pm.sendRequest({ url: 'http://localhost/bad', method: 'POST', header: { 'X-A': '1' }, body: { mode: 'raw', raw: '{}' } }, (err, res) => pm.test("callback", () => pm.expect(err.message).to.equal('refused')));
            tests["old way"] = responseCode.code === 200; tests["old fail"] = false;
            console.log('hello', json.user, 5); console.error('bad');
            pm.test("info", () => { pm.expect(pm.info.requestName).to.equal('R'); pm.expect(pm.request.headers.get('x-a')).to.equal('1'); pm.expect(pm.request.url.query.get('b')).to.equal('1'); });
            pm.test("base64", () => pm.expect(atob(btoa('ab'))).to.equal('ab'));
            """);
        Assert.Null(error);
        var failed = tests.Where(t => t.Failed).ToDictionary(t => t.Name, t => t.Error!);
        Assert.Equal(["fails", "fails by status", "old fail", "throws"], failed.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("AssertionError: expected 7 to equal 8", failed["fails"]);
        Assert.Equal("AssertionError: expected response to have status 404 but got 200", failed["fails by status"]);
        Assert.Contains("TypeError", failed["throws"]);
        Assert.True(tests.Single(t => t.Name == "later").IsSkipped);
        Assert.Equal(["async", "base64", "body", "callback", "chai", "done", "headers", "info", "old way", "status"], tests.Where(t => t.Passed).Select(t => t.Name).Order(StringComparer.Ordinal));
        Assert.Equal(["hello {\"name\":\"ada\"} 5", "error: bad"], console);
    }

    [Fact]
    public void A_script_sets_variables_and_reaches_nothing_else_of_the_machine()
    {
        var (_, _, error, scope) = Script("""
            pm.variables.set('n', 5); pm.collectionVariables.set('o', { a: 1 }); pm.variables.set('next', pm.variables.get('n') + 1);
            pm.variables.set('flag', pm.variables.has('nope')); pm.variables.set('text', pm.variables.replaceIn('{{n}}-{{next}}'));
            """);
        Assert.Null(error);
        Assert.Equal("5-6 {\"a\":1} false 5-6", scope.Resolve("{{n}}-{{next}} {{o}} {{flag}} {{text}}"));
        foreach (var script in new[] { "System.IO.File.ReadAllText('/etc/passwd')", "importNamespace('System')", "require('fs')", "process.env.HOME", "fetch('http://x')", "new XMLHttpRequest()" })
            Assert.NotNull(Script(script).Error);
        // Errors are said, with their line.
        Assert.Contains("ReferenceError", Script("const a = 1;\nnope();").Error);
        Assert.NotNull(Script("const = ;").Error);
        // One that does not end is ended.
        var before = PostmanScripts.Timeout;
        PostmanScripts.Timeout = TimeSpan.FromSeconds(1);
        try
        {
            Assert.Contains("did not end", Script("while (true) {}").Error);
        }
        finally
        {
            PostmanScripts.Timeout = before;
        }
        Assert.NotNull(Script("const f = () => f(); f();").Error);
    }

    // ---- Sending ----------------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Requests_are_sent_with_their_variables_sign_in_and_scripts()
    {
        using var server = new EchoServer();
        var folder = Folder();
        var collection = PostmanCollection.Load(Shop(folder));
        var environment = PostmanEnvironment.Load(Local(folder, server.Url), Session());
        var sender = new PostmanSender(environment, null) { Folder = folder };

        // The sign-in's test keeps its token; the orders, of a folder that signs in with it, go with it.
        var signIn = await sender.SendAsync(collection.Find("Sign in")!);
        Assert.True(signIn.Succeeded, signIn.Exchange.StatusText + signIn.ScriptError);
        Assert.Equal("""{"user": "ada"}""", signIn.Exchange.RequestBody);
        Assert.Contains(("Content-Type", "application/json"), signIn.Exchange.RequestHeaders);
        Assert.Equal("abc123", environment.Variables.Single(v => v.Key == "token").Value);
        var list = await sender.SendAsync(collection.Find("Orders/List")!);
        Assert.Equal("2/2 tests passed", list.TestsText);
        Assert.Equal($"{server.Url}/orders/7?day=today&limit=5", list.Exchange.Url);
        Assert.Equal("Bearer abc123", JsonNode.Parse(list.Exchange.RawBody)!["auth"]!.GetValue<string>());
        Assert.DoesNotContain(list.Exchange.RequestHeaders, h => h.Name == "X-Off");

        // Edited and not saved: sent as edited. A pre-request script changes the request; a variable without a value is said.
        var item = collection.Find("Orders/List")!;
        var edited = item.Read() with
        {
            Method = "POST", Url = "{{baseUrl}}/a b/{{what}}", Query = [], Auth = new PostmanAuth(PostmanAuth.ApiKey, new Dictionary<string, string> { ["key"] = "k", ["value"] = "v 1", ["in"] = "query" }),
            PreRequest = "pm.request.headers.upsert({ key: 'X-Made', value: 'yes' }); pm.variables.set('what', 'thing'); pm.request.body.raw = 'changed {{gone}}';", Tests = "",
            Body = new PostmanBody(PostmanBody.RawMode) { Raw = "as it was", Language = "text" },
        };
        var sent = await sender.SendAsync(item, edited);
        Assert.Contains(("X-Made", "yes"), sent.Exchange.RequestHeaders);
        Assert.Equal("changed {{gone}}", sent.Exchange.RequestBody);
        Assert.Equal(["gone"], sent.Missing);
        Assert.EndsWith("/thing?k=v%201", sent.Exchange.Url);
        Assert.Contains("{{nowhere}} has no value", (await sender.SendAsync(item, edited with { Url = "{{nowhere}}/x", PreRequest = "" })).Exchange.Error);
        var skipped = await sender.SendAsync(item, edited with { PreRequest = "pm.execution.skipRequest();" });
        Assert.True(skipped is { IsSkipped: true, Succeeded: true });
        Assert.Contains("stopped", (await sender.SendAsync(item, edited with { PreRequest = "throw new Error('no');" })).Exchange.Error);
        Assert.Contains("hawk", (await sender.SendAsync(item, edited with { Auth = new PostmanAuth("hawk", new Dictionary<string, string>()) })).Exchange.Error);
        Assert.Contains("no access key", (await sender.SendAsync(item, edited with { Auth = new PostmanAuth("awsv4", new Dictionary<string, string>()) })).Exchange.Error);
        // A test's failure is not a request's: it came back.
        var failing = await sender.SendAsync(item, edited with { Tests = "pm.test('no', () => pm.expect(1).to.equal(2));" });
        Assert.True(failing is { Succeeded: false, Failed: 1, Exchange.Succeeded: true });
    }

    [AvaloniaFact]
    public async Task Bodies_are_sent_in_their_forms_and_files_only_where_they_may_be_read()
    {
        using var server = new EchoServer();
        var folder = Folder();
        var collection = PostmanCollection.Load(Shop(folder));
        var item = collection.Find("Sign in")!;
        File.WriteAllText(Path.Combine(folder, "note.txt"), "the note");
        async Task<JsonNode> Send(PostmanBody body, PostmanSender? sender = null)
        {
            var result = await (sender ?? new PostmanSender(null, null) { Folder = folder }).SendAsync(item, new PostmanRequest { Method = "POST", Url = server.Url + "/x", Body = body });
            return result.Exchange.Error is { } error ? error : JsonNode.Parse(result.Exchange.RawBody)!;
        }
        var form = await Send(new PostmanBody(PostmanBody.UrlEncoded) { Pairs = [new("a b", "1&2"), new("off", "x") { Enabled = false }] });
        Assert.Equal(("application/x-www-form-urlencoded", "a+b=1%262"), (form["type"]!.GetValue<string>().Split(';')[0], form["body"]!.GetValue<string>()));
        var graph = await Send(new PostmanBody(PostmanBody.GraphQl) { Query = "{ a }", Variables = """{"n": 1}""" });
        Assert.Equal("""{"query":"{ a }","variables":{"n":1}}""", graph["body"]!.GetValue<string>());
        var multipart = await Send(new PostmanBody(PostmanBody.FormData) { Pairs = [new("title", "T"), new("file", "note.txt") { IsFile = true }] });
        Assert.StartsWith("multipart/form-data; boundary=", multipart["type"]!.GetValue<string>());
        Assert.Contains("the note", multipart["body"]!.GetValue<string>());
        Assert.Contains("name=\"title\"", multipart["body"]!.GetValue<string>());
        Assert.Equal("the note", (await Send(new PostmanBody(PostmanBody.FileMode) { File = "note.txt" }))["body"]!.GetValue<string>());
        Assert.Contains("There is no", (await Send(new PostmanBody(PostmanBody.FileMode) { File = "none.txt" })).GetValue<string>());
        Assert.Equal("not that one", (await Send(new PostmanBody(PostmanBody.FileMode) { File = "note.txt" }, new PostmanSender(null, null, mayRead: _ => "not that one") { Folder = folder })).GetValue<string>());
    }

    [AvaloniaFact]
    public async Task A_run_goes_in_order_or_as_its_scripts_say_and_stops_when_asked()
    {
        using var server = new EchoServer();
        var folder = Folder();
        static string Request(string name, string path, string tests) => new JsonObject
        {
            ["name"] = name,
            ["event"] = new JsonArray(new JsonObject { ["listen"] = "test", ["script"] = new JsonObject { ["exec"] = new JsonArray(tests) } }),
            ["request"] = new JsonObject { ["method"] = "GET", ["url"] = "{{baseUrl}}/" + path },
        }.ToJsonString();
        var collection = PostmanCollection.Load(Write(folder, "r.postman_collection.json", $$"""
            {"info":{"name":"R"},"item":[
              {{Request("One", "1", "pm.collectionVariables.set('n', (pm.collectionVariables.get('n') || 0) + 1);")}},
              {{Request("Two", "2", "pm.test('two', () => pm.expect(pm.info.iteration).to.equal(0)); if (pm.collectionVariables.get('n') < 2) pm.execution.setNextRequest('One');")}},
              {{Request("Three", "missing", "pm.test('three', () => pm.response.to.have.status(200));")}},
              {{Request("Four", "4", "postman.setNextRequest(null);")}},
              {{Request("Five", "5", "")}}]}
            """));
        var sender = new PostmanSender(PostmanEnvironment.Load(Local(folder, server.Url), Session()), null);
        var seen = new List<string>();
        var results = await sender.RunAsync([.. collection.Requests], new PostmanRunOptions(), r => seen.Add(r.Path));
        Assert.Equal(["One", "Two", "One", "Two", "Three", "Four"], seen);
        Assert.Equal([true, true, true, true, false, true], results.Select(r => r.Succeeded));
        Assert.StartsWith("6 sent, 1 failed · 2/3 tests passed · ", PostmanRunDocument.Summarize(results));
        Assert.Equal(["One", "Two", "Three"], (await sender.RunAsync([.. collection.Requests], new PostmanRunOptions { StopOnFailure = true })).Select(r => r.Path));
        Assert.Equal(6, (await sender.RunAsync([collection.Find("Five")!, collection.Find("Five")!], new PostmanRunOptions { Iterations = 3 })).Count);
    }

    // ---- Claude's tools ---------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Claudes_tools_send_where_they_were_to_and_an_environment_is_names_to_them()
    {
        using var server = new EchoServer();
        var folder = Folder();
        Shop(folder);
        var file = Local(folder, server.Url);
        var vm = NewViewModel();
        await vm.OpenSolutionAsync(folder);
        var module = vm.Module<PostmanModule>()!;
        module.Store = Sealed(out _);
        var tools = KarvonTools.Create(module.ClaudeTools());
        static JsonElement Input(object value) => JsonSerializer.SerializeToElement(value);
        async Task<ClaudeToolOutput> Call(string name, object input)
        {
            var json = Input(input);
            tools.Plan(ClaudeToolServer.Prefix + name, json);
            return await tools.Tools.Single(t => t.Name == name).CallAsync(json, default);
        }
        ClaudeToolPlan Plan(string name, object input) => tools.Plan(ClaudeToolServer.Prefix + name, Input(input))!;
        string? Refusal(string name, object input) => tools.Tools.Single(t => t.Name == name).Refusal(Input(input));

        var listed = (await Call("postman_collections", new { })).Text;
        Assert.Equal("""
            Shop (api/shop.postman_collection.json)
              POST Sign in — {{baseUrl}}/login (tests)
              Orders/
              GET Orders/List — {{baseUrl}}/orders/:id?day=today&limit=5 (tests)

            Environments: Local
            """, listed);
        Assert.DoesNotContain("Sign in", (await Call("postman_collections", new { filter = "orders get" })).Text);

        // No environment in use: where it goes cannot be told, so it asks. With it, it is this machine's.
        Assert.Equal((ToolAccess.Write, "Allow Claude to send Sign in to a host set by a variable or a script?"), (Plan("postman_send", new { request = "Sign in" }).Access, Plan("postman_send", new { request = "Sign in" }).Question));
        Assert.Equal(ToolAccess.Run, Plan("postman_send", new { request = "Sign in", environment = "Local" }).Access);
        Assert.Equal("Local", tools.Tools.Single(t => t.Name == "postman_send").Target(Input(new { request = "Sign in", environment = "Local" })));
        Assert.Equal(ToolAccess.Write, Plan("postman_set", new { environment = "Local", use = true }).Access);
        Assert.Contains("in use", (await Call("postman_set", new { environment = "Local", use = true })).Text);
        Assert.Equal("Local", module.Library.Active!.Name);
        // Names, no value: neither told nor set.
        var shown = (await Call("postman_environments", new { })).Text;
        Assert.Equal($"Environments: Local (in use)\n\nLocal (kept from agents: ask the user to send, or give your own values with variables):\n  baseUrl\n  apiSecret", shown);
        Assert.Contains("values are the user's", (await Call("postman_set", new { set = new { baseUrl = "http://evil.example" } })).Text);
        Assert.Contains("added token", (await Call("postman_set", new { add = new[] { "token" } })).Text);

        // Kept from agents until the user says otherwise: refused before anyone is asked, and when it runs.
        Assert.Contains("which the user keeps from agents", Refusal("postman_send", new { request = "Sign in" }));
        Assert.Contains("{{baseUrl}}", (await Call("postman_send", new { request = "Sign in" })).Text);
        // With values of its own, no environment is needed; its script's own go apart from the user's.
        Assert.Null(Refusal("postman_send", new { request = "Sign in", variables = new { baseUrl = server.Url } }));
        var sent = await Call("postman_send", new { request = "Sign in", variables = new { baseUrl = server.Url } });
        Assert.StartsWith("200 OK", sent.Text);
        Assert.Contains("Tests: 1/1 passed\n  ✓ signed in", sent.Text);
        var run = await Call("postman_run", new { variables = new { baseUrl = server.Url } });
        Assert.StartsWith("2 sent · 3/3 tests passed", run.Text);
        Assert.Contains("✓ GET Orders/List: 200 OK", run.Text);
        Assert.Equal("", module.Library.Active!.Variables.Single(v => v.Key == "token").Value);

        // Let by the user, which takes the file's values out of it first: the values go, and are not shown.
        module.Library.Active!.AgentsMaySend = true;
        Assert.False(module.Library.Active.AgentsMaySend);
        await module.Library.Active.MoveToVaultAsync();
        Assert.True(module.Library.Active.AgentsMaySend);
        Assert.DoesNotContain(server.Url, File.ReadAllText(file));
        var echoed = await Call("postman_send", new { request = "Orders/List", variables = new { token = "{{apiSecret}}" } });
        Assert.StartsWith("200 OK", echoed.Text);
        Assert.Contains("\"auth\": \"«hidden: its sign-in»\"", echoed.Text);
        Assert.DoesNotContain("s3cr3t-value-in-file", echoed.Text);
        Assert.Equal("Bearer s3cr3t-value-in-file", JsonNode.Parse(((PostmanSent)echoed.Detail!).Result.Exchange.RawBody)!["auth"]!.GetValue<string>());

        // Elsewhere asks, with where; a script or a file changed since cannot take it anywhere else.
        var outside = Plan("postman_send", new { request = "Sign in", variables = new { baseUrl = "https://shop.example" } });
        Assert.Equal((ToolAccess.Write, "Allow Claude to send Sign in to shop.example with the environment Local?"), (outside.Access, outside.Question));
        var asked = Input(new { request = "Orders/List", environment = "Local" });
        Assert.Equal(ToolAccess.Run, tools.Plan(ClaudeToolServer.Prefix + "postman_send", asked)!.Access);
        module.Library.Active.Set("baseUrl", "http://evil.example");
        var refused = await tools.Tools.Single(t => t.Name == "postman_send").CallAsync(asked, default);
        Assert.True(refused.IsError);
        Assert.Contains("is not where this call was to go", refused.Text);
        // Planned twice (asked, then changed): only where both plans said.
        Assert.Equal(ToolAccess.Write, tools.Plan(ClaudeToolServer.Prefix + "postman_send", asked)!.Access);
        module.Library.Active.Set("baseUrl", "http://other.example");
        Assert.Contains("other.example", tools.Plan(ClaudeToolServer.Prefix + "postman_send", asked)!.Question);
        Assert.Contains("is not where this call was to go", (await tools.Tools.Single(t => t.Name == "postman_send").CallAsync(asked, default)).Text);
        module.Library.Active.Set("baseUrl", server.Url);
        // Not asked for (no plan): this machine alone.
        Assert.Contains("Not sent", (await tools.Tools.Single(t => t.Name == "postman_send").CallAsync(Input(new { request = "Sign in", variables = new { baseUrl = "http://shop.example" } }), default)).Text);

        Assert.True((await Call("postman_send", new { request = "Nothing" })).IsError);
        Assert.Contains("postman_run", (await Call("postman_send", new { request = "Orders" })).Text);
        Assert.Contains(module.ClaudeShellRules(), r => r.Applies("npx newman run api/shop.postman_collection.json -e api/local.postman_environment.json", folder));
        await vm.CloseSolutionCoreAsync();
    }

    [AvaloniaFact]
    public async Task Claude_makes_and_changes_requests_in_their_file_without_its_marks_for_secrets()
    {
        var folder = Folder();
        var file = Shop(folder);
        var vm = NewViewModel();
        await vm.OpenSolutionAsync(folder);
        var module = vm.Module<PostmanModule>()!;
        var tools = KarvonTools.Create(module.ClaudeTools());
        Task<ClaudeToolOutput> Call(string name, object input) => tools.Tools.Single(t => t.Name == name).CallAsync(JsonSerializer.SerializeToElement(input), default);

        var made = await Call("postman_save", new
        {
            path = "Orders/Archive/Create", method = "post", url = "{{baseUrl}}/orders?dry=1", headers = new Dictionary<string, string> { ["X-A"] = "1" },
            body = new { mode = "raw", raw = "{\"n\": 1}", language = "json" }, auth = new { type = "bearer", token = "{{token}}" }, tests = "pm.test('made', () => pm.response.to.have.status(201));",
        });
        Assert.StartsWith("Made Orders/Archive/Create in Shop", made.Text);
        var collection = PostmanCollection.Load(file);
        var request = collection.Find("Orders/Archive/Create")!.Read();
        Assert.Equal(("POST", "{{baseUrl}}/orders?dry=1", "json", "{{token}}"), (request.Method, request.Url, request.Body.Language, request.Auth!["token"]));
        Assert.Equal("keep-me", collection.Find("Sign in")!.Node["event"]![0]!["script"]!["id"]!.GetValue<string>());
        // Only what is given changes.
        Assert.StartsWith("Saved", (await Call("postman_save", new { path = "Orders/Archive/Create", url = "{{baseUrl}}/orders/:id", path_variables = new { id = "9" }, rename = "Replace" })).Text);
        var changed = PostmanCollection.Load(file).Find("Orders/Archive/Replace")!.Read();
        Assert.Equal(("POST", "9", "{\"n\": 1}"), (changed.Method, changed.Path[0].Value, changed.Body.Raw));
        // What it was told hidden is not written over the secret.
        Assert.Contains("hidden secret's mark", (await Call("postman_save", new { path = "Orders/Archive/Replace", auth = new { type = "bearer", token = "***" } })).Text);
        Assert.True((await Call("postman_save", new { path = "Orders", url = "http://x" })).IsError);
        Assert.Equal(ToolAccess.Destructive, tools.Plan(ClaudeToolServer.Prefix + "postman_delete", JsonSerializer.SerializeToElement(new { path = "Orders/Archive" }))!.Access);
        Assert.StartsWith("Deleted", (await Call("postman_delete", new { path = "Orders/Archive" })).Text);
        Assert.Null(PostmanCollection.Load(file).Find("Orders/Archive"));

        // A collection and an environment of its own: in the folder, as Postman's files.
        Assert.StartsWith("Made Ping", (await Call("postman_save", new { collection = "Pings", create = true, path = "Ping", url = "http://localhost:1/ping" })).Text);
        Assert.True(File.Exists(Path.Combine(folder, "postman", "Pings.postman_collection.json")));
        Assert.Contains("added a", (await Call("postman_set", new { environment = "Mine", create = true, add = new[] { "a" } })).Text);
        Assert.True(File.Exists(Path.Combine(folder, "postman", "Mine.postman_environment.json")));
        // With two, which one is to be said.
        Assert.Contains("Say which collection", (await Call("postman_send", new { request = "Ping" })).Text);

        // A secret written in a request is hidden when it is read.
        await Call("postman_save", new { collection = "Pings", path = "Ping", headers = new Dictionary<string, string> { ["Authorization"] = "Basic dXNlcjpwYXNzd29yZA==", ["X-Api-Key"] = "k-123456789", ["X-B"] = "plain" } });
        var read = (await Call("postman_request", new { collection = "Pings", request = "Ping" })).Text;
        Assert.DoesNotContain("dXNlcjpwYXNzd29yZA", read);
        Assert.DoesNotContain("k-123456789", read);
        Assert.Contains("X-B: plain", read);
        // A file outside the folder, or one that holds secrets, does not go.
        File.WriteAllText(Path.Combine(folder, ".env"), "KEY=1");
        var outside = Write(Folder(), "out.txt", "out");
        foreach (var (path, why) in new[] { (".env", "holds secrets"), (outside, "not a file of the open folder") })
        {
            await Call("postman_save", new { collection = "Pings", path = "Ping", method = "POST", body = new { mode = "file", file = path } });
            Assert.Contains(why, (await Call("postman_send", new { collection = "Pings", request = "Ping" })).Text);
        }
        await vm.CloseSolutionCoreAsync();
    }

    // ---- The windows ------------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task The_window_lists_sends_edits_and_runs()
    {
        using var server = new EchoServer();
        var folder = Folder();
        var file = Shop(folder);
        Local(folder, server.Url);
        var vm = NewViewModel();
        var module = vm.Module<PostmanModule>()!;
        module.Store = Sealed(out var vault);
        var window = new MainWindow { DataContext = vm, Width = 1500, Height = 1000 };
        window.Show();
        await vm.OpenSolutionAsync(folder);
        vm.ShowToolWindow(PostmanModule.ToolWindowId);
        var explorer = module.EnsureExplorer();
        await WaitUntil(() => explorer.Roots.Count == 2, TimeSpan.FromSeconds(10));
        Assert.Equal(["Shop", "Environments"], explorer.Roots.Select(r => r.Title));
        Assert.Equal(["Sign in", "Orders"], explorer.Roots[0].Children.Select(c => c.Title));
        Assert.Equal(["No environment", "Local"], explorer.Environments.Select(e => e.Name));
        explorer.Environment = explorer.Environments[1];
        Assert.Equal("Local", module.Library.Active!.Name);
        // Its file came with values: moved to the vault, as its document's button does.
        var environment = module.Open(module.Library.Active!);
        Assert.True(environment.HasFileValues);
        // The file's values are shown (hidden until the eye), not only their names; changed, they are Karvon's.
        Assert.Equal([(server.Url, false), ("s3cr3t-value-in-file", false)], environment.Rows.Take(2).Select(r => (r.Current, r.Variable.Current is not null)));
        Assert.False(environment.IsDirty);
        Dispatcher.UIThread.RunJobs();
        Snapshot(window, "postman-environment-file");
        await environment.MoveToVaultCommand.ExecuteAsync(null);
        Assert.False(environment.HasFileValues);
        Assert.DoesNotContain(server.Url, File.ReadAllText(environment.File));
        Assert.Contains(server.Url, environment.HostsText.Replace("127.0.0.1", "http://127.0.0.1", StringComparison.Ordinal));
        explorer.Filter = "orders get";
        Assert.Equal(["List"], explorer.Roots.Single().Children.Single().Children.Select(c => c.Title));
        explorer.Filter = "";

        // A request: its parts, the URL and its query's rows in step.
        explorer.Open(explorer.Roots[0].Children[0]);
        var signIn = Assert.IsType<PostmanRequestDocument>(vm.ActiveDocument);
        await signIn.SendCommand.ExecuteAsync(null);
        Assert.Equal(("200 OK", "Tests (1/1)"), (signIn.StatusText, signIn.TestsTitle));
        var list = module.Open(module.Library.Collections[0].Find("Orders/List")!)!;
        Assert.Equal("GET List", list.Title);
        Assert.Equal("Local", list.EnvironmentText);
        Assert.Equal(["day", "limit", "all", ""], list.Params.Rows.Select(r => r.Key));
        Assert.False(list.IsDirty);
        list.Url = "{{baseUrl}}/orders/:id/lines/:line?day=tomorrow&x=1";
        Assert.Equal([("day", "tomorrow", true), ("x", "1", true), ("all", "1", false)], list.Params.Pairs.Select(p => (p.Key, p.Value, p.Enabled)));
        Assert.Equal([("id", "7"), ("line", "")], list.PathVariables.Pairs.Concat(list.PathVariables.Rows.Skip(1).Where(r => r.Value.Length == 0).Select(r => r.Pair)).DistinctBy(p => p.Key).Select(p => (p.Key, p.Value)));
        list.Params.Rows[2].Enabled = true;
        Assert.Equal("{{baseUrl}}/orders/:id/lines/:line?day=tomorrow&x=1&all=1", list.Url);
        list.PathVariables.Rows[1].Value = "3";
        Assert.True(list.IsDirty);
        Assert.Equal("GET List*", list.Title);
        // Sent as it is here, saved or not; Ctrl+S puts it in its file.
        await list.SendCommand.ExecuteAsync(null);
        Assert.EndsWith("/orders/7/lines/3?day=tomorrow&x=1&all=1", list.Result!.Exchange.Url);
        Assert.Equal("tests", list.ResponseTab = "tests");
        vm.SaveCommand.Execute(null);
        Assert.False(list.IsDirty);
        Assert.Equal("{{baseUrl}}/orders/:id/lines/:line?day=tomorrow&x=1&all=1", PostmanCollection.Load(file).Find("Orders/List")!.Read().Url);
        Dispatcher.UIThread.RunJobs();
        Snapshot(window, "postman-request");
        Assert.Contains(window.GetVisualDescendants().OfType<TextBox>(), t => t is { Name: "UrlBox", IsEffectivelyVisible: true } && t.Text == list.Url);

        // Changed on disk (a checkout): an unchanged document follows, a changed one keeps its own.
        signIn.Method = "PUT";
        var text = File.ReadAllText(file).Replace("tomorrow", "yesterday", StringComparison.Ordinal);
        File.WriteAllText(file, text);
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(5));
        await module.Library.RefreshAsync();
        Assert.Contains("day=yesterday", list.Url);
        Assert.Equal("PUT", signIn.Method);
        Assert.True(signIn.Save());
        var saved = PostmanCollection.Load(file);
        Assert.Equal("PUT", saved.Find("Sign in")!.Read().Method);
        Assert.Contains("day=yesterday", saved.Find("Orders/List")!.Read().Url);

        // A run: a folder's, with its tests.
        var run = module.ShowRun(module.Library.Collections[0], null);
        await run.RunCommand.ExecuteAsync(null);
        Assert.StartsWith("2 sent", run.Summary);
        run.Selected = run.Results[1];
        Assert.Contains("/orders/7/lines/3", run.SelectedText);
        Dispatcher.UIThread.RunJobs();
        Snapshot(window, "postman-run");

        // The environment: the token its script kept is its current value, not in the file.
        Assert.Same(environment, module.Open(module.Library.Environments[0]));
        Assert.Equal(["baseUrl", "apiSecret", "token", ""], environment.Rows.Select(r => r.Key));
        Assert.Equal("abc123", environment.Rows[2].Current);
        // Every value is hidden until its eye is clicked.
        Assert.All(environment.Rows, r => Assert.Equal('•', r.Mask));
        environment.Rows[2].ShowCommand.Execute(null);
        Assert.Equal('\0', environment.Rows[2].Mask);
        environment.Rows[2].ShowCommand.Execute(null);
        Assert.False(environment.IsDirty);
        environment.Rows[1].Current = "mine-12345678";
        Assert.True(environment.IsDirty);
        vm.SaveCommand.Execute(null);
        Assert.False(environment.IsDirty);
        await module.Store.Written;
        Assert.Contains("mine-12345678", vault.GetSealed("postman/environment/env-1"));
        Assert.DoesNotContain("mine-12345678", File.ReadAllText(module.Library.Environments[0].File));
        // Agents: off until the user turns it on; what it warns of.
        Assert.False(environment.AgentsMaySend);
        Assert.False(environment.WarnsOfThisMachine);
        environment.AgentsMaySend = true;
        environment.AddedHosts = "https://id.example.com/x, api.example.com:8443";
        Assert.Equal((true, "id.example.com, api.example.com:8443"), (environment.WarnsOfThisMachine, environment.AddedHosts));
        Assert.True(module.Library.Environments[0].Reaches(new Uri("https://id.example.com/token")));
        Assert.False(module.Library.Environments[0].Reaches(new Uri("https://id.example.com.evil.example/token")));
        Dispatcher.UIThread.RunJobs();
        Snapshot(window, "postman-environment");

        // A run with a data file; the cookies; a sign-in's rows.
        var data = Write(folder, "rows.csv", "day\nmon\ntue\n");
        run.UseData(data);
        Assert.Equal(("rows.csv (2 rows)", 2), (run.DataText, run.Iterations));
        await run.RunCommand.ExecuteAsync(null);
        Assert.StartsWith("4 sent", run.Summary);
        Assert.Equal([1, 1, 2, 2], run.Results.Select(r => r.Row));
        run.UseData(Write(folder, "bad.json", "{}"));
        Assert.Contains("not an array", run.DataText);
        module.Cookies.Set(new PostmanCookie("sid", "cookie-value", "127.0.0.1", "/"));
        Assert.Contains("sid=cookie-value", list.CookiesText);
        module.ShowCookies();
        var cookies = Assert.IsType<PostmanCookiesDocument>(vm.ActiveDocument);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(["sid"], cookies.Cookies.Select(c => c.Name));
        Snapshot(window, "postman-cookies");
        cookies.RemoveCommand.Execute(cookies.Cookies[0]);
        Dispatcher.UIThread.RunJobs();
        Assert.True(cookies.IsEmpty);
        module.Open(module.Library.Collections[0].Find("Orders/List")!);
        list.ShowTabCommand.Execute("auth");
        list.AuthIndex = list.AuthNames.IndexOf("OAuth 2.0");
        Assert.True(list is { IsOAuth: true, CanGetToken: false, TokenText: "No access token yet." });
        list.AuthFields.Shown.Single(f => f.Key == "accessTokenUrl").Text = server.Url + "/token";
        list.AuthFields.Shown.Single(f => f.Key == "grant_type").Index = 4;
        list.AuthFields.Shown.Single(f => f.Key == "clientId").Text = "app";
        Assert.True(list.CanGetToken);
        await list.GetTokenCommand.ExecuteAsync(null);
        Assert.Contains("gave no access token", list.TokenProblem);
        Dispatcher.UIThread.RunJobs();
        Snapshot(window, "postman-oauth");
        Assert.True(list.Save());
        Assert.Equal("client_credentials", PostmanCollection.Load(file).Find("Orders/List")!.Read().Auth!["grant_type"]);
        list.AuthIndex = list.AuthNames.IndexOf("AWS Signature");
        Assert.Equal(["accessKey", "secretKey", "region", "service", "sessionToken", "addAuthDataToQuery"], list.AuthFields.Shown.Select(f => f.Key));
        Assert.False(list.IsOAuth);
        list.AuthIndex = 0;
        Assert.True(list.Save());

        // Made, renamed, copied, removed, in the file.
        var naming = explorer.NewRequestCommand.ExecuteAsync(explorer.Roots[0].Children[1]);
        var dialog = Assert.IsType<Karvon.Ui.InputDialog>(explorer.Dialog);
        dialog.Text = "Create";
        dialog.Confirm();
        await naming;
        Assert.Equal("GET Create", vm.ActiveDocument!.Title);
        explorer.DuplicateCommand.Execute(explorer.Roots[0].Children[1].Children[1]);
        Assert.Equal(["List", "Create", "Create Copy"], explorer.Roots[0].Children[1].Children.Select(c => c.Title));
        var deleting = explorer.DeleteCommand.ExecuteAsync(explorer.Roots[0].Children[1].Children[2]);
        Assert.IsType<Karvon.Ui.ConfirmDialog>(explorer.Dialog).Confirm();
        await deleting;
        Assert.Equal(["Sign in", "Orders/List", "Orders/Create"], PostmanCollection.Load(file).Requests.Select(r => r.Path));
        Assert.DoesNotContain(vm.Documents, d => d.Title.Contains("Copy", StringComparison.Ordinal));
        await vm.CloseSolutionCoreAsync();
        Assert.Empty(vm.Documents.OfType<PostmanDocument>());
        window.Close();
    }

    [AvaloniaFact]
    public async Task Files_opened_from_elsewhere_are_listed_until_they_are_removed_from_the_list()
    {
        var elsewhere = Folder();
        var file = Shop(elsewhere, "other");
        var list = Path.Combine(Folder(), "postman.json");
        var library = new PostmanLibrary(() => null, list, Session());
        await library.RefreshAsync();
        Assert.Empty(library.Collections);
        Assert.Null(await library.OpenAsync([file]));
        Assert.Equal(["Shop"], library.Collections.Select(c => c.Name));
        Assert.True(library.IsOpened(file));
        Assert.Contains("not a Postman collection", await library.OpenAsync([Write(elsewhere, "x.json", "{}")]));
        // A file that breaks is said, and comes back once it is right.
        File.WriteAllText(file, "{");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(5));
        await library.RefreshAsync();
        Assert.Empty(library.Collections);
        Assert.Contains("not JSON", Assert.Single(library.Problems).Message);
        Shop(elsewhere, "other");
        await library.RefreshAsync();
        Assert.Single(library.Collections);
        await library.ForgetAsync(file);
        Assert.Empty(library.Collections);
        Assert.True(File.Exists(file));
        // Without a folder open, a new one is Karvon's own.
        var made = await library.CreateCollectionAsync("A/b: c");
        Assert.Equal(Path.Combine(Path.GetDirectoryName(list)!, "postman", "A-b- c" + PostmanCollection.Extension), made.File);
    }
}
