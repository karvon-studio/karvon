using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace VSL.ViewModels;

/// <summary>
/// Tab groups and floating document windows, as in Visual Studio: Window > New Vertical / Horizontal Tab Group, Move to
/// Next / Previous Tab Group, Float and Dock as Tabbed Document, and tabs dragged between them. <see cref="Documents"/>
/// stays the list of every open document; each of them is in one group, which follows it as it is opened and closed.
/// </summary>
public partial class MainViewModel
{
    DocumentGroup? _placement;
    int? _placementIndex;
    bool _syncingGroups;
    DocumentGroup? _lastDockedGroup;

    /// <summary>The groups of the main window's document well: left to right, or top to bottom.</summary>
    public ObservableCollection<DocumentGroup> DocumentGroups { get; } = [];

    /// <summary>Groups in windows of their own (the view opens a window for each).</summary>
    public ObservableCollection<DocumentGroup> FloatingGroups { get; } = [];

    /// <summary>Side by side (New Vertical Tab Group) or one above the other (New Horizontal Tab Group).</summary>
    [ObservableProperty]
    public partial bool DocumentGroupsVertical { get; set; } = true;

    /// <summary>The group the active document is in.</summary>
    [ObservableProperty]
    public partial DocumentGroup ActiveGroup { get; private set; } = null!;

    IEnumerable<DocumentGroup> AllGroups => DocumentGroups.Concat(FloatingGroups);

    public DocumentGroup? GroupOf(DocumentTab doc) => AllGroups.FirstOrDefault(g => g.Tabs.Contains(doc));

    void InitializeDocumentGroups()
    {
        var first = NewGroup(floating: false);
        DocumentGroups.Add(first);
        ActiveGroup = first;
        first.IsActive = true;
        _lastDockedGroup = first;
        Documents.CollectionChanged += OnDocumentsChanged;
    }

    DocumentGroup NewGroup(bool floating)
    {
        var group = new DocumentGroup(floating);
        group.PropertyChanged += (_, e) =>
        {
            // The group's tab strip chose another tab: in the group the user works in, that is the active document.
            if (e.PropertyName == nameof(DocumentGroup.Active) && !_syncingGroups && group == ActiveGroup && group.Active is { } doc)
                ActiveDocument = doc;
        };
        return group;
    }

    /// <summary>The main window's group at this position, adding empty groups up to it (restoring a layout).</summary>
    DocumentGroup DocumentGroupAt(int index)
    {
        while (DocumentGroups.Count <= index)
            DocumentGroups.Add(NewGroup(floating: false));
        return DocumentGroups[index];
    }

    /// <summary>Documents opened while the returned scope is alive go into this group at this position.</summary>
    internal IDisposable PlaceNewDocuments(DocumentGroup group, int index)
    {
        _placement = group;
        _placementIndex = index;
        return new Scope(() =>
        {
            _placement = null;
            _placementIndex = null;
        });
    }

    sealed class Scope(Action end) : IDisposable
    {
        public void Dispose() => end();
    }

