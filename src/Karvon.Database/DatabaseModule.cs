using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using Avalonia.Threading;
using Bengine.Introspection.Servers;
using Karvon.Claude;
using Karvon.Claude.Tools;
using Karvon.Database.ViewModels;
using Karvon.Database.Views;
using Karvon.Modules;
using Karvon.ViewModels;
using DbDocument = Karvon.Database.ViewModels.DocumentViewModel;

namespace Karvon.Database;

/// <summary>
/// The Database Explorer (Bengine Data Studio's explorer, from the bengine submodule). Each solution has one of its own,
/// kept in .vs/&lt;Solution&gt;/Karvon/Database: its data sources, query history, settings, SQL files and workspace (the
/// tree and the tabs). Nothing of it shows in another solution: when the solution closes, its explorer saves its workspace
/// and closes with it. With no solution open it is Bengine Data Studio's list (~/.config/bengine/servers.json) and a
/// workspace of Karvon's own. Its documents open as Karvon document tabs; Claude Code gets the database tools.
/// </summary>
public sealed class DatabaseModule : KarvonModule, IDatabaseService, IDatabaseToolHost
{
    public const string ToolWindowId = "DatabaseExplorer";

    ExplorerViewModel? _explorer;
    ServerStore? _servers;
    DatabaseExplorerView? _view;
    bool _closing;
    readonly Dictionary<DbDocument, DatabaseDocument> _tabs = [];
    bool _syncingTab;

    /// <summary>Claude's Query Tool of each data source, used again while it is open.</summary>
    readonly Dictionary<string, QueryDocumentViewModel> _claudeQueries = new(StringComparer.Ordinal);

    public override string Id => "database";

    public override string Name => "Database Explorer";


    public override string Icon => "database";

    public override string Description =>
        "Bengine Data Studio in Karvon: the solution's data sources (PostgreSQL, MySQL, SQL Server, Oracle, SQLite, MongoDB…), "
        + "consoles, tables' data, diagrams and comparisons; Claude Code's database tools.";

    public override IReadOnlyList<ToolWindowInfo> ToolWindows =>
        [new(ToolWindowId, "Database Explorer", "_Database Explorer", "ToolDatabase", DockSide.Right, CreateView) { Gesture = "Ctrl+Alt+S" }];

    public override IReadOnlyList<IDataTemplate> DocumentTemplates =>
    [
        Template<DatabaseDocument>(document =>
        {
            var frame = new Border
            {
                Classes = { "docframe", "dbx" },
                BorderBrush = new SolidColorBrush(Color.Parse("#007ACC")),
                BorderThickness = new Thickness(0, 2, 0, 0),
                Child = new DatabaseDocumentHost { Content = document.Inner },
            };
            frame.Bind(Border.BackgroundProperty, frame.GetResourceObservable("Bg"));
            return frame;
        }),
        // The explorer's documents, in their tab's frame (Bengine Data Studio's views).
        Template<PropertiesDocumentViewModel>(_ => new PropertiesView()),
        Template<SqlDocumentViewModel>(_ => new SqlView()),
        Template<DataDocumentViewModel>(_ => new DataView()),
        Template<QueryDocumentViewModel>(_ => new QueryView()),
        Template<SchemaCompareViewModel>(_ => new SchemaCompareView()),
        Template<DiagramDocumentViewModel>(_ => new DiagramView()),
        Template<ActivityDocumentViewModel>(_ => new ActivityView()),
        Template<DataCompareViewModel>(_ => new DataCompareView()),
    ];

    /// <summary>The open solution's explorer, made the first time it is needed.</summary>
    public ExplorerViewModel Explorer => _explorer ??= CreateExplorer();

    /// <summary>The open solution's data sources (with no solution: Studio's list; BENGINE_SERVERS_FILE points it elsewhere).</summary>
    public ServerStore Servers => _servers ??= Directory is { } directory
        ? new ServerStore(Path.Combine(directory, "servers.json"), Karvon.Services.Keyring.System)
        : new ServerStore();

    /// <summary>The folder of the open solution's Database Explorer (beside its state.json); null with no solution open.</summary>
    public string? Directory => Workbench.WorkspaceStateDirectory is { } state ? Path.Combine(state, "Database") : null;

