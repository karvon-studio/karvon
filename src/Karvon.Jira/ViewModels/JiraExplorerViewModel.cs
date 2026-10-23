using System.Collections.ObjectModel;
using Avalonia.Threading;
using Bengine.Introspection.Servers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Karvon.Ui;
using Karvon.ViewModels;

namespace Karvon.Jira.ViewModels;

/// <summary>What the Jira explorer needs of the window.</summary>
public interface IJiraHost
{
    /// <summary>Karvon's settings folder (jira.json, the sites without their secrets).</summary>
    string ConfigDirectory { get; }

    IEnumerable<DocumentTab> Documents { get; }

    void ShowDocument(DocumentTab document);

    void OpenUrl(string url);

    /// <summary>A prompt for Claude Code (an issue to work on).</summary>
    void AskClaude(string prompt);

    void Status(string text);
}

public enum JiraNodeKind
{
    Site,
    /// <summary>A JQL query's issues (Assigned to me, a favourite filter).</summary>
    Query,
    /// <summary>What a site has (its filters, its boards), read when it opens.</summary>
    Folder,
    Board,
    Issue,
    /// <summary>Loading..., an error, nothing.</summary>
    Message,
}

/// <summary>A row of the Jira explorer: what it is, and what is under it, read the first time it opens.</summary>
public sealed partial class JiraNode(JiraNodeKind kind, string title, string? detail = null) : ObservableObject
{
    static readonly JiraNode Placeholder = new(JiraNodeKind.Message, "Loading...");

    public JiraNodeKind Kind => kind;

    public string Title => title;

    public string? Detail => detail;

    public JiraSite? Site { get; init; }

    public string? Jql { get; init; }

    public JiraIssueSummary? Issue { get; init; }

    public JiraBoard? Board { get; init; }

    /// <summary>What reads its children (null: they are as they are).</summary>
    public Func<Task<IReadOnlyList<JiraNode>>>? Load { get; init; }

    public ObservableCollection<JiraNode> Children { get; } = [];

    public bool IsSite => kind == JiraNodeKind.Site;

    public bool IsIssue => kind == JiraNodeKind.Issue;

    public bool IsMessage => kind == JiraNodeKind.Message;

    /// <summary>The status category of an issue (new, indeterminate, done), its dot's color.</summary>
    public string? StatusCategory => Issue?.StatusCategory;

    bool _loaded;

    /// <summary>A node with children to read shows its expander (a placeholder until it opens).</summary>
    public JiraNode WithPlaceholder()
    {
        if (Load is not null)
            Children.Add(Placeholder);
        return this;
    }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && !_loaded)
            _ = ReloadAsync();
    }

    /// <summary>Its children read (again).</summary>
    public async Task ReloadAsync()
    {
        if (Load is null)
            return;
        _loaded = true;
        Children.Clear();
        Children.Add(new JiraNode(JiraNodeKind.Message, "Loading..."));
        IReadOnlyList<JiraNode> children;
        try
        {
            children = await Load();
        }
        catch (JiraException ex)
        {
            children = [new JiraNode(JiraNodeKind.Message, ex.Message)];
        }
        Children.Clear();
        foreach (var child in children.Count > 0 ? children : [new JiraNode(JiraNodeKind.Message, "Nothing here.")])
            Children.Add(child);
    }
}

/// <summary>An issue's page in the document well.</summary>
public sealed class JiraIssueDocument(JiraIssuePageViewModel page) : DocumentTab
{
    public JiraIssuePageViewModel Page => page;

    public override string Key => $"jira:{page.Site.Id}:{page.IssueKey}";

    public override string Title => page.IssueKey;

    public override string ToolTip => page.Issue is { } issue ? $"{issue.Key}: {issue.Summary.Summary}" : page.IssueKey;
}

/// <summary>A JQL search's page.</summary>
public sealed class JiraSearchDocument(JiraSearchPageViewModel page) : DocumentTab
{
    public JiraSearchPageViewModel Page => page;

