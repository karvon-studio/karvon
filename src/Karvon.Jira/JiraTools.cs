using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using Karvon.Claude;
using Karvon.Claude.Tools;
using Karvon.Jira.ViewModels;
using static Karvon.Claude.Tools.ToolInput;

namespace Karvon.Jira;

/// <summary>Claude Code's Jira tools.</summary>
public static class JiraClaudeTools
{
    public static IEnumerable<IClaudeTool> Create(Func<JiraExplorerViewModel> jira) =>
    [
        new JiraSitesTool(jira), new JiraSearchTool(jira), new JiraIssueTool(jira), new JiraProjectsTool(jira),
        new JiraCreateTool(jira), new JiraTransitionTool(jira), new JiraCommentTool(jira), new JiraAssignTool(jira),
    ];
}

/// <summary>
/// The Jira sites added in Karvon, for Claude: through Karvon's own client, signed in with the token or password the
/// keyring keeps (or the user gives Karvon), which Claude never sees, nor any header. Reading runs at once; an issue made,
/// moved, commented on or assigned asks the user each time.
/// </summary>
abstract class JiraTool(Func<JiraExplorerViewModel> jira) : IClaudeTool
{
    protected JiraExplorerViewModel Jira => jira();

    public abstract string Name { get; }

    public abstract string Description { get; }

    public abstract JsonObject InputSchema { get; }

    public abstract ClaudeToolPlan Plan(JsonElement input);

    public async Task<ClaudeToolOutput> CallAsync(JsonElement input, CancellationToken cancellationToken)
    {
        try
        {
            return await Dispatcher.UIThread.InvokeAsync(() => RunAsync(input, cancellationToken));
        }
        catch (Exception ex) when (ex is JiraException or InvalidOperationException)
        {
            return ClaudeToolOutput.Error(Redaction.Text(ex.Message));
        }
    }

    protected abstract Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken);

    /// <summary>The site the call names (or the only one), or an error that lists them.</summary>
    protected JiraSite Site(JsonElement input) =>
        Jira.Find(Str(input, "site")) ?? throw new InvalidOperationException(Jira.AllSites.Count == 0
            ? "There are no Jira sites. The user adds them in Karvon's Jira window (View > Jira)."
            : $"Name the site: {string.Join(", ", Jira.AllSites.Select(s => s.Name))}.");

    protected Task<JiraClient> ClientAsync(JsonElement input) => Jira.ClientAsync(Site(input));

    protected static string Required(JsonElement input, string name) =>
        Str(input, name) is { Length: > 0 } value ? value : throw new InvalidOperationException($"'{name}' is required.");

    protected static JsonObject SiteProperty => new() { ["type"] = "string", ["description"] = "The site's name, from jira_sites (not needed when there is one)." };

    protected static JsonObject KeyProperty => new() { ["type"] = "string", ["description"] = "The issue's key, such as SHOP-12." };

    protected static string Row(JiraIssueSummary i) =>
        $"{i.Key} [{i.Status}] {i.Type}: {i.Summary}" + (i.Assignee is { } a ? $" ({a})" : " (unassigned)");
}

/// <summary><c>jira_sites</c>: the sites, how each signs in, as whom (never its secret).</summary>
sealed class JiraSitesTool(Func<JiraExplorerViewModel> jira) : JiraTool(jira)
{
    public override string Name => "jira_sites";

    public override string Description => "Lists the Jira sites the user added in Karvon: name, address, Cloud or Server/Data Center, and the user it signs in as.";

    public override JsonObject InputSchema => Schema("""{"type":"object","properties":{}}""");

    public override ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Read, "Jira Sites", null, "");

    protected override Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var sites = Jira.AllSites;
        return Task.FromResult(new ClaudeToolOutput(sites.Count == 0
            ? "There are no Jira sites. The user adds them in Karvon's Jira window (View > Jira)."
            : string.Join("\n", sites.Select(s => $"{s.Name}: {s.Url}, {(s.IsCloud ? "Jira Cloud" : "Jira Server/Data Center")}"
                + (s.User.Length > 0 ? $", signed in as {s.User}" : "")
                + (s.StartDateField is not null || s.EndDateField is not null ? $", dates in {s.StartDateField ?? "-"} and {s.EndDateField ?? "duedate"}" : "")))));
    }
}

/// <summary><c>jira_search</c>: a JQL query's issues.</summary>
sealed class JiraSearchTool(Func<JiraExplorerViewModel> jira) : JiraTool(jira)
{
    public override string Name => "jira_search";

    public override string Description =>
        "Searches a Jira site with JQL (such as 'assignee = currentUser() AND statusCategory != Done ORDER BY updated DESC') and "
        + "returns the issues: key, status, type, summary and assignee.";

    public override JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["site"] = SiteProperty,
            ["jql"] = new JsonObject { ["type"] = "string", ["description"] = "The JQL query." },
            ["max"] = new JsonObject { ["type"] = "integer", ["description"] = "How many issues at most (default 30)." },
        },
        ["required"] = new JsonArray("jql"),
    };

    public override ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Read, "Jira Search", OneLine(Str(input, "jql") ?? ""), "");

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var issues = await (await ClientAsync(input)).SearchAsync(Required(input, "jql"), Int(input, "max", 30, 1, 200), cancellationToken);
        return issues.Count == 0 ? new ClaudeToolOutput("No issues.") : new ClaudeToolOutput(string.Join("\n", issues.Select(Row))) { Outside = "the issues Jira found" };
    }
}

