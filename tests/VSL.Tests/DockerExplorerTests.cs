using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using VSL.Docker;
using VSL.Docker.ViewModels;
using VSL.Docker.Views;
using static VSL.Tests.UiTests;

namespace VSL.Tests;

/// <summary>A docker command line for the tests (TestAssets/FakeDocker), each with a state of its own.</summary>
public static class FakeDocker
{
    static readonly string Script = Path.Combine(AppContext.BaseDirectory, "TestAssets", "FakeDocker", "docker");

    /// <summary>A folder with the state file and a "docker" that answers from it.</summary>
    public static (string Docker, string State, string Folder) Create(JsonObject? state = null)
    {
        var folder = Path.Combine(Path.GetTempPath(), "vsl-docker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var statePath = Path.Combine(folder, "state.json");
        File.WriteAllText(statePath, (state ?? Sample()).ToJsonString());
        var docker = Path.Combine(folder, "docker");
        File.WriteAllText(docker, $"#!/bin/sh\nVSL_FAKE_DOCKER_STATE='{statePath}' exec python3 -I '{Script}' \"$@\"\n");
        File.SetUnixFileMode(docker, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return (docker, statePath, folder);
    }

    public static JsonObject Read(string state) => JsonNode.Parse(File.ReadAllText(state))!.AsObject();

    public static void Write(string state, Action<JsonObject> change)
    {
        var json = Read(state);
        change(json);
        File.WriteAllText(state + ".tmp", json.ToJsonString());
        File.Move(state + ".tmp", state, overwrite: true);
    }

    static string Id(char c) => new(c, 64);

    /// <summary>A small shop: its compose stack (web, api, db), a cache, a migration that failed, a worker that stopped.</summary>
    public static JsonObject Sample(string? workdir = null) => JsonNode.Parse(JsonSerializer.Serialize(new
    {
        containers = new object[]
        {
            new { id = Id('a'), name = "shop-web-1", image = "nginx:1.27", state = "running", project = "shop", service = "web", workdir = workdir ?? "/srv/shop",
                  ports = new[] { new { host = 8080, container = "80/tcp" } }, networks = new[] { "shop_default" }, cpu = 0.4,
                  logs = new[] { "\u001b[32m200\u001b[0m GET /", "\u001b[33m404\u001b[0m GET /favicon.ico" } },
            new { id = Id('b'), name = "shop-api-1", image = "shop-api:latest", state = "running", project = "shop", service = "api", workdir = workdir ?? "/srv/shop",
                  ports = new[] { new { host = 5000, container = "8080/tcp" } }, networks = new[] { "shop_default" }, cpu = 1.2,
                  logs = new[] { "info: Now listening on: http://[::]:8080", "ERR connection refused, retrying" } },
            new { id = Id('c'), name = "shop-db-1", image = "postgres:17", state = "running", project = "shop", service = "db", workdir = workdir ?? "/srv/shop",
                  ports = new[] { new { host = 5432, container = "5432/tcp" } }, networks = new[] { "shop_default" }, cpu = 0.8, health = "healthy",
                  env = new[] { "POSTGRES_USER=shop", "POSTGRES_PASSWORD=not-a-secret", "POSTGRES_DB=shop" },
                  mounts = new[] { new { name = "shop_pgdata", destination = "/var/lib/postgresql/data" } },
                  logs = new[] { "database system is ready to accept connections" } },
            new { id = Id('d'), name = "cache", image = "redis:7-alpine", state = "running", project = (string?)null, service = (string?)null, workdir = (string?)null,
                  ports = new[] { new { host = 6379, container = "6379/tcp" } }, networks = new[] { "bridge" }, cpu = 0.1, health = (string?)null,
                  env = Array.Empty<string>(), mounts = new[] { new { name = "cache-data", destination = "/data" } }, logs = new[] { "Ready to accept connections tcp" } },
            new { id = Id('e'), name = "migrate", image = "shop-api:latest", state = "exited", exit = 1, networks = new[] { "bridge" },
                  logs = new[] { "Applying migration 0042", "ERR relation \"orders\" does not exist" } },
            new { id = Id('f'), name = "old-worker", image = "alpine:3.20", state = "exited", exit = 0, networks = new[] { "bridge" }, logs = new[] { "done" } },
        },
        images = new object[]
        {
            new { id = "sha256:" + Id('1'), repo = "nginx", tag = "1.27", size = "192MB", exposed = new[] { "80/tcp" } },
            new { id = "sha256:" + Id('2'), repo = "shop-api", tag = "latest", size = "231MB", exposed = new[] { "8080/tcp" } },
            new { id = "sha256:" + Id('3'), repo = "postgres", tag = "17", size = "438MB", exposed = new[] { "5432/tcp" } },
            new { id = "sha256:" + Id('4'), repo = "redis", tag = "7-alpine", size = "41.4MB", exposed = new[] { "6379/tcp" } },
            new { id = "sha256:" + Id('5'), repo = "alpine", tag = "3.20", size = "7.8MB", exposed = Array.Empty<string>() },
            new { id = "sha256:" + Id('6'), repo = "node", tag = "22", size = "1.12GB", exposed = new[] { "3000/tcp" } },
            new { id = "sha256:" + Id('7'), repo = "<none>", tag = "<none>", size = "88MB", exposed = Array.Empty<string>() },
        },
        volumes = new object[]
        {
            new { name = "shop_pgdata", size = "68.8MB" },
            new { name = "cache-data", size = "1.2MB" },
            new { name = "scratch", size = "0B" },
            new { name = new string('9', 64), size = "12kB" },
        },
        networks = new object[]
        {
            new { id = Id('7'), name = "bridge", driver = "bridge" },
            new { id = Id('8'), name = "host", driver = "host" },
            new { id = Id('9'), name = "none", driver = "null" },
            new { id = Id('0'), name = "shop_default", driver = "bridge", labels = "com.docker.compose.project=shop" },
        },
        events = Array.Empty<object>(),
    }))!.AsObject();
}

/// <summary>What the explorer asked of VSL; its documents show in <see cref="Well"/>, one at a time.</summary>
sealed class TestDockerHost(string? solution) : IDockerHost
{
    public List<DockerDocument> Open { get; } = [];

    public DockerDocument? Active { get; private set; }

    /// <summary>The tests' document well.</summary>
    public ContentControl? Well { get; set; }

    public IEnumerable<DockerDocument> Documents => Open;

    public void ShowDocument(DockerDocument document)
    {
        if (!Open.Contains(document))
            Open.Add(document);
        Active = document;
        Well?.Content = document;
    }

    public void CloseDocument(DockerDocument document)
    {
        if (!Open.Remove(document))
            return;
        document.Dispose();
        if (Active == document)
        {
            Active = Open.LastOrDefault();
            Well?.Content = Active;
        }
    }

    /// <summary>The active document's content, as the test expects it.</summary>
    public T Page<T>() => Assert.IsType<T>(Active?.Content);

    public List<string> Urls { get; } = [];

    public List<string> Output { get; } = [];

    public List<string> Prompts { get; } = [];

    public List<string> Databases { get; } = [];

    public string? SolutionDirectory { get; } = solution;

    public void OpenUrl(string url) => Urls.Add(url);

    public void WriteOutput(string line)
    {
        lock (Output)
            Output.Add(line);
    }

    public void ShowOutput()
    {
    }

    public string? OpenInDatabaseExplorer(DockerContainer container, JsonElement inspect)
    {
        Databases.Add(container.Name);
        return null;
    }

    public void AskClaude(string prompt) => Prompts.Add(prompt);
}

public class DockerExplorerTests
{
    /// <summary>The explorer as VSL shows it: the tool window at the left, its active document beside it, its dialogs over both.</summary>
    static (DockerExplorerViewModel Vm, TestDockerHost Host, Window Window, string State) Open(string? solution = null, JsonObject? state = null)
    {
        var (docker, statePath, _) = FakeDocker.Create(state ?? FakeDocker.Sample(solution));
        var host = new TestDockerHost(solution);
        var vm = new DockerExplorerViewModel(new DockerEngine(docker), host);
        var well = new ContentControl { DataTemplates = { new FuncDataTemplate<DockerDocument>((_, _) => new DockerDocumentView()) } };
        host.Well = well;
        var tree = new DockerExplorerView { DataContext = vm, Background = Avalonia.Media.Brush.Parse("#252526") };
        var overlay = new DockerOverlay { DataContext = vm };
        Grid.SetColumn(well, 1);
        Grid.SetColumnSpan(overlay, 2);
        var window = new Window { Width = 1360, Height = 860, Content = new Grid { ColumnDefinitions = new("300,*"), Children = { tree, well, overlay } } };
        window.Show();
        return (vm, host, window, statePath);
    }

    static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    static async Task<bool> Confirm(DockerExplorerViewModel vm)
    {
        await WaitUntil(() => vm.Dialog is ConfirmDialog, TimeSpan.FromSeconds(5));
        var dialog = (ConfirmDialog)vm.Dialog!;
        dialog.Confirm();
        await WaitUntil(() => vm.Dialog is null, TimeSpan.FromSeconds(5));
        return dialog.IsDanger;
    }

    [AvaloniaFact]
    public async Task Containers_show_like_docker_desktop_and_follow_the_engine()
    {
        var solution = Path.Combine(Path.GetTempPath(), "vsl-docker-solution-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(solution);
        File.WriteAllText(Path.Combine(solution, "compose.yaml"), "services:\n  web:\n    image: nginx\n  worker:\n    image: alpine\n");
        var (vm, host, window, state) = Open(solution);
        await vm.InitializeAsync();
        Assert.True(vm.IsEngineRunning);
        Assert.Equal(("docker 29.1.0", "Fake Linux 1.0 · 8 CPUs · 16 GB"), (vm.VersionText, vm.MachineText));
        vm.OpenSection(DockerSection.Containers);
        var page = host.Page<ContainersPage>();
        Assert.Equal("Containers", host.Active!.Title);

        // The solution's stack first (compose ran in its folder), its containers under it, then the others by name.
        Assert.Equal(["shop", "shop-api-1", "shop-db-1", "shop-web-1", "cache", "migrate", "old-worker"],
            page.Rows.Select(r => r switch { StackRow s => s.Name, ContainerRow c => c.Name, _ => "" }));
        var shop = page.Stacks.Single();
        Assert.True(shop.IsThisSolution);
        Assert.Equal(("Running", "running"), (shop.StateText, shop.StateKind));
        var web = page.AllRows.Single(r => r.Name == "shop-web-1");
        Assert.Equal(("8080:80", "http://localhost:8080"), (web.FirstPort!.Text, web.FirstPort.Url));
        Assert.Equal("aaaaaaaaaaaa", web.ShortId);
        Assert.Equal("Running (healthy)", page.AllRows.Single(r => r.Name == "shop-db-1").StateText);
        var migrate = page.AllRows.Single(r => r.Name == "migrate");
        Assert.Equal(("Exited (1)", "failing", "N/A"), (migrate.StateText, migrate.StateKind, migrate.CpuText));
        Assert.Equal("stopped", page.AllRows.Single(r => r.Name == "old-worker").StateKind);

        // CPU and memory come from the stats stream, per container and for them all.
        await WaitUntil(() => web.Cpu is not null && page.CpuUsageText != "0.00%", TimeSpan.FromSeconds(10));
        Assert.EndsWith("%", web.CpuText);
        Assert.Equal("/ 800% (8 CPUs available)", page.CpuLimitText);
        Assert.Equal("/ 16 GB", page.MemoryLimitText);
        Assert.StartsWith("RAM ", vm.RamText);
        Assert.True(page.HasComposeFile);
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(300));
        Snapshot(window, "docker-containers");

        // Search, and only the running ones.
        page.Search = "cach";
        Assert.Equal(["cache"], page.Rows.OfType<ContainerRow>().Select(r => r.Name));
        // The image counts too: migrate runs shop-api.
        page.Search = "shop-api";
        Assert.Equal(["shop", "shop-api-1", "migrate"], page.Rows.Select(r => r is StackRow s ? s.Name : ((ContainerRow)r).Name));
        page.Search = "nothing-like-this";
        Assert.True(page.HasNoMatches);
        page.ClearSearchCommand.Execute(null);
        page.OnlyRunning = true;
        Assert.DoesNotContain(page.Rows.OfType<ContainerRow>(), r => r.Name is "migrate" or "old-worker");
        page.OnlyRunning = false;
        // A stack folds.
        shop.ToggleCommand.Execute(null);
        Assert.DoesNotContain(page.Rows, r => r == web);
        shop.ToggleCommand.Execute(null);

        // Stopping a container: the row says so while it happens, and the engine's events bring its new state.
        var cache = page.AllRows.Single(r => r.Name == "cache");
        await cache.StartStopCommand.ExecuteAsync(null);
        await WaitUntil(() => cache.StateText == "Exited (0)", TimeSpan.FromSeconds(10));
        Assert.False(cache.IsBusy);
        Assert.Contains(FakeDocker.Read(state)["calls"]!.AsArray(), c => c!.ToJsonString() == """["stop","dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"]""");
        // A change made elsewhere (the terminal) shows too.
        FakeDocker.Write(state, s =>
        {
            s["containers"]![5]!["state"] = "running";
            s["events"]!.AsArray().Add(JsonNode.Parse("""{"Type":"container","Action":"start","Actor":{"ID":"old-worker"}}"""));
        });
        var worker = page.AllRows.Single(r => r.Name == "old-worker");
        await WaitUntil(() => worker.IsRunning, TimeSpan.FromSeconds(10));

        // Ticked containers are stopped or deleted together; deleting asks first.
        migrate.IsSelected = true;
        worker.IsSelected = true;
        Assert.Equal(("2 items selected", true), (page.SelectionText, page.HasSelection));
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(150));
        Snapshot(window, "docker-selection");
        var deleting = page.DeleteSelectedCommand.ExecuteAsync(null);
        Assert.True(await Confirm(vm));
        await deleting;
        await WaitUntil(() => page.AllRows.All(r => r.Name is not ("migrate" or "old-worker")), TimeSpan.FromSeconds(10));
        Assert.False(page.HasSelection);

        // A stack stops as one.
        await shop.StartStopCommand.ExecuteAsync(null);
        await WaitUntil(() => shop.Children.All(c => !c.IsUp), TimeSpan.FromSeconds(10));
        Assert.Equal("Exited", shop.StateText);

        // Compose up in the solution's folder: its services, its output in Output > Docker.
        await page.ComposeUpCommand.ExecuteAsync(null);
        await WaitUntil(() => page.Stacks.Any(s => s.Name == Path.GetFileName(solution).ToLowerInvariant()), TimeSpan.FromSeconds(10));
        Assert.Contains(host.Output, l => l.Contains("Started", StringComparison.Ordinal));
        Assert.True(page.Stacks.Single(s => s.Name == Path.GetFileName(solution).ToLowerInvariant()).IsThisSolution);

        // A port opens in the browser.
        web.OpenPortCommand.Execute(null);
        Assert.Equal(["http://localhost:8080"], host.Urls);
        vm.Dispose();
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_container_page_has_its_logs_inspect_mounts_shell_and_stats()
    {
        var (vm, host, window, state) = Open();
        await vm.InitializeAsync();
        var db = vm.Containers.AllRows.Single(r => r.Name == "shop-db-1");
        db.OpenCommand.Execute(null);
        var page = host.Page<ContainerDetailViewModel>();
        Assert.Equal(("shop-db-1", "docker:container:" + new string('c', 64)), (host.Active!.Title, host.Active.Key));

        // Logs follow, colored as written; inspect is the engine's JSON.
        await WaitUntil(() => page.Logs.Document.Text.Contains("ready to accept connections", StringComparison.Ordinal), TimeSpan.FromSeconds(10));
        FakeDocker.Write(state, s => s["containers"]![2]!["logs"]!.AsArray().Add("checkpoint complete"));
        await WaitUntil(() => page.Logs.Document.Text.Contains("checkpoint complete", StringComparison.Ordinal), TimeSpan.FromSeconds(10));
        await WaitUntil(() => page.InspectJson.Contains("\"POSTGRES_DB=shop\"", StringComparison.Ordinal), TimeSpan.FromSeconds(10));
        Assert.StartsWith("Running (healthy) (", page.StatusText);
        Assert.True(page.IsDatabase);
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(300));
        Snapshot(window, "docker-container-logs");

        // Timestamps read them again with times.
        page.Timestamps = true;
        await WaitUntil(() => page.Logs.Document.Text.StartsWith("2026-10-20T08:00:00", StringComparison.Ordinal), TimeSpan.FromSeconds(10));

        page.ShowTabCommand.Execute("inspect");
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(200));
        Snapshot(window, "docker-container-inspect");
        page.ShowTabCommand.Execute("mounts");
        var mount = Assert.Single(page.Mounts);
        Assert.Equal(("volume", "shop_pgdata", "/var/lib/postgresql/data"), (mount.Type, mount.Source, mount.Destination));

        // Stats: a minute of samples for the charts.
        page.ShowTabCommand.Execute("stats");
        await WaitUntil(() => db.CpuHistory.Count >= 3, TimeSpan.FromSeconds(10));
        Assert.EndsWith("/s sent", db.NetRateText);
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(200));
        Snapshot(window, "docker-container-stats");

