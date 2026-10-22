using System.Collections.Specialized;
using Bengine.Introspection.Servers;
using Karvon.Database.ViewModels;
using Karvon.Services;
using DbDocument = Karvon.Database.ViewModels.DocumentViewModel;

namespace Karvon.ViewModels;

/// <summary>
/// The Database Explorer (Bengine Data Studio's explorer, from the bengine submodule). Each solution has one of its own,
/// kept in .vs/&lt;Solution&gt;/Karvon/Database: its data sources, query history, settings, SQL files and workspace (the
/// tree and the tabs). Nothing of it shows in another solution: when the solution closes, its explorer saves its workspace
/// and closes with it. With no solution open it is Bengine Data Studio's list (~/.config/bengine/servers.json) and a
/// workspace of Karvon's own. Its documents open as Karvon document tabs.
/// </summary>
public partial class MainViewModel
{
    ExplorerViewModel? _database;
    ServerStore? _databaseServers;
    readonly Dictionary<DbDocument, DatabaseDocument> _databaseTabs = [];
    bool _syncingDatabaseTab;

    /// <summary>The open solution's explorer, made the first time the Database Explorer shows.</summary>
    public ExplorerViewModel Database => _database ??= CreateDatabase();

    /// <summary>The open solution's data sources (with no solution: Studio's list; BENGINE_SERVERS_FILE points it elsewhere).</summary>
    public ServerStore DatabaseServers => _databaseServers ??= DatabaseDirectory is { } directory
        ? new ServerStore(Path.Combine(directory, "servers.json"), CredentialStores.System)
        : new ServerStore();

    /// <summary>The folder of the open solution's Database Explorer (beside its state.json); null with no solution open.</summary>
    public string? DatabaseDirectory =>
        Solution is { } solution ? Path.Combine(Path.GetDirectoryName(SolutionState.FileFor(solution.FilePath))!, "Database") : null;

    /// <summary>Where the explorer's tree and tabs are kept (Karvon's own; Studio keeps its own workspace).</summary>
    public string DatabaseWorkspaceFile => DatabaseDirectory is { } directory
        ? Path.Combine(directory, "workspace.json")
        : Path.Combine(Settings.ConfigDirectory, "database-workspace.json");

    /// <summary>Raised when the explorer closes with its solution: the window saves its workspace and lets go of its view.</summary>
    public event Action? DatabaseClosing;

    /// <summary>Raised when another solution (or none) is open: the window shows that one's explorer if it shows one.</summary>
    public event Action? DatabaseChanged;

    /// <summary>True from closing the previous solution until the next one is open (the explorer between them is nobody's).</summary>
    public bool IsOpeningSolution { get; private set; }

    ExplorerViewModel CreateDatabase()
    {
        StudioSettings? settings = null;
        if (DatabaseDirectory is { } directory)
        {
            CreatePrivateDirectory(directory);
            // A solution's SQL files are its own: the Files panel starts in its folder.
            settings = StudioSettings.Load(Path.Combine(directory, "studio.json"));
            if (!File.Exists(settings.FilePath) && Solution is { } solution)
                settings.ProjectFolder = solution.Directory;
        }
        var explorer = new ExplorerViewModel(DatabaseServers, settings: settings);
        explorer.Tabs.CollectionChanged += OnDatabaseTabsChanged;
        explorer.PropertyChanged += OnDatabasePropertyChanged;
        foreach (var tab in explorer.Tabs)
            AddDatabaseTab(tab);
        return explorer;
    }

    /// <summary>The folder, readable by its owner only (data sources, consoles), and never in git.</summary>
    static void CreatePrivateDirectory(string directory)
    {
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(directory);
        else
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var ignore = Path.Combine(directory, ".gitignore");
        if (!File.Exists(ignore))
            File.WriteAllText(ignore, "*\n");
    }

    void OnDatabasePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ExplorerViewModel.ActiveTab) && !_syncingDatabaseTab && sender is ExplorerViewModel { ActiveTab: { } tab }
            && _databaseTabs.TryGetValue(tab, out var document))
            ActiveDocument = document;
    }

    /// <summary>
    /// Closes the explorer with its solution: its workspace is saved first (the window does), then its tabs and
    /// connections close. What that would lose is settled before anything closes; false when the user keeps it.
    /// </summary>
    async Task<bool> CloseDatabaseAsync()
    {
        if (_database is not { } explorer)
        {
            _databaseServers = null;
            return true;
        }
        if (!await explorer.SettleWorkspaceAsync())
            return false;
        DatabaseClosing?.Invoke();
        await explorer.CloseWorkspaceAsync();
        explorer.Tabs.CollectionChanged -= OnDatabaseTabsChanged;
        explorer.PropertyChanged -= OnDatabasePropertyChanged;
        foreach (var tab in _databaseTabs.Keys.ToList())
            RemoveDatabaseTab(tab);
        _database = null;
        _databaseServers = null;
        return true;
    }

    void OnDatabaseTabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            var tabs = (ICollection<DbDocument>)sender!;
            foreach (var gone in _databaseTabs.Keys.Where(t => !tabs.Contains(t)).ToList())
                RemoveDatabaseTab(gone);
            foreach (var tab in tabs.Where(t => !_databaseTabs.ContainsKey(t)).ToList())
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
