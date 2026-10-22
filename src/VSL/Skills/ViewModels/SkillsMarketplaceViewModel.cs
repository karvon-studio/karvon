using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace VSL.Skills.ViewModels;

/// <summary>A skill in the marketplace's list: where it is from, its price, and whether it is installed (and older).</summary>
public sealed record SkillsMarketRow(SkillSource Source, RemoteSkill Skill, bool Installed, bool Update)
{
    public string Badge => Update ? "update" : Installed ? "installed" : Skill.IsLicensed ? Skill.Price : "";

    public bool HasBadge => Badge.Length > 0;

    /// <summary>Installed or free: green; an update or a price: blue.</summary>
    public bool IsGreen => !Update && (Installed || Skill.IsLicensed && Skill.IsFree);

    public bool IsBlue => Update || !Installed && Skill.IsLicensed && !Skill.IsFree;

    public string Meta => string.Join(" · ", new[]
    {
        Skill.Source,
        Skill.Category,
        Skill.Rating is { } r ? $"★ {r:0.0}" : null,
        Skill.Installs is { } n ? $"{n} installs" : null,
    }.Where(s => s is { Length: > 0 }));
}

/// <summary>A script a skill ships, with its text (shown when asked).</summary>
public sealed partial class SkillScript(string path, string text) : ObservableObject
{
    public string Path { get; } = path;

    public string Text { get; } = text;

    [ObservableProperty]
    public partial bool IsShown { get; set; }
}

/// <summary>
/// A marketplace skill's page: what it asks for, read before it is installed (its files), or, for a skill licensed
/// through its source (Agensi's), its page there and using it in Claude Code.
/// </summary>
public sealed partial class SkillsMarketDetail(SkillsMarketplaceViewModel marketplace, SkillsMarketRow row, SkillReview review, Skill? installed, bool update,
    string? page = null) : ObservableObject
{
    public SkillsMarketRow Row { get; } = row;

    public SkillReview Review { get; } = review;

    public Skill? Installed { get; } = installed;

    public bool IsInstalled => Installed is not null;

    public bool IsNew => Installed is null && !IsLicensed;

    public bool IsLicensed => Row.Skill.IsLicensed;

    public bool HasFiles => !IsLicensed;

    /// <summary>A licensed skill's page at its source (Markdown).</summary>
    public string? Page { get; } = page;

    public string Name => Row.Skill.Name;

    public string Description => Row.Skill.Description;

    public string Meta => Row.Meta + (Row.Skill.Version.Length > 0 ? $" · version {Row.Skill.Version}" : "") + (IsLicensed ? " · " + PriceText : "");

    public string PriceText => Row.Skill.IsFree ? "free" : Row.Skill.Price;

    public string Tools => Review.AllowedTools.Count == 0 ? "none named (Claude Code's own permissions apply)" : string.Join(", ", Review.AllowedTools);

    public string Network => Review.Network.Count == 0 ? "no sign of it" : string.Join("; ", Review.Network);

    public bool UsesNetwork => Review.Network.Count > 0;

    public string Requires => Row.Skill.Requires.Count == 0 ? "nothing named" : string.Join(", ", Row.Skill.Requires);

    public string ScriptsText => Review.Scripts.Count == 0 ? "none" : string.Join(", ", Review.Scripts.Select(s => s.Path));

    public bool HasScripts => Review.Scripts.Count > 0;

    public IReadOnlyList<SkillScript> Scripts { get; } = [.. review.Scripts.Select(s => new SkillScript(s.Path, s.Text))];

    public string FilesText => string.Join(", ", Review.Files.Select(f => f.Path));

    public string InstalledText => Installed is null ? "" : $"In {Installed.Where}: {Installed.Folder}" + (update ? ". A newer version is there." : "");

    /// <summary>How a licensed skill is used: through its source's MCP server, never saved in a skills folder.</summary>
    public string LicensedText => (Row.Skill.IsFree ? "Free. " : $"{Row.Skill.Price}: Claude Code asks before it buys it (Agensi's credit, 1 credit = $1). ")
        + "Agensi's skills are licensed to run through Agensi: Claude Code gets its MCP server (you sign in once) and loads the skill when it is used, its latest version each time. It is not saved in a skills folder.";

    /// <summary>A new one goes in this folder's skills (with a solution open) or the user's.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ForUser))]
    public partial bool ForFolder { get; set; } = marketplace.Explorer.Library.Workspace is not null;

    public bool ForUser
    {
        get => !ForFolder;
        set => ForFolder = !value;
    }

    public bool CanChooseFolder => marketplace.Explorer.Library.Workspace is not null;

    public string InstallText => Installed is null ? "Install" : update ? "Update" : "Install again";

    [ObservableProperty]
    public partial string? Result { get; set; }

    [RelayCommand]
    void Install()
    {
        try
        {
            var forFolder = Installed is null ? ForFolder && CanChooseFolder : Installed.Scope == SkillScope.Project;
            var folder = SkillMarketplace.Install(marketplace.Explorer.Library, Row.Skill, Review, forFolder);
            Result = $"Installed in {folder}.";
            marketplace.Explorer.Refresh();
            _ = marketplace.LoadAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Result = "Not installed: " + ex.Message;
        }
    }

    /// <summary>Agensi's server in Claude Code (added once, with `claude mcp add`), then a prompt that loads the skill.</summary>
    [RelayCommand]
    async Task UseInClaude()
    {
        Result = "Adding Agensi to Claude Code…";
        if (await marketplace.Explorer.Host.EnsureClaudeMcpServerAsync(AgensiSkillSource.Id, AgensiSkillSource.ConnectorUrl) is { } problem)
        {
            Result = "Agensi was not added to Claude Code: " + problem;
            return;
        }
        marketplace.Explorer.Host.AskClaude($"Use the Agensi skill \"{Row.Skill.Name}\" (slug {Row.Skill.Location}): load it with get_skill and ask me what you need to get started.");
        Result = "The prompt is in Claude Code. The first time, sign in to Agensi when Claude Code asks (or Authenticate it in the Claude panel's MCP servers)."
            + (Row.Skill.IsFree ? "" : " Claude asks before it buys the skill.");
    }

    [RelayCommand]
    void OpenPage()
    {
        if (Row.Skill.Url is { } url)
            marketplace.Explorer.Host.OpenUrl(url);
    }

    [RelayCommand]
    async Task Remove()
    {
        if (Installed is not { } skill || !await marketplace.Explorer.ConfirmAsync($"Remove {skill.Name}?", $"Its folder is deleted: {skill.Folder}.", "Remove", danger: true))
            return;
        try
        {
            Directory.Delete(skill.Folder, recursive: true);
            Result = "Removed.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Result = "Not removed: " + ex.Message;
        }
        marketplace.Explorer.Refresh();
        _ = marketplace.LoadAsync();
    }
}

