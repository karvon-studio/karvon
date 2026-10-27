using Avalonia.Headless.XUnit;
using Karvon.Modules;
using Karvon.Views;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>A run without the debugger in a language with no Hot Reload restarts with the changes once a file is saved.</summary>
public class RestartOnSaveTests
{
    /// <summary>A module of the tests': a run task that says what value.txt held when it started, and runs on.</summary>
    sealed class SaysValueModule : KarvonModule
    {
        public override string Id => "says-value";

        public override string Name => "Says value";

        public override string Description => "A run task for the tests.";

        public override IReadOnlyList<WorkspaceTask> WorkspaceTasks(string workspace) =>
        [
            new("says", WorkspaceTaskKind.Run, "sh", ["-c", "echo \"started with $(cat value.txt)\"; exec sleep 60"], workspace) { Source = "test" },
            // A build first, which fails while value.txt says "bad".
            new("check", WorkspaceTaskKind.Build, "sh", ["-c", "! grep -q bad value.txt"], workspace) { Source = "test" },
            new("checked", WorkspaceTaskKind.Run, "sh", ["-c", "echo \"checked $(cat value.txt)\"; exec sleep 60"], workspace) { Source = "test", DependsOn = ["check"] },
        ];
    }

    [AvaloniaFact]
    public async Task A_saved_file_restarts_the_run_and_Stop_ends_it()
    {
        var dir = Directory.CreateTempSubdirectory("karvon-restart-").FullName;
        File.WriteAllText(Path.Combine(dir, "value.txt"), "1\n");
        File.WriteAllText(Path.Combine(dir, "notes.md"), "# notes\n");
        var vm = NewViewModel(modules: [.. Karvon.BuiltInModules.Create(), new SaysValueModule()]);
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        try
        {
            window.Show();
            await vm.OpenSolutionAsync(dir);
            vm.StartupItem = vm.StartupItems.Single(i => i.Name == "says");
            Assert.False(vm.ApplyHotReloadCommand.CanExecute(null));
            var run = vm.StartWithoutDebuggingCommand.ExecuteAsync(null);
            await WaitUntil(() => vm.DebugOutput.Document.Text.Contains("started with 1", StringComparison.Ordinal), TimeSpan.FromSeconds(30));
            Assert.Contains("started with 1", vm.DebugOutput.Document.Text);
            Assert.False(vm.IsHotReloading);
            Assert.True(vm.ApplyHotReloadCommand.CanExecute(null));

            // Saved in the editor: the program stops and starts again, with the change.
            var value = vm.OpenDocument(Path.Combine(dir, "value.txt"))!;
            value.Host.Editor.Document.Text = "2\n";
            value.Host.Save();
            await WaitUntil(() => vm.DebugOutput.Document.Text.Contains("started with 2", StringComparison.Ordinal), TimeSpan.FromSeconds(30));
            Assert.Contains("started with 2", vm.DebugOutput.Document.Text);
            await WaitUntil(() => vm.StatusText == "Restart on Save: says restarted with value.txt", TimeSpan.FromSeconds(5));
            Assert.Equal("Restart on Save: says restarted with value.txt", vm.StatusText);
            Assert.True(vm.IsRunning);

            // Notes are not the program: no restart. Hot Reload (Alt+F10) with nothing to save restarts it all the same.
            var notes = vm.OpenDocument(Path.Combine(dir, "notes.md"))!;
            notes.Host.Editor.Document.Text = "# more notes\n";
            notes.Host.Save();
            await WaitUntil(() => false, TimeSpan.FromSeconds(1));
            Assert.Contains("started with 2", vm.DebugOutput.Document.Text);
            File.WriteAllText(Path.Combine(dir, "value.txt"), "3\n");
            vm.ApplyHotReloadCommand.Execute(null);
            await WaitUntil(() => vm.DebugOutput.Document.Text.Contains("started with 3", StringComparison.Ordinal), TimeSpan.FromSeconds(30));
            Assert.Contains("started with 3", vm.DebugOutput.Document.Text);

            // Stop ends the run, and Restart on Save with it.
            await vm.StopCommand.ExecuteAsync(null);
            await WaitUntil(() => !vm.IsRunning, TimeSpan.FromSeconds(10));
            value.Host.Editor.Document.Text = "4\n";
            value.Host.Save();
            await WaitUntil(() => false, TimeSpan.FromSeconds(1));
            Assert.False(vm.IsRunning);
            Assert.DoesNotContain("started with 4", vm.DebugOutput.Document.Text);
            Assert.False(vm.ApplyHotReloadCommand.CanExecute(null));

            // With Hot Reload off, a save does not restart the run.
            vm.Settings.HotReload = false;
            run = vm.StartWithoutDebuggingCommand.ExecuteAsync(null);
            await WaitUntil(() => vm.DebugOutput.Document.Text.Contains("started with 4", StringComparison.Ordinal), TimeSpan.FromSeconds(30));
            value.Host.Editor.Document.Text = "5\n";
            value.Host.Save();
            await WaitUntil(() => false, TimeSpan.FromSeconds(1));
            Assert.DoesNotContain("started with 5", vm.DebugOutput.Document.Text);
            Assert.Contains("started with 4", vm.DebugOutput.Document.Text);
            await vm.StopCommand.ExecuteAsync(null);
            await run.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await vm.CloseSolutionCoreAsync();
            window.Close();
            Directory.Delete(dir, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task A_restart_whose_build_fails_starts_again_once_the_fix_is_saved()
    {
        var dir = Directory.CreateTempSubdirectory("karvon-restart-").FullName;
        File.WriteAllText(Path.Combine(dir, "value.txt"), "good\n");
        var vm = NewViewModel(modules: [.. Karvon.BuiltInModules.Create(), new SaysValueModule()]);
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        try
        {
            window.Show();
            await vm.OpenSolutionAsync(dir);
            vm.StartupItem = vm.StartupItems.Single(i => i.Name == "checked");
            _ = vm.StartWithoutDebuggingCommand.ExecuteAsync(null);
            await WaitUntil(() => vm.DebugOutput.Document.Text.Contains("checked good", StringComparison.Ordinal), TimeSpan.FromSeconds(30));
            Assert.Contains("checked good", vm.DebugOutput.Document.Text);

            // The change breaks its build: it does not start, and says so; the run is still on.
            var value = vm.OpenDocument(Path.Combine(dir, "value.txt"))!;
            value.Host.Editor.Document.Text = "bad\n";
            value.Host.Save();
            await WaitUntil(() => vm.StatusText.Contains("did not start", StringComparison.Ordinal), TimeSpan.FromSeconds(30));
            Assert.Contains("Restart on Save: checked did not start", vm.StatusText);
            Assert.False(vm.IsRunning);
            Assert.True(vm.StopCommand.CanExecute(null));

            // The fix saved: it starts again.
            value.Host.Editor.Document.Text = "fixed\n";
            value.Host.Save();
            await WaitUntil(() => vm.DebugOutput.Document.Text.Contains("checked fixed", StringComparison.Ordinal), TimeSpan.FromSeconds(30));
            Assert.Contains("checked fixed", vm.DebugOutput.Document.Text);
            Assert.True(vm.IsRunning);
        }
        finally
        {
            await vm.CloseSolutionCoreAsync();
            window.Close();
            Directory.Delete(dir, recursive: true);
        }
    }
}
