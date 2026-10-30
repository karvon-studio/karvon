using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Bengine.Enums;
using Bengine.Introspection.Servers;
using Microsoft.Data.Sqlite;
using Karvon.Database;
using Karvon.Database.ViewModels;
using Karvon.Database.Views;
using Karvon.ViewModels;
using Karvon.Views;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>The Database Explorer: Bengine Data Studio's explorer (the bengine submodule) as a Karvon tool window.</summary>
public class DatabaseExplorerTests
{
    /// <summary>Test Connection reports a connection the driver refuses outright (a MongoDB user with no password) instead of crashing Karvon.</summary>
    [Fact]
    public async Task Test_connection_reports_what_the_driver_refuses()
    {
        var form = new ServerFormViewModel(new ServerStore(Path.Combine(Path.GetTempPath(), "karvon-db-" + Guid.NewGuid().ToString("N"), "servers.json")), null) { Name = "mongo" };
        form.SelectedProvider = form.ProviderOptions.First(p => p.Provider == DatabaseProvider.MongoDb);
        form.Host = "127.0.0.1";
        form.Port = 1;
        form.Username = "app";
        form.Password = "";
        await form.TestCommand.ExecuteAsync(null);
        Assert.True(form.IsError);
        Assert.Contains("needs a password", form.Message);
    }

    /// <summary>A SQLite data source in a server list of its own (never the user's ~/.config/bengine).</summary>
    static string NewServers() => NewServers(out _);