        // Exec: a shell in the container, in a real terminal.
        page.ShowTabCommand.Execute("exec");
        Assert.NotNull(page.Terminal);
        await WaitUntil(() => page.Terminal!.ReadText().Contains("fake shell in", StringComparison.Ordinal), TimeSpan.FromSeconds(10));

        // Claude gets its state and last lines; the Database Explorer gets it as a data source.
        page.AskClaudeCommand.Execute(null);
        Assert.Contains("The Docker container shop-db-1 (image postgres:17, compose project shop, service db)", Assert.Single(host.Prompts));
        Assert.Contains("checkpoint complete", host.Prompts[0]);
        await page.OpenInDatabaseExplorerCommand.ExecuteAsync(null);
        Assert.Equal(["shop-db-1"], host.Databases);

        // The image link opens the image's page in a tab of its own; the container's stays open.
        page.OpenImageCommand.Execute(null);
        var image = host.Page<ImageDetailViewModel>();
        Assert.Equal(("postgres", "17"), (image.Row.Name, image.Row.Tag));
        Assert.Equal("postgres:17", host.Active!.Title);
        Assert.Equal("Used by 1 container", image.UsedByText);
        await WaitUntil(() => image.Layers.Count == 3, TimeSpan.FromSeconds(10));
        Assert.Equal(2, host.Open.Count);
        // Opening the container again brings its tab back, on the tab asked for (the tree's Stats).
        db.OpenTabCommand.Execute("stats");
        Assert.Same(page, host.Page<ContainerDetailViewModel>());
        Assert.Equal(("stats", 2), (page.Tab, host.Open.Count));