    public override string Key => $"jira-search:{page.Site.Id}";

    public override string Title => $"Jira: {page.Site.Name}";
}

/// <summary>A board's page: its columns and their issues.</summary>
public sealed class JiraBoardDocument(JiraBoardPageViewModel page) : DocumentTab
{
    public JiraBoardPageViewModel Page => page;

    public override string Key => $"jira-board:{page.Site.Id}:{page.Board.Id}";

    public override string Title => page.Board.Name;
}

/// <summary>
/// The Jira tool window: the sites added in Karvon (Cloud, Server and Data Center) and, under each, the user's issues,
/// those they reported, their favourite filters and the boards. An issue opens as a page (its fields, description,
/// comments and transitions), a board as its columns, a search as a JQL query's issues. A site's token or password is
/// the keyring's (or this session's), asked over the window when Karvon has none; Claude's Jira tools go through here and
/// never see it.
/// </summary>
public sealed partial class JiraExplorerViewModel : ObservableObject, IDisposable
{
    readonly Func<JiraSite, string, JiraClient> _clients;
    readonly Dictionary<string, JiraClient> _open = new(StringComparer.Ordinal);
    readonly Dictionary<string, JiraUser> _me = new(StringComparer.Ordinal);

    public JiraExplorerViewModel(IJiraHost host, ICredentialStore? keyring = null, Func<JiraSite, string, JiraClient>? clients = null)
    {
        Host = host;
        List = new JiraSiteList(Path.Combine(host.ConfigDirectory, "jira.json"));
        Secrets = new JiraSecrets(keyring);
        _clients = clients ?? ((site, secret) => new JiraClient(site, secret));
        Refresh();
    }

    internal IJiraHost Host { get; }

    internal JiraSiteList List { get; }

    internal JiraSecrets Secrets { get; }

    public ObservableCollection<JiraNode> Sites { get; } = [];

    public IReadOnlyList<JiraSite> AllSites => List.Sites;

    public bool IsEmpty => Sites.Count == 0;

    /// <summary>The dialog over the window (a site, a secret, a new issue), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDialog))]
    public partial OverlayDialog? Dialog { get; set; }

    public bool HasDialog => Dialog is not null;

    [ObservableProperty]
    public partial JiraNode? Selected { get; set; }

