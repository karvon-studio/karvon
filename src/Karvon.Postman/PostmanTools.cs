using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using Karvon.Claude;
using Karvon.Claude.Tools;
using Karvon.Http;
using Karvon.Postman.ViewModels;
using static Karvon.Claude.Tools.ToolInput;

namespace Karvon.Postman;

/// <summary>A request Claude sent and its result, for the window to show.</summary>
sealed record PostmanSent(PostmanItem Item, PostmanResult Result);

/// <summary>A run Claude made, for the window to show.</summary>
sealed record PostmanRan(PostmanCollection Collection, PostmanItem? Folder, IReadOnlyList<PostmanResult> Results);

/// <summary>
/// Claude Code's Postman tools: the collections and environments in files Karvon shows, their requests read, sent with their
/// scripts and tests, run and changed. A secret's value is never told: it goes into the request, not to Claude. To this
/// machine requests go at once, anywhere else the user approves; a script cannot take one elsewhere.
/// </summary>
static class PostmanClaudeTools
{
    public static IEnumerable<IClaudeTool> Create(PostmanModule module) =>
    [
        new CollectionsTool(module), new RequestTool(module), new SendTool(module), new RunTool(module), new SaveTool(module), new DeleteTool(module),
        new EnvironmentsTool(module), new SetTool(module),
    ];

    const string Collection = "\"collection\":{\"type\":\"string\",\"description\":\"The collection's name (postman_collections lists them; may be left out when there is one).\"}";
    const string Environment = "\"environment\":{\"type\":\"string\",\"description\":\"The environment's name (default: the one chosen in Karvon); \\\"none\\\" for none.\"}";
    const string Variables = "\"variables\":{\"type\":\"object\",\"additionalProperties\":{\"type\":\"string\"},\"description\":\"Values for this call, over the environment's.\"}";

    // ---- What they share --------------------------------------------------------------------------------------

    abstract class Tool(PostmanModule module) : IClaudeTool
    {
        protected PostmanModule Module => module;

        protected PostmanLibrary Library => module.Library;

        public abstract string Name { get; }

        public abstract string Description { get; }

        public abstract JsonObject InputSchema { get; }

        public abstract ClaudeToolPlan Plan(JsonElement input);

        public virtual string? Target(JsonElement input) => null;