    void OnDocumentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                foreach (DocumentTab doc in e.NewItems!)
                    AddToGroup(doc);
                break;
            case NotifyCollectionChangedAction.Remove:
                foreach (DocumentTab doc in e.OldItems!)
                    RemoveFromGroup(doc);
                break;
            case NotifyCollectionChangedAction.Replace:
                foreach (DocumentTab doc in e.OldItems!)
                    RemoveFromGroup(doc);
                foreach (DocumentTab doc in e.NewItems!)
                    AddToGroup(doc);
                break;
            case NotifyCollectionChangedAction.Reset:
                ResetGroups();
                break;
        }
    }

    void AddToGroup(DocumentTab doc)
    {
        var group = _placement is { } placed && AllGroups.Contains(placed) ? placed
            : AllGroups.Contains(ActiveGroup) ? ActiveGroup : DocumentGroups[0];
        _syncingGroups = true;
        try
        {
            if (_placementIndex is { } index)
            {
                var at = Math.Clamp(index, 0, group.Tabs.Count);
                group.Tabs.Insert(at, doc);
                _placementIndex = at + 1;
            }
            else
            {
                group.Tabs.Add(doc);
            }
            group.Active ??= doc;
        }
        finally
        {
            _syncingGroups = false;
        }
    }

    void RemoveFromGroup(DocumentTab doc)
    {
        if (GroupOf(doc) is not { } group)
            return;
        var wasActive = ActiveDocument == doc;
        _syncingGroups = true;
        try
        {
            var index = group.Tabs.IndexOf(doc);
            group.Tabs.RemoveAt(index);
            if (group.Active == doc || group.Active is null)
                group.Active = group.Tabs.Count == 0 ? null : group.Tabs[Math.Clamp(index, 0, group.Tabs.Count - 1)];
        }
        finally
        {
            _syncingGroups = false;
        }
        if (group.Tabs.Count == 0)
            RemoveEmptyGroup(group);
        // Like Visual Studio: the next tab of the same group comes to the front, else the group the user moves to.
        if (wasActive)
            ActiveDocument = (AllGroups.Contains(group) ? group.Active : null) ?? ActiveGroup.Active ?? AllGroups.Select(g => g.Active).FirstOrDefault(d => d is not null);
    }

    void RemoveEmptyGroup(DocumentGroup group)
    {
        if (group.IsFloating)
            FloatingGroups.Remove(group);
        else if (DocumentGroups.Count > 1)
        {
            var index = DocumentGroups.IndexOf(group);
            DocumentGroups.Remove(group);
            if (_lastDockedGroup == group)
                _lastDockedGroup = DocumentGroups[Math.Clamp(index - 1, 0, DocumentGroups.Count - 1)];
        }
        else
            return;
        if (ActiveGroup == group)
            SetActiveGroup(_lastDockedGroup ?? DocumentGroups[0]);
    }

    void ResetGroups()
    {
        _syncingGroups = true;
        try
        {
            FloatingGroups.Clear();
            while (DocumentGroups.Count > 1)
                DocumentGroups.RemoveAt(DocumentGroups.Count - 1);
            DocumentGroups[0].Tabs.Clear();
            DocumentGroups[0].Active = null;
        }
        finally
        {
            _syncingGroups = false;
        }
        _lastDockedGroup = DocumentGroups[0];
        SetActiveGroup(DocumentGroups[0]);
    }

    void SetActiveGroup(DocumentGroup group)
    {
        if (!group.IsFloating)
            _lastDockedGroup = group;
        if (ActiveGroup == group)
            return;
        ActiveGroup.IsActive = false;
        ActiveGroup = group;
        group.IsActive = true;
    }

    /// <summary>The active document changed: its group becomes the active group and shows it.</summary>
    void SyncGroupWithActiveDocument(DocumentTab? doc)
    {
        if (_syncingGroups || doc is null || GroupOf(doc) is not { } group)
            return;
        _syncingGroups = true;
        try
        {
            SetActiveGroup(group);
            group.Active = doc;
        }
        finally
        {
            _syncingGroups = false;
        }
    }

    /// <summary>The user clicked into a group (its tabs or the document in it).</summary>
    public void ActivateGroup(DocumentGroup group)
    {
        SetActiveGroup(group);
        if (group.Active is { } doc && ActiveDocument != doc)
            ActiveDocument = doc;
    }

    /// <summary>
    /// Moves a tab into a group at a position (the end when null), or to another position in its own group. Pinned tabs
    /// stay in front of the others, as in Visual Studio.
    /// </summary>
    public void MoveToGroup(DocumentTab doc, DocumentGroup to, int? index = null)
    {
        if (GroupOf(doc) is not { } from || !AllGroups.Contains(to))
            return;
        _syncingGroups = true;
        try
        {
            var oldIndex = from.Tabs.IndexOf(doc);
            from.Tabs.RemoveAt(oldIndex);
            if (from != to && from.Active == doc)
                from.Active = from.Tabs.Count == 0 ? null : from.Tabs[Math.Clamp(oldIndex, 0, from.Tabs.Count - 1)];
            var pinned = to.Tabs.Count(t => t.IsPinned);
            var at = Math.Clamp(index ?? to.Tabs.Count, 0, to.Tabs.Count);
            at = doc.IsPinned ? Math.Min(at, pinned) : Math.Max(at, pinned);
            to.Tabs.Insert(at, doc);
            to.Active = doc;
        }
        finally
        {
            _syncingGroups = false;
        }
        if (from != to && from.Tabs.Count == 0)
            RemoveEmptyGroup(from);
        SetActiveGroup(to);
        ActiveDocument = doc;
        SaveSession();
    }

    /// <summary>VS enables a new group of one orientation while the groups are in that orientation (or there is one).</summary>
    public bool CanCreateTabGroup(DocumentTab? doc, bool vertical) =>
        doc is not null && GroupOf(doc) is { } group && (group.IsFloating || group.Tabs.Count > 1)
        && (DocumentGroups.Count == 1 || DocumentGroupsVertical == vertical);

    [RelayCommand]
    void NewVerticalTabGroup(DocumentTab? doc) => NewTabGroup(doc ?? ActiveDocument, vertical: true);

    [RelayCommand]
    void NewHorizontalTabGroup(DocumentTab? doc) => NewTabGroup(doc ?? ActiveDocument, vertical: false);

    /// <summary>A new group after the document's own (or at the end, from a floating window) with the document in it.</summary>
    public void NewTabGroup(DocumentTab? doc, bool vertical, int? position = null)
    {
        if (doc is null || GroupOf(doc) is not { } from || !CanCreateTabGroup(doc, vertical))
            return;
        DocumentGroupsVertical = vertical;
        var group = NewGroup(floating: false);
        DocumentGroups.Insert(Math.Clamp(position ?? (from.IsFloating ? DocumentGroups.Count : DocumentGroups.IndexOf(from) + 1), 0, DocumentGroups.Count), group);
        MoveToGroup(doc, group);
    }

    /// <summary>A tab dropped on a group's edge: a new group before or after that one, with the tab in it.</summary>
    public void NewTabGroupBeside(DocumentTab doc, DocumentGroup beside, bool vertical, bool after)
    {
        if (beside.IsFloating || !DocumentGroups.Contains(beside) || GroupOf(doc) is null
            || DocumentGroups.Count > 1 && DocumentGroupsVertical != vertical)
            return;
        DocumentGroupsVertical = vertical;
        var group = NewGroup(floating: false);
        DocumentGroups.Insert(DocumentGroups.IndexOf(beside) + (after ? 1 : 0), group);
        MoveToGroup(doc, group);
    }

    public bool CanMoveToTabGroup(DocumentTab? doc, int step) =>
        doc is not null && GroupOf(doc) is { IsFloating: false } group && DocumentGroups.IndexOf(group) + step is var target
        && target >= 0 && target < DocumentGroups.Count;

    [RelayCommand]
    void MoveToNextTabGroup(DocumentTab? doc) => MoveToTabGroup(doc ?? ActiveDocument, 1);

    [RelayCommand]
    void MoveToPreviousTabGroup(DocumentTab? doc) => MoveToTabGroup(doc ?? ActiveDocument, -1);

    void MoveToTabGroup(DocumentTab? doc, int step)
    {
        if (doc is not null && CanMoveToTabGroup(doc, step))
            MoveToGroup(doc, DocumentGroups[DocumentGroups.IndexOf(GroupOf(doc)!) + step]);
    }

    /// <summary>Window > Float: the document in a window of its own.</summary>
    [RelayCommand]
    public void FloatDocument(DocumentTab? doc)
    {
        doc ??= ActiveDocument;
        if (doc is null || GroupOf(doc) is not { } from || from.IsFloating && from.Tabs.Count == 1)
            return;
        var group = NewGroup(floating: true);
        FloatingGroups.Add(group);
        MoveToGroup(doc, group);
    }

    /// <summary>Window > Dock as Tabbed Document: back into the group of the main window the user worked in last.</summary>
    [RelayCommand]
    public void DockDocument(DocumentTab? doc)
    {
        doc ??= ActiveDocument;
        if (doc is not null && GroupOf(doc) is { IsFloating: true })
            MoveToGroup(doc, _lastDockedGroup is { } last && DocumentGroups.Contains(last) ? last : DocumentGroups[0]);
    }

    /// <summary>Closing a floating window closes its documents (asking to save each); false when the user cancelled.</summary>
    public async Task<bool> CloseGroupAsync(DocumentGroup group)
    {
        await CloseDocumentsAsync(group.Tabs.ToList());
        return group.Tabs.Count == 0;
    }
}
