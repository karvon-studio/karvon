using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Karvon.ViewModels;

namespace Karvon.Views;

/// <summary>
/// Tool window sets: tool windows of one kind as one tab of their group (one icon at the sides), each of them a tab at
/// its bottom, as Visual Studio's panes have theirs: Git (Git Changes, Pull Requests, Pipelines) and Testing (Test
/// Explorer, Code Coverage Results). A set docks, floats and folds as one tool window; showing one of its tool windows
/// shows the set with that tab in front. A set with fewer than two of its tool windows (their modules off) is not made:
/// the one there stays a tool window of its own.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// The sets: their ids, names and tool windows in their order (the modules' by their ids: Pull Requests, Pipelines);
    /// a module's that says it is in a set and is not named comes after these.
    /// </summary>
    static readonly (string Id, string Title, string[] Members)[] ToolSets =
    [
        (ToolWindows.Git, "Git", [ToolWindows.GitChanges, "PullRequests", "Pipelines"]),
        (ToolWindows.Testing, "Testing", [ToolWindows.TestExplorer, ToolWindows.CoverageResults]),
    ];

    /// <summary>The sets made: each one's tab, and the tab control of its tool windows in it.</summary>
    readonly Dictionary<string, (TabItem Item, TabControl Members)> _toolSets = [];

    /// <summary>The set a tool window is a tab of, by the tool window's id.</summary>
    readonly Dictionary<string, string> _setOf = [];

    /// <summary>The sets, made of the tool windows there are (the core's, then the modules' in their order), each where its first one was.</summary>
    void BuildToolSets(MainViewModel vm)
    {
        var modules = vm.Modules.SelectMany(m => m.ToolWindows).ToList();
        var theme = this.FindResource("VsToolTab") as ControlTheme;
        foreach (var (setId, title, named) in ToolSets)
        {
            string[] ids = [.. named, .. modules.Where(t => t.Set == setId && !named.Contains(t.Id)).Select(t => t.Id)];
            var members = ids.Select(id => ToolTabs.FirstOrDefault(t => (string?)t.Tag == id)).OfType<TabItem>().ToList();
            if (members.Count < 2 || _toolSets.ContainsKey(setId))
                continue;
            var group = ToolGroups.First(g => g.Items.Contains(members[0]));
            var index = group.Items.IndexOf(members[0]);
            var tabs = new TabControl { TabStripPlacement = Dock.Bottom, Padding = new Thickness(0), ItemContainerTheme = theme };
            tabs.Classes.Add("toolSet");
            foreach (var member in members)
            {
                ToolGroups.First(g => g.Items.Contains(member)).Items.Remove(member);
                DetachToolTab(member);
                member.Theme = theme;
                tabs.Items.Add(member);
                _setOf[(string)member.Tag!] = setId;
            }
            tabs.SelectedIndex = 0;
            var item = new TabItem { Header = title, Tag = setId, Content = tabs };
            group.Items.Insert(Math.Min(index, group.Items.Count), item);
            AttachToolTab(item);
            _toolSets[setId] = (item, tabs);
            if (group.SelectedItem is null)
                group.SelectedItem = item;

            // Its icon counts what its tool windows count (Git Changes' changes).
            void Count() => ToolWindowTabs.SetBadge(item, members.Sum(ToolWindowTabs.GetBadge));
            foreach (var member in members)
                member.PropertyChanged += (_, e) =>
                {
                    if (e.Property == ToolWindowTabs.BadgeProperty)
                        Count();
                };
            Count();
            // A module's tool window gets its view when it shows: the set in front, and it in front in the set.
            item.PropertyChanged += (_, e) =>
            {
                if (e.Property == TabItem.IsSelectedProperty)
                    FillSetFront(setId);
            };
            tabs.SelectionChanged += (_, e) =>
            {
                if (e.Source == tabs)
                    FillSetFront(setId);
            };
        }
    }

    void FillSetFront(string setId)
    {
        if (_toolSets[setId].Members.SelectedItem is TabItem { Tag: string id })
            FillModuleToolWindow(id);
    }

    /// <summary>A tab of a set is not dragged to dock, nor has the docking menu: its set has them.</summary>
    void DetachToolTab(TabItem item)
    {
        item.RemoveHandler(PointerPressedEvent, OnToolTabPressed);
        item.RemoveHandler(PointerMovedEvent, OnToolTabMoved);
        item.RemoveHandler(PointerReleasedEvent, OnToolTabReleased);
        item.ContextRequested -= OnToolTabContextRequested;
    }

    /// <summary>The id a tool window is docked by: its set's, or its own.</summary>
    string DockedId(string toolWindow) => _setOf.GetValueOrDefault(toolWindow) ?? toolWindow;

    /// <summary>The tab a tool window is docked as: its own, or its set's.</summary>
    TabItem? DockedTab(string toolWindow)
    {
        var id = DockedId(toolWindow);
        return ToolTabs.FirstOrDefault(t => (string?)t.Tag == id);
    }

    /// <summary>The ids a docked tab stands for: its own, and its set's tool windows'.</summary>
    IEnumerable<string> IdsOf(TabItem item) =>
        item.Tag is not string id ? [] : _toolSets.TryGetValue(id, out var set) ? [id, .. set.Members.Items.OfType<TabItem>().Select(t => t.Tag).OfType<string>()] : [id];

    /// <summary>The tool window in front of a group: its tab's, or, for a set, the set's tab in front.</summary>
    string? FrontOf(TabControl group) =>
        group.SelectedItem is not TabItem { Tag: string id } ? null
            : _toolSets.TryGetValue(id, out var set) && set.Members.SelectedItem is TabItem { Tag: string member } ? member : id;

    /// <summary>A tool window of a set in front in it (its set itself put in front by the caller).</summary>
    void SelectInSet(string toolWindow)
    {
        if (_setOf.TryGetValue(toolWindow, out var setId)
            && _toolSets[setId].Members.Items.OfType<TabItem>().FirstOrDefault(t => (string?)t.Tag == toolWindow) is { } tab)
            _toolSets[setId].Members.SelectedItem = tab;
    }

    /// <summary>
    /// Whether a module's tool window shows: in front of its group, or of its set in front of its group (by the set's
    /// choice: its tabs know they are chosen only once the set has shown).
    /// </summary>
    bool IsInFront(TabItem item) =>
        item.Tag is string id && _setOf.TryGetValue(id, out var setId)
            ? _toolSets[setId].Item.IsSelected && _toolSets[setId].Members.SelectedItem == item
            : item.IsSelected;
}