    static string NewServers(out string database)
    {
        var dir = Path.Combine(Path.GetTempPath(), "karvon-db-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "shop.db");
        using (var connection = new SqliteConnection($"Data Source={file}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE users (id INTEGER PRIMARY KEY, email TEXT NOT NULL UNIQUE, age INTEGER);
                INSERT INTO users (email, age) VALUES ('a@x.io', 31), ('b@x.io', 27), ('c@x.io', 45);
                """;
            command.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        database = file;
        var servers = Path.Combine(dir, "servers.json");
        new ServerStore(servers).Upsert(new ServerProfile { Name = "shop (sqlite)", Provider = DatabaseProvider.Sqlite, FilePath = file });
        return servers;
    }

    static async Task Until(Func<bool> condition) => await WaitUntil(condition, TimeSpan.FromSeconds(15));

    /// <summary>The users table's row of the (only) data source, its tree expanded down to it.</summary>
    static async Task<TreeItemViewModel> UsersOf(ExplorerViewModel explorer)
    {
        var item = explorer.ServerItems.Single();
        item.IsExpanded = true;
        await Until(() => !item.IsLoading && item.Children.Count > 0 && !item.Children[0].IsPlaceholder);
        foreach (var label in new[] { "main", "tables" })
        {
            item = item.Children.First(c => c.Label == label);
            item.IsExpanded = true;
            await Until(() => !item.IsLoading && item.Children.Count > 0 && !item.Children[0].IsPlaceholder);
        }
        return item.Children.First(c => c.Label == "users");
    }

    static DatabaseExplorerView? ViewOf(Window window) => window.GetVisualDescendants().OfType<DatabaseExplorerView>().SingleOrDefault();

    [AvaloniaFact]
    public async Task Explores_a_database_and_opens_its_documents_as_tabs()
    {
        Environment.SetEnvironmentVariable("BENGINE_SERVERS_FILE", NewServers());
        Environment.SetEnvironmentVariable("BENGINE_KEYRING", "off");
        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1500, Height = 950 };
        window.Show();

        // View > Database Explorer: the tool window with the data sources.
        vm.ShowToolWindow(Karvon.Database.DatabaseModule.ToolWindowId);
        Assert.True(window.IsToolWindowShowing(Karvon.Database.DatabaseModule.ToolWindowId));
        var explorer = Db(vm).Explorer;
        await Until(() => explorer.Roots.Count > 0);
        var view = window.GetVisualDescendants().OfType<DatabaseExplorerView>().Single();
        Assert.Same(view, DatabaseExplorerView.Current);

        var item = explorer.ServerItems.First();
        Assert.Equal("shop (sqlite)", item.Label);
        item.IsExpanded = true;
        await Until(() => !item.IsLoading && item.Children.Count > 0 && !item.Children[0].IsPlaceholder);
        foreach (var label in new[] { "main", "tables" })
        {
            item = item.Children.First(c => c.Label == label);
            item.IsExpanded = true;
            await Until(() => !item.IsLoading && item.Children.Count > 0 && !item.Children[0].IsPlaceholder);
        }
        var users = item.Children.First(c => c.Label == "users");
        users.IsExpanded = true;
        await Until(() => !users.IsLoading && !users.Children[0].IsPlaceholder);
        Assert.Equal("Column", users.Children[0].Kind);

        // A table's data opens as a Karvon document tab, in the Studio's grid.
        explorer.OpenData(users);
        var data = explorer.Tabs.OfType<DataDocumentViewModel>().Single();
        await Until(() => data.Rows.Count > 0 && !data.IsBusy);
        Assert.False(data.IsError, data.Status);
        Assert.Equal(3, data.Rows.Count);

        // A filter the server rejects says why in the document's status, and Karvon goes on.
        data.WhereText = "email = 'a@x.io' 'b@x.io'";
        await data.ApplyFilterCommand.ExecuteAsync(null);
        Assert.True(data.IsError);
        Assert.Contains("syntax error", data.Status, StringComparison.OrdinalIgnoreCase);
        await data.ClearFilterCommand.ExecuteAsync(null);
        await Until(() => data.Rows.Count == 3 && !data.IsBusy);
        Assert.False(data.IsError, data.Status);
        var tab = Assert.IsType<DatabaseDocument>(vm.ActiveDocument);
        Assert.Same(data, tab.Inner);
        Assert.Equal(data.Title, tab.Title);
        await Until(() => window.GetVisualDescendants().OfType<DataView>().Any(v => v.IsEffectivelyVisible));
        Snapshot(window, "database-data");

        // A console runs SQL; switching tabs in Karvon switches the explorer's active document too.
        await explorer.OpenQueryAsync(users.Server!, "main", "SELECT email, age FROM users WHERE age > 30 ORDER BY age;");
        var query = explorer.Tabs.OfType<QueryDocumentViewModel>().Single();
        await query.ExecuteAsync(query.QueryText);
        await Until(() => query.Results.Count > 0 && !query.IsBusy);
        Assert.False(query.IsError, query.Status);
        Assert.Same(query, ((DatabaseDocument)vm.ActiveDocument!).Inner);
        await Until(() => window.GetVisualDescendants().OfType<QueryView>().Any(v => v.IsEffectivelyVisible));
        Snapshot(window, "database-query");
        vm.ActiveDocument = tab;
        Assert.Same(data, explorer.ActiveTab);

        // Closing the Karvon tab closes the explorer's document.
        await vm.CloseDocumentCommand.ExecuteAsync(tab);
        Assert.DoesNotContain(data, explorer.Tabs);
        Assert.DoesNotContain(tab, vm.Documents);

        // The workspace (tree and tabs) is Karvon's own file, written when Karvon closes.
        window.Close();
        await WaitUntil(() => !window.IsVisible, TimeSpan.FromSeconds(10));
        Assert.True(File.Exists(Path.Combine(Karvon.Services.Settings.ConfigDirectory, "database-workspace.json")));
        Environment.SetEnvironmentVariable("BENGINE_SERVERS_FILE", null);
    }

    [AvaloniaFact]
    public async Task Each_solution_has_a_database_explorer_of_its_own()
    {
        // Studio's list (no solution open) has "shop (sqlite)": no solution sees it.
        Environment.SetEnvironmentVariable("BENGINE_SERVERS_FILE", NewServers(out var database));
        Environment.SetEnvironmentVariable("BENGINE_KEYRING", "off");
        var first = SolutionExplorerTests.CopySample();
        var second = SolutionExplorerTests.CopySample();
        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1500, Height = 950 };
        window.Show();
        await vm.OpenSolutionAsync(first);
        vm.ShowToolWindow(Karvon.Database.DatabaseModule.ToolWindowId);
        var explorer = Db(vm).Explorer;
        Assert.Empty(explorer.ServerItems);

        // The first solution's own data source, and a table of it open; kept beside the solution, in its .vs.
        Db(vm).Servers.Upsert(new ServerProfile { Name = "orders (sqlite)", Provider = DatabaseProvider.Sqlite, FilePath = database });
        explorer.ReloadServers();
        explorer.OpenData(await UsersOf(explorer));
        Assert.Single(vm.Documents.OfType<DatabaseDocument>());
        var directory = Db(vm).Directory!;
        Assert.StartsWith(Path.Combine(Path.GetDirectoryName(first)!, ".vs") + Path.DirectorySeparatorChar, directory);
        Assert.True(File.Exists(Path.Combine(directory, "servers.json")));
        Assert.Equal("*\n", File.ReadAllText(Path.Combine(directory, ".gitignore")));

        // Another solution: nothing of the first one's, neither its data source nor its tab.
        await vm.OpenSolutionAsync(second);
        Assert.NotSame(explorer, Db(vm).Explorer);
        Assert.Same(Db(vm).Explorer, ViewOf(window)?.DataContext);
        Assert.Empty(Db(vm).Explorer.ServerItems);
        Assert.Empty(vm.Documents.OfType<DatabaseDocument>());
        Assert.Empty(explorer.Tabs);
        Assert.True(File.Exists(Path.Combine(directory, "workspace.json")));

        // Back in the first one: its data source, tree and tab, as they were.
        await vm.OpenSolutionAsync(first);
        Assert.Same(Db(vm).Explorer, ViewOf(window)?.DataContext);
        Assert.Equal(["orders (sqlite)"], Db(vm).Explorer.ServerItems.Select(s => s.Label));
        var data = Assert.IsType<DataDocumentViewModel>(Db(vm).Explorer.Tabs.Single());
        Assert.Equal("users", data.Table.Name);
        Assert.Same(data, vm.Documents.OfType<DatabaseDocument>().Single().Inner);
        await Until(() => data.Rows.Count == 3);
        await Until(() => Db(vm).Explorer.ServerItems.Single() is { IsExpanded: true, IsLoading: false });

        // No solution: Studio's list, and none of the first one's tabs.
        await vm.CloseSolutionCommand.ExecuteAsync(null);
        Assert.Same(Db(vm).Explorer, ViewOf(window)?.DataContext);
        Assert.Equal(["shop (sqlite)"], Db(vm).Explorer.ServerItems.Select(s => s.Label));
        Assert.Empty(vm.Documents.OfType<DatabaseDocument>());
        Environment.SetEnvironmentVariable("BENGINE_SERVERS_FILE", null);
        window.Close();
    }

    /// <summary>A dialog the explorer opens (it belongs to Karvon's window), and what its task returned once it is closed.</summary>
    static async Task<Window> DialogOf(Window owner, Func<Task> open)
    {
        var before = owner.OwnedWindows.ToList();
        var task = open();
        await Until(() => owner.OwnedWindows.Except(before).Any(w => w.IsVisible) || task.IsCompleted);
        // One that failed to open says why.
        if (task.IsFaulted)
            await task;
        var dialog = owner.OwnedWindows.Except(before).First(w => w.IsVisible);
        Assert.Contains("dbx", dialog.Classes);
        dialog.Close();
        await task.WaitAsync(TimeSpan.FromSeconds(10));
        return dialog;
    }

    [AvaloniaFact]
    public async Task Every_document_and_dialog_of_the_studio_opens()
    {
        Environment.SetEnvironmentVariable("BENGINE_SERVERS_FILE", NewServers());
        Environment.SetEnvironmentVariable("BENGINE_KEYRING", "off");
        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1500, Height = 950 };
        window.Show();
        vm.ShowToolWindow(Karvon.Database.DatabaseModule.ToolWindowId);
        var explorer = Db(vm).Explorer;
        await Until(() => explorer.Roots.Count > 0 && window.GetVisualDescendants().OfType<DatabaseExplorerView>().Any());
        var view = window.GetVisualDescendants().OfType<DatabaseExplorerView>().Single();
        var server = explorer.ServerItems.First();
        server.IsExpanded = true;
        await Until(() => !server.IsLoading && server.Children.Count > 0 && !server.Children[0].IsPlaceholder);
        var main = server.Children.First(c => c.Label == "main");
        main.IsExpanded = true;
        await Until(() => !main.IsLoading && main.Children.Count > 0 && !main.Children[0].IsPlaceholder);
        var tables = main.Children.First(c => c.Label == "tables");
        tables.IsExpanded = true;
        await Until(() => !tables.IsLoading && tables.Children.Count > 0 && !tables.Children[0].IsPlaceholder);
        var users = tables.Children.First(c => c.Label == "users");
        explorer.SelectedItem = users;

        async Task Shows<TView>(Func<Task> open) where TView : Control
        {
            await open();
            await Until(() => window.GetVisualDescendants().OfType<TView>().Any(v => v.IsEffectivelyVisible));
        }

        // Documents: DDL, the ER diagram, server activity, schema and data comparisons.
        await Shows<SqlView>(async () => Assert.Contains("CREATE TABLE", (await explorer.OpenDdlAsync(users))!.Sql!));
        await Shows<DiagramView>(() => { Assert.NotNull(explorer.OpenDiagram(main)); return Task.CompletedTask; });
        await Shows<ActivityView>(() => { explorer.OpenActivity(users.Server!); return Task.CompletedTask; });
        await Shows<SchemaCompareView>(() => explorer.OpenCompareAsync(users));
        await Shows<DataCompareView>(() => explorer.OpenDataCompareAsync(users));
        Snapshot(window, "database-compare");
        Assert.Equal(explorer.Tabs.Count, vm.Documents.OfType<DatabaseDocument>().Count());

        // Dialogs: data sources, table design, backup, data import, history, Go to Object, Find Action, keymap.
        await DialogOf(window, () => view.EditServerAsync(null));
        await DialogOf(window, () => explorer.ModifyTableAsync(users));
        await DialogOf(window, () => explorer.BackupAsync(main, restore: false));
        await DialogOf(window, () => explorer.ImportDataAsync(users));
        await DialogOf(window, () => explorer.ShowHistoryCommand.ExecuteAsync(null));
        foreach (var action in new[] { "goto.object", "actions.find", "keymap.edit" })
            await DialogOf(window, () =>
            {
                Assert.True(view.RunAction(action), action);
                return Task.CompletedTask;
            });

        // With the focus in the explorer, Ctrl+Shift+A is Studio's Find Action (not Karvon's Add New Item).
        view.FindControl<TextBox>("TreeFilter")!.Focus();
        await DialogOf(window, () =>
        {
            window.KeyPress(Avalonia.Input.Key.A, Avalonia.Input.RawInputModifiers.Control | Avalonia.Input.RawInputModifiers.Shift, Avalonia.Input.PhysicalKey.A, "A");
            return Task.CompletedTask;
        });
        Assert.DoesNotContain(window.OwnedWindows, w => w is AddItemWindow);

        // The tree's context menu and Quick Documentation.
        var quickDoc = await view.ShowQuickDocAsync(users);
        Assert.Contains(explorer.QuickDoc.Rows, r => r.Name == "Name" && r.Value == "users");
        quickDoc?.Hide();
        Environment.SetEnvironmentVariable("BENGINE_SERVERS_FILE", null);
        window.Close();
    }
}
