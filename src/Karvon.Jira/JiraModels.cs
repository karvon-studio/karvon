namespace Karvon.Jira;

/// <summary>How Karvon signs in to a Jira site.</summary>
public enum JiraAuth
{
    /// <summary>Jira Cloud (*.atlassian.net): the account's email and an API token (id.atlassian.com, API tokens).</summary>
    CloudToken,
    /// <summary>Jira Server or Data Center: a personal access token (Profile, Personal Access Tokens), sent as a bearer token.</summary>
    PersonalAccessToken,
    /// <summary>Jira Server or Data Center: the user's name and password (basic authentication, where the site allows it).</summary>
    Password,
}

/// <summary>
/// A Jira site Karvon signs in to: what is not secret (its address, how it signs in, the user's name or email), kept in
/// Karvon's settings; its token or password is the keyring's, never here.
/// </summary>
/// <param name="StartDateField">The custom field of an issue's start date (customfield_10015), when the site has one.</param>
/// <param name="EndDateField">The custom field of an issue's end date (or due date's), when the site has one.</param>
public sealed record JiraSite(string Id, string Name, string Url, JiraAuth Auth, string User = "",
    string? StartDateField = null, string? EndDateField = null)
{
    /// <summary>Jira Cloud: REST API 3 (its descriptions and comments in Atlassian Document Format); Server and Data Center, 2.</summary>
    public bool IsCloud => Auth == JiraAuth.CloudToken;

    public string ApiPath => IsCloud ? "rest/api/3" : "rest/api/2";

    /// <summary>The site's page of an issue.</summary>
    public string BrowseUrl(string key) => $"{Url.TrimEnd('/')}/browse/{key}";
}

/// <summary>A Jira user: Cloud knows them by their account id, Server and Data Center by their name.</summary>
public sealed record JiraUser(string Id, string DisplayName, string? Email)
{
    /// <summary>The user as an issue's assignee field says them: { "accountId": ... } on Cloud, { "name": ... } elsewhere.</summary>
    public object AsField(bool cloud) => cloud ? new { accountId = Id } : new { name = Id };
}

public sealed record JiraIssueType(string Id, string Name, bool Subtask);

public sealed record JiraProject(string Id, string Key, string Name, IReadOnlyList<JiraIssueType> IssueTypes);

/// <summary>An issue in a list: what a row of it shows.</summary>
/// <param name="StatusCategory">The status's category: new (to do), indeterminate (in progress), done.</param>
public sealed record JiraIssueSummary(string Key, string Summary, string Status, string StatusCategory, string Type, string? Priority,
    string? Assignee, DateTimeOffset? Updated);

public sealed record JiraComment(string Author, DateTimeOffset? Created, string Body);

/// <summary>A transition of an issue's workflow from its status: its id (what a transition is done by) and where it goes.</summary>
public sealed record JiraTransition(string Id, string Name, string ToStatus, string ToStatusId = "");

/// <summary>An issue, whole: its fields, its description and comments as Markdown, the transitions it has now.</summary>
public sealed record JiraIssue(JiraIssueSummary Summary, string Project, string? Reporter, DateTimeOffset? Created, IReadOnlyList<string> Labels,
    string Description, IReadOnlyList<JiraComment> Comments, IReadOnlyList<JiraTransition> Transitions, string? Parent,
    IReadOnlyList<string> Subtasks, string? StartDate, string? EndDate)
{
    public string Key => Summary.Key;
}

public sealed record JiraFilter(string Id, string Name, string Jql);

public sealed record JiraBoard(int Id, string Name, string Type);

public sealed record JiraSprint(int Id, string Name, string State);

/// <summary>A column of a board: its name and the statuses it holds.</summary>
public sealed record JiraBoardColumn(string Name, IReadOnlyList<string> StatusIds);

/// <summary>What Jira said went wrong (its errorMessages and its fields' errors), as it said it.</summary>
public sealed class JiraException(string message, int? status = null) : Exception(message)
{
    /// <summary>The HTTP status (401: not signed in, 403: not allowed, 404: no such issue); null when the site was not reached.</summary>
    public int? Status => status;
}
