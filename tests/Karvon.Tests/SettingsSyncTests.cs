using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Karvon.Pipelines;
using Karvon.Pipelines.Providers;
using Karvon.Services;
using Karvon.ViewModels;
using Karvon.Workspace;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>GitHub's gists API of the tests': gists kept in memory, every call logged.</summary>
sealed class FakeGistsApi : IGitHubApi
{
    public Dictionary<string, JsonObject> Gists { get; } = [];

    public List<string> Calls { get; } = [];

    public Task<string> GetAsync(string path, CancellationToken ct)
    {
        Calls.Add("GET " + path);
        if (path.StartsWith("/gists?", StringComparison.Ordinal))
            return Task.FromResult(new JsonArray([.. Gists.Values.Select(g => (JsonNode)g.DeepClone())]).ToJsonString());
        return Gists.TryGetValue(path["/gists/".Length..], out var gist) ? Task.FromResult(gist.ToJsonString()) : throw new PipelineException("GitHub: HTTP 404: Not Found");
    }

    public Task PostAsync(string path, CancellationToken ct) => throw new NotSupportedException();

    public Task<string> SendAsync(string method, string path, string? json, CancellationToken ct)
    {
        Calls.Add($"{method} {path}");
        var body = JsonNode.Parse(json!)!.AsObject();
        if (method == "POST")
        {
            var id = "g" + (Gists.Count + 1);
            Gists[id] = new JsonObject { ["id"] = id, ["description"] = body["description"]!.DeepClone(), ["public"] = body["public"]!.DeepClone(), ["files"] = body["files"]!.DeepClone() };
            return Task.FromResult(Gists[id].ToJsonString());
        }
        var gist = Gists[path["/gists/".Length..]];
        gist["files"] = body["files"]!.DeepClone();
        return Task.FromResult(gist.ToJsonString());
    }

    public void Dispose()
    {
    }
}

/// <summary>Settings Sync: the preferences only, merged by setting, kept in a folder or a secret gist.</summary>
public class SettingsSyncTests
{
    sealed class Dialogs(string folder) : IDialogService
    {
        public List<string> Confirmations { get; } = [];

        public bool? Answer { get; set; } = true;

        public Task<string?> PickSolutionAsync() => Task.FromResult<string?>(null);

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(folder);

        public Task<string?> PickAsync(string title, string label, IReadOnlyList<string> options, string? initial) => Task.FromResult<string?>("A folder");

        public Task<string?> PromptAsync(string title, string label, string initial) => Task.FromResult<string?>(null);

        public Task<bool?> ConfirmSaveAsync(string fileName) => Task.FromResult<bool?>(false);

        public Task<bool?> ConfirmAsync(string title, string message)
        {
            Confirmations.Add(message);
            return Task.FromResult(Answer);
        }

        public Task<NewItemChoice?> ChooseNewItemAsync(string title, string folder, ItemTemplate initial) => Task.FromResult<NewItemChoice?>(null);

        public Task<IReadOnlyList<string>> PickFilesAsync(string title) => Task.FromResult<IReadOnlyList<string>>([]);
    }

    [Fact]
    public void Only_the_preferences_are_synced_and_merged_by_setting()
    {
        var settings = new Settings { InMemory = true, InlayHints = false, EditorZoom = 1.2, RecentSolutions = ["/home/me/app.sln"], NewProjectLocation = "/home/me/src" };
        settings.Layout.Width = 900;
        settings.Sync = new SettingsSyncState { Location = "/sync" };
        var synced = SettingsSync.Of(settings);
        Assert.False((bool)synced["InlayHints"]!);
        Assert.Equal(1.2, (double)synced["EditorZoom"]!);
        foreach (var mine in new[] { "RecentSolutions", "Layout", "Sync", "NewProjectLocation", "Solutions", "ClaudeAccount", "LastSolution" })
            Assert.False(synced.ContainsKey(mine), mine);

        var other = new Settings { InMemory = true, RecentSolutions = ["/elsewhere.sln"] };
        SettingsSync.Apply(other, synced);
        Assert.False(other.InlayHints);
        Assert.Equal(1.2, other.EditorZoom);
        Assert.Equal(["/elsewhere.sln"], other.RecentSolutions);

        // Changed here since the last sync: mine; the rest: theirs.
        var last = SettingsSync.Of(new Settings());
        var here = SettingsSync.Of(new Settings { Minimap = true });
        var there = SettingsSync.Of(new Settings { CodeLens = false, Minimap = false });
        var merged = SettingsSync.Merge(last, here, there);
        Assert.True((bool)merged["Minimap"]!);
        Assert.False((bool)merged["CodeLens"]!);
        // No sync before: theirs.
        Assert.False((bool)SettingsSync.Merge(null, here, there)["Minimap"]!);
    }