    /// <summary>Where the explorer's tree and tabs are kept (Karvon's own; Studio keeps its own workspace).</summary>
    public string WorkspaceFile => Directory is { } directory
        ? Path.Combine(directory, "workspace.json")
        : Path.Combine(Services.Settings.ConfigDirectory, "database-workspace.json");

    protected override void Started()
    {
        Workbench.ActiveDocumentChanged += SyncActiveTab;
        // The explorer that shows from now on is this solution's (or no solution's).
        Workbench.WorkspaceChanged += () =>
        {
            if (_view is null)
                ResetToolWindow(ToolWindowId);
        };
    }

    /// <summary>
    /// The explorer view of the open solution, its workspace restored. Opening the workspace may reconnect to data sources
    /// and ask for their passwords, which nobody wants from a Karvon that never opened it: it is made when it first shows.
    /// </summary>
    Control? CreateView()
    {
        // Between two solutions the explorer is nobody's: the next one's shows once it is open.
        if (_view is not null || _closing || Workbench.IsOpeningWorkspace)
            return _view;
        var view = _view = new DatabaseExplorerView(Servers) { DataContext = Explorer };
        // Passwords, confirmations, files and the other questions the explorer asks come from the view.
        Explorer.Dialogs = view;
        view.RestoreWorkspace(WorkspaceFile);
        return view;
    }

    ExplorerViewModel CreateExplorer()
    {
        StudioSettings? settings = null;
        if (Directory is { } directory)
        {
            CreatePrivateDirectory(directory);
            // A solution's SQL files are its own: the Files panel starts in its folder.
            settings = StudioSettings.Load(Path.Combine(directory, "studio.json"));
            if (!File.Exists(settings.FilePath) && Workbench.Workspace is { } workspace)
                settings.ProjectFolder = workspace;
        }
        var explorer = new ExplorerViewModel(Servers, settings: settings);
        explorer.Tabs.CollectionChanged += OnTabsChanged;
        explorer.PropertyChanged += OnExplorerChanged;
        foreach (var tab in explorer.Tabs)
            AddTab(tab);
        return explorer;
    }

    /// <summary>The folder, readable by its owner only (data sources, consoles), and never in git.</summary>
    static void CreatePrivateDirectory(string directory)
    {
        if (OperatingSystem.IsWindows())
            System.IO.Directory.CreateDirectory(directory);
        else
            System.IO.Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var ignore = Path.Combine(directory, ".gitignore");
        if (!File.Exists(ignore))
            File.WriteAllText(ignore, "*\n");
    }

    /// <summary>
    /// Closes the explorer with its solution: its workspace is saved first, then its tabs and connections close. What that
    /// would lose is settled before anything closes; false when the user keeps it.
    /// </summary>
    public override async Task<bool> CloseWorkspaceAsync()
    {
        if (_explorer is not { } explorer)
        {
            _servers = null;
            return true;
        }
        if (!await explorer.SettleWorkspaceAsync())
            return false;
        _closing = true;
        try
        {
            _view?.CloseWorkspace();
            _view = null;
            ResetToolWindow(ToolWindowId);
            await explorer.CloseWorkspaceAsync();
            explorer.Tabs.CollectionChanged -= OnTabsChanged;
            explorer.PropertyChanged -= OnExplorerChanged;
            foreach (var tab in _tabs.Keys.ToList())
                RemoveTab(tab);
            _explorer = null;
            _servers = null;
            _claudeQueries.Clear();
        }
        finally
        {
            _closing = false;
        }
        return true;
    }

    /// <summary>The window closes: the workspace is written.</summary>
    public override void SaveState() => _view?.SaveWorkspace();

    // ---- Its documents as Karvon's tabs ----------------------------------------------------------------------------