        // A failed container: Claude is asked why.
        vm.Containers.AllRows.Single(r => r.Name == "migrate").OpenCommand.Execute(null);
        var failed = host.Page<ContainerDetailViewModel>();
        Assert.Equal("Ask Claude why it failed", failed.AskClaudeText);
        await WaitUntil(() => failed.Logs.Document.Text.Contains("does not exist", StringComparison.Ordinal), TimeSpan.FromSeconds(10));
        failed.AskClaudeCommand.Execute(null);
        Assert.EndsWith("Why does it fail, and how do I fix it?", host.Prompts[1]);

        // Deleted, its page closes with it.
        var deleting = failed.DeleteCommand.ExecuteAsync(null);
        await Confirm(vm);
        await deleting;
        await WaitUntil(() => host.Open.All(d => d.Content != failed), TimeSpan.FromSeconds(10));
        Assert.Equal(2, host.Open.Count);
        vm.Dispose();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Images_volumes_and_networks_are_listed_run_and_cleaned_up()
    {
        var (vm, host, window, state) = Open();
        await vm.InitializeAsync();
        vm.OpenSection(DockerSection.Images);
        var images = host.Page<ImagesPage>();
        Assert.Equal(7, images.Count);
        Assert.Equal("In use", images.AllRows.Single(r => r.Name == "nginx").StatusText);
        Assert.Equal("Unused", images.AllRows.Single(r => r.Name == "node").StatusText);
        Assert.Equal("1.21 GB in 2 unused images", images.UnusedText);
        images.OnlyUnused = true;
        Assert.Equal(["<none>", "node"], images.Rows.Select(r => r.Name).Order());
        images.OnlyUnused = false;
        // Size sorts the largest first, then the other way round.
        images.SortByCommand.Execute("size");
        Assert.Equal("node", images.Rows[0].Name);
        images.SortByCommand.Execute("size");
        Assert.Equal("alpine", images.Rows[0].Name);
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(250));
        Snapshot(window, "docker-images");