    /// <summary>The sites again, as jira.json has them.</summary>
    [RelayCommand]
    public void Refresh()
    {
        Sites.Clear();
        foreach (var site in List.Sites.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase))
            Sites.Add(SiteNode(site));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(AllSites));
    }

    JiraNode SiteNode(JiraSite site)
    {
        var node = new JiraNode(JiraNodeKind.Site, site.Name, new Uri(site.Url).Host) { Site = site };
        node.Children.Add(Query(site, "Assigned to me", "assignee = currentUser() AND statusCategory != Done ORDER BY updated DESC"));
        node.Children.Add(Query(site, "Reported by me", "reporter = currentUser() ORDER BY updated DESC"));
        node.Children.Add(new JiraNode(JiraNodeKind.Folder, "Filters")
        {
            Site = site,
            Load = async () => [.. (await (await ClientAsync(site)).FavouriteFiltersAsync(default)).Select(f => Query(site, f.Name, f.Jql))],
        }.WithPlaceholder());
        node.Children.Add(new JiraNode(JiraNodeKind.Folder, "Boards")
        {
            Site = site,
            Load = async () => [.. (await (await ClientAsync(site)).BoardsAsync(null, default)).Select(b => new JiraNode(JiraNodeKind.Board, b.Name, b.Type) { Site = site, Board = b })],
        }.WithPlaceholder());
        node.IsExpanded = true;
        return node;
    }

    JiraNode Query(JiraSite site, string title, string jql) => new JiraNode(JiraNodeKind.Query, title) { Site = site, Jql = jql, Load = () => IssuesAsync(site, jql) }.WithPlaceholder();

    async Task<IReadOnlyList<JiraNode>> IssuesAsync(JiraSite site, string jql) =>
        [.. (await (await ClientAsync(site)).SearchAsync(jql, 50, default)).Select(i => new JiraNode(JiraNodeKind.Issue, $"{i.Key} {i.Summary}", i.Status) { Site = site, Issue = i })];

    /// <summary>
    /// The site's client, signed in with its secret: the keyring's or this session's, else asked over the window (null when
    /// the user said no).
    /// </summary>
    public async Task<JiraClient> ClientAsync(JiraSite site)
    {
        if (_open.TryGetValue(site.Id, out var open))
            return open;
        var secret = Secrets.Get(site.Id);
        if (secret is null)
        {
            if (await ShowAsync(new JiraSecretDialog(site, Secrets.HasKeyring)) is not { } asked)
                throw new JiraException($"Not signed in to {site.Name}: no token or password was given.");
            Secrets.Set(site, asked.Secret, asked.Keep);
            secret = asked.Secret;
        }
        return _open[site.Id] = _clients(site, secret);
    }

    /// <summary>Who the site signs in as (once a session).</summary>
    public async Task<JiraUser> MeAsync(JiraSite site)
    {
        if (_me.TryGetValue(site.Id, out var me))
            return me;
        return _me[site.Id] = await (await ClientAsync(site)).MyselfAsync(default);
    }

    /// <summary>A site by its name (or its id, or the only one).</summary>
    public JiraSite? Find(string? name)
    {
        var sites = List.Sites;
        if (name is null or "")
            return sites.Count == 1 ? sites[0] : null;
        return sites.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.CurrentCultureIgnoreCase) || s.Id == name
            || string.Equals(new Uri(s.Url).Host, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A dialog over the window, one at a time: it, when confirmed; else null.</summary>
    public async Task<T?> ShowAsync<T>(T dialog) where T : OverlayDialog
    {
        while (Dialog is { } open)
            await open.Result;
        Dialog = dialog;
        try
        {
            return await dialog.Result ? dialog : null;
        }
        finally
        {
            Dialog = null;
        }
    }

    /// <summary>Test connection: the site signed in to, as whom, its version.</summary>
    async Task<string> TestAsync(JiraSite site, string secret)
    {
        using var client = _clients(site, secret);
        var me = await client.MyselfAsync(default);
        var (title, version, cloud) = await client.ServerInfoAsync(default);
        if (cloud != site.IsCloud)
            throw new JiraException(cloud ? $"{site.Url} is Jira Cloud: it signs in with an email and an API token." : $"{site.Url} is not Jira Cloud: it signs in with a personal access token or a password.");
        return $"Signed in as {me.DisplayName} ({title}{(version.Length > 0 ? " " + version : "")}).";
    }

    [RelayCommand]
    async Task AddSiteAsync()
    {
        if (await ShowAsync(new JiraSiteDialog(null, Secrets.HasKeyring, false, TestAsync)) is not { } form)
            return;
        Save(form);
        Host.Status($"Jira: {form.Site.Name} added.");
    }

    [RelayCommand]
    async Task EditSiteAsync(JiraNode? node)
    {
        if (node?.Site is not { } site)
            return;
        var secret = Secrets.Get(site.Id);
        if (await ShowAsync(new JiraSiteDialog(site, Secrets.HasKeyring, secret is not null, TestAsync) { KnownSecret = () => Secrets.Get(site.Id) }) is not { } form)
            return;
        Save(form);
    }

    void Save(JiraSiteDialog form)
    {
        var site = form.Site;
        List.Save(site);
        if (form.Secret.Length > 0)
            Secrets.Set(site, form.Secret, form.Keep);
        Forget(site.Id);
        Refresh();
    }

    void Forget(string site)
    {
        if (_open.Remove(site, out var client))
            client.Dispose();
        _me.Remove(site);
    }

    [RelayCommand]
    async Task RemoveSiteAsync(JiraNode? node)
    {
        if (node?.Site is not { } site
            || await ShowAsync(new ConfirmDialog($"Remove {site.Name}?", $"Karvon forgets {site.Url} and its token or password. Jira itself does not change.", "Remove", true)) is null)
            return;
        List.Remove(site.Id);
        Secrets.Forget(site.Id);
        Forget(site.Id);
        Refresh();
    }

    /// <summary>A row opened: an issue's page, a board's columns.</summary>
    [RelayCommand]
    public void Open(JiraNode? node)
    {
        switch (node)
        {
            case { Kind: JiraNodeKind.Issue, Site: { } site, Issue: { } issue }:
                OpenIssue(site, issue.Key);
                break;
            case { Kind: JiraNodeKind.Board, Site: { } site, Board: { } board }:
                OpenBoard(site, board);
                break;
            case { Kind: JiraNodeKind.Query, Site: { } site, Jql: { } jql }:
                OpenSearch(site, jql);
                break;
        }
    }

    public JiraIssueDocument OpenIssue(JiraSite site, string key)
    {
        var document = Host.Documents.OfType<JiraIssueDocument>().FirstOrDefault(d => d.Page.Site.Id == site.Id && d.Page.IssueKey == key)
            ?? new JiraIssueDocument(new JiraIssuePageViewModel(this, site, key));
        Host.ShowDocument(document);
        _ = document.Page.RefreshCommand.ExecuteAsync(null);
        return document;
    }

    public JiraSearchDocument OpenSearch(JiraSite site, string? jql = null)
    {
        var document = Host.Documents.OfType<JiraSearchDocument>().FirstOrDefault(d => d.Page.Site.Id == site.Id)
            ?? new JiraSearchDocument(new JiraSearchPageViewModel(this, site));
        Host.ShowDocument(document);
        if (jql is not null)
            document.Page.Jql = jql;
        _ = document.Page.SearchCommand.ExecuteAsync(null);
        return document;
    }

    public JiraBoardDocument OpenBoard(JiraSite site, JiraBoard board)
    {
        var document = Host.Documents.OfType<JiraBoardDocument>().FirstOrDefault(d => d.Page.Site.Id == site.Id && d.Page.Board.Id == board.Id)
            ?? new JiraBoardDocument(new JiraBoardPageViewModel(this, site, board));
        Host.ShowDocument(document);
        _ = document.Page.RefreshCommand.ExecuteAsync(null);
        return document;
    }

    [RelayCommand]
    void Search(JiraNode? node)
    {
        if ((node?.Site ?? Selected?.Site ?? Find(null)) is { } site)
            OpenSearch(site);
    }

    /// <summary>New Issue: the form over the window, then the issue made (the signed-in user's) and opened.</summary>
    [RelayCommand]
    async Task CreateIssueAsync(JiraNode? node)
    {
        if ((node?.Site ?? Selected?.Site ?? Find(null)) is not { } site)
            return;
        try
        {
            var client = await ClientAsync(site);
            var projects = await client.ProjectsAsync(default);
            if (await ShowAsync(new JiraCreateDialog(site, projects, async key => (await client.ProjectAsync(key, default)).IssueTypes)) is not { } form)
                return;
            var key = await client.CreateAsync(form.Project!.Key, form.Type!.Id, form.Summary.Trim(), form.Description.Trim(),
                JiraCreateDialog.DateOf(form.Start), JiraCreateDialog.DateOf(form.End), default);
            Host.Status($"Jira: {key} made.");
            OpenIssue(site, key);
        }
        catch (JiraException ex)
        {
            Host.Status(ex.Message);
        }
    }

    public void Dispose()
    {
        foreach (var client in _open.Values)
            client.Dispose();
        _open.Clear();
    }

    /// <summary>Runs on the UI thread (a tool's call, a page's refresh).</summary>
    internal static Task<T> OnUi<T>(Func<Task<T>> work) => Dispatcher.UIThread.CheckAccess() ? work() : Dispatcher.UIThread.InvokeAsync(work);
}