        public async Task<ClaudeToolOutput> CallAsync(JsonElement input, CancellationToken cancellationToken) => await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            try
            {
                await Library.RefreshAsync();
                return await RunAsync(input, cancellationToken);
            }
            catch (Exception ex) when (ex is PostmanException or IOException or UnauthorizedAccessException)
            {
                return ClaudeToolOutput.Error(Redaction.Text(ex.Message));
            }
        });

        protected abstract Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken);

        protected PostmanCollection CollectionOf(JsonElement input) => Str(input, "collection") is { } name
            ? Library.FindCollection(name) ?? throw new PostmanException($"There is no collection {name}: {Names(Library.Collections.Select(c => c.Name))}.")
            : Library.Collections is [var only] ? only : throw new PostmanException($"Say which collection: {Names(Library.Collections.Select(c => c.Name))}.");

        protected static PostmanItem ItemOf(PostmanCollection collection, string? path) =>
            (path is null ? null : collection.Find(path)) ?? throw new PostmanException($"{collection.Name} has no {path}: postman_collections lists what it has.");

        protected PostmanEnvironment? EnvironmentOf(JsonElement input) => Str(input, "environment") switch
        {
            null => Library.Active,
            "none" => null,
            var name => Library.FindEnvironment(name) ?? throw new PostmanException($"There is no environment {name}: {Names(Library.Environments.Select(e => e.Name))}."),
        };

        static string Names(IEnumerable<string> names) => names.ToList() is { Count: > 0 } list ? string.Join(", ", list) : "there is none (postman_save makes a collection)";

        protected static Dictionary<string, string> VariablesOf(JsonElement input) =>
            input.ValueKind == JsonValueKind.Object && input.TryGetProperty("variables", out var values) && values.ValueKind == JsonValueKind.Object
                ? values.EnumerateObject().ToDictionary(v => v.Name, v => v.Value.ValueKind == JsonValueKind.String ? v.Value.GetString()! : v.Value.GetRawText(), StringComparer.Ordinal) : [];

        /// <summary>A text Claude gives back as it was told it: a secret's mark, which is not the secret.</summary>
        protected static void NoMasks(string? text, string what)
        {
            if (text is not null && (text.Contains(Redaction.Mask, StringComparison.Ordinal) || text.Contains("«secret:", StringComparison.Ordinal)))
                throw new PostmanException($"{what} has a hidden secret's mark in it: leave it out to keep what is there, or use a variable ({{{{name}}}}).");
        }
    }

    /// <summary>A tool that sends requests: where they go decides whether it asks, and they go nowhere else.</summary>
    abstract class Sending(PostmanModule module) : Tool(module)
    {
        protected abstract string Title { get; }

        protected abstract IReadOnlyList<PostmanItem> Items(JsonElement input);

        protected abstract string Subject(JsonElement input);

        /// <summary>Where they go, as it can be told before their scripts run: the hosts outside this machine, and whether some cannot be told.</summary>
        sealed record Reach(IReadOnlyList<string> Outside, bool Unknown, DateTime At);

        /// <summary>How long what was planned for a call holds (the user answers meanwhile).</summary>
        static readonly TimeSpan PlanHolds = TimeSpan.FromMinutes(10);

        /// <summary>
        /// What each call was planned to reach, by its input: it may go there and nowhere else, however its collection or its
        /// environment changed since. Planned again before it ran, it may go only where each of the plans said.
        /// </summary>
        readonly Dictionary<string, Reach> _planned = new(StringComparer.Ordinal);

        Reach Find(JsonElement input)
        {
            var (environment, variables) = (EnvironmentOf(input), VariablesOf(input));
            var hosts = Items(input).Select(i => PostmanSender.HostOf(i, environment, Library.Globals, variables)).ToList();
            return new([.. hosts.OfType<Uri>().Where(u => !HttpRunner.IsLocal(u.Host)).Select(u => u.Authority).Distinct(StringComparer.OrdinalIgnoreCase)], hosts.Contains(null), DateTime.UtcNow);
        }

        void Hold(string key, Reach reach) => _planned[key] = _planned.TryGetValue(key, out var before) && reach.At - before.At < PlanHolds
            ? new([.. before.Outside.Intersect(reach.Outside, StringComparer.OrdinalIgnoreCase)], before.Unknown && reach.Unknown, before.At) : reach;

        public override ClaudeToolPlan Plan(JsonElement input) => OnWindow(() =>
        {
            var key = input.GetRawText();
            try
            {
                Library.ReadNow();
                Reach reach;
                try
                {
                    reach = Find(input);
                }
                catch (PostmanException)
                {
                    // A file made since: found now, or not at all.
                    Library.ReadNow(all: true);
                    reach = Find(input);
                }
                Hold(key, reach);
                var environment = EnvironmentOf(input)?.Name;
                if (reach.Outside.Count == 0 && !reach.Unknown)
                    return new ClaudeToolPlan(ToolAccess.Run, Title, Subject(input), "");
                var to = string.Join(", ", reach.Outside.Append(reach.Unknown ? "a host set by a variable or a script" : "").Where(h => h.Length > 0));
                return new(ToolAccess.Write, Title, Subject(input), $"Allow Claude to send {Subject(input)} to {to}{(environment is null ? "" : $" with the environment {environment}")}?")
                {
                    Warning = "It goes to a server outside this machine, with the environment's values (a token among them) in it.",
                };
            }
            // What cannot be found is said when it is called; found by then, it may go to this machine alone.
            catch (Exception ex) when (ex is PostmanException or IOException or UnauthorizedAccessException)
            {
                Hold(key, new Reach([], false, DateTime.UtcNow));
                return new(ToolAccess.Run, Title, Subject(input), "");
            }
        });

        /// <summary>The environment, so that a rule can be about one (postman_send@prod).</summary>
        public override string? Target(JsonElement input) => OnWindow(() =>
        {
            try
            {
                Library.ReadNow();
                return EnvironmentOf(input)?.Name;
            }
            catch (Exception ex) when (ex is PostmanException or IOException or UnauthorizedAccessException)
            {
                return null;
            }
        });

        /// <summary>Plans are asked for on the server's threads; the collections are the window's.</summary>
        static T OnWindow<T>(Func<T> body) => Dispatcher.UIThread.CheckAccess() ? body() : Dispatcher.UIThread.Invoke(body);

        /// <summary>The sender of a call of Claude's: only where the call was planned to go, and no file but the workspace's that holds no secret.</summary>
        protected PostmanSender Sender(JsonElement input)
        {
            var reach = _planned.Remove(input.GetRawText(), out var planned) ? planned : new Reach([], false, DateTime.UtcNow);
            var workspace = ((IPostmanHost)Module).Workspace;
            return new PostmanSender(EnvironmentOf(input), Library.Globals,
                uri => HttpRunner.IsLocal(uri.Host) || reach.Unknown || reach.Outside.Contains(uri.Authority, StringComparer.OrdinalIgnoreCase),
                file => Refused(file, workspace))
            {
                Folder = workspace,
                Local = VariablesOf(input),
            };
        }

        static string? Refused(string file, string? workspace)
        {
            var real = File.Exists(file) && File.ResolveLinkTarget(file, returnFinalTarget: true) is { } target ? target.FullName : file;
            if (workspace is null || !Path.GetFullPath(real).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace)) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                return $"{file} is not a file of the open folder: Claude does not send it.";
            return SecretFiles.IsSecret(real) || SecretFiles.IsSecret(file) ? $"{Path.GetFileName(file)} holds secrets: Claude does not send it." : null;
        }
    }

    /// <summary>A result as Claude reads it.</summary>
    static string Text(PostmanResult result)
    {
        var text = new StringBuilder(HttpClaudeTools.Text(result.Exchange));
        text.Append(Tests(result, all: true));
        if (result.Console.Count > 0)
            text.Append("\n\nConsole:\n").Append(string.Join('\n', result.Console.Take(60)));
        if (result.Missing.Count > 0 && result.Exchange.Error is null)
            text.Append("\n\nWithout a value, sent as written: ").Append(string.Join(", ", result.Missing.Select(m => $"{{{{{m}}}}}")));
        return Redaction.Text(text.ToString());
    }

    static string Tests(PostmanResult result, bool all)
    {
        var text = new StringBuilder();
        if (result.ScriptError is { } error)
            text.Append("\n\nA script stopped: ").Append(error);
        if (result.Tests.Count > 0)
        {
            text.Append(CultureInfo.InvariantCulture, $"\n\nTests: {result.Passed}/{result.Tests.Count(t => !t.IsSkipped)} passed");
            foreach (var test in result.Tests.Where(t => all || t.Failed))
                text.Append("\n  ").Append(test.IsSkipped ? "- " : test.Passed ? "✓ " : "✗ ").Append(test.Name).Append(test.Error is { } why ? ": " + why : "");
        }
        return text.ToString();
    }

    /// <summary>A value as Claude may see it: a secret's hidden, a variable's name as it is.</summary>
    static string Shown(string name, string value, bool secret = false) =>
        value.Length == 0 || (value.StartsWith("{{", StringComparison.Ordinal) && value.EndsWith("}}", StringComparison.Ordinal)) ? value
        : secret || name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) || name.Equals("Cookie", StringComparison.OrdinalIgnoreCase) && !value.Contains("{{", StringComparison.Ordinal)
            ? value.Contains("{{", StringComparison.Ordinal) ? Redaction.Text(value) : Redaction.Mask
            : PostmanCollection.IsSecretName(name) ? Redaction.Mask : Redaction.Text(value);

    // ---- Reading ----------------------------------------------------------------------------------------------

    sealed class CollectionsTool(PostmanModule module) : Tool(module)
    {
        const int MaxLines = 400;

        public override string Name => "postman_collections";

        public override string Description =>
            "Lists the Postman collections Karvon shows (the open folder's *.postman_collection.json and those the user opened), each with its folders "
            + "and requests (method, path, URL), and the environments, with the one in use.";

        public override JsonObject InputSchema => Schema("""
            {"type":"object","properties":{
              "collection":{"type":"string","description":"Only this collection."},
              "filter":{"type":"string","description":"Only requests with these words in their method, path or URL."}}}
            """);

        public override ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Read, "Postman Collections", Str(input, "collection") ?? Str(input, "filter"), "");

        protected override Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
        {
            var words = (Str(input, "filter") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var text = new StringBuilder();
            var lines = 0;
            foreach (var collection in Str(input, "collection") is not null ? [CollectionOf(input)] : Library.Collections)
            {
                text.Append(CultureInfo.InvariantCulture, $"{collection.Name} ({Relative(collection.File)})\n");
                foreach (var item in All(collection.Items))
                {
                    var request = item.IsFolder ? null : item.Read();
                    var line = item.IsFolder ? $"  {item.Path}/" : $"  {item.Method} {collection.PathOf(item)} — {request!.Url}{(request.Tests.Length > 0 ? " (tests)" : "")}";
                    if ((words.Length > 0 && (item.IsFolder || !words.All(w => line.Contains(w, StringComparison.OrdinalIgnoreCase)))) || ++lines > MaxLines)
                        continue;
                    text.Append(Redaction.Text(line)).Append('\n');
                }
            }
            if (lines > MaxLines)
                text.Append(CultureInfo.InvariantCulture, $"… {lines - MaxLines} more: use filter.\n");
            if (Library.Collections.Count == 0)
                text.Append("No collections: postman_save makes one.\n");
            text.Append("\nEnvironments: ").Append(Library.Environments.Count == 0 ? "none" : string.Join(", ", Library.Environments.Select(e => e.Name + (e == Library.Active ? " (in use)" : ""))));
            foreach (var (file, message) in Library.Problems)
                text.Append(CultureInfo.InvariantCulture, $"\nNot read: {Relative(file)}: {message}");
            return Task.FromResult(new ClaudeToolOutput(text.ToString().Trim()));
        }

        string Relative(string file) => ((IPostmanHost)Module).Workspace is { } root ? Path.GetRelativePath(root, file).Replace('\\', '/') : file;

        static IEnumerable<PostmanItem> All(IEnumerable<PostmanItem> items) => items.SelectMany(i => All(i.Items).Prepend(i));
    }

    sealed class RequestTool(PostmanModule module) : Tool(module)
    {
        public override string Name => "postman_request";

        public override string Description =>
            "A request of a Postman collection as it is written: method, URL, parameters, sign-in, headers, body, its pre-request script and tests (and its "
            + "folders' and collection's). Variables are as {{names}}; a secret's value is hidden.";

        public override JsonObject InputSchema => Schema("{\"type\":\"object\",\"properties\":{" + Collection
            + ",\"request\":{\"type\":\"string\",\"description\":\"Its path (Folder/Name), or its name.\"}},\"required\":[\"request\"]}");

        public override ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Read, "Postman Request", Str(input, "request"), "");

        protected override Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
        {
            var item = ItemOf(CollectionOf(input), Str(input, "request"));
            if (item.IsFolder)
                throw new PostmanException($"{item.Path} is a folder: postman_collections lists its requests.");
            var request = item.Read();
            var text = new StringBuilder($"{request.Method} {Redaction.Text(request.Url)}\n");
            Rows(text, "Params (off)", request.Query.Where(q => !q.Enabled));
            Rows(text, "Path variables", request.Path);
            Rows(text, "Headers", request.Headers);
            var auth = request.Auth ?? item.EffectiveAuth();
            text.Append("Auth: ").Append(auth is null ? "none" : auth.Type + string.Concat(auth.Values.Select(v => $", {v.Key}={Shown(v.Key, v.Value, v.Key is "token" or "password" or "value" or "accessToken" or "clientSecret" or "secretKey")}")))
                .Append(request.Auth is null && auth is not null ? " (inherited)" : "").Append('\n');
            switch (request.Body.Mode)
            {
                case PostmanBody.RawMode:
                    text.Append(CultureInfo.InvariantCulture, $"Body (raw, {request.Body.Language}):\n{Redaction.Text(request.Body.Raw)}\n");
                    break;
                case PostmanBody.UrlEncoded or PostmanBody.FormData:
                    Rows(text, $"Body ({request.Body.Mode})", request.Body.Pairs);
                    break;
                case PostmanBody.FileMode:
                    text.Append(CultureInfo.InvariantCulture, $"Body: the file {request.Body.File}\n");
                    break;
                case PostmanBody.GraphQl:
                    text.Append(CultureInfo.InvariantCulture, $"Body (graphql):\n{request.Body.Query}\nvariables: {Redaction.Text(request.Body.Variables)}\n");
                    break;
            }
            foreach (var (listen, title) in new[] { ("prerequest", "Pre-request script"), ("test", "Tests") })
                foreach (var script in item.Scripts(listen))
                    text.Append(CultureInfo.InvariantCulture, $"{title}{(script == (listen == "test" ? request.Tests : request.PreRequest) ? "" : " (inherited)")}:\n{Redaction.Text(script)}\n");
            if (request.Description.Length > 0)
                text.Append(CultureInfo.InvariantCulture, $"Description: {Redaction.Text(request.Description)}\n");
            return Task.FromResult(new ClaudeToolOutput(text.ToString().TrimEnd()) { Outside = "the collection's request" });
        }

        static void Rows(StringBuilder text, string title, IEnumerable<PostmanPair> rows)
        {
            var list = rows.ToList();
            if (list.Count == 0)
                return;
            text.Append(title).Append(":\n");
            foreach (var row in list)
                text.Append(CultureInfo.InvariantCulture, $"  {row.Key}: {(row.IsFile ? "(file) " + row.Value : Shown(row.Key, row.Value))}{(row.Enabled ? "" : " (off)")}\n");
        }
    }

    sealed class EnvironmentsTool(PostmanModule module) : Tool(module)
    {
        public override string Name => "postman_environments";

        public override string Description =>
            "The Postman environments, and one's variables with their values: the current one (this machine's) where there is one, else the file's. A secret's "
            + "value is hidden; requests are sent with it all the same.";

        public override JsonObject InputSchema => Schema("""
            {"type":"object","properties":{"environment":{"type":"string","description":"An environment's name (default: the one in use), or \"globals\"."}}}
            """);

        public override ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Read, "Postman Environment", Str(input, "environment"), "");

        protected override Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
        {
            var text = new StringBuilder("Environments: ");
            text.Append(Library.Environments.Count == 0 ? "none" : string.Join(", ", Library.Environments.Select(e => e.Name + (e == Library.Active ? " (in use)" : ""))));
            var environment = Str(input, "environment") == "globals" ? Library.Globals : EnvironmentOf(input);
            if (environment is not null)
            {
                text.Append(CultureInfo.InvariantCulture, $"\n\n{environment.Name}:\n");
                foreach (var variable in environment.Variables)
                    text.Append(CultureInfo.InvariantCulture, $"  {variable.Key} = {(variable.HoldsSecret && variable.Value.Length > 0 ? Redaction.Mask + " (secret)" : Redaction.Text(variable.Value))}")
                        .Append(variable.Current is not null ? " (current)" : "").Append(variable.Enabled ? "" : " (off)").Append('\n');
            }
            return Task.FromResult(new ClaudeToolOutput(text.ToString().TrimEnd()));
        }
    }

    // ---- Sending ----------------------------------------------------------------------------------------------

    sealed class SendTool(PostmanModule module) : Sending(module)
    {
        public override string Name => "postman_send";

        public override string Description =>
            "Sends a request of a Postman collection as Postman does (its variables, sign-in, pre-request scripts, body), runs its tests, and returns the "
            + "response, the tests' results and what its scripts printed. The environment's secrets go into the request without being shown. To this machine "
            + "it is sent at once; anywhere else the user approves it.";

        public override JsonObject InputSchema => Schema("{\"type\":\"object\",\"properties\":{" + Collection
            + ",\"request\":{\"type\":\"string\",\"description\":\"Its path (Folder/Name), or its name.\"}," + Environment + "," + Variables + "},\"required\":[\"request\"]}");

        protected override string Title => "Send Request";

        protected override string Subject(JsonElement input) => Str(input, "request") ?? "a request";

        protected override IReadOnlyList<PostmanItem> Items(JsonElement input)
        {
            var item = ItemOf(CollectionOf(input), Str(input, "request"));
            return item.IsFolder ? throw new PostmanException($"{item.Path} is a folder: postman_run runs it.") : [item];
        }

        protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
        {
            var item = Items(input)[0];
            var result = await Sender(input).SendAsync(item, ct: cancellationToken);
            Library.RaiseChanged();
            return new ClaudeToolOutput(Text(result), result.Exchange.Error is not null && !result.IsSkipped) { Detail = new PostmanSent(item, result), Outside = "the HTTP response" };
        }
    }

    sealed class RunTool(PostmanModule module) : Sending(module)
    {
        public override string Name => "postman_run";

        public override string Description =>
            "Runs a Postman collection, or one of its folders, as Postman's runner does: its requests in their order with their scripts and tests "
            + "(setNextRequest too), and returns each one's status and failed tests, and the totals. postman_send shows one's whole response.";

        public override JsonObject InputSchema => Schema("{\"type\":\"object\",\"properties\":{" + Collection
            + ",\"folder\":{\"type\":\"string\",\"description\":\"Only this folder's requests (its path).\"}," + Environment + "," + Variables
            + ",\"iterations\":{\"type\":\"integer\",\"description\":\"Default 1, at most 100.\"},\"delay_ms\":{\"type\":\"integer\",\"description\":\"Between two requests.\"}"
            + ",\"stop_on_failure\":{\"type\":\"boolean\"}}}");

        protected override string Title => "Run Collection";

        protected override string Subject(JsonElement input) => Str(input, "folder") ?? Str(input, "collection") ?? "the collection";

        PostmanItem? Folder(JsonElement input) => Str(input, "folder") is { } path ? ItemOf(CollectionOf(input), path) : null;

        protected override IReadOnlyList<PostmanItem> Items(JsonElement input) => Folder(input) is { } folder ? [.. folder.Requests] : [.. CollectionOf(input).Requests];

        protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
        {
            var (collection, folder, items) = (CollectionOf(input), Folder(input), Items(input));
            if (items.Count == 0)
                throw new PostmanException("There is no request to run.");
            var results = await Sender(input).RunAsync(items, new PostmanRunOptions
            {
                Iterations = Int(input, "iterations", 1, 1, 100),
                Delay = TimeSpan.FromMilliseconds(Int(input, "delay_ms", 0, 0, 60_000)),
                StopOnFailure = Bool(input, "stop_on_failure"),
            }, ct: cancellationToken);
            Library.RaiseChanged();
            var text = new StringBuilder(PostmanRunDocument.Summarize(results)).Append('\n');
            foreach (var result in results)
            {
                text.Append(CultureInfo.InvariantCulture, $"\n{(result.Succeeded ? "✓" : "✗")} {result.Exchange.Method} {result.Path}: {result.Exchange.Summary}{(result.Tests.Count > 0 ? " · " + result.TestsText : "")}");
                if (!result.Succeeded)
                    text.Append(Tests(result, all: false).Replace("\n\n", "\n  ", StringComparison.Ordinal));
            }
            return new ClaudeToolOutput(Redaction.Text(text.ToString())) { Detail = new PostmanRan(collection, folder, results), Outside = "the requests' results" };
        }
    }

    // ---- Changing ---------------------------------------------------------------------------------------------

    sealed class SaveTool(PostmanModule module) : Tool(module)
    {
        public override string Name => "postman_save";

        public override string Description =>
            "Makes or changes a request, a folder or a collection, in its Postman file (v2.1, which Postman imports). Only what is given changes. Its folders "
            + "(Folder/Name) are made when they are not there; with create, the collection too. Put secrets in variables ({{token}}), set in the environment "
            + "(postman_set), not in the request.";

        public override JsonObject InputSchema => Schema("""
            {"type":"object","properties":{
              "collection":{"type":"string"},
              "create":{"type":"boolean","description":"Make the collection when there is none of that name."},
              "path":{"type":"string","description":"The request's path: Folder/Name."},
              "folder":{"type":"boolean","description":"path is a folder to make, not a request."},
              "rename":{"type":"string","description":"The item's new name (the collection's, without a path)."},
              "method":{"type":"string"},
              "url":{"type":"string","description":"With its query; :name in the path is a path variable."},
              "path_variables":{"type":"object","additionalProperties":{"type":"string"}},
              "headers":{"type":"object","additionalProperties":{"type":"string"},"description":"All of them: those left out go."},
              "auth":{"type":"object","description":"type: inherit, noauth, bearer (token), basic (username, password), apikey (key, value, in: header or query), oauth2 (accessToken).","properties":{"type":{"type":"string"}},"additionalProperties":{"type":"string"}},
              "body":{"type":"object","description":"mode: none, raw (raw, language: json, text, xml...), urlencoded and formdata (fields), graphql (query, variables), file (file).",
                "properties":{"mode":{"type":"string","enum":["none","raw","urlencoded","formdata","graphql","file"]},"raw":{"type":"string"},"language":{"type":"string"},
                  "fields":{"type":"object","additionalProperties":{"type":"string"}},"query":{"type":"string"},"variables":{"type":"string"},"file":{"type":"string"}}},
              "pre_request":{"type":"string","description":"The script that runs before it (pm.*)."},
              "tests":{"type":"string","description":"The script that runs on its response (pm.test, pm.expect, pm.response)."},
              "description":{"type":"string"}}}
            """);

        public override ClaudeToolPlan Plan(JsonElement input)
        {
            var what = Str(input, "path") ?? Str(input, "collection") ?? "a collection";
            return new(ToolAccess.Write, "Save Postman Request", what, $"Allow Claude to change {what}{(Str(input, "path") is not null && Str(input, "collection") is { } collection ? $" in {collection}" : "")}?")
            {
                Code = Str(input, "url") is { } url ? $"{Str(input, "method")?.ToUpperInvariant() ?? "GET"} {url}" : null,
            };
        }

        protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
        {
            NoMasks(input.GetRawText(), "What is given");
            PostmanCollection collection;
            var made = false;
            if (Bool(input, "create") && Str(input, "collection") is { } name && Library.FindCollection(name) is null)
                (collection, made) = (await Library.CreateCollectionAsync(name), true);
            else
                collection = CollectionOf(input);
            if (Str(input, "path")?.Trim('/', ' ') is not { Length: > 0 } path)
            {
                if (Str(input, "rename") is { } renamed)
                    collection.Name = renamed;
                else if (!made)
                    throw new PostmanException("Give the path of a request or a folder, or a new name.");
                Library.Save(collection);
                return new ClaudeToolOutput($"{(made ? "Made" : "Saved")} {collection.Name} in {collection.File}.");
            }
            var isFolder = Bool(input, "folder");
            var item = collection.Find(path);
            var isNew = item is null;
            if (item is null)
            {
                PostmanItem? parent = null;
                foreach (var step in path.Split('/')[..^1])
                    parent = (parent?.Items ?? collection.Items).FirstOrDefault(i => i.IsFolder && i.Name.Equals(step, StringComparison.OrdinalIgnoreCase)) ?? collection.Add(parent, step, isFolder: true);
                item = collection.Add(parent, path.Split('/')[^1], isFolder);
            }
            if (!item.IsFolder)
                Changed(item.Read(), input).Write(item.Node);
            else if (Given(input) is { } given)
                throw new PostmanException($"{item.Path} is a folder: it has no {given}.");
            if (Str(input, "rename") is { } to)
                item.Name = to;
            Library.Save(collection);
            return new ClaudeToolOutput($"{(isNew ? "Made" : "Saved")} {(item.IsFolder ? "the folder " : "")}{collection.PathOf(item)} in {collection.Name} ({collection.File}).") { Detail = item };
        }

        static string? Given(JsonElement input) => new[] { "method", "url", "headers", "body", "path_variables" }.FirstOrDefault(n => input.TryGetProperty(n, out _));

        static PostmanRequest Changed(PostmanRequest request, JsonElement input)
        {
            if (Str(input, "method") is { } method)
                request = request with { Method = method.Trim().ToUpperInvariant() };
            if (Str(input, "url") is { } url)
                request = request with
                {
                    Url = url,
                    Query = PostmanRequest.QueryOf(url),
                    Path = [.. PostmanRequest.PathNames(url).Select(n => request.Path.FirstOrDefault(p => p.Key == n) ?? new PostmanPair(n, ""))],
                };
            if (Map(input, "path_variables") is { } path)
                request = request with { Path = [.. PostmanRequest.PathNames(request.Url).Select(n => new PostmanPair(n, path.GetValueOrDefault(n) ?? request.Path.FirstOrDefault(p => p.Key == n)?.Value ?? ""))] };
            if (Map(input, "headers") is { } headers)
                request = request with { Headers = [.. headers.Select(h => new PostmanPair(h.Key, h.Value))] };
            if (Map(input, "auth") is { } auth)
            {
                var type = auth.GetValueOrDefault("type") ?? throw new PostmanException("auth needs its type.");
                request = request with { Auth = type == "inherit" ? null : new PostmanAuth(type, auth.Where(a => a.Key != "type").ToDictionary(a => a.Key, a => a.Value)) };
            }
            if (input.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.Object)
            {
                var mode = Str(body, "mode") ?? (Str(body, "raw") is not null ? PostmanBody.RawMode : throw new PostmanException("body needs its mode."));
                if (!PostmanBody.Modes.Contains(mode))
                    throw new PostmanException($"body's mode is one of {string.Join(", ", PostmanBody.Modes)}.");
                request = request with
                {
                    Body = new PostmanBody(mode)
                    {
                        Raw = Str(body, "raw") ?? (request.Body.Mode == mode ? request.Body.Raw : ""),
                        Language = Str(body, "language") ?? (request.Body.Mode == mode ? request.Body.Language : "json"),
                        Pairs = Map(body, "fields") is { } fields ? [.. fields.Select(f => new PostmanPair(f.Key, f.Value))] : request.Body.Mode == mode ? request.Body.Pairs : [],
                        File = Str(body, "file") ?? (request.Body.Mode == mode ? request.Body.File : ""),
                        Query = Str(body, "query") ?? (request.Body.Mode == mode ? request.Body.Query : ""),
                        Variables = Str(body, "variables") ?? (request.Body.Mode == mode ? request.Body.Variables : ""),
                    },
                };
            }
            // An empty text takes a script or a description away.
            if (input.TryGetProperty("pre_request", out var before) && before.ValueKind == JsonValueKind.String)
                request = request with { PreRequest = before.GetString()! };
            if (input.TryGetProperty("tests", out var tests) && tests.ValueKind == JsonValueKind.String)
                request = request with { Tests = tests.GetString()! };
            if (input.TryGetProperty("description", out var description) && description.ValueKind == JsonValueKind.String)
                request = request with { Description = description.GetString()! };
            return request;
        }

        static Dictionary<string, string>? Map(JsonElement input, string name) => input.TryGetProperty(name, out var map) && map.ValueKind == JsonValueKind.Object
            ? map.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText()) : null;
    }

    sealed class DeleteTool(PostmanModule module) : Tool(module)
    {
        public override string Name => "postman_delete";

        public override string Description => "Removes a request, or a folder with its requests, from a Postman collection's file. The user approves each one.";

        public override JsonObject InputSchema => Schema("{\"type\":\"object\",\"properties\":{" + Collection + ",\"path\":{\"type\":\"string\",\"description\":\"Folder/Name.\"}},\"required\":[\"path\"]}");

        public override ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Destructive, "Delete from Collection", Str(input, "path"), $"Allow Claude to delete {Str(input, "path")} from the collection?")
        {
            Warning = "A folder goes with its requests.",
        };

        protected override Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
        {
            var collection = CollectionOf(input);
            var item = ItemOf(collection, Str(input, "path"));
            collection.Remove(item);
            Library.Save(collection);
            return Task.FromResult(new ClaudeToolOutput($"Deleted {item.Path} from {collection.Name}."));
        }
    }

    sealed class SetTool(PostmanModule module) : Tool(module)
    {
        public override string Name => "postman_set";

        public override string Description =>
            "Sets variables of a Postman environment to current values (this machine's, in Karvon's vault; the file stays as it is), takes them away, or "
            + "chooses the environment requests are sent with. With create, makes the environment when there is none of that name.";

        public override JsonObject InputSchema => Schema("""
            {"type":"object","properties":{
              "environment":{"type":"string","description":"The environment's name (default: the one in use)."},
              "set":{"type":"object","additionalProperties":{"type":"string"}},
              "unset":{"type":"array","items":{"type":"string"},"description":"Back to the file's values."},
              "use":{"type":"boolean","description":"Requests are sent with it from now on."},
              "create":{"type":"boolean"}}}
            """);

        public override string? Target(JsonElement input) => Str(input, "environment");

        public override ClaudeToolPlan Plan(JsonElement input)
        {
            var names = input.ValueKind == JsonValueKind.Object && input.TryGetProperty("set", out var set) && set.ValueKind == JsonValueKind.Object ? set.EnumerateObject().Select(p => p.Name).ToList() : [];
            var environment = Str(input, "environment") ?? "the environment in use";
            return new(ToolAccess.Write, "Postman Environment", environment, names.Count > 0 ? $"Allow Claude to set {string.Join(", ", names)} in {environment}?" : $"Allow Claude to change {environment}?");
        }

        protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
        {
            var environment = Bool(input, "create") && Str(input, "environment") is { } name && Library.FindEnvironment(name) is null
                ? await Library.CreateEnvironmentAsync(name) : EnvironmentOf(input) ?? throw new PostmanException("Say which environment: none is in use.");
            var done = new List<string>();
            if (input.TryGetProperty("set", out var set) && set.ValueKind == JsonValueKind.Object)
                foreach (var value in set.EnumerateObject())
                {
                    var text = value.Value.ValueKind == JsonValueKind.String ? value.Value.GetString()! : value.Value.GetRawText();
                    NoMasks(text, value.Name);
                    environment.Set(value.Name, text);
                    done.Add($"set {value.Name}");
                }
            if (input.TryGetProperty("unset", out var unset) && unset.ValueKind == JsonValueKind.Array)
                foreach (var key in unset.EnumerateArray().Select(k => k.GetString()).OfType<string>())
                {
                    environment.Unset(key);
                    done.Add($"unset {key}");
                }
            if (Bool(input, "use"))
            {
                Library.ActiveId = environment.Id;
                done.Add("in use");
            }
            Library.RaiseChanged();
            return new ClaudeToolOutput($"{environment.Name}: {(done.Count > 0 ? string.Join(", ", done) : "there it is")}.");
        }
    }
}
