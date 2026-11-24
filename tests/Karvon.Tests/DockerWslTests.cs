using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Karvon.Docker;
using Karvon.Docker.ViewModels;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>Docker in WSL, run from Windows by wsl.exe (a stand-in for it here: TestAssets/FakeWsl): its commands, its paths, what ends with Karvon.</summary>
public class DockerWslTests
{
    /// <summary>A wsl whose distribution has the fake docker (or none): its program, the docker's state, and the ids of what it ran.</summary>
    static (string Wsl, string State, string Pids) Create(JsonObject? state = null, bool docker = true)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The stand-in for wsl.exe forks as Unix does.");
        var (_, statePath, folder) = FakeDocker.Create(state);
        var dir = Directory.CreateTempSubdirectory("karvon-wsl-").FullName;
        var script = Path.Combine(dir, "wsl.py");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "TestAssets", "FakeWsl", "wsl.py"), script);
        var pids = Path.Combine(dir, "pids");
        var wsl = TestTools.Command(script, dir, new Dictionary<string, string> { ["FAKE_WSL_PATH"] = docker ? folder : dir, ["FAKE_WSL_PIDS"] = pids });
        return (wsl, statePath, pids);
    }

    static string[][] Calls(string state) => FakeDocker.Read(state)["calls"]!.Deserialize<string[][]>()!;

    [Theory]
    [InlineData(@"E:\shop\api", "/mnt/e/shop/api")]
    [InlineData("e:/shop/../api/./x", "/mnt/e/api/x")]
    [InlineData(@"C:\", "/mnt/c")]
    [InlineData(@"\\wsl.localhost\Ubuntu\home\me\shop", "/home/me/shop")]
    [InlineData(@"\\wsl$\Ubuntu-24.04\home\me", "/home/me")]
    [InlineData("//wsl.localhost/Ubuntu/home/me", "/home/me")]
    [InlineData("/home/me/../../etc", "/etc")]
    [InlineData(@"\\server\share\x", null)]
    [InlineData(@"\\?\C:\Users\me", null)]
    [InlineData("data", null)]
    public void A_path_is_said_as_WSL_says_it(string path, string? there) => Assert.Equal(there, WslPaths.ToLinux(path));

    [Fact]
    public void A_path_of_WSLs_is_known_on_Windows_and_in_a_folder()
    {
        Assert.Equal(@"E:\shop\api", WslPaths.ToWindows("/mnt/e/shop/api"));
        Assert.Null(WslPaths.ToWindows("/home/me"));
        Assert.Null(WslPaths.ToWindows("/mnt/wslg/x"));
        // A drive's files whatever their case, as Windows; the distribution's own as they are.
        Assert.True(WslPaths.IsInside("/mnt/e/Shop/api", "/mnt/e/shop"));
        Assert.False(WslPaths.IsInside("/home/me/Shop/api", "/home/me/shop"));
        Assert.False(WslPaths.IsInside("/mnt/e/shop2", "/mnt/e/shop"));
        Assert.True(WslPaths.IsInside("/home/me/shop", "/home/me/shop"));
    }

    [Theory]
    [InlineData(@"E:\shop:/src", true, @"E:\shop", "/src")]
    [InlineData("E:/shop:/src:ro", true, "E:/shop", "/src:ro")]
    [InlineData("./src:/src:ro", true, "./src", "/src:ro")]
    [InlineData("a:/x", false, "a", "/x")]
    [InlineData("/home/me/x:/x", true, "/home/me/x", "/x")]
    [InlineData(@"E:\shop", true, null, null)]
    [InlineData("data", true, null, null)]
    [InlineData("data:", true, null, null)]
    public void A_mounts_host_and_container_are_told_apart_with_a_drives_colon(string mount, bool drives, string? host, string? container)
    {
        var parts = ContainerRunTool.Mount(mount, drives);
        Assert.Equal((host, container), (parts?.Host, parts?.Container));
    }

    [AvaloniaFact]
    public async Task The_engine_in_WSL_is_listed_followed_and_its_solutions_stack_known()
    {
        var (wsl, state, _) = Create(FakeDocker.Sample("/mnt/e/Shop"));
        var engine = new DockerEngine(wsl);
        Assert.True(engine.IsWsl);
        Assert.Equal("docker", engine.Name);
        Assert.Equal(["--exec", "docker", "exec", "-it"], engine.Command(["exec", "-it"]));

        // Its arguments whole ({{json .}} has a space), WSL's own note beside its words not taken for them.
        var containers = await engine.ContainersAsync(TestContext.Current.CancellationToken);
        Assert.Equal(6, containers.Count);
        Assert.Equal(["ps", "-a", "--no-trunc", "--format", "{{json .}}"], Calls(state).First(c => c[0] == "ps"));
        var failed = await engine.RunAsync(["inspect", "no-such"], cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(failed.Succeeded);
        Assert.DoesNotContain("wsl: ", failed.Message);

        // Compose ran there in the folder Windows has open.
        var web = containers.First(c => c.Service == "web");
        Assert.True(Compose.IsInFolder(engine, web.WorkingDir, @"E:\shop"));
        Assert.False(Compose.IsInFolder(engine, web.WorkingDir, @"E:\other"));

        var vm = new DockerExplorerViewModel(engine, new TestDockerHost(@"E:\shop"));
        try
        {
            await vm.InitializeAsync();
            await WaitUntil(() => vm.IsEngineRunning && vm.Containers.Count == 6, TimeSpan.FromSeconds(10));
            Assert.EndsWith(" in WSL", vm.VersionText);
            Assert.Contains(vm.Containers.Stacks, s => s.IsThisSolution);
            // Its events are followed (from when it started to say them).
            await WaitUntil(() => Calls(state).Any(c => c[0] == "events"), TimeSpan.FromSeconds(10));
            await WaitUntil(() => false, TimeSpan.FromMilliseconds(500));
            FakeDocker.Write(state, s =>
            {
                s["containers"]!.AsArray().RemoveAt(3);
                s["events"]!.AsArray().Add(JsonNode.Parse("""{"Type":"container","Action":"destroy","Actor":{"ID":"cache"}}"""));
            });
            await WaitUntil(() => vm.Containers.Count == 5, TimeSpan.FromSeconds(15));
            Assert.Equal(5, vm.Containers.Count);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task What_is_followed_in_WSL_ends_with_it_and_says_how_it_ended()
    {
        var (wsl, _, pids) = Create();
        var engine = new DockerEngine(wsl);
        var lines = new ConcurrentQueue<string>();
        var stream = engine.Stream(["events", "--format", "{{json .}}"], lines.Enqueue);
        await WaitUntil(() => File.Exists(pids), TimeSpan.FromSeconds(10));
        await Task.Delay(500, TestContext.Current.CancellationToken);
        var pid = File.ReadAllLines(pids)[0];
        Assert.True(Directory.Exists("/proc/" + pid));

        // wsl.exe is ended with the stream; what ran in WSL (not its child) ends too.
        stream.Dispose();
        await WaitUntil(() => !Directory.Exists("/proc/" + pid), TimeSpan.FromSeconds(10));
        Assert.False(Directory.Exists("/proc/" + pid));
        Assert.DoesNotContain(lines, line => line.StartsWith("wsl: ", StringComparison.Ordinal));

        // One that ends by itself gives its output and its code.
        var exit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var inspect = engine.Stream(["inspect", "no-such"], lines.Enqueue, exit.SetResult);
        Assert.NotEqual(0, await exit.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_WSL_without_docker_says_so()
    {
        var (wsl, _, _) = Create(docker: false);
        var problem = await Assert.ThrowsAsync<DockerException>(() => new DockerEngine(wsl).InfoAsync(TestContext.Current.CancellationToken));
        Assert.Equal(DockerProblem.NotInstalled, problem.Problem);
        Assert.Contains("WSL's default distribution", problem.Message);
    }

    [Fact]
    public async Task Windows_folders_are_mounted_where_WSL_has_them_and_its_own_only_when_the_solutions()
    {
        var (wsl, state, _) = Create();
        var engine = new DockerEngine(wsl);
        await engine.RunContainerAsync("redis", null, [], [(@"E:\data\redis", "/data"), ("cache", "/cache")], []);
        Assert.Equal(["run", "-d", "-v", "/mnt/e/data/redis:/data", "-v", "cache:/cache", "redis"], Calls(state).Last());

        var tools = new TestToolHost("/home/me/shop", engine).Tools();
        async Task<string> Run(string mount)
        {
            var tool = tools.Find(Karvon.Claude.ClaudeToolServer.Prefix + "container_run")!;
            var output = await tool.CallAsync(JsonSerializer.SerializeToElement(new { image = "alpine", command = "ls", mounts = new[] { mount } }), TestContext.Current.CancellationToken);
            return output.Text;
        }
        await Run(@"E:\shop\src:/src:ro");
        Assert.Contains("/mnt/e/shop/src:/src:ro", Calls(state).Last());
        await Run(@"\\wsl.localhost\Ubuntu\home\me\shop\src:/src");
        Assert.Contains("/home/me/shop/src:/src", Calls(state).Last());

        // The rest of the distribution's files, its engine's socket, a whole drive, what WSL does not see.
        var before = Calls(state).Length;
        foreach (var mount in new[] { "/home/me/.ssh:/x", "/:/x", "/home/me/shop/../.aws:/x", "/mnt:/x", "/var/run/docker.sock:/x", @"C:\:/x", "/mnt/c:/x",
            "/home/me/shop/.env:/x", "//server/share:/x" })
            Assert.Contains("is not mounted", await Run(mount));
        Assert.Equal(before, Calls(state).Length);
    }
}
