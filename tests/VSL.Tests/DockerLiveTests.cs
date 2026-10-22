using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using VSL.Docker;
using VSL.Docker.ViewModels;
using VSL.Docker.Views;
using static VSL.Tests.UiTests;

namespace VSL.Tests;

/// <summary>
/// The Docker Explorer against the real engine: what it lists matches the docker command line, and a container of the
/// test's own (redis, named vsl-live-...) is followed from start to removal. No other container is touched. Skipped
/// unless VSL_DOCKER_LIVE=1 (it needs Docker running and the redis:7-alpine image).
/// </summary>
public class DockerLiveTests
{
    [AvaloniaFact]
    public async Task The_explorer_follows_the_real_engine()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("VSL_DOCKER_LIVE") == "1", "Set VSL_DOCKER_LIVE=1 to try the Docker Explorer against the local engine.");
        // The real engine itself: every other test finds the fake one (TestEnvironment).
        var engine = new DockerEngine("docker");
        var name = "vsl-live-" + Guid.NewGuid().ToString("N")[..8];
        var host = new TestDockerHost(null);
        var vm = new DockerExplorerViewModel(engine, host);
        var window = new Window { Width = 1360, Height = 860, Content = new DockerExplorerView { DataContext = vm } };
        window.Show();
        try
        {
            await vm.InitializeAsync();
            Assert.True(vm.IsEngineRunning, vm.EngineMessage);
            var ids = (await engine.OutputAsync(["ps", "-a", "-q", "--no-trunc"])).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(ids.Order(), vm.Containers.AllRows.Select(r => r.Container.Id).Order());
            var images = (await engine.OutputAsync(["images", "-q", "--no-trunc"])).Split('\n', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
            Assert.Equal(images.Order(), vm.Images.AllRows.Select(r => r.Image.Id).ToHashSet().Order());
            Assert.Equal((await engine.OutputAsync(["volume", "ls", "-q"])).Split('\n', StringSplitOptions.RemoveEmptyEntries).Order(),
                vm.Volumes.AllRows.Select(r => r.Name).Order());
            if (vm.Containers.AllRows.Any(r => r.IsRunning))
                await WaitUntil(() => vm.Containers.AllRows.Any(r => r.Cpu is not null), TimeSpan.FromSeconds(15));

            // A container started outside the explorer shows through the engine's events.
            var run = await engine.RunContainerAsync("redis:7-alpine", name, [], [], [("VSL_LIVE", "1")]);
            Assert.True(run.Succeeded, run.Message);
            await WaitUntil(() => vm.Containers.AllRows.Any(r => r.Name == name && r.IsRunning), TimeSpan.FromSeconds(15));
            var row = vm.Containers.AllRows.Single(r => r.Name == name);
            Assert.Equal("redis:7-alpine", row.Image);

            // Its page: logs as they come, its inspect, a stats sample.
            row.OpenCommand.Execute(null);
            var page = host.Page<ContainerDetailViewModel>();
            await WaitUntil(() => page.Logs.Document.Text.Contains("Ready to accept connections", StringComparison.Ordinal), TimeSpan.FromSeconds(15));
            await WaitUntil(() => page.InspectJson.Contains("VSL_LIVE=1", StringComparison.Ordinal), TimeSpan.FromSeconds(15));
            await WaitUntil(() => row.Cpu is not null, TimeSpan.FromSeconds(15));

            // Stopped and deleted from the page.
            await page.StartStopCommand.ExecuteAsync(null);
            await WaitUntil(() => !row.IsUp, TimeSpan.FromSeconds(30));
            var deleting = page.DeleteCommand.ExecuteAsync(null);
            await WaitUntil(() => vm.Dialog is ConfirmDialog, TimeSpan.FromSeconds(5));
            vm.Dialog!.Confirm();
            await deleting;
            await WaitUntil(() => vm.Containers.AllRows.All(r => r.Name != name), TimeSpan.FromSeconds(15));
            await WaitUntil(() => host.Open.Count == 0, TimeSpan.FromSeconds(5));
        }
        finally
        {
            vm.Dispose();
            window.Close();
            await engine.RunAsync(["rm", "-f", name]);
        }
    }
}
