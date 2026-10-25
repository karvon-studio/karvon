using Avalonia.Headless.XUnit;
using Karvon.Modules;
using Karvon.Servers;
using Karvon.Views;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>A machine's files of the tests': a folder here, its times to the second as SFTP gives them.</summary>
sealed class FolderRemoteFiles(string root) : IRemoteFiles
{
    public string Root { get; } = root;

    public string Real(string path) => Path.Combine(Root, path.TrimStart('/'));

    static DateTime Seconds(DateTime time) => new(time.Ticks - time.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);

    public int Downloads { get; private set; }

    public Task<IReadOnlyList<RemoteFileInfo>> ListAsync(string folder, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<RemoteFileInfo>>([.. new DirectoryInfo(Real(folder)).EnumerateFileSystemInfos().Select(Info)]);

    static RemoteFileInfo Info(FileSystemInfo info) =>
        new(info.Name, info is DirectoryInfo, info is FileInfo file ? file.Length : 0, Seconds(info.LastWriteTimeUtc), info.LinkTarget is not null);

    public Task<RemoteFileInfo?> InfoAsync(string path, CancellationToken ct) =>
        Task.FromResult(File.Exists(Real(path)) ? Info(new FileInfo(Real(path))) : Directory.Exists(Real(path)) ? Info(new DirectoryInfo(Real(path))) : null);

    public async Task DownloadAsync(string path, Stream to, CancellationToken ct)
    {
        Downloads++;
        await using var from = File.OpenRead(Real(path));
        await from.CopyToAsync(to, ct);
    }

    public async Task<DateTime> UploadAsync(Stream from, string path, CancellationToken ct)
    {
        await using (var to = File.Create(Real(path)))
            await from.CopyToAsync(to, ct);
        return Seconds(File.GetLastWriteTimeUtc(Real(path)));
    }

    public Task DeleteAsync(string path, CancellationToken ct)
    {
        File.Delete(Real(path));
        return Task.CompletedTask;
    }

    public Task CreateFolderAsync(string path, CancellationToken ct)
    {
        Directory.CreateDirectory(Real(path));
        return Task.CompletedTask;
    }

    /// <summary>A file written there, its time moved on (as another write, a second later, would leave it).</summary>
    public void Write(string path, string text, int seconds = 5)
    {
        var real = Real(path);
        Directory.CreateDirectory(Path.GetDirectoryName(real)!);
        var before = File.Exists(real) ? File.GetLastWriteTimeUtc(real) : DateTime.UtcNow;
        File.WriteAllText(real, text);
        File.SetLastWriteTimeUtc(real, before.AddSeconds(seconds));
    }
}

/// <summary>Remote folders: a machine's folder copied here, kept in step both ways, its commands run there.</summary>
public class RemoteFolderTests
{
    static (FolderRemoteFiles Server, string Mirrors) NewServer()
    {
        var root = Path.Combine(Path.GetTempPath(), "karvon-remote-" + Guid.NewGuid().ToString("N"));
        var server = new FolderRemoteFiles(Path.Combine(root, "server"));
        server.Write("/srv/my app/src/app.js", "console.log('hi');\n");
        server.Write("/srv/my app/README.md", "# app\n");
        server.Write("/srv/my app/node_modules/left/index.js", "module.exports = 1;\n");
        server.Write("/srv/my app/.git/HEAD", "ref: refs/heads/main\n");
        File.WriteAllBytes(server.Real("/srv/my app/big.bin"), new byte[RemoteFolder.LargestFile + 1]);
        return (server, Path.Combine(root, "mirrors"));
    }

    static void Touch(string file, string text)
    {
        var before = File.GetLastWriteTimeUtc(file);
        File.WriteAllText(file, text);
        File.SetLastWriteTimeUtc(file, before.AddSeconds(3));
    }

