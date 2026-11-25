using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Karvon.Modules;

namespace Karvon.ViewModels;

/// <summary>Settings under a heading, as they show: a group of the page chosen, or of any page with what a search found.</summary>
public sealed class SettingsSection(string title, IReadOnlyList<Setting> settings) : ObservableObject
{
    public string Title => title;

    public IReadOnlyList<Setting> Settings => settings;

    /// <summary>None of its settings applies now: no heading over nothing.</summary>
    public bool IsShown => settings.Any(s => s.IsShown);

    public void Refresh() => OnPropertyChanged(nameof(IsShown));
}

/// <summary>
/// Settings (Tools > Settings, Ctrl+,): every setting of Karvon's and its modules' in one place, in pages, with a search of
/// all of them. A change is kept at once; what is changed elsewhere meanwhile (a menu, a module's page, another machine
/// through Settings Sync) shows here.
/// </summary>
public sealed partial class SettingsDocument : DocumentTab
{
    public const string DocumentKey = "karvon:settings";

    readonly Services.Settings _settings;
    bool _refreshing;

    public SettingsDocument(Services.Settings settings, IEnumerable<SettingsPage> pages)
    {
        _settings = settings;
        Pages = SettingsPage.Merge(pages);
        foreach (var setting in AllSettings)
            setting.Changed += RefreshAll;
        _settings.Saved += OnSaved;
        SelectedPage = Pages.FirstOrDefault();
    }

    public override string Key => DocumentKey;

    public override string Title => "Settings";

    public override string ToolTip => "Settings (Ctrl+,)";

    public IReadOnlyList<SettingsPage> Pages { get; }

    IEnumerable<Setting> AllSettings => Pages.SelectMany(p => p.Groups).SelectMany(g => g.Settings);

    [ObservableProperty]
    public partial SettingsPage? SelectedPage { get; set; }

    [ObservableProperty]
    public partial string Search { get; set; } = "";

    /// <summary>The page's name, or what is searched for.</summary>
    [ObservableProperty]
    public partial string Heading { get; private set; } = "";

    public ObservableCollection<SettingsSection> Sections { get; } = [];

    [ObservableProperty]
    public partial bool HasNoResults { get; private set; }

    /// <summary>A page chosen: it shows, a search done with.</summary>
    partial void OnSelectedPageChanged(SettingsPage? value)
    {
        if (value is null)
            return;
        if (Search.Length > 0)
            Search = "";
        else
            Show();
    }

    partial void OnSearchChanged(string value) => Show();

    /// <summary>The page chosen; with a search, what it finds in all of them, under their page's and group's names (no page is chosen then).</summary>
    void Show()
    {
        var words = Search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Sections.Clear();
        if (words.Length == 0)
        {
            SelectedPage ??= _page ?? Pages.FirstOrDefault();
            _page = SelectedPage;
            Heading = SelectedPage?.Title ?? "";
            foreach (var group in SelectedPage?.Groups ?? [])
                Sections.Add(new SettingsSection(group.Title, group.Settings));
        }
        else
        {
            _page = SelectedPage ?? _page;
            SelectedPage = null;
            Heading = "Search results";
            foreach (var page in Pages)
                foreach (var group in page.Groups)
                    if (group.Settings.Where(s => s.IsShown && s.Matches(words, $"{page.Title} {group.Title}")).ToList() is { Count: > 0 } found)
                        Sections.Add(new SettingsSection($"{page.Title}: {group.Title}", found));
        }
        HasNoResults = Sections.Count == 0;
    }

    /// <summary>The page that shows again when a search is done with.</summary>
    SettingsPage? _page;

    /// <summary>A page chosen by its id (another place's "Settings..."), or the one that shows.</summary>
    public void ShowPage(string? id)
    {
        if (id is not null && Pages.FirstOrDefault(p => p.Id == id) is { } page)
            SelectedPage = page;
    }

    void OnSaved() => Dispatcher.UIThread.Post(RefreshAll);

    void RefreshAll()
    {
        // A setting read again may say it changed (a combo box's): once is enough.
        if (_refreshing)
            return;
        _refreshing = true;
        try
        {
            foreach (var setting in AllSettings)
                setting.Refresh();
            foreach (var section in Sections)
                section.Refresh();
        }
        finally
        {
            _refreshing = false;
        }
    }

    public override void Dispose() => _settings.Saved -= OnSaved;
}