/// <summary>
/// The skills marketplace: the skills of the sources (GitHub repositories, teams' registries, and Agensi), chosen by
/// source, searched by what they do and filtered; a source with a big catalog (Agensi) is searched at the source. A
/// skill's page says what it asks for (the tools it allows, its scripts and their text, the network) before it is
/// installed, updated or removed; a licensed one is used in Claude Code through its source.
/// </summary>
public sealed partial class SkillsMarketplaceViewModel : ObservableObject
{
    public const string EverySource = "Every source";

    List<SkillsMarketRow> _rows = [];
    List<SkillsMarketRow> _found = [];
    CancellationTokenSource? _searching;
    string? _searchingAt;
    int _loads;
    int _opens;

    public SkillsMarketplaceViewModel(SkillsExplorerViewModel explorer)
    {
        Explorer = explorer;
        Sources = string.Join("\n", explorer.Host.Settings.SkillSources);
        Listed = LoadAsync();
    }

    internal SkillsExplorerViewModel Explorer { get; }

    /// <summary>The list has been read (tests).</summary>
    internal Task Listed { get; private set; }

    /// <summary>The search at the sources that search their own catalog has answered (tests).</summary>
    internal Task Searched { get; private set; } = Task.CompletedTask;

    /// <summary>The chosen skill's page has been read (tests).</summary>
    internal Task Opened { get; private set; } = Task.CompletedTask;

    /// <summary>One source a line: github:owner/repo[/folder][@ref], a registry's URL, or a registry on disk.</summary>
    [ObservableProperty]
    public partial string Sources { get; set; }

    [ObservableProperty]
    public partial bool IsEditingSources { get; set; }

