using System.Collections.Specialized;
using Bengine.Introspection.Servers;
using VSL.Database.ViewModels;
using DbDocument = VSL.Database.ViewModels.DocumentViewModel;

namespace VSL.ViewModels;

/// <summary>
/// The Database Explorer (Bengine Data Studio's explorer, from the bengine submodule): its data sources are Studio's own
/// (~/.config/bengine/servers.json), and its documents open as VSL document tabs. They stay open when a solution closes.
/// </summary>
public partial class MainViewModel
{
    ExplorerViewModel? _database;
    readonly Dictionary<DbDocument, DatabaseDocument> _databaseTabs = [];
    bool _syncingDatabaseTab;

    /// <summary>The explorer, made the first time the Database Explorer shows.</summary>
    public ExplorerViewModel Database => _database ??= CreateDatabase();

    public bool HasDatabase => _database is not null;

    /// <summary>The data sources (Bengine Data Studio's list; BENGINE_SERVERS_FILE points it elsewhere).</summary>
    public ServerStore DatabaseServers => _databaseServers ??= new ServerStore();

    ServerStore? _databaseServers;

    ExplorerViewModel CreateDatabase()
    {
        var explorer = new ExplorerViewModel(DatabaseServers);
        explorer.Tabs.CollectionChanged += OnDatabaseTabsChanged;
        explorer.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ExplorerViewModel.ActiveTab) && !_syncingDatabaseTab && explorer.ActiveTab is { } tab
                && _databaseTabs.TryGetValue(tab, out var document))
                ActiveDocument = document;
        };
        foreach (var tab in explorer.Tabs)
            AddDatabaseTab(tab);
        return explorer;
    }

    void OnDatabaseTabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var gone in _databaseTabs.Keys.Where(t => !Database.Tabs.Contains(t)).ToList())
                RemoveDatabaseTab(gone);
            foreach (var tab in Database.Tabs.Where(t => !_databaseTabs.ContainsKey(t)).ToList())
                AddDatabaseTab(tab);
            return;
        }
        foreach (DbDocument tab in e.OldItems ?? Array.Empty<DbDocument>())
            RemoveDatabaseTab(tab);
        foreach (DbDocument tab in e.NewItems ?? Array.Empty<DbDocument>())
            AddDatabaseTab(tab);
    }

    void AddDatabaseTab(DbDocument tab)
    {
        if (_databaseTabs.ContainsKey(tab))
            return;
        var document = new DatabaseDocument(tab);
        _databaseTabs[tab] = document;
        Documents.Add(document);
        if (Database.ActiveTab == tab)
            ActiveDocument = document;
    }

    void RemoveDatabaseTab(DbDocument tab)
    {
        if (!_databaseTabs.Remove(tab, out var document))
            return;
        Documents.Remove(document);
        document.Dispose();
    }

    /// <summary>A database tab that comes to the front is the explorer's active document too.</summary>
    void SyncDatabaseActiveTab(DocumentTab? value)
    {
        if (value is not DatabaseDocument document || _database is not { } explorer || explorer.ActiveTab == document.Inner)
            return;
        _syncingDatabaseTab = true;
        try
        {
            explorer.ActiveTab = document.Inner;
        }
        finally
        {
            _syncingDatabaseTab = false;
        }
    }

    /// <summary>Closing a database tab closes its document in the explorer (which may ask first); the tab follows.</summary>
    static bool CloseDatabaseDocument(DatabaseDocument document)
    {
        if (!document.Inner.CanClose)
            return false;
        document.Inner.Close?.Invoke();
        return true;
    }
}