/// <summary><c>jira_issue</c>: an issue whole: its fields, description, comments and the transitions it has now.</summary>
sealed class JiraIssueTool(Func<JiraExplorerViewModel> jira) : JiraTool(jira)
{
    public override string Name => "jira_issue";

    public override string Description =>
        "Returns a Jira issue: its fields, description, comments, and the transitions its workflow has from its status now "
        + "(their names are what jira_transition takes).";

    public override JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject { ["site"] = SiteProperty, ["key"] = KeyProperty },
        ["required"] = new JsonArray("key"),
    };

    public override ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Read, "Jira Issue", Str(input, "key"), "");

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var site = Site(input);
        var issue = await (await Jira.ClientAsync(site)).IssueAsync(Required(input, "key"), cancellationToken);
        var text = new StringBuilder();
        text.AppendLine($"{issue.Key}: {issue.Summary.Summary}");
        text.AppendLine($"Status: {issue.Summary.Status}; type: {issue.Summary.Type}; priority: {issue.Summary.Priority ?? "-"}; assignee: {issue.Summary.Assignee ?? "unassigned"}; reporter: {issue.Reporter ?? "-"}");
        if (issue.Parent is { } parent)
            text.AppendLine($"Parent: {parent}");
        if (issue.Subtasks.Count > 0)
            text.AppendLine($"Subtasks: {string.Join(", ", issue.Subtasks)}");
        if (issue.Labels.Count > 0)
            text.AppendLine($"Labels: {string.Join(", ", issue.Labels)}");
        if (issue.StartDate is not null || issue.EndDate is not null)
            text.AppendLine($"Start: {issue.StartDate ?? "-"}; end: {issue.EndDate ?? "-"}");
        text.AppendLine($"Page: {site.BrowseUrl(issue.Key)}");
        text.AppendLine().AppendLine("Description:").AppendLine(issue.Description.Length > 0 ? Tail(issue.Description, 20_000) : "(none)");
        if (issue.Comments.Count > 0)
        {
            text.AppendLine().AppendLine($"Comments ({issue.Comments.Count}):");
            foreach (var comment in issue.Comments.TakeLast(20))
                text.AppendLine($"- {comment.Author}, {comment.Created?.ToString("yyyy-MM-dd HH:mm") ?? "?"}: {Tail(comment.Body, 4000)}");
        }
        text.AppendLine().Append("Transitions now: ").Append(issue.Transitions.Count == 0 ? "none" : string.Join(", ", issue.Transitions.Select(t => $"{t.Name} (to {t.ToStatus})")));
        return new ClaudeToolOutput(text.ToString()) { Outside = $"Jira issue {issue.Key}" };
    }
}

/// <summary><c>jira_projects</c>: the projects and their issue types (what jira_create takes).</summary>
sealed class JiraProjectsTool(Func<JiraExplorerViewModel> jira) : JiraTool(jira)
{
    public override string Name => "jira_projects";

    public override string Description => "Lists a Jira site's projects (key and name) and, for one project, its issue types.";

    public override JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["site"] = SiteProperty,
            ["project"] = new JsonObject { ["type"] = "string", ["description"] = "A project's key, for its issue types." },
        },
    };

    public override ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Read, "Jira Projects", Str(input, "project"), "");

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var client = await ClientAsync(input);
        if (Str(input, "project") is { Length: > 0 } key)
        {
            var project = await client.ProjectAsync(key, cancellationToken);
            return new ClaudeToolOutput($"{project.Key}: {project.Name}\nIssue types: {string.Join(", ", project.IssueTypes.Select(t => t.Name + (t.Subtask ? " (subtask)" : "")))}");
        }
        var projects = await client.ProjectsAsync(cancellationToken);
        return new ClaudeToolOutput(projects.Count == 0 ? "No projects." : string.Join("\n", projects.Select(p => $"{p.Key}: {p.Name}")));
    }
}

/// <summary><c>jira_create</c> (asks first): an issue made as the signed-in user's, its type by the project's id for it.</summary>
sealed class JiraCreateTool(Func<JiraExplorerViewModel> jira) : JiraTool(jira)
{
    public override string Name => "jira_create";

    public override string Description =>
        "Makes a Jira issue, assigned to the signed-in user (the owner of the token Karvon signs in with), and returns its key. "
        + "The user approves each one. Its type is a name of the project's issue types (jira_projects); start and end dates "
        + "(yyyy-MM-dd) go to the site's date fields. Keep the description short.";