        // Run: the image's exposed port is offered; the new container's page opens on its logs.
        var running = images.AllRows.Single(r => r.Name == "node").RunCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.Dialog is RunDialog, TimeSpan.FromSeconds(5));
        var run = (RunDialog)vm.Dialog!;
        Assert.Equal("3000/tcp", Assert.Single(run.Ports).Container);
        run.ShowOptions = true;
        run.ContainerName = "frontend";
        run.Ports[0].Host = "3000";
        run.AddVariableCommand.Execute(null);
        run.Environment[0].Name = "NODE_ENV";
        run.Environment[0].Value = "production";
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(200));
        Snapshot(window, "docker-run");
        run.Confirm();
        await running;
        var created = host.Page<ContainerDetailViewModel>();
        Assert.Equal("frontend", created.Row.Name);
        Assert.Equal("3000:3000", created.Row.FirstPort!.Text);
        Assert.Contains(FakeDocker.Read(state)["calls"]!.AsArray(), c => c!.ToJsonString() ==
            """["run","-d","--name","frontend","-p","3000:3000/tcp","-e","NODE_ENV=production","node:22"]""");

        // Deleting an image a container uses: Docker refuses, and says so.
        vm.OpenSection(DockerSection.Images);
        var deleting = images.AllRows.Single(r => r.Name == "nginx").DeleteCommand.ExecuteAsync(null);
        await Confirm(vm);
        await deleting;
        Assert.Contains("container is using its referenced image", vm.Error);
        vm.DismissErrorCommand.Execute(null);

        // Clean up: what nothing uses goes.
        var cleaning = images.CleanUpCommand.ExecuteAsync(null);
        await Confirm(vm);
        await cleaning;
        await WaitUntil(() => images.AllRows.All(r => r.InUse), TimeSpan.FromSeconds(10));
        Assert.Equal("All images are in use", images.UnusedText);

        // Pull adds it.
        images.PullReference = "busybox:1.37";
        await images.PullCommand.ExecuteAsync(null);
        await WaitUntil(() => images.AllRows.Any(r => r.Name == "busybox"), TimeSpan.FromSeconds(10));

        // Volumes: named first, which are in use, created and deleted.
        vm.OpenSection(DockerSection.Volumes);
        var volumes = host.Page<VolumesPage>();
        Assert.Equal(["cache-data", "scratch", "shop_pgdata", new string('9', 64)], volumes.Rows.Select(r => r.Name));
        Assert.True(volumes.AllRows.Single(r => r.Name == "shop_pgdata").InUse);
        Assert.Equal("68.8 MB", volumes.AllRows.Single(r => r.Name == "shop_pgdata").SizeText);
        var creating = volumes.CreateCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.Dialog is InputDialog, TimeSpan.FromSeconds(5));
        ((InputDialog)vm.Dialog!).Text = "uploads";
        vm.Dialog!.Confirm();
        await creating;
        await WaitUntil(() => volumes.AllRows.Any(r => r.Name == "uploads"), TimeSpan.FromSeconds(10));
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(200));
        Snapshot(window, "docker-volumes");
        volumes.AllRows.Single(r => r.Name == "shop_pgdata").OpenCommand.Execute(null);
        var volume = host.Page<VolumeDetailViewModel>();
        Assert.Equal("shop-db-1", Assert.Single(volume.UsedBy).Name);
        var removing = volumes.AllRows.Single(r => r.Name == "scratch").DeleteCommand.ExecuteAsync(null);
        await Confirm(vm);
        await removing;
        await WaitUntil(() => volumes.AllRows.All(r => r.Name != "scratch"), TimeSpan.FromSeconds(10));

        // Networks: the engine's own first and not deletable; how many containers are on each.
        vm.OpenSection(DockerSection.Networks);
        var networks = host.Page<NetworksPage>();
        Assert.Equal(["bridge", "host", "none", "shop_default"], networks.Rows.Select(r => r.Name));
        Assert.False(networks.Rows[0].CanDelete);
        Assert.Equal((3, "shop"), (networks.Rows[3].Containers, networks.Rows[3].Project));
        networks.Rows[3].OpenCommand.Execute(null);
        var network = host.Page<NetworkDetailViewModel>();
        await WaitUntil(() => network.Members.Count == 3, TimeSpan.FromSeconds(10));
        Assert.Equal("172.18.0.0/16", network.SubnetText);
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(200));
        Snapshot(window, "docker-network");
        vm.Dispose();
        window.Close();
    }

    [AvaloniaFact]
    public async Task An_engine_that_is_not_running_says_so_and_is_tried_again()
    {
        var sample = FakeDocker.Sample();
        sample["daemon"] = "down";
        var (vm, _, window, state) = Open(state: sample);
        await vm.InitializeAsync();
        Assert.True(vm.IsEngineStopped);
        Assert.Contains("failed to connect to the docker API", vm.EngineMessage);
        Assert.StartsWith("Start the Docker daemon", vm.EngineHint);
        Assert.Equal("Engine stopped", vm.EngineStatusText);
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(200));
        Snapshot(window, "docker-stopped");

        // Once it runs, Retry (or the next try) connects.
        FakeDocker.Write(state, s => s.Remove("daemon"));
        await vm.RetryCommand.ExecuteAsync(null);
        Assert.True(vm.IsEngineRunning);
        Assert.Equal(6, vm.Containers.Count);
        Assert.Equal([6, 7, 4, 4], vm.Sections.Select(s => s.Count));
        vm.Dispose();
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_tree_lists_what_the_engine_has_and_opens_it_as_documents()
    {
        var solution = Path.Combine(Path.GetTempPath(), "vsl-docker-solution-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(solution);
        var (vm, host, window, state) = Open(solution);
        await vm.InitializeAsync();
        string Name(DockerTreeItem item) => item switch
        {
            StackRow s => s.Name,
            ContainerRow c => c.Name,
            ImageRow i => i.Reference,
            VolumeRow v => v.TreeName,
            NetworkRow n => n.Name,
            _ => "",
        };

        // Containers (the solution's stack first, its containers under it), images (untagged last), volumes, networks.
        Assert.Equal(["Containers", "Images", "Volumes", "Networks"], vm.Sections.Select(s => s.Title));
        Assert.Equal([6, 7, 4, 4], vm.Sections.Select(s => s.Count));
        var containers = vm.Sections[0];
        Assert.True(containers.IsTreeExpanded);
        Assert.Equal(["shop", "cache", "migrate", "old-worker"], containers.Items.Select(Name));
        var shop = Assert.IsType<StackRow>(containers.Items[0]);
        Assert.Equal(["shop-api-1", "shop-db-1", "shop-web-1"], shop.TreeChildren.Select(Name));
        Assert.Equal(("3/3 running", true), (shop.TreeText, shop.IsThisSolution));
        Assert.Equal(["8080:80", "Exited (1)"], new[] { "shop-web-1", "migrate" }.Select(n => vm.Containers.AllRows.Single(r => r.Name == n).TreeText));
        Assert.Equal(["alpine:3.20", "nginx:1.27", "node:22", "postgres:17", "redis:7-alpine", "shop-api:latest", "<none> (777777777777)"], vm.Sections[1].Items.Select(Name));
        Assert.Equal(["cache-data", "scratch", "shop_pgdata", "999999999999…"], vm.Sections[2].Items.Select(Name));
        Assert.Equal(["bridge", "host", "none", "shop_default"], vm.Sections[3].Items.Select(Name));
        vm.Sections[1].IsTreeExpanded = true;
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(300));
        Snapshot(window, "docker-tree");

        // The search keeps what matches, a stack's containers by theirs, and opens the sections it finds something in.
        vm.CollapseAllCommand.Execute(null);
        vm.Filter = "db";
        Assert.Equal(["shop"], containers.Items.Select(Name));
        Assert.Equal(["shop-db-1"], shop.TreeChildren.Select(Name));
        Assert.True(containers.IsTreeExpanded);
        Assert.Empty(vm.Sections[3].Items);
        vm.Filter = "";
        Assert.Equal(3, shop.TreeChildren.Count);

        // Enter on a section opens its table; again, the same document.
        var tree = window.GetVisualDescendants().OfType<TreeView>().Single(t => t.Name == "Tree");
        tree.SelectedItem = vm.Sections[2];
        tree.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Assert.IsType<VolumesPage>(host.Active?.Content);
        vm.Open(vm.Sections[2]);
        Assert.Single(host.Open);

        // A double-click on a container opens its page.
        containers.IsTreeExpanded = true;
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(200));
        var cache = vm.Containers.AllRows.Single(r => r.Name == "cache");
        var item = (TreeViewItem)tree.GetVisualDescendants().OfType<TreeViewItem>().Single(i => i.DataContext == cache);
        var point = item.TranslatePoint(new Point(80, 10), window)!.Value;
        for (var click = 0; click < 2; click++)
        {
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
        }
        await WaitUntil(() => host.Active?.Content is ContainerDetailViewModel { Row.Name: "cache" }, TimeSpan.FromSeconds(5));
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(300));
        Snapshot(window, "docker-tree-container");

        // Stopped from the tree: the tree, the open page and the table follow the engine.
        await cache.StartStopCommand.ExecuteAsync(null);
        await WaitUntil(() => cache.TreeText == "Exited (0)", TimeSpan.FromSeconds(10));
        // Deleted elsewhere (the terminal): it leaves the tree, and its page closes.
        FakeDocker.Write(state, s =>
        {
            s["containers"]!.AsArray().RemoveAt(3);
            s["events"]!.AsArray().Add(JsonNode.Parse("""{"Type":"container","Action":"destroy","Actor":{"ID":"cache"}}"""));
        });
        await WaitUntil(() => containers.Items.All(i => i != cache), TimeSpan.FromSeconds(10));
        Assert.Equal(5, containers.Count);
        Assert.IsType<VolumesPage>(Assert.Single(host.Open).Content);
        vm.Dispose();
        window.Close();
    }

    [AvaloniaFact]
    public async Task View_menu_shows_the_docker_explorer_tool_window()
    {
        var (docker, _, _) = FakeDocker.Create();
        var engine = Environment.GetEnvironmentVariable("VSL_DOCKER_PATH");
        Environment.SetEnvironmentVariable("VSL_DOCKER_PATH", docker);
        try
        {
            var vm = NewViewModel();
            var window = new Views.MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            // Nothing asks the engine until the explorer shows.
            Assert.Null(vm.Docker);
            vm.ShowDockerExplorerCommand.Execute(null);
            var explorer = vm.Docker!;
            await WaitUntil(() => window.GetVisualDescendants().OfType<DockerExplorerView>().Any(v => v.IsEffectivelyVisible), TimeSpan.FromSeconds(5));
            var tab = window.GetVisualDescendants().OfType<TabItem>().Single(t => (string?)t.Tag == "DockerExplorer");
            Assert.True(tab.IsSelected);
            await WaitUntil(() => explorer.IsEngineRunning && explorer.Containers.Count == 6, TimeSpan.FromSeconds(10));
            Assert.Contains(vm.OutputChannels, c => c.Name == "Docker");

            // What it opens are documents in the document well, each once.
            explorer.Open(explorer.Sections[0]);
            explorer.Open(explorer.Sections[0]);
            var containers = Assert.Single(vm.Documents.OfType<DockerDocument>());
            Assert.Equal((containers, "Containers"), (vm.ActiveDocument, containers.Title));
            await WaitUntil(() => window.GetVisualDescendants().OfType<DockerContainersView>().Any(v => v.IsEffectivelyVisible), TimeSpan.FromSeconds(5));
            var cache = explorer.Containers.AllRows.Single(r => r.Name == "cache");
            explorer.Open(cache);
            Assert.Equal(2, vm.Documents.OfType<DockerDocument>().Count());
            await WaitUntil(() => window.GetVisualDescendants().OfType<DockerContainerView>().Any(v => v.IsEffectivelyVisible), TimeSpan.FromSeconds(5));

            // Deleting asks over the window; the container's page goes with it.
            var deleting = cache.DeleteCommand.ExecuteAsync(null);
            await WaitUntil(() => window.GetVisualDescendants().OfType<Panel>().Any(p => p.Name == "DialogLayer" && p.IsEffectivelyVisible), TimeSpan.FromSeconds(5));
            explorer.Dialog!.Confirm();
            await deleting;
            await WaitUntil(() => vm.Documents.OfType<DockerDocument>().Count() == 1, TimeSpan.FromSeconds(10));

            // Ask Claude puts the prompt in the Claude panel without sending it.
            ((IDockerHost)vm).AskClaude("Why did migrate exit?");
            Assert.Equal("Why did migrate exit?", vm.Claude.Prompt);
            await vm.CloseDocumentCommand.ExecuteAsync(containers);
            Assert.Empty(vm.Documents.OfType<DockerDocument>());
            vm.ShutdownDocker();
            window.Close();
        }
        finally
        {
            Environment.SetEnvironmentVariable("VSL_DOCKER_PATH", engine);
        }
    }

    /// <summary>A compose file whose api reaches the database through localhost (and a worker that is right).</summary>
    const string BrokenCompose = """
        services:
          api:
            image: shop-api:latest
            environment:
              ConnectionStrings__Default: Host=localhost;Port=5432;Database=shop
              ASPNETCORE_URLS: http://+:8080
          db:
            image: postgres:17
          worker:
            image: alpine:3.20
            environment:
              - CACHE=redis:6379
        """;

    [Fact]
    public void A_service_that_reaches_another_through_localhost_is_a_compose_problem()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vsl-compose-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "compose.yaml");
        File.WriteAllText(file, BrokenCompose);
        var problem = Assert.Single(Compose.Inspect(file));
        Assert.Equal("api", problem.Service);
        Assert.Equal("api: ConnectionStrings__Default uses localhost, which inside a container is api itself. Use the service name: db.", problem.Text);
        // A list of NAME=value counts as well; a file that cannot be read has none.
        File.WriteAllText(file, "services:\n  web:\n    environment:\n      - API=http://127.0.0.1:5000\n  api:\n    image: shop-api\n");
        Assert.Equal("web: API uses localhost, which inside a container is web itself. Use the service name: api.", Assert.Single(Compose.Inspect(file)).Text);
        File.WriteAllText(file, "services: [unclosed");
        Assert.Empty(Compose.Inspect(file));
    }

    [AvaloniaFact]
    public async Task Compose_problems_show_on_the_solutions_stack_and_go_to_Claude()
    {
        var solution = Path.Combine(Path.GetTempPath(), "vsl-docker-solution-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(solution);
        File.WriteAllText(Path.Combine(solution, "compose.yaml"), BrokenCompose);
        var sample = FakeDocker.Sample(solution);
        sample["containers"]![1]!["logs"]!.AsArray().Add("Npgsql: connecting with Password=hunter2 failed");
        var (vm, host, window, _) = Open(solution, sample);
        await vm.InitializeAsync();
        vm.OpenSection(DockerSection.Containers);
        var page = host.Page<ContainersPage>();

        // The banner lists it; the stack and the api container are marked, the others are not.
        Assert.True(page.HasComposeProblems);
        var api = page.AllRows.Single(r => r.Name == "shop-api-1");
        Assert.True(api.HasProblems);
        Assert.False(page.AllRows.Single(r => r.Name == "shop-db-1").HasProblems);
        Assert.False(page.AllRows.Single(r => r.Name == "cache").HasProblems);
        Assert.True(page.Stacks.Single().HasProblems);
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(300));
        Snapshot(window, "docker-compose-problems");

        // Asking Claude about it: its state, the problem and its last lines, with the password hidden.
        await api.AskClaudeCommand.ExecuteAsync(null);
        var prompt = Assert.Single(host.Prompts);
        Assert.Contains("The compose file: api: ConnectionStrings__Default uses localhost", prompt);
        Assert.Contains("ERR connection refused, retrying", prompt);
        Assert.Contains("Password=***", prompt);
        Assert.DoesNotContain("hunter2", prompt);
        Assert.EndsWith("Why does it fail, and how do I fix it?", prompt);

        // Its page says so too.
        api.OpenCommand.Execute(null);
        var detail = host.Page<ContainerDetailViewModel>();
        Assert.Equal("Ask Claude about it", detail.AskClaudeText);
        vm.Dispose();
        window.Close();
    }

    [Fact]
    public void The_cli_output_is_read_as_docker_prints_it()
    {
        Assert.Equal(
            [new DockerPort(5433, 5432, "tcp"), new DockerPort(null, 6379, "tcp")],
            DockerFormat.ParsePorts("0.0.0.0:5433->5432/tcp, [::]:5433->5432/tcp, 6379/tcp"));
        Assert.Equal([8000, 8001, 8002], DockerFormat.ParsePorts("0.0.0.0:8000-8002->8000-8002/tcp").Select(p => p.HostPort!.Value));
        // A port that is published is not also listed as only exposed.
        Assert.Single(DockerFormat.ParsePorts("0.0.0.0:8080->80/tcp, 80/tcp"));
        Assert.Equal(345_000_000, DockerFormat.ParseSize("345MB"));
        Assert.Equal(20_500, DockerFormat.ParseSize("20.5kB (virtual 379MB)"));
        Assert.Equal((23_802_675L, 32_684_701_123L), DockerFormat.ParsePair("22.7MiB / 30.44GiB"));
        Assert.Null(DockerFormat.ParseSize("N/A"));
        Assert.Equal("345 MB", DockerFormat.Size(345_000_000));
        Assert.Equal("1.27 GB", DockerFormat.Size(1_270_000_000));
        Assert.Equal(new DateTimeOffset(2026, 10, 15, 17, 39, 57, TimeSpan.FromHours(5)), DockerFormat.ParseTime("2026-10-15 17:39:57 +0500 +05"));
        Assert.Equal(new DateTimeOffset(2026, 10, 8, 10, 46, 23, TimeSpan.FromHours(5)), DockerFormat.ParseTime("2026-10-08 10:46:23.50047149 +0500 +05"));
        Assert.Null(DockerFormat.ParseTime("0001-01-01T00:00:00Z"));
        var now = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal("5 minutes ago", DockerFormat.Ago(now.AddMinutes(-5), now));
        Assert.Equal("1 day ago", DockerFormat.Ago(now.AddHours(-30), now));
        var stats = DockerEngine.ParseStats("\u001b[H{\"BlockIO\":\"52.1MB / 0B\",\"CPUPerc\":\"1.25%\",\"ID\":\"976923\",\"MemUsage\":\"22.7MiB / 30.44GiB\",\"NetIO\":\"37.7kB / 126B\",\"PIDs\":\"7\"}");
        Assert.Equal(("976923", 1.25, 37_700L, 7), (stats!.Id, stats.Cpu, stats.NetReceived, stats.Pids));
        var labels = DockerFormat.ParseLabels(JsonDocument.Parse("\"com.docker.compose.project=shop,com.docker.compose.service=web\"").RootElement);
        Assert.Equal("shop", labels["com.docker.compose.project"]);
        Assert.True(new DockerContainer("x", "x", "i", "exited", "Exited (137) 2 hours ago", labels, [], "", null, [], []).IsFailing);
        Assert.Equal("unhealthy", new DockerContainer("x", "x", "i", "running", "Up 2 hours (unhealthy)", labels, [], "", null, [], []).Health);
        Assert.Equal("starting", new DockerContainer("x", "x", "i", "running", "Up 1 second (health: starting)", labels, [], "", null, [], []).Health);
    }
}
