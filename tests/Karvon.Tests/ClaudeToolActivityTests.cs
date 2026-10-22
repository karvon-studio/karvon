using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Bengine.Enums;
using Bengine.Introspection.Servers;
using Microsoft.Data.Sqlite;
using Karvon.Database;
using Karvon.Database.ViewModels;
using Karvon.Docker.ViewModels;
using Karvon.ViewModels;
using Karvon.Views;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>What Claude does with Karvon's tools shows in Karvon, where the user would have done it.</summary>
public class ClaudeToolActivityTests
{
    static Task<System.Text.Json.Nodes.JsonObject> CallAsync(MainViewModel vm, string tool, object arguments) =>
        ClaudeOf(vm).Claude.Tools!.HandleAsync(JsonSerializer.SerializeToElement(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = tool, arguments } }), CancellationToken.None);

    static string Database()
    {
        var file = Path.Combine(Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "karvon-activity-" + Guid.NewGuid().ToString("N"))).FullName, "orders.db");
        using var connection = new SqliteConnection($"Data Source={file}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE users (id INTEGER PRIMARY KEY, email TEXT NOT NULL); INSERT INTO users (email) VALUES ('a@x.io'), ('b@x.io'), ('c@x.io');";
        command.ExecuteNonQuery();
        return file;
    }

    [AvaloniaFact]
    public async Task Claudes_queries_containers_searches_and_files_open_in_karvon()
    {
        var sln = SolutionExplorerTests.CopySample();
        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1500, Height = 950 };
        window.Show();
        await vm.OpenSolutionAsync(sln);
        // The solution's own data source (kept in its .vs).
        var file = Database();
        Db(vm).Servers.Upsert(new ServerProfile { Name = "orders (sqlite)", Provider = DatabaseProvider.Sqlite, FilePath = file });
        Db(vm).Explorer.ReloadServers();
        vm.ShowToolWindow(Karvon.Claude.ClaudeModule.ToolWindowId);
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(300));

        // A query: its results in a Query Tool of the data source, as if run there; it ran once.
        await CallAsync(vm, "db_query", new { connection = "orders (sqlite)", sql = "INSERT INTO users (email) VALUES ('d@x.io')" });
        await CallAsync(vm, "db_query", new { connection = "orders (sqlite)", sql = "SELECT email FROM users ORDER BY id" });
        await WaitUntil(() => vm.Documents.OfType<DatabaseDocument>().Any(d => d.Inner is QueryDocumentViewModel { HasResults: true }), TimeSpan.FromSeconds(10));
        var query = (QueryDocumentViewModel)vm.Documents.OfType<DatabaseDocument>().Single().Inner;
        Assert.Equal("SELECT email FROM users ORDER BY id", query.QueryText);
        var rows = Assert.Single(query.Results).Rows;
        Assert.Equal(4, rows.Count);
        Assert.Contains(query.Messages, m => m.Text == "Claude: INSERT INTO users (email) VALUES ('d@x.io')");
        Assert.Contains(query.Messages, m => m.Text.StartsWith("1 row(s) affected", StringComparison.Ordinal));

        // The Claude conversation stays in view: the Database Explorer beside it does not take its place.
        await CallAsync(vm, "db_connections", new { });
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(200));
        Assert.True(window.IsToolWindowShowing(Karvon.Claude.ClaudeModule.ToolWindowId));
        Assert.False(window.IsToolWindowShowing(Karvon.Database.DatabaseModule.ToolWindowId));

        // A container's logs: its page (any container: the sample has no compose file).
        vm.Settings.ClaudeAllContainers = true;
        await CallAsync(vm, "container_logs", new { name = "cache" });
        await WaitUntil(() => vm.Documents.OfType<DockerDocument>().Any(), TimeSpan.FromSeconds(10));
        Assert.True(vm.Documents.OfType<DockerDocument>().Any(), ClaudeOf(vm).Output.Document.Text);
        Assert.Equal("cache", vm.Documents.OfType<DockerDocument>().Single().Title);

        // A search: Find Results; a file read: the editor, its lines selected.
        await CallAsync(vm, "find_in_files", new { query = "Greeting" });
        await WaitUntil(() => vm.FindResults.Count > 0, TimeSpan.FromSeconds(10));
        Assert.StartsWith("Claude: 'Greeting'", vm.FindResultsTitle);
        Assert.All(vm.FindResults, r => Assert.True(File.Exists(r.FilePath), r.FilePath));
        Assert.True(window.IsToolWindowShowing(ToolWindows.FindResults));
        await CallAsync(vm, "open_document", new { path = "Components/Greeting.razor.cs", startLine = 2, endLine = 4 });
        await WaitUntil(() => vm.ActiveEditor?.FilePath.EndsWith("Greeting.razor.cs", StringComparison.Ordinal) == true, TimeSpan.FromSeconds(10));

        // Every call, in Output (Claude).
        var log = ClaudeOf(vm).Output.Document.Text;
        foreach (var line in new[] { "db_query (orders (sqlite))  1 row(s) affected", "container_logs (cache)", "find_in_files (Greeting)", "open_document (Components/Greeting.razor.cs)" })
            Assert.Contains(line, log);
        Snapshot(window, "claude-tool-activity");

        // Turned off: Output only.
        vm.Settings.ClaudeShowToolActivity = false;
        vm.FindResults.Clear();
        await CallAsync(vm, "find_in_files", new { query = "Home" });
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(200));
        Assert.Empty(vm.FindResults);
        Assert.Contains("find_in_files (Home)", ClaudeOf(vm).Output.Document.Text);
        vm.Settings.ClaudeShowToolActivity = true;
        vm.Settings.ClaudeAllContainers = false;
        window.Close();
    }
}
