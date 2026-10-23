using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Karvon.Tests;

/// <summary>
/// A Jira site for the tests, in the process (an HttpMessageHandler): Cloud's API 3 or Server's API 2, the agile API, a
/// project whose types and workflow are named in Cyrillic (as some sites have them) and which refuses an issue type
/// by its name; every request kept (its method, path, body and authorization), and only the right secret let in.
/// </summary>
sealed class FakeJira(bool cloud, string secret = "the-secret", string user = "me@example.com") : HttpMessageHandler
{
    public sealed record Request(string Method, string Path, string? Body, string? Authorization);

    public List<Request> Requests { get; } = [];

    /// <summary>Issues by key: their fields (as Jira's JSON) and comments.</summary>
    public Dictionary<string, JsonObject> Issues { get; } = [];

    /// <summary>Make the site leave a new issue's assignee empty, as some workflows do.</summary>
    public bool DropsAssignee { get; set; }

    public bool Down { get; set; }

    readonly string _me = cloud ? "acc-1" : "dev";

    static readonly (string Id, string Name, string Category)[] Statuses =
        [("1", "К выполнению", "new"), ("3", "В работе", "indeterminate"), ("10", "QA", "indeterminate"), ("5", "Готово", "done")];

    /// <summary>The workflow: from each status, its transitions (id, name, to).</summary>
    static readonly Dictionary<string, (string Id, string Name, string To)[]> Workflow = new()
    {
        ["1"] = [("11", "Начать работу", "3")],
        ["3"] = [("21", "На проверку", "10"), ("31", "Вернуть", "1")],
        ["10"] = [("41", "Готово", "5"), ("51", "Вернуть в работу", "3")],
        ["5"] = [],
    };

    int _next = 1;

    public FakeJira Seed()
    {
        Add("SHOP", "Fix the cart's total", "3", "Bug", "<p>The total is <b>off</b> by one.</p>", "The total is *off* by one.");
        Add("SHOP", "Add a wish list", "1", "Задача", "<p>A list of what one wants.</p>", "A list of what one wants.");
        Issues["SHOP-1"]["fields"]!["comment"] = new JsonObject
        {
            ["comments"] = new JsonArray(new JsonObject { ["author"] = User(), ["created"] = "2026-10-20T10:00:00.000+0500", ["body"] = Body("Seen on staging.") }),
        };
        return this;
    }

    JsonObject User() => cloud
        ? new JsonObject { ["accountId"] = _me, ["displayName"] = "Dev Eloper", ["emailAddress"] = user }
        : new JsonObject { ["name"] = _me, ["key"] = _me, ["displayName"] = "Dev Eloper", ["emailAddress"] = user };

    JsonNode Body(string text) => cloud
        ? JsonNode.Parse(JsonSerializer.Serialize(Karvon.Jira.JiraText.ToAdf(text)))!
        : text;

