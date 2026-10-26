using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Karvon.Jira.ViewModels;

/// <summary>
/// An issue's page: its key and summary, status, fields, description and comments (as the site renders them), the
/// transitions it has now; a comment written, Assign to me, Open in Jira, Ask Claude (the issue as the task).
/// </summary>
public sealed partial class JiraIssuePageViewModel(JiraExplorerViewModel explorer, JiraSite site, string key) : ObservableObject
{
    public JiraSite Site => site;

    public string IssueKey => key;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIssue), nameof(Fields), nameof(Title))]
    public partial JiraIssue? Issue { get; private set; }

    public bool HasIssue => Issue is not null;

    public string Title => Issue is { } issue ? $"{issue.Key}  {issue.Summary.Summary}" : key;

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial string? Error { get; private set; }

    [ObservableProperty]
    public partial string NewComment { get; set; } = "";

    /// <summary>Its fields, as rows of a name and what it is.</summary>
    public IReadOnlyList<JiraField> Fields => Issue is not { } issue ? [] :
    [
        .. new (string, string?)[]
        {
            ("Status", issue.Summary.Status), ("Type", issue.Summary.Type), ("Priority", issue.Summary.Priority),
            ("Assignee", issue.Summary.Assignee ?? "Unassigned"), ("Reporter", issue.Reporter), ("Project", issue.Project),
            ("Parent", issue.Parent), ("Subtasks", issue.Subtasks.Count > 0 ? string.Join(", ", issue.Subtasks) : null),
            ("Labels", issue.Labels.Count > 0 ? string.Join(", ", issue.Labels) : null), ("Start", issue.StartDate), ("End", issue.EndDate),
            ("Created", issue.Created?.ToLocalTime().ToString("yyyy-MM-dd HH:mm")), ("Updated", issue.Summary.Updated?.ToLocalTime().ToString("yyyy-MM-dd HH:mm")),
        }.Where(f => f.Item2 is { Length: > 0 }).Select(f => new JiraField(f.Item1, f.Item2!)),
    ];

    public ObservableCollection<JiraTransition> Transitions { get; } = [];

    public bool HasTransitions => Transitions.Count > 0;

    async Task RunAsync(Func<JiraClient, Task> work, string? done = null)
    {
        IsBusy = true;
        Error = null;
        try
        {
            await work(await explorer.ClientAsync(site));
            if (done is not null)
                explorer.Host.Status(done);
        }
        catch (JiraException ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public Task RefreshAsync() => RunAsync(async client =>
    {
        Issue = await client.IssueAsync(key, default);
        Transitions.Clear();
        foreach (var transition in Issue.Transitions)
            Transitions.Add(transition);
        OnPropertyChanged(nameof(HasTransitions));
    });

    [RelayCommand]
    async Task TransitionAsync(JiraTransition? transition)
    {
        if (transition is null)
            return;
        await RunAsync(client => client.TransitionAsync(key, transition.Id, default), $"Jira: {key} → {transition.ToStatus}.");
        await RefreshAsync();
    }

    [RelayCommand]
    async Task CommentAsync()
    {
        if (NewComment.Trim() is not { Length: > 0 } text)
            return;
        await RunAsync(client => client.CommentAsync(key, text, default), $"Jira: commented on {key}.");
        if (Error is null)
            NewComment = "";
        await RefreshAsync();
    }

    [RelayCommand]
    async Task AssignToMeAsync()
    {
        await RunAsync(async client => await client.AssignAsync(key, await explorer.MeAsync(site), default), $"Jira: {key} is yours.");
        await RefreshAsync();
    }

    [RelayCommand]
    void OpenInBrowser() => explorer.Host.OpenUrl(site.BrowseUrl(key));

    /// <summary>The issue as Claude's task: Claude reads it with its Jira tools (jira_issue), through Karvon.</summary>
    [RelayCommand]
    void AskClaude() => explorer.Host.AskClaude($"Work on the Jira issue {key} ({site.Name}): read it with jira_issue first. ");
}

/// <summary>A field of an issue's page: its name and what it is.</summary>
public sealed record JiraField(string Name, string Value);

/// <summary>A JQL query's page: the query, and the issues it finds (opened by a double click).</summary>
public sealed partial class JiraSearchPageViewModel(JiraExplorerViewModel explorer, JiraSite site) : ObservableObject
{
    public JiraSite Site => site;

    [ObservableProperty]
    public partial string Jql { get; set; } = "assignee = currentUser() AND statusCategory != Done ORDER BY updated DESC";

    public ObservableCollection<JiraIssueSummary> Results { get; } = [];

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial string? Message { get; private set; }

    [ObservableProperty]
    public partial JiraIssueSummary? Selected { get; set; }

    [RelayCommand]
    public async Task SearchAsync()
    {
        IsBusy = true;
        Message = null;
        try
        {
            var found = await (await explorer.ClientAsync(site)).SearchAsync(Jql, 100, default);
            Results.Clear();
            foreach (var issue in found)
                Results.Add(issue);
            Message = found.Count == 0 ? "No issues." : $"{found.Count} issue(s).";
        }
        catch (JiraException ex)
        {
            Message = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    void Open(JiraIssueSummary? issue)
    {
        if ((issue ?? Selected) is { } chosen)
            explorer.OpenIssue(site, chosen.Key);
    }
}

/// <summary>A board's column: its name and its issues (the first of them, when it has more).</summary>
public sealed class JiraColumn(JiraBoardColumn column)
{
    public JiraBoardColumn Column => column;

    public string Name => column.Name;

    public IReadOnlyList<string> StatusIds => column.StatusIds;

    public ObservableCollection<JiraIssueSummary> Cards { get; } = [];

    /// <summary>How many issues the column has, those not shown too.</summary>
    public int Total { get; set; }

    public string Header => Total > Cards.Count ? $"{Name}  {Cards.Count} of {Total}" : $"{Name}  {Cards.Count}";
}

/// <summary>
/// A board's page: its columns (as the board has them) with their issues, of its active sprint (a scrum board's) or all
/// of them (kanban's); an issue moved to another column by the transition that goes to that column's status.
/// </summary>
public sealed partial class JiraBoardPageViewModel(JiraExplorerViewModel explorer, JiraSite site, JiraBoard board) : ObservableObject
{
    public JiraSite Site => site;

    public JiraBoard Board => board;

    public ObservableCollection<JiraColumn> Columns { get; } = [];

    public ObservableCollection<JiraSprint> Sprints { get; } = [];

    /// <summary>A scrum board has sprints to choose from; a kanban board none.</summary>
    public bool HasSprints => Sprints.Count > 0;

    [ObservableProperty]
    public partial JiraSprint? Sprint { get; set; }

    bool _loading;

    partial void OnSprintChanged(JiraSprint? value)
    {
        if (!_loading)
            _ = LoadCardsAsync();
    }

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial string? Error { get; private set; }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsBusy = true;
        Error = null;
        try
        {
            var client = await explorer.ClientAsync(site);
            (var columns, _subQuery) = await client.ColumnsAsync(board.Id, default);
            var sprints = await client.SprintsAsync(board.Id, default);
            _loading = true;
            Columns.Clear();
            foreach (var column in columns)
                Columns.Add(new JiraColumn(column));
            Sprints.Clear();
            foreach (var sprint in sprints)
                Sprints.Add(sprint);
            OnPropertyChanged(nameof(HasSprints));
            Sprint = sprints.FirstOrDefault(s => s.State == "active") ?? sprints.FirstOrDefault();
            _loading = false;
            await LoadCardsAsync();
        }
        catch (JiraException ex)
        {
            Error = ex.Message;
        }
        finally
        {
            _loading = false;
            IsBusy = false;
        }
    }

    /// <summary>The cards a column shows at most (its header says how many more it has).</summary>
    public const int CardsPerColumn = 100;

    string? _subQuery;

    async Task LoadCardsAsync()
    {
        try
        {
            // Each column by itself; the last (done) one's latest first, as Jira's board shows them.
            var client = await explorer.ClientAsync(site);
            var columns = Columns.ToList();
            var read = await Task.WhenAll(columns.Select((c, i) =>
                client.ColumnIssuesAsync(board.Id, Sprint?.Id, c.Column, _subQuery, latestFirst: i == columns.Count - 1, CardsPerColumn, default)));
            for (var i = 0; i < columns.Count; i++)
            {
                columns[i].Cards.Clear();
                foreach (var issue in read[i].Issues)
                    columns[i].Cards.Add(issue);
                columns[i].Total = read[i].Total;
            }
            // The headers' counts.
            Columns.Clear();
            foreach (var column in columns)
                Columns.Add(column);
        }
        catch (JiraException ex)
        {
            Error = ex.Message;
        }
    }

    /// <summary>An issue to another column: the transition (of those it has now) that goes to one of the column's statuses.</summary>
    public async Task<bool> MoveAsync(JiraIssueSummary issue, JiraColumn column)
    {
        Error = null;
        try
        {
            var client = await explorer.ClientAsync(site);
            var transitions = await client.TransitionsAsync(issue.Key, default);
            if (transitions.FirstOrDefault(t => column.StatusIds.Contains(t.ToStatusId)) is not { } transition)
            {
                Error = $"{issue.Key} cannot go to {column.Name} from {issue.Status}: its workflow has no such transition now.";
                return false;
            }
            await client.TransitionAsync(issue.Key, transition.Id, default);
            explorer.Host.Status($"Jira: {issue.Key} → {transition.ToStatus}.");
            await LoadCardsAsync();
            return true;
        }
        catch (JiraException ex)
        {
            Error = ex.Message;
            return false;
        }
    }

    [RelayCommand]
    void Open(JiraIssueSummary? issue)
    {
        if (issue is not null)
            explorer.OpenIssue(site, issue.Key);
    }
}
