using Avalonia.Headless.XUnit;
using Karvon.DotNet.Debugging;
using Karvon.Modules;
using Karvon.Views;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>Hot Reload of a .NET project run without the debugger (Ctrl+F5), under dotnet watch.</summary>
public class HotReloadTests
{
    [Fact]
    public void Dotnet_watchs_lines_say_what_Hot_Reload_did_and_when_the_program_ended()
    {
        var read = DotNetWatch.Reader();
        RunNews? Read(string line) => read(line);
        Assert.Null(Read("dotnet watch 🔥 Hot reload enabled. For a list of supported edits, see https://aka.ms/dotnet/hot-reload."));
        Assert.Null(Read("one"));
        Assert.Null(Read("dotnet watch ⌚ Waiting for changes"));
        Assert.Equal("Hot Reload: C# and Razor changes applied in 302ms", Read("dotnet watch 🔥 C# and Razor changes applied in 302ms.")!.Status);
        Assert.Equal("Hot Reload: C# and Razor changes applied in 28ms", Read("dotnet watch : C# and Razor changes applied in 28ms.")!.Status);
        Assert.Equal("Hot Reload: no code changes to apply", Read("dotnet watch ⌚ No managed code changes to apply.")!.Status);
        Assert.Equal("Hot Reload: the code does not compile, so nothing was applied", Read("dotnet watch 🔥 Unable to apply changes due to compilation errors.")!.Status);
        Assert.Equal("Hot Reload: Program.cs(9,46): error CS1525: Invalid expression term ';'",
            Read("dotnet watch ❌ /src/app/Program.cs(9,46): error CS1525: Invalid expression term ';'")!.Status);
        // With its colors (Output keeps them).
        Assert.Equal("Hot Reload: these changes need a restart, which is under way...", Read("\u001b[33mdotnet watch : Restart is needed to apply the changes.\u001b[0m")!.Status);
        Assert.Null(Read("dotnet watch : [App (net10.0)] Exited"));
        Assert.Equal("Hot Reload: restarted with the changes", Read("dotnet watch : Waiting for changes")!.Status);
        Assert.Null(Read("dotnet watch : Waiting for changes"));

        // The program exits by itself: dotnet watch waits for a change to start it again; the run ends with its code.
        Assert.Null(Read("dotnet watch : [App (net10.0)] Exited with error code 3"));
        Assert.Equal(new RunNews(null, 3), Read("dotnet watch : Waiting for a file to change before restarting ..."));
        Assert.Null(Read("dotnet watch : [App (net10.0)] Exited"));
        Assert.Equal(new RunNews(null, 0), Read("dotnet watch : Waiting for a file to change before restarting ..."));
    }

    static string NewProject(string outputType, string program)
    {
        var dir = Directory.CreateTempSubdirectory("karvon-hot-reload-").FullName;
        File.WriteAllText(Path.Combine(dir, "app.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><OutputType>{outputType}</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(dir, "Program.cs"), program);
        return dir;
    }

    const string Loop = """
        while (true)
        {
            Console.WriteLine("says " + Worker.Message());
            Thread.Sleep(300);
        }

        static class Worker
        {
            public static string Message() => "one";
        }
        """;

    /// <summary>The run stopped (dotnet watch and the app with it, also when the test failed), the solution and the window closed.</summary>
    static async Task CloseAsync(Karvon.ViewModels.MainViewModel vm, MainWindow window, string dir)
    {
        await vm.CloseSolutionCoreAsync();
        window.Close();
        Directory.Delete(dir, recursive: true);
    }

    [AvaloniaFact]
    public async Task Ctrl_F5_applies_saved_changes_restarts_for_a_rude_edit_and_ends_when_the_app_does()
    {
        // Not a console app (a windowed one's project): its output in Output, under dotnet watch.
        var dir = NewProject("WinExe", Loop);
        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        try
        {
            window.Show();
            await vm.OpenSolutionAsync(Path.Combine(dir, "app.csproj"));
            await WaitUntil(() => vm.StartupProject is not null, TimeSpan.FromSeconds(10));
            Assert.False(vm.ApplyHotReloadCommand.CanExecute(null));
            var run = vm.StartWithoutDebuggingCommand.ExecuteAsync(null);
            await WaitUntil(() => vm.DebugOutput.Document.Text.Contains("says one", StringComparison.Ordinal), TimeSpan.FromSeconds(180));
            Assert.True(vm.DebugOutput.Document.Text.Contains("says one", StringComparison.Ordinal), vm.DebugOutput.Document.Text);
            Assert.Contains("> dotnet watch --project", vm.DebugOutput.Document.Text);
            Assert.True(vm.IsHotReloading);
            Assert.True(vm.ApplyHotReloadCommand.CanExecute(null));

            // An edit in the editor, applied by Hot Reload (Alt+F10 saves it): the app goes on, with the new code.
            var program = Path.Combine(dir, "Program.cs");
            var doc = vm.OpenDocument(program)!;
            doc.Host.Editor.Document.Text = doc.Host.Editor.Document.Text.Replace("\"one\"", "\"two\"", StringComparison.Ordinal);
            vm.ApplyHotReloadCommand.Execute(null);
            Assert.False(doc.IsDirty);
            await WaitUntil(() => vm.DebugOutput.Document.Text.Contains("says two", StringComparison.Ordinal), TimeSpan.FromSeconds(60));
            Assert.Contains("says two", vm.DebugOutput.Document.Text);
            await WaitUntil(() => vm.StatusText.StartsWith("Hot Reload: C# and Razor changes applied", StringComparison.Ordinal), TimeSpan.FromSeconds(10));
            Assert.StartsWith("Hot Reload: C# and Razor changes applied", vm.StatusText);
            Assert.False(run.IsCompleted);

            // A change it cannot apply (the loop that runs): the app restarts with it, and runs to its end, which ends the run.
            File.WriteAllText(program, "Console.WriteLine(\"says bye\");\nreturn 4;\n");
            await run.WaitAsync(TimeSpan.FromSeconds(120));
            Assert.Contains("says bye", vm.DebugOutput.Document.Text);
            Assert.Contains("The program 'app' has exited with code 4", vm.DebugOutput.Document.Text);
            Assert.False(vm.IsHotReloading);
            Assert.False(vm.IsRunning);
        }
        finally
        {
            await CloseAsync(vm, window, dir);
        }
    }

    [AvaloniaFact]
    public async Task Stop_ends_dotnet_watch_and_the_app()
    {
        var dir = NewProject("WinExe", Loop);
        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        try
        {
            window.Show();
            await vm.OpenSolutionAsync(Path.Combine(dir, "app.csproj"));
            await WaitUntil(() => vm.StartupProject is not null, TimeSpan.FromSeconds(10));
            var run = vm.StartWithoutDebuggingCommand.ExecuteAsync(null);
            await WaitUntil(() => vm.DebugOutput.Document.Text.Contains("says one", StringComparison.Ordinal), TimeSpan.FromSeconds(180));
            Assert.True(vm.IsRunning);
            vm.StopCommand.Execute(null);
            await run.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.False(vm.IsRunning);
            Assert.False(vm.IsHotReloading);
            // Nothing of the run is left: the app's output stops.
            var output = vm.DebugOutput.Document.Text;
            await WaitUntil(() => false, TimeSpan.FromSeconds(1));
            Assert.Equal(output, vm.DebugOutput.Document.Text);
        }
        finally
        {
            await CloseAsync(vm, window, dir);
        }
    }
}