    /// <summary>Agensi's marketplace (agensi.io) among the sources.</summary>
    public bool UsesAgensi
    {
        get => Explorer.Host.Settings.SkillsAgensi;
        set
        {
            if (value == UsesAgensi)
                return;
            Explorer.Host.Settings.SkillsAgensi = value;
            Explorer.Host.SaveSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(SourcesLabel));
            Listed = LoadAsync();
        }
    }

    IReadOnlyList<string> Specs =>
    [
        .. Explorer.Host.Settings.SkillSources,
        .. UsesAgensi && !Explorer.Host.Settings.SkillSources.Contains(AgensiSkillSource.Id, StringComparer.OrdinalIgnoreCase) ? [AgensiSkillSource.Id] : Array.Empty<string>(),
    ];

    public string SourcesLabel => Specs.Count switch
    {
        0 => "Sources: none",
        1 => "Sources: " + Specs[0],
        var n => $"Sources: {n}",
    };

    public static IReadOnlyList<string> Filters { get; } = ["All", "Installed", "Updates", "Free"];

    [ObservableProperty]
    public partial string Filter { get; set; } = "All";

    [ObservableProperty]
    public partial string Search { get; set; } = "";

    /// <summary>"Every source", then each source by its name.</summary>
    public ObservableCollection<string> SourceNames { get; } = [EverySource];

    [ObservableProperty]
    public partial string? Source { get; set; } = EverySource;

    public ObservableCollection<string> Categories { get; } = ["Every category"];

    [ObservableProperty]
    public partial string? Category { get; set; } = "Every category";

    public ObservableCollection<SkillsMarketRow> Shown { get; } = [];

    [ObservableProperty]
    public partial SkillsMarketRow? Selected { get; set; }

    /// <summary>The chosen skill's page; null while it is read, or with nothing chosen.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetail))]
    public partial SkillsMarketDetail? Detail { get; set; }

    public bool HasDetail => Detail is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string? Status { get; set; }

    public bool HasStatus => Status is { Length: > 0 };

    partial void OnSearchChanged(string value)
    {
        Show();
        Searched = SearchSourcesAsync(value);
    }

    partial void OnFilterChanged(string value) => Show();

    partial void OnSourceChanged(string? value) => Show();

    partial void OnCategoryChanged(string? value) => Show();

    partial void OnSelectedChanged(SkillsMarketRow? value) => Opened = OpenAsync(value);

    [RelayCommand]
    async Task SaveSources()
    {
        Explorer.Host.Settings.SkillSources = [.. Sources.Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0)];
        Explorer.Host.SaveSettings();
        OnPropertyChanged(nameof(SourcesLabel));
        IsEditingSources = false;
        await LoadAsync();
    }

    [RelayCommand]
    Task Refresh() => Listed = LoadAsync();

    internal async Task LoadAsync()
    {
        var load = ++_loads;
        var specs = Specs;
        Status = specs.Count == 0 ? "No sources: add one above." : "Reading the sources…";
        var (skills, problems) = await SkillMarketplace.ListAsync(specs, CancellationToken.None);
        if (load != _loads)
            return;
        _rows = [.. skills.Select(s => RowOf(s.Source, s.Skill))];
        _found = [];
        Reset(SourceNames, Source, EverySource, _rows.Select(r => r.Source.Name).Distinct(), value => Source = value);
        Reset(Categories, Category, "Every category", _rows.Select(r => r.Skill.Category).OfType<string>().Distinct().Order(StringComparer.Ordinal), value => Category = value);
        Status = specs.Count == 0 ? "No sources: add one above." : string.Join("\n", problems.Select(p => "Could not read " + p));
        Show();
        if (Search.Trim().Length > 0)
            Searched = SearchSourcesAsync(Search);
    }

    SkillsMarketRow RowOf(SkillSource source, RemoteSkill skill)
    {
        // A licensed skill is not installed as files: one of the same name in a skills folder is another skill.
        if (skill.IsLicensed)
            return new SkillsMarketRow(source, skill, false, false);
        var (installed, version, hash) = SkillMarketplace.Installed(Explorer.Library, skill);
        return new SkillsMarketRow(source, skill, installed is not null, installed is not null && SkillMarketplace.HasUpdate(skill, version, hash, null));
    }

    static void Reset(ObservableCollection<string> list, string? chosen, string all, IEnumerable<string> names, Action<string> choose)
    {
        list.Clear();
        foreach (var name in new[] { all }.Concat(names))
            list.Add(name);
        choose(chosen is not null && list.Contains(chosen) ? chosen : all);
    }

    /// <summary>The sources that search their own catalog (Agensi), a moment after the search stops changing.</summary>
    async Task SearchSourcesAsync(string query)
    {
        _searching?.Cancel();
        var searching = _searching = new CancellationTokenSource();
        var sources = _rows.Select(r => r.Source).Where(s => s.CanSearch).DistinctBy(s => s.Spec).ToList();
        if (query.Trim().Length == 0 || sources.Count == 0)
        {
            _found = [];
            Show();
            return;
        }
        _searchingAt = string.Join(", ", sources.Select(s => s.Name));
        Show();
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(400), searching.Token);
            var found = new List<SkillsMarketRow>();
            foreach (var source in sources)
                try
                {
                    found.AddRange((await source.SearchAsync(query.Trim(), searching.Token)).Select(s => RowOf(source, s)));
                }
                catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or IOException)
                {
                    Status = $"Could not search {source.Name}: {ex.Message}";
                }
            if (searching.IsCancellationRequested)
                return;
            _found = found;
            _searchingAt = null;
            Show();
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// The skills the source, the search and the filters keep, the best match first: those of a source that searches its
    /// own catalog in the order it found them.
    /// </summary>
    void Show()
    {
        var query = Search.Trim();
        var source = Source is EverySource or null ? null : Source;
        var category = Category is "Every category" or null ? null : Category;
        var listed = query.Length == 0 ? _rows : _rows.Where(r => !r.Source.CanSearch);
        var scored = listed.Select(r => (Row: r, Score: query.Length == 0 ? 1 : SkillMarketplace.Score(r.Skill, query)))
            .Concat(query.Length == 0 ? [] : _found.Select((r, i) => (Row: r, Score: 1000 - i)))
            .Where(r => r.Score > 0)
            .Where(r => source is null || r.Row.Source.Name == source)
            .Where(r => category is null || r.Row.Skill.Category == category)
            .Where(r => Filter switch
            {
                "Installed" => r.Row.Installed,
                "Updates" => r.Row.Update,
                "Free" => r.Row.Skill.IsFree,
                _ => true,
            });
        var shown = (query.Length == 0 ? scored.OrderBy(_ => 0) : scored.OrderByDescending(r => r.Score))
            .ThenByDescending(r => r.Row.Skill.Rating ?? 0).ThenBy(r => r.Row.Skill.Name, StringComparer.Ordinal)
            .Select(r => r.Row).ToList();
        var selected = Selected;
        Shown.Clear();
        foreach (var row in shown)
            Shown.Add(row);
        Selected = shown.FirstOrDefault(r => r.Skill.Name == selected?.Skill.Name && r.Source == selected.Source);
        // What the list says when it is empty: a search still under way, or nothing found.
        if (Status is "Nothing matches." || Status?.StartsWith("Searching ", StringComparison.Ordinal) == true)
            Status = null;
        if (_searchingAt is { } at && query.Length > 0)
            Status = $"Searching {at}…";
        else if (shown.Count == 0 && _rows.Count > 0 && !HasStatus)
            Status = "Nothing matches.";
    }

    /// <summary>Chooses a skill by its name (tests).</summary>
    internal void Select(string name) => Selected = Shown.FirstOrDefault(r => r.Skill.Name == name);

    async Task OpenAsync(SkillsMarketRow? row)
    {
        var open = ++_opens;
        Detail = null;
        if (row is null)
            return;
        try
        {
            if (row.Skill.IsLicensed)
            {
                // Its page at the source: what it does, what it may use (tools, hosts), what it needs.
                var page = await row.Source.DetailsAsync(row.Skill, CancellationToken.None);
                if (open != _opens)
                    return;
                var (tools, hosts) = AgensiSkillSource.PermissionsOf(page);
                page = AgensiSkillSource.WithoutPermissions(page);
                var network = hosts.Select(h => "it reaches " + h).Concat(row.Skill.Requires.Where(r => r.Contains("web", StringComparison.OrdinalIgnoreCase)).Select(r => "it needs " + r)).ToList();
                Detail = new SkillsMarketDetail(this, row, new SkillReview(tools, [], network, []), null, false, page);
                return;
            }
            var files = await row.Source.FilesAsync(row.Skill, CancellationToken.None);
            if (open != _opens)
                return;
            var review = SkillMarketplace.Review(files);
            var (installed, version, hash) = SkillMarketplace.Installed(Explorer.Library, row.Skill);
            Detail = new SkillsMarketDetail(this, row, review, installed, installed is not null && SkillMarketplace.HasUpdate(row.Skill, version, hash, review.Hash));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or UnauthorizedAccessException or InvalidOperationException)
        {
            Status = $"Could not read {row.Skill.Name}: {ex.Message}";
        }
    }
}