    [Fact]
    public async Task A_folder_is_copied_and_kept_in_step_both_ways()
    {
        var (server, mirrors) = NewServer();
        var ct = TestContext.Current.CancellationToken;
        var folder = RemoteFolder.For(mirrors, "lab", "/srv/my app");
        Assert.Matches(@"[/\\]lab[/\\]my_app-[0-9a-f]{8}$", folder.LocalRoot);

        // What is built or installed, and what is too large, stays there.
        var pulled = await folder.PullAsync(server, ct);
        Assert.Equal((2, 0), (pulled.Copied, pulled.Deleted));
        Assert.Equal(["big.bin (4.8 MB)"], pulled.Left);
        var app = Path.Combine(folder.LocalRoot, "src", "app.js");
        Assert.Equal("console.log('hi');\n", File.ReadAllText(app));
        Assert.False(Directory.Exists(Path.Combine(folder.LocalRoot, "node_modules")));
        Assert.False(Directory.Exists(Path.Combine(folder.LocalRoot, ".git")));
        // Nothing changed: nothing comes again; the manifest says whose copy it is.
        var downloads = server.Downloads;
        Assert.Equal(0, (await folder.PullAsync(server, ct)).Copied);
        Assert.Equal(downloads, server.Downloads);
        Assert.Equal(("lab", "/srv/my app"), (RemoteFolder.Of(folder.LocalRoot)!.Server, RemoteFolder.Of(folder.LocalRoot)!.RemoteRoot));
        Assert.Empty(folder.LocalChanges());

        // Changed, made and deleted here: sent there, its folders made.
        Touch(app, "console.log('changed here');\n");
        Directory.CreateDirectory(Path.Combine(folder.LocalRoot, "docs"));
        File.WriteAllText(Path.Combine(folder.LocalRoot, "docs", "notes.md"), "notes\n");
        File.Delete(Path.Combine(folder.LocalRoot, "README.md"));
        Assert.Equal([("README.md", true), ("docs/notes.md", false), ("src/app.js", false)], folder.LocalChanges().OrderBy(c => c.Path, StringComparer.Ordinal));
        var pushed = await folder.PushAsync(server, ct);
        Assert.Equal((2, 1), (pushed.Copied, pushed.Deleted));
        Assert.Equal("console.log('changed here');\n", File.ReadAllText(server.Real("/srv/my app/src/app.js")));
        Assert.Equal("notes\n", File.ReadAllText(server.Real("/srv/my app/docs/notes.md")));
        Assert.False(File.Exists(server.Real("/srv/my app/README.md")));
        Assert.Empty(folder.LocalChanges());

        // Changed there: it comes here; deleted there: gone here.
        server.Write("/srv/my app/src/app.js", "console.log('changed there');\n");
        File.Delete(server.Real("/srv/my app/docs/notes.md"));
        pulled = await folder.PullAsync(server, ct);
        Assert.Equal((1, 1), (pulled.Copied, pulled.Deleted));
        Assert.Equal("console.log('changed there');\n", File.ReadAllText(app));
        Assert.False(File.Exists(Path.Combine(folder.LocalRoot, "docs", "notes.md")));

        // Changed on both sides: left as it is on each, until one is chosen.
        server.Write("/srv/my app/src/app.js", "theirs\n");
        Touch(app, "mine\n");
        Assert.Equal(["src/app.js"], (await folder.PushAsync(server, ct)).Conflicts);
        Assert.Equal("theirs\n", File.ReadAllText(server.Real("/srv/my app/src/app.js")));
        Assert.Equal(["src/app.js"], (await folder.PullAsync(server, ct)).Conflicts);
        Assert.Equal("mine\n", File.ReadAllText(app));
        await folder.PushOneAsync(server, "src/app.js", ct);
        Assert.Equal("mine\n", File.ReadAllText(server.Real("/srv/my app/src/app.js")));
        Assert.Empty(folder.LocalChanges());
        server.Write("/srv/my app/src/app.js", "theirs again\n");
        Touch(app, "mine again\n");
        await folder.PullOneAsync(server, "src/app.js", ct);
        Assert.Equal("theirs again\n", File.ReadAllText(app));
    }