    [AvaloniaFact]
    public async Task Two_machines_keep_their_settings_in_step_through_a_folder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "karvon-sync-" + Guid.NewGuid().ToString("N"));
        var a = NewViewModel(s => { s.InMemory = true; s.InlayHints = false; s.Minimap = true; s.RecentSolutions = ["/only/on/a.sln"]; });
        a.Dialogs = new Dialogs(folder);
        await a.TurnOnSettingsSyncCommand.ExecuteAsync(null);
        Assert.True(a.IsSettingsSyncOn);
        var file = Path.Combine(folder, FolderSettingsStore.FileName);
        var kept = SyncedSettings.Parse(File.ReadAllText(file))!;
        Assert.False((bool)kept.Settings["InlayHints"]!);
        Assert.DoesNotContain("/only/on/a.sln", File.ReadAllText(file));
        Assert.Equal(Environment.MachineName, kept.Machine);

        // Another machine: asked, then it takes them; its own history stays.
        var b = NewViewModel(s => { s.InMemory = true; s.RecentSolutions = ["/only/on/b.sln"]; });
        var dialogs = new Dialogs(folder);
        b.Dialogs = dialogs;
        var changed = new List<string?>();
        b.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        await b.TurnOnSettingsSyncCommand.ExecuteAsync(null);
        Assert.Single(dialogs.Confirmations);
        Assert.False(b.InlayHints);
        Assert.True(b.Minimap);
        Assert.Contains(nameof(MainViewModel.InlayHints), changed);
        Assert.Equal(["/only/on/b.sln"], b.Settings.RecentSolutions);

        // Changed on B: sent a moment after.
        b.CodeLens = false;
        await WaitUntil(() => File.ReadAllText(file).Contains("\"CodeLens\": false"), TimeSpan.FromSeconds(15));

        // Changed on A meanwhile: both changes kept, A's and B's.
        a.StickyScroll = false;
        await a.SyncSettingsNowCommand.ExecuteAsync(null);
        Assert.False(a.CodeLens);
        Assert.False(a.StickyScroll);
        Assert.False((bool)SyncedSettings.Parse(File.ReadAllText(file))!.Settings["StickyScroll"]!);
        Assert.StartsWith("Settings synced with ", a.StatusText);

        a.TurnOffSettingsSyncCommand.Execute(null);
        b.TurnOffSettingsSyncCommand.Execute(null);
        Assert.False(a.IsSettingsSyncOn);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task GitHub_keeps_them_in_a_secret_gist_found_by_another_machine()
    {
        var api = new FakeGistsApi();
        Func<CancellationToken, Task<IGitHubApi>> connect = _ => Task.FromResult<IGitHubApi>(api);
        var ct = TestContext.Current.CancellationToken;
        var first = new SettingsSyncState { Target = "github" };
        var store = new GistSettingsStore(connect, first);
        Assert.Null(await store.ReadAsync(ct));
        await store.WriteAsync(new SyncedSettings(SettingsSync.Of(new Settings { Minimap = true }), DateTimeOffset.Now, "laptop"), ct);
        var gist = Assert.Single(api.Gists.Values);
        Assert.Equal((GistSettingsStore.Description, false), ((string)gist["description"]!, (bool)gist["public"]!));
        Assert.Equal("g1", first.Location);

        // Another machine finds it by its description, then reads and writes it by its id.
        var second = new SettingsSyncState { Target = "github" };
        var other = new GistSettingsStore(connect, second);
        var read = (await other.ReadAsync(ct))!;
        Assert.True((bool)read.Settings["Minimap"]!);
        Assert.Equal(("laptop", "g1"), (read.Machine, second.Location));
        await other.WriteAsync(read with { Machine = "desktop" }, ct);
        Assert.Equal(["GET /gists?per_page=100", "POST /gists", "GET /gists?per_page=100", "GET /gists/g1", "PATCH /gists/g1"], api.Calls);
        Assert.Equal("desktop", (await store.ReadAsync(ct))!.Machine);

        // Deleted on GitHub: none, and made again with the next write.
        api.Gists.Clear();
        Assert.Null(await store.ReadAsync(ct));
        Assert.Null(first.Location);
    }
}
