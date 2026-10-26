using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Karvon.Jira;

/// <summary>
/// A Jira site's REST API, signed in with its secret (an API token, a personal access token or a password), which only
/// this holds: Cloud's API 3 (its rich text in Atlassian Document Format), Server's and Data Center's API 2 (plain
/// text), and the agile API (boards, sprints). Bodies go as UTF-8 (sites whose workflows are named in Cyrillic);
/// issue types go by their id (some sites refuse a type by its name); an issue Karvon makes is the signed-in user's.
/// What Jira says went wrong comes back as it said it.
/// </summary>
public sealed class JiraClient : IDisposable
{
    readonly HttpClient _http;

    public JiraSite Site { get; }

    /// <summary>The fields an issue's row needs.</summary>
    static readonly string[] RowFields = ["summary", "status", "issuetype", "priority", "assignee", "updated"];

    public JiraClient(JiraSite site, string secret, HttpMessageHandler? handler = null)
    {
        Site = site;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = new Uri(site.Url.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(60);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Karvon-Studio (https://github.com/karvon-studio/karvon)");
        _http.DefaultRequestHeaders.Authorization = site.Auth switch
        {
            JiraAuth.PersonalAccessToken => new AuthenticationHeaderValue("Bearer", secret),
            _ => new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{site.User}:{secret}"))),
        };
        // Server's anti-XSRF check: a REST call is not a browser's form.
        _http.DefaultRequestHeaders.Add("X-Atlassian-Token", "no-check");
    }

    public void Dispose() => _http.Dispose();

    string Api(string path) => $"{Site.ApiPath}/{path}";

    async Task<JsonDocument?> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new JiraException($"{Site.Url} could not be reached ({ex.Message}); a site of a company may need its VPN.");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new JiraException($"{Site.Url} did not answer in time; a site of a company may need its VPN.");
        }
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new JiraException(ErrorOf(response.StatusCode, text), (int)response.StatusCode);
            if (text.Length == 0 || response.StatusCode == HttpStatusCode.NoContent)
                return null;
            try
            {
                return JsonDocument.Parse(text);
            }
            catch (JsonException)
            {
                // A sign-in page instead of JSON: the site's single sign-on in front of its API.
                throw new JiraException($"{Site.Url} answered with a page, not Jira's API (a sign-in page in front of it?).");
            }
        }
    }

    /// <summary>Jira's own words: its errorMessages and its fields' errors; else what the status means.</summary>
    static string ErrorOf(HttpStatusCode status, string text)
    {
        var said = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("errorMessages", out var messages) && messages.ValueKind == JsonValueKind.Array)
                said.AddRange(messages.EnumerateArray().Select(m => m.GetString()).OfType<string>());
            if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
                said.AddRange(errors.EnumerateObject().Select(e => $"{e.Name}: {e.Value.GetString()}"));
            if (said.Count == 0 && doc.RootElement.TryGetProperty("message", out var message))
                said.Add(message.GetString() ?? "");
        }
        catch (JsonException)
        {
        }
        var what = status switch
        {
            HttpStatusCode.Unauthorized => "not signed in (the token or password is wrong, or has expired)",
            HttpStatusCode.Forbidden => "not allowed",
            HttpStatusCode.NotFound => "not found",
            _ => $"HTTP {(int)status}",
        };
        return said.Count > 0 ? $"Jira: {string.Join(" ", said)} ({what})" : $"Jira: {what}.";
    }

    static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static JsonElement Obj(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : default;

    static DateTimeOffset? Date(JsonElement e, string name) => DateTimeOffset.TryParse(Str(e, name), out var d) ? d : null;

    static JiraUser UserOf(JsonElement u) =>
        new(Str(u, "accountId") ?? Str(u, "name") ?? Str(u, "key") ?? "", Str(u, "displayName") ?? Str(u, "name") ?? "", Str(u, "emailAddress"));

    /// <summary>Who the secret signs in as (an issue Karvon makes is theirs).</summary>
    public async Task<JiraUser> MyselfAsync(CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get, Api("myself"), null, ct) ?? throw new JiraException("Jira said nothing of the user.");
        return UserOf(doc.RootElement);
    }

    /// <summary>The site: its title and version, and whether it is Cloud (its deployment type).</summary>
    public async Task<(string Title, string Version, bool Cloud)> ServerInfoAsync(CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get, "rest/api/2/serverInfo", null, ct) ?? throw new JiraException("Jira said nothing of itself.");
        var root = doc.RootElement;
        return (Str(root, "serverTitle") ?? "Jira", Str(root, "version") ?? "", Str(root, "deploymentType") == "Cloud");
    }

    static JiraIssueSummary RowOf(JsonElement issue)
    {
        var fields = Obj(issue, "fields");
        var status = Obj(fields, "status");
        return new JiraIssueSummary(Str(issue, "key") ?? "", Str(fields, "summary") ?? "", Str(status, "name") ?? "",
            Str(Obj(status, "statusCategory"), "key") ?? "", Str(Obj(fields, "issuetype"), "name") ?? "", Str(Obj(fields, "priority"), "name"),
            Str(Obj(fields, "assignee"), "displayName"), Date(fields, "updated"));
    }

    /// <summary>The issues a JQL query finds (at most <paramref name="max"/>), in its order.</summary>
    public async Task<IReadOnlyList<JiraIssueSummary>> SearchAsync(string jql, int max, CancellationToken ct)
    {
        // Cloud's enhanced search (its /search went); Server's and Data Center's /search.
        using var doc = Site.IsCloud
            ? await SendAsync(HttpMethod.Post, Api("search/jql"), new { jql, maxResults = max, fields = RowFields }, ct)
            : await SendAsync(HttpMethod.Post, Api("search"), new { jql, startAt = 0, maxResults = max, fields = RowFields }, ct);
        return doc is not null && doc.RootElement.TryGetProperty("issues", out var issues) ? [.. issues.EnumerateArray().Select(RowOf)] : [];
    }

    /// <summary>An issue, whole: its description and comments as the site renders them (as Markdown), its transitions now.</summary>
    public async Task<JiraIssue> IssueAsync(string key, CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get, Api($"issue/{Uri.EscapeDataString(key)}?expand=renderedFields,transitions"), null, ct)
            ?? throw new JiraException($"Jira said nothing of {key}.");
        var issue = doc.RootElement;
        var fields = Obj(issue, "fields");
        var rendered = Obj(issue, "renderedFields");
        string RichText(JsonElement raw, string? html) => html is { Length: > 0 } ? JiraText.FromHtml(html) : raw.ValueKind == JsonValueKind.Object ? JiraText.FromAdf(raw) : raw.ValueKind == JsonValueKind.String ? raw.GetString()! : "";
        var description = RichText(fields.TryGetProperty("description", out var d) ? d : default, Str(rendered, "description"));
        var renderedComments = Obj(rendered, "comment").TryGetProperty("comments", out var rc) && rc.ValueKind == JsonValueKind.Array ? rc.EnumerateArray().ToList() : [];
        var comments = Obj(fields, "comment").TryGetProperty("comments", out var cs) && cs.ValueKind == JsonValueKind.Array
            ? cs.EnumerateArray().Select((c, i) => new JiraComment(Str(Obj(c, "author"), "displayName") ?? "", Date(c, "created"),
                RichText(c.TryGetProperty("body", out var b) ? b : default, i < renderedComments.Count ? Str(renderedComments[i], "body") : null))).ToList()
            : [];
        var transitions = issue.TryGetProperty("transitions", out var ts) && ts.ValueKind == JsonValueKind.Array
            ? ts.EnumerateArray().Select(t => new JiraTransition(Str(t, "id") ?? "", Str(t, "name") ?? "", Str(Obj(t, "to"), "name") ?? "", Str(Obj(t, "to"), "id") ?? "")).ToList()
            : [];
        var labels = fields.TryGetProperty("labels", out var ls) && ls.ValueKind == JsonValueKind.Array ? ls.EnumerateArray().Select(l => l.GetString() ?? "").ToList() : [];
        var subtasks = fields.TryGetProperty("subtasks", out var ss) && ss.ValueKind == JsonValueKind.Array ? ss.EnumerateArray().Select(s => Str(s, "key") ?? "").ToList() : [];
        string? Field(string? id) => id is null ? null : fields.TryGetProperty(id, out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
        return new JiraIssue(RowOf(issue), Str(Obj(fields, "project"), "key") ?? "", Str(Obj(fields, "reporter"), "displayName"), Date(fields, "created"),
            labels, description, comments, transitions, Str(Obj(fields, "parent"), "key"), subtasks,
            Field(Site.StartDateField), Field(Site.EndDateField) ?? Str(fields, "duedate"));
    }

    /// <summary>The transitions an issue has from its status now.</summary>
    public async Task<IReadOnlyList<JiraTransition>> TransitionsAsync(string key, CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get, Api($"issue/{Uri.EscapeDataString(key)}/transitions"), null, ct);
        return doc is not null && doc.RootElement.TryGetProperty("transitions", out var ts)
            ? [.. ts.EnumerateArray().Select(t => new JiraTransition(Str(t, "id") ?? "", Str(t, "name") ?? "", Str(Obj(t, "to"), "name") ?? "", Str(Obj(t, "to"), "id") ?? ""))]
            : [];
    }

    /// <summary>
    /// An issue moved by a transition, which is named (its name, or the status it goes to, as a workflow says them in any
    /// language) or its id: the transition it took.
    /// </summary>
    public async Task<JiraTransition> TransitionAsync(string key, string transition, CancellationToken ct)
    {
        var all = await TransitionsAsync(key, ct);
        var chosen = all.FirstOrDefault(t => t.Id == transition)
            ?? all.FirstOrDefault(t => string.Equals(t.Name, transition, StringComparison.CurrentCultureIgnoreCase))
            ?? all.FirstOrDefault(t => string.Equals(t.ToStatus, transition, StringComparison.CurrentCultureIgnoreCase))
            ?? throw new JiraException($"{key} has no transition \"{transition}\" now; it has: {string.Join(", ", all.Select(t => $"{t.Name} (to {t.ToStatus})"))}.");
        await SendAsync(HttpMethod.Post, Api($"issue/{Uri.EscapeDataString(key)}/transitions"), new { transition = new { id = chosen.Id } }, ct);
        return chosen;
    }

    /// <summary>A comment on an issue: Cloud's in Atlassian Document Format, else as it is.</summary>
    public async Task CommentAsync(string key, string text, CancellationToken ct) =>
        (await SendAsync(HttpMethod.Post, Api($"issue/{Uri.EscapeDataString(key)}/comment"), new { body = Site.IsCloud ? JiraText.ToAdf(text) : text }, ct))?.Dispose();

    /// <summary>An issue's assignee: a user (null: nobody).</summary>
    public async Task AssignAsync(string key, JiraUser? user, CancellationToken ct) =>
        (await SendAsync(HttpMethod.Put, Api($"issue/{Uri.EscapeDataString(key)}/assignee"),
            user?.AsField(Site.IsCloud) ?? (Site.IsCloud ? new { accountId = (string?)null } : new { name = (string?)null }), ct))?.Dispose();

    /// <summary>A project and its issue types (an issue is made with a type's id).</summary>
    public async Task<JiraProject> ProjectAsync(string key, CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get, Api($"project/{Uri.EscapeDataString(key)}"), null, ct) ?? throw new JiraException($"Jira said nothing of {key}.");
        return ProjectOf(doc.RootElement);
    }

    static JiraProject ProjectOf(JsonElement p) => new(Str(p, "id") ?? "", Str(p, "key") ?? "", Str(p, "name") ?? "",
        p.TryGetProperty("issueTypes", out var types) && types.ValueKind == JsonValueKind.Array
            ? [.. types.EnumerateArray().Select(t => new JiraIssueType(Str(t, "id") ?? "", Str(t, "name") ?? "", t.TryGetProperty("subtask", out var s) && s.ValueKind == JsonValueKind.True))]
            : []);

    public async Task<IReadOnlyList<JiraProject>> ProjectsAsync(CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get, Api("project"), null, ct);
        return doc is null ? [] : [.. doc.RootElement.EnumerateArray().Select(ProjectOf).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>
    /// An issue made, as the signed-in user's: its project's type by its id (or its name, looked up as the project has
    /// it), its description (Atlassian Document Format on Cloud), its dates in the site's date fields; its assignee checked
    /// afterwards, and set when the site left it empty. Its key.
    /// </summary>
    public async Task<string> CreateAsync(string project, string type, string summary, string? description, DateOnly? start, DateOnly? end,
        CancellationToken ct)
    {
        var info = await ProjectAsync(project, ct);
        var issueType = info.IssueTypes.FirstOrDefault(t => t.Id == type)
            ?? info.IssueTypes.FirstOrDefault(t => string.Equals(t.Name, type, StringComparison.CurrentCultureIgnoreCase))
            ?? throw new JiraException($"{project} has no issue type \"{type}\"; it has: {string.Join(", ", info.IssueTypes.Where(t => !t.Subtask).Select(t => t.Name))}.");
        var me = await MyselfAsync(ct);
        var fields = new JsonObject
        {
            ["project"] = new JsonObject { ["key"] = info.Key },
            ["summary"] = summary,
            ["issuetype"] = new JsonObject { ["id"] = issueType.Id },
            ["assignee"] = JsonSerializer.SerializeToNode(me.AsField(Site.IsCloud)),
        };
        if (description is { Length: > 0 })
            fields["description"] = Site.IsCloud ? JsonSerializer.SerializeToNode(JiraText.ToAdf(description)) : description;
        if (start is { } s && Site.StartDateField is { } startField)
            fields[startField] = s.ToString("yyyy-MM-dd");
        if (end is { } e)
            fields[Site.EndDateField ?? "duedate"] = e.ToString("yyyy-MM-dd");
        using var doc = await SendAsync(HttpMethod.Post, Api("issue"), new JsonObject { ["fields"] = fields }, ct) ?? throw new JiraException("Jira made no issue.");
        var key = Str(doc.RootElement, "key") ?? throw new JiraException("Jira made an issue without a key.");
        // Some workflows leave the assignee empty whatever was sent: it is set again.
        using var made = await SendAsync(HttpMethod.Get, Api($"issue/{Uri.EscapeDataString(key)}?fields=assignee"), null, ct);
        if (made is not null && Obj(Obj(made.RootElement, "fields"), "assignee").ValueKind != JsonValueKind.Object)
            await AssignAsync(key, me, ct);
        return key;
    }

    /// <summary>The user's favourite filters (their JQL).</summary>
    public async Task<IReadOnlyList<JiraFilter>> FavouriteFiltersAsync(CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get, Api("filter/favourite"), null, ct);
        return doc is null ? [] : [.. doc.RootElement.EnumerateArray().Select(f => new JiraFilter(Str(f, "id") ?? "", Str(f, "name") ?? "", Str(f, "jql") ?? ""))];
    }

    /// <summary>The boards (of a project, when one is named).</summary>
    public async Task<IReadOnlyList<JiraBoard>> BoardsAsync(string? project, CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get, "rest/agile/1.0/board?maxResults=50" + (project is null ? "" : "&projectKeyOrId=" + Uri.EscapeDataString(project)), null, ct);
        return doc is not null && doc.RootElement.TryGetProperty("values", out var values)
            ? [.. values.EnumerateArray().Select(b => new JiraBoard(b.GetProperty("id").GetInt32(), Str(b, "name") ?? "", Str(b, "type") ?? ""))]
            : [];
    }

    /// <summary>A board's columns, each with the statuses it holds, and a kanban board's sub-filter (its JQL, when it has one).</summary>
    public async Task<(IReadOnlyList<JiraBoardColumn> Columns, string? SubQuery)> ColumnsAsync(int board, CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get, $"rest/agile/1.0/board/{board}/configuration", null, ct);
        if (doc is null || !Obj(doc.RootElement, "columnConfig").TryGetProperty("columns", out var columns))
            return ([], null);
        return ([.. columns.EnumerateArray().Select(c => new JiraBoardColumn(Str(c, "name") ?? "",
                c.TryGetProperty("statuses", out var ss) ? [.. ss.EnumerateArray().Select(s => Str(s, "id") ?? "")] : []))],
            Str(Obj(doc.RootElement, "subQuery"), "query") is { Length: > 0 } sub ? sub : null);
    }

    /// <summary>A scrum board's active and future sprints.</summary>
    public async Task<IReadOnlyList<JiraSprint>> SprintsAsync(int board, CancellationToken ct)
    {
        try
        {
            using var doc = await SendAsync(HttpMethod.Get, $"rest/agile/1.0/board/{board}/sprint?state=active,future", null, ct);
            return doc is not null && doc.RootElement.TryGetProperty("values", out var values)
                ? [.. values.EnumerateArray().Select(s => new JiraSprint(s.GetProperty("id").GetInt32(), Str(s, "name") ?? "", Str(s, "state") ?? ""))]
                : [];
        }
        catch (JiraException ex) when (ex.Status == 400)
        {
            // A kanban board has no sprints.
            return [];
        }
    }

    /// <summary>
    /// A board column's issues (of its sprint, when one is named), each column read by itself: the board's first issues
    /// are its oldest, mostly done ones, which would leave the other columns empty. Those in the column's statuses and the
    /// board's sub-filter, the first <paramref name="max"/> in the board's order (the latest updated first, as Jira's board
    /// shows its done ones), and how many there are.
    /// </summary>
    public async Task<(IReadOnlyList<JiraIssueSummary> Issues, int Total)> ColumnIssuesAsync(int board, int? sprint, JiraBoardColumn column, string? subQuery,
        bool latestFirst, int max, CancellationToken ct)
    {
        if (column.StatusIds.Count == 0)
            return ([], 0);
        var jql = (subQuery is null ? "" : $"({subQuery}) AND ") + $"status in ({string.Join(',', column.StatusIds)})" + (latestFirst ? " ORDER BY updated DESC" : "");
        var path = sprint is { } s ? $"rest/agile/1.0/board/{board}/sprint/{s}/issue" : $"rest/agile/1.0/board/{board}/issue";
        using var doc = await SendAsync(HttpMethod.Get, $"{path}?jql={Uri.EscapeDataString(jql)}&maxResults={max}&fields={string.Join(',', RowFields)}", null, ct);
        if (doc is null || !doc.RootElement.TryGetProperty("issues", out var issues))
            return ([], 0);
        IReadOnlyList<JiraIssueSummary> rows = [.. issues.EnumerateArray().Select(RowOf)];
        return (rows, doc.RootElement.TryGetProperty("total", out var total) && total.TryGetInt32(out var n) ? Math.Max(n, rows.Count) : rows.Count);
    }
}