    [Fact]
    public void Commands_run_in_the_folder_there_and_its_paths_read_as_the_copys()
    {
        var (_, mirrors) = NewServer();
        var folder = RemoteFolder.For(mirrors, "lab", "/srv/my app");
        var src = Path.Combine(folder.LocalRoot, "src");
        Assert.Equal("/srv/my app/src", folder.RemotePathOf(src));
        Assert.Equal("/srv/my app", folder.RemotePathOf(folder.LocalRoot));
        Assert.Null(folder.RemotePathOf(Path.GetTempPath()));
        Assert.Equal(Path.Combine(folder.LocalRoot, "src", "app.js"), folder.LocalPathOf("/srv/my app/src/app.js"));
        Assert.Equal("/etc/hosts", folder.LocalPathOf("/etc/hosts"));

        // A login shell there, in the folder; the program found there by its name, the copy's paths as the machine's.
        Assert.Equal("sh -lc 'cd '\\''/srv/my app/src'\\'' && env NODE_ENV='\\''dev'\\'' exec '\\''node'\\'' '\\''/srv/my app/src/app.js'\\'' '\\''it'\\''\\'\\'''\\''s'\\'''",
            folder.CommandThere("/usr/local/bin/node", [Path.Combine(src, "app.js"), "it's"], src, new Dictionary<string, string> { ["PATH"] = "/here", ["NODE_ENV"] = "dev" }));
        Assert.Equal("'it'\\''s'", RemoteFolder.Quote("it's"));

        var host = new ServerHost("lab", "10.0.0.5", 2222, "tester") { Source = "Karvon" };
        Assert.Equal(["-tt", "-p", "2222", "tester@10.0.0.5", "uptime"], SshCommand.Running(host, "uptime"));
    }

    [AvaloniaFact]
    public async Task A_remote_folder_opens_as_the_workspace_and_its_commands_run_there()
    {
        var (server, _) = NewServer();
        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        window.Show();
        var servers = vm.Module<ServersModule>()!;
        var host = new ServerHost("lab-" + Guid.NewGuid().ToString("N")[..6], "10.0.0.5", 22, "tester") { Source = "Karvon" };
        await servers.OpenRemoteFolderAsync(host, "/srv/my app", server);
        var dir = vm.Solution!.Directory;
        Assert.StartsWith(ServersModule.RemoteFoldersRoot, dir);
        Assert.True(File.Exists(Path.Combine(dir, "src", "app.js")));
        Assert.Equal($"SSH: {host.Name}", servers.StatusItem!.Text);
        Assert.True(servers.StatusItem.IsVisible);

        // Its tasks run there: ssh, in the folder there.
        var remote = vm.Remote!;
        var task = remote.Run(new WorkspaceTask("dev", WorkspaceTaskKind.Run, "npm", ["run", "dev"], Path.Combine(dir, "src")));
        Assert.Equal("ssh", task.Program);
        Assert.Equal(["-tt", "tester@10.0.0.5", "sh -lc 'cd '\\''/srv/my app/src'\\'' && exec '\\''npm'\\'' '\\''run'\\'' '\\''dev'\\'''"], task.Arguments);
        Assert.Equal(["-tt", "tester@10.0.0.5", "cd '/srv/my app' && exec \"${SHELL:-sh}\" -l"], remote.Terminal(dir).Arguments);
        Assert.Equal(Path.Combine(dir, "src", "app.js"), remote.LocalPath("/srv/my app/src/app.js"));

        // Saved in the editor: sent there a moment after; before a task, at once.
        var doc = vm.OpenDocument(Path.Combine(dir, "src", "app.js"))!;
        doc.Host.Document.Text = "console.log('edited in Karvon');\n";
        doc.Host.Save();
        await WaitUntil(() => File.ReadAllText(server.Real("/srv/my app/src/app.js")).Contains("edited in Karvon"), TimeSpan.FromSeconds(10));
        File.WriteAllText(Path.Combine(dir, "new.txt"), "new\n");
        await remote.FlushAsync();
        Assert.Equal("new\n", File.ReadAllText(server.Real("/srv/my app/new.txt")));

        // Pull (the status item): what changed there comes here.
        server.Write("/srv/my app/README.md", "# changed there\n");
        servers.StatusItem.Click!();
        await WaitUntil(() => File.ReadAllText(Path.Combine(dir, "README.md")) == "# changed there\n", TimeSpan.FromSeconds(10));

        // Closed: what is left goes there, and the workspace is here again.
        File.WriteAllText(Path.Combine(dir, "last.txt"), "last\n");
        await vm.CloseSolutionCommand.ExecuteAsync(null);
        Assert.Equal("last\n", File.ReadAllText(server.Real("/srv/my app/last.txt")));
        Assert.False(servers.StatusItem.IsVisible);
        Assert.Null(vm.Remote);
        window.Close();
    }
}