    void Add(string project, string summary, string status, string type, string html, string text, JsonNode? assignee = null, bool unassigned = false)
    {
        var key = $"{project}-{_next++}";
        var s = Statuses.First(x => x.Id == status);
        Issues[key] = new JsonObject
        {
            ["key"] = key,
            ["html"] = html,
            ["fields"] = new JsonObject
            {
                ["summary"] = summary,
                ["status"] = new JsonObject { ["id"] = s.Id, ["name"] = s.Name, ["statusCategory"] = new JsonObject { ["key"] = s.Category } },
                ["issuetype"] = new JsonObject { ["name"] = type },
                ["priority"] = new JsonObject { ["name"] = "Medium" },
                ["assignee"] = unassigned ? null : assignee ?? User(),
                ["reporter"] = User(),
                ["project"] = new JsonObject { ["key"] = project },
                ["updated"] = "2026-10-24T09:00:00.000+0500",
                ["created"] = "2026-10-19T09:00:00.000+0500",
                ["labels"] = new JsonArray("web"),
                ["description"] = Body(text),
                ["comment"] = new JsonObject { ["comments"] = new JsonArray() },
            },
        };
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        var path = request.RequestUri!.PathAndQuery.TrimStart('/');
        lock (Requests)
            Requests.Add(new(request.Method.Method, path, body, request.Headers.Authorization?.ToString()));
        if (Down)
            throw new HttpRequestException("Name or service not known");
        var expected = cloud ? "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{secret}")) : "Bearer " + secret;
        if (request.Headers.Authorization?.ToString() != expected)
            return Json(HttpStatusCode.Unauthorized, new JsonObject { ["errorMessages"] = new JsonArray("You are not authenticated. Authentication required to perform this operation.") });
        var api = cloud ? "rest/api/3/" : "rest/api/2/";
        var route = path.Split('?')[0];
        var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
        lock (Issues)
            return Route(request.Method.Method, route, query, body, api);
    }

    HttpResponseMessage Route(string method, string route, System.Collections.Specialized.NameValueCollection query, string? body, string api)
    {
        if (route == "rest/api/2/serverInfo")
            return Json(HttpStatusCode.OK, new JsonObject { ["serverTitle"] = "Shop Jira", ["version"] = cloud ? "1001.0.0" : "9.12.2", ["deploymentType"] = cloud ? "Cloud" : "Server" });
        if (!route.StartsWith(api, StringComparison.Ordinal) && !route.StartsWith("rest/agile/1.0/", StringComparison.Ordinal))
            return Json(HttpStatusCode.NotFound, new JsonObject { ["errorMessages"] = new JsonArray($"No API here: {route}") });
        var rest = route.StartsWith(api, StringComparison.Ordinal) ? route[api.Length..] : route;
        Match m;
        switch (method)
        {
            case "GET" when rest == "myself":
                return Json(HttpStatusCode.OK, User());
            case "POST" when rest is "search" or "search/jql":
            {
                if ((rest == "search/jql") != cloud)
                    return Json(HttpStatusCode.Gone, new JsonObject { ["errorMessages"] = new JsonArray("This search is not here.") });
                var jql = JsonNode.Parse(body!)!["jql"]!.GetValue<string>();
                var found = Issues.Values.Where(i => Matches(i, jql)).Select(Row).ToArray();
                return Json(HttpStatusCode.OK, new JsonObject { ["issues"] = new JsonArray(found) });
            }
            case "GET" when (m = Regex.Match(rest, @"^issue/([A-Z]+-\d+)$")).Success:
            {
                if (!Issues.TryGetValue(m.Groups[1].Value, out var issue))
                    return Json(HttpStatusCode.NotFound, new JsonObject { ["errorMessages"] = new JsonArray("Issue does not exist or you do not have permission to see it.") });
                var copy = Row(issue).AsObject();
                if (query["expand"]?.Contains("renderedFields") == true)
                {
                    var comments = issue["fields"]!["comment"]!["comments"]!.AsArray();
                    copy["renderedFields"] = new JsonObject
                    {
                        ["description"] = issue["html"]!.GetValue<string>(),
                        ["comment"] = new JsonObject { ["comments"] = new JsonArray([.. comments.Select(c => (JsonNode)new JsonObject { ["body"] = $"<p>{(cloud ? Karvon.Jira.JiraText.FromAdf(JsonDocument.Parse(c!["body"]!.ToJsonString()).RootElement) : c!["body"]!.GetValue<string>())}</p>" })]) },
                    };
                }
                if (query["expand"]?.Contains("transitions") == true)
                    copy["transitions"] = TransitionsOf(issue);
                return Json(HttpStatusCode.OK, copy);
            }
            case "GET" when (m = Regex.Match(rest, @"^issue/([A-Z]+-\d+)/transitions$")).Success:
                return Json(HttpStatusCode.OK, new JsonObject { ["transitions"] = TransitionsOf(Issues[m.Groups[1].Value]) });
            case "POST" when (m = Regex.Match(rest, @"^issue/([A-Z]+-\d+)/transitions$")).Success:
            {
                var issue = Issues[m.Groups[1].Value];
                var id = JsonNode.Parse(body!)!["transition"]!["id"]!.GetValue<string>();
                var from = issue["fields"]!["status"]!["id"]!.GetValue<string>();
                if (Workflow[from].FirstOrDefault(t => t.Id == id) is not { Id: not null } t)
                    return Json(HttpStatusCode.BadRequest, new JsonObject { ["errorMessages"] = new JsonArray($"Transition id '{id}' is not valid for this issue.") });
                var to = Statuses.First(x => x.Id == t.To);
                issue["fields"]!["status"] = new JsonObject { ["id"] = to.Id, ["name"] = to.Name, ["statusCategory"] = new JsonObject { ["key"] = to.Category } };
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            case "POST" when (m = Regex.Match(rest, @"^issue/([A-Z]+-\d+)/comment$")).Success:
            {
                var said = JsonNode.Parse(body!)!["body"]!;
                if (cloud != (said is JsonObject))
                    return Json(HttpStatusCode.BadRequest, new JsonObject { ["errors"] = new JsonObject { ["comment"] = "Comment body is not valid." } });
                Issues[m.Groups[1].Value]["fields"]!["comment"]!["comments"]!.AsArray().Add(new JsonObject { ["author"] = User(), ["created"] = "2026-10-24T12:00:00.000+0500", ["body"] = said.DeepClone() });
                return Json(HttpStatusCode.Created, new JsonObject { ["id"] = "100" });
            }
            case "PUT" when (m = Regex.Match(rest, @"^issue/([A-Z]+-\d+)/assignee$")).Success:
                Issues[m.Groups[1].Value]["fields"]!["assignee"] = User();
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            case "GET" when (m = Regex.Match(rest, @"^project/([A-Z]+)$")).Success:
                return m.Groups[1].Value == "SHOP"
                    ? Json(HttpStatusCode.OK, Project())
                    : Json(HttpStatusCode.NotFound, new JsonObject { ["errorMessages"] = new JsonArray($"No project could be found with key '{m.Groups[1].Value}'.") });
            case "GET" when rest == "project":
                return Json(HttpStatusCode.OK, new JsonArray(Project()));
            case "POST" when rest == "issue":
            {
                var fields = JsonNode.Parse(body!)!["fields"]!;
                // As some sites: a type by its name is refused.
                if (fields["issuetype"]!["id"] is null)
                    return Json(HttpStatusCode.BadRequest, new JsonObject { ["errors"] = new JsonObject { ["issuetype"] = "Выбранный тип запроса недействителен" } });
                var type = fields["issuetype"]!["id"]!.GetValue<string>() == "10001" ? "Задача" : "Bug";
                var description = fields["description"] is JsonObject adf ? Karvon.Jira.JiraText.FromAdf(JsonDocument.Parse(adf.ToJsonString()).RootElement) : fields["description"]?.GetValue<string>() ?? "";
                Add("SHOP", fields["summary"]!.GetValue<string>(), "1", type, $"<p>{description}</p>", description, fields["assignee"]?.DeepClone(), unassigned: DropsAssignee);
                var key = $"SHOP-{_next - 1}";
                foreach (var (name, value) in fields.AsObject().Where(f => f.Key.StartsWith("customfield_", StringComparison.Ordinal) || f.Key == "duedate"))
                    Issues[key]["fields"]![name] = value!.DeepClone();
                return Json(HttpStatusCode.Created, new JsonObject { ["id"] = "20000", ["key"] = key });
            }
            case "GET" when rest == "filter/favourite":
                return Json(HttpStatusCode.OK, new JsonArray(new JsonObject { ["id"] = "10200", ["name"] = "Shop bugs", ["jql"] = "project = SHOP AND issuetype = Bug" }));
            case "GET" when rest.StartsWith("rest/agile/1.0/board", StringComparison.Ordinal) && Regex.IsMatch(rest, @"^rest/agile/1\.0/board$"):
                return Json(HttpStatusCode.OK, new JsonObject { ["values"] = new JsonArray(new JsonObject { ["id"] = 7, ["name"] = "SHOP board", ["type"] = "scrum" }) });
            case "GET" when rest == "rest/agile/1.0/board/7/configuration":
                return Json(HttpStatusCode.OK, new JsonObject
                {
                    ["columnConfig"] = new JsonObject
                    {
                        ["columns"] = new JsonArray(
                            new JsonObject { ["name"] = "To Do", ["statuses"] = new JsonArray(new JsonObject { ["id"] = "1" }) },
                            new JsonObject { ["name"] = "In Progress", ["statuses"] = new JsonArray(new JsonObject { ["id"] = "3" }, new JsonObject { ["id"] = "10" }) },
                            new JsonObject { ["name"] = "Done", ["statuses"] = new JsonArray(new JsonObject { ["id"] = "5" }) }),
                    },
                });
            case "GET" when rest == "rest/agile/1.0/board/7/sprint":
                return Json(HttpStatusCode.OK, new JsonObject { ["values"] = new JsonArray(new JsonObject { ["id"] = 3, ["name"] = "Sprint 3", ["state"] = "active" }) });
            case "GET" when rest is "rest/agile/1.0/board/7/sprint/3/issue" or "rest/agile/1.0/board/7/issue":
                return Json(HttpStatusCode.OK, new JsonObject { ["issues"] = new JsonArray([.. Issues.Values.Select(Row)]) });
        }
        return Json(HttpStatusCode.NotFound, new JsonObject { ["errorMessages"] = new JsonArray($"No such route: {method} {route}") });
    }

    static JsonObject Project() => new()
    {
        ["id"] = "10000",
        ["key"] = "SHOP",
        ["name"] = "Shop",
        ["issueTypes"] = new JsonArray(
            new JsonObject { ["id"] = "10001", ["name"] = "Задача", ["subtask"] = false },
            new JsonObject { ["id"] = "10004", ["name"] = "Bug", ["subtask"] = false },
            new JsonObject { ["id"] = "10005", ["name"] = "Sub-task", ["subtask"] = true }),
    };

    static JsonArray TransitionsOf(JsonObject issue) =>
        new([.. Workflow[issue["fields"]!["status"]!["id"]!.GetValue<string>()].Select(t => (JsonNode)new JsonObject
        {
            ["id"] = t.Id,
            ["name"] = t.Name,
            ["to"] = new JsonObject { ["id"] = t.To, ["name"] = Statuses.First(s => s.Id == t.To).Name },
        })]);

    static JsonNode Row(JsonObject issue) => new JsonObject { ["key"] = issue["key"]!.DeepClone(), ["fields"] = issue["fields"]!.DeepClone() };

    /// <summary>The JQL the tests ask: assignee = currentUser(), project = X, key = X, issuetype = X, joined by AND; ORDER BY ignored.</summary>
    bool Matches(JsonObject issue, string jql)
    {
        var where = Regex.Split(jql, @"\s+ORDER\s+BY\s+", RegexOptions.IgnoreCase)[0];
        foreach (var clause in Regex.Split(where, @"\s+AND\s+", RegexOptions.IgnoreCase).Where(c => c.Trim().Length > 0))
        {
            var parts = clause.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2)
                continue;
            var value = parts[1].Trim('"');
            var fields = issue["fields"]!;
            var ok = parts[0].ToLowerInvariant() switch
            {
                "assignee" => value == "currentUser()" && (fields["assignee"]?["accountId"] ?? fields["assignee"]?["name"])?.GetValue<string>() == _me,
                "project" => fields["project"]!["key"]!.GetValue<string>() == value,
                "key" => issue["key"]!.GetValue<string>() == value,
                "issuetype" => fields["issuetype"]!["name"]!.GetValue<string>() == value,
                "statuscategory" => fields["status"]!["statusCategory"]!["key"]!.GetValue<string>() != "done" || value == "Done",
                _ => true,
            };
            if (!ok)
                return false;
        }
        return true;
    }

    static HttpResponseMessage Json(HttpStatusCode status, JsonNode body) =>
        new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
}