    void OnExplorerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ExplorerViewModel.ActiveTab) && !_syncingTab && sender is ExplorerViewModel { ActiveTab: { } tab }
            && _tabs.TryGetValue(tab, out var document))
            Workbench.ActiveDocument = document;
    }

    void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            var tabs = (ICollection<DbDocument>)sender!;
            foreach (var gone in _tabs.Keys.Where(t => !tabs.Contains(t)).ToList())
                RemoveTab(gone);
            foreach (var tab in tabs.Where(t => !_tabs.ContainsKey(t)).ToList())
                AddTab(tab);
            return;
        }
        foreach (DbDocument tab in e.OldItems ?? Array.Empty<DbDocument>())
            RemoveTab(tab);
        foreach (DbDocument tab in e.NewItems ?? Array.Empty<DbDocument>())
            AddTab(tab);
    }

    void AddTab(DbDocument tab)
    {
        if (_tabs.ContainsKey(tab))
            return;
        var document = new DatabaseDocument(tab);
        _tabs[tab] = document;
        Workbench.AddDocument(document);
        if (Explorer.ActiveTab == tab)
            Workbench.ActiveDocument = document;
    }

    void RemoveTab(DbDocument tab)
    {
        if (!_tabs.Remove(tab, out var document))
            return;
        Workbench.RemoveDocument(document);
        document.Dispose();
    }

    /// <summary>A database tab that comes to the front is the explorer's active document too.</summary>
    void SyncActiveTab(DocumentTab? value)
    {
        if (value is not DatabaseDocument document || _explorer is not { } explorer || explorer.ActiveTab == document.Inner)
            return;
        _syncingTab = true;
        try
        {
            explorer.ActiveTab = document.Inner;
        }
        finally
        {
            _syncingTab = false;
        }
    }

    // ---- Claude Code ----------------------------------------------------------------------------------------------

    public override IEnumerable<IClaudeTool> ClaudeTools() => DatabaseClaudeTools.Create(this);

    /// <summary>The databases' shells: Karvon queries the user's data sources with their saved connections instead.</summary>
    public override IEnumerable<ClaudeShellRule> ClaudeShellRules() =>
    [
        ClaudeShellRule.Command("database shells", @"(psql|mysql|mariadb|sqlcmd|sqlite3|mongosh|mongo)\b",
            "Karvon queries the user's databases: mcp__karvon__db_connections lists their data sources, and db_schema, db_query and "
            + "db_explain sign in with the connections they saved in the Database Explorer, so no password is needed (nor is any in the "
            + "environment). Use them, not the database's shell."),
    ];

    public override async Task ShowClaudeToolAsync(string tool, JsonElement input, ClaudeToolOutput output)
    {
        switch (tool)
        {
            case "db_query" when output.Detail is DbQueryRun run:
                (await ClaudeQueryAsync(run.Server, run.Database))?.ShowRun(run.Sql, run.Results, "Claude");
                break;
            case "db_explain" when output.Detail is DbPlanRun plan:
                (await ClaudeQueryAsync(plan.Server, plan.Database))?.ShowPlan(plan.Sql, plan.Plan, "Claude");
                break;
            case "db_connections" or "db_schema":
                Workbench.ShowToolWindowBesideClaude(ToolWindowId);
                break;
        }
    }

    /// <summary>Claude's Query Tool of a data source: the one it had while it is open, else a new one.</summary>
    async Task<QueryDocumentViewModel?> ClaudeQueryAsync(ServerProfile server, string? database)
    {
        var key = server.Name + "\n" + database;
        if (_claudeQueries.TryGetValue(key, out var open) && Explorer.Tabs.Contains(open))
        {
            Explorer.ActiveTab = open;
            return open;
        }
        var query = await Explorer.OpenQueryAsync(server, database);
        if (query is not null)
            _claudeQueries[key] = query;
        return query;
    }

    IReadOnlyList<ServerProfile> IDatabaseToolHost.DataSources() => Servers.List();

    /// <summary>The explorer's passwords are the UI thread's.</summary>
    Task<string?> IDatabaseToolHost.PasswordForAsync(ServerProfile server) =>
        Dispatcher.UIThread.InvokeAsync<string?>(() => _explorer is { } explorer ? explorer.PasswordFor(server) : server.Password).GetTask();

    // ---- IDatabaseService -----------------------------------------------------------------------------------------

    void IDatabaseService.AddDataSource(ServerProfile server, Func<ServerProfile, bool> isSame)
    {
        if (!Servers.List().Any(isSame))
            Servers.Upsert(server);
        _explorer?.ReloadServers();
        Workbench.ShowToolWindow(ToolWindowId);
    }
}