    public override JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["site"] = SiteProperty,
            ["project"] = new JsonObject { ["type"] = "string", ["description"] = "The project's key." },
            ["type"] = new JsonObject { ["type"] = "string", ["description"] = "The issue type's name, as the project has it (such as Task or Bug)." },
            ["summary"] = new JsonObject { ["type"] = "string", ["description"] = "A short title." },
            ["description"] = new JsonObject { ["type"] = "string", ["description"] = "Plain text, a few lines." },
            ["start"] = new JsonObject { ["type"] = "string", ["description"] = "Start date, yyyy-MM-dd." },
            ["end"] = new JsonObject { ["type"] = "string", ["description"] = "End (due) date, yyyy-MM-dd." },
        },
        ["required"] = new JsonArray("project", "type", "summary"),
    };

    public override ClaudeToolPlan Plan(JsonElement input) =>
        new(ToolAccess.Write, "New Jira Issue", $"{Str(input, "project")}: {OneLine(Str(input, "summary") ?? "")}", $"Allow Claude to make this {Str(input, "type")} in Jira?")
        {
            Code = $"{Str(input, "summary")}\n\n{Str(input, "description")}".Trim(),
        };

    static DateOnly? Date(JsonElement input, string name) =>
        Str(input, name) is { Length: > 0 } text
            ? DateOnly.TryParseExact(text, "yyyy-MM-dd", out var d) ? d : throw new InvalidOperationException($"'{name}' is a date as yyyy-MM-dd.")
            : null;

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var site = Site(input);
        var key = await (await Jira.ClientAsync(site)).CreateAsync(Required(input, "project"), Required(input, "type"), Required(input, "summary"),
            Str(input, "description"), Date(input, "start"), Date(input, "end"), cancellationToken);
        return new ClaudeToolOutput($"{key} made, assigned to the signed-in user: {site.BrowseUrl(key)}");
    }
}

/// <summary><c>jira_transition</c> (asks first): an issue moved along its workflow.</summary>
sealed class JiraTransitionTool(Func<JiraExplorerViewModel> jira) : JiraTool(jira)
{
    public override string Name => "jira_transition";

    public override string Description =>
        "Moves a Jira issue along its workflow: the transition's name, or the status it goes to, as jira_issue lists them (in the "
        + "workflow's language). The user approves each one.";

    public override JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["site"] = SiteProperty,
            ["key"] = KeyProperty,
            ["transition"] = new JsonObject { ["type"] = "string", ["description"] = "The transition's name, or the status it goes to." },
        },
        ["required"] = new JsonArray("key", "transition"),
    };

    public override ClaudeToolPlan Plan(JsonElement input) =>
        new(ToolAccess.Write, "Jira Transition", $"{Str(input, "key")} → {Str(input, "transition")}", $"Allow Claude to move {Str(input, "key")} to {Str(input, "transition")}?");

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var key = Required(input, "key");
        var done = await (await ClientAsync(input)).TransitionAsync(key, Required(input, "transition"), cancellationToken);
        return new ClaudeToolOutput($"{key}: {done.Name} → {done.ToStatus}.");
    }
}

/// <summary><c>jira_comment</c> (asks first): a comment on an issue.</summary>
sealed class JiraCommentTool(Func<JiraExplorerViewModel> jira) : JiraTool(jira)
{
    public override string Name => "jira_comment";

    public override string Description => "Adds a comment (plain text) to a Jira issue. The user approves each one.";

    public override JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["site"] = SiteProperty,
            ["key"] = KeyProperty,
            ["text"] = new JsonObject { ["type"] = "string", ["description"] = "The comment." },
        },
        ["required"] = new JsonArray("key", "text"),
    };

    public override ClaudeToolPlan Plan(JsonElement input) =>
        new(ToolAccess.Write, "Jira Comment", Str(input, "key"), $"Allow Claude to comment on {Str(input, "key")}?") { Code = Str(input, "text") };

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var key = Required(input, "key");
        await (await ClientAsync(input)).CommentAsync(key, Required(input, "text"), cancellationToken);
        return new ClaudeToolOutput($"Commented on {key}.");
    }
}

/// <summary><c>jira_assign</c> (asks first): an issue to the signed-in user, or to nobody.</summary>
sealed class JiraAssignTool(Func<JiraExplorerViewModel> jira) : JiraTool(jira)
{
    public override string Name => "jira_assign";

    public override string Description => "Assigns a Jira issue to the signed-in user (to: me) or to nobody (to: nobody). The user approves each one.";

    public override JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["site"] = SiteProperty,
            ["key"] = KeyProperty,
            ["to"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("me", "nobody"), ["description"] = "me or nobody." },
        },
        ["required"] = new JsonArray("key", "to"),
    };

    public override ClaudeToolPlan Plan(JsonElement input) =>
        new(ToolAccess.Write, "Jira Assign", $"{Str(input, "key")} → {Str(input, "to")}",
            Str(input, "to") == "nobody" ? $"Allow Claude to unassign {Str(input, "key")}?" : $"Allow Claude to assign {Str(input, "key")} to you?");

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var site = Site(input);
        var key = Required(input, "key");
        var me = Str(input, "to") == "nobody" ? null : await Jira.MeAsync(site);
        await (await Jira.ClientAsync(site)).AssignAsync(key, me, cancellationToken);
        return new ClaudeToolOutput(me is null ? $"{key} is unassigned." : $"{key} is assigned to {me.DisplayName}.");
    }
}
