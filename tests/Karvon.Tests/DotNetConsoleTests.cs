using System.Text;
using Avalonia.Headless.XUnit;
using Exclr8.Terminal.Input;
using Karvon.DotNet.Debugging;
using Karvon.Views;
using Karvon.Workspace;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>A console app run (Ctrl+F5) and debugged (F5) in its own console, as Visual Studio's console window: its output there, the keyboard its input.</summary>
public class DotNetConsoleTests
{
    [Fact]
    public void A_console_app_runs_in_its_console_unless_the_configuration_says_otherwise()
    {
        ProjectInfo Project(string sdk, string? outputType) => new() { Name = "app", FilePath = "/src/app/app.csproj", Sdk = sdk, OutputType = outputType };
        Assert.True(DotNetDebugger.IsConsoleApp(Project("Microsoft.NET.Sdk", "Exe")));
        Assert.False(DotNetDebugger.IsConsoleApp(Project("Microsoft.NET.Sdk.Web", "Exe")));
        Assert.False(DotNetDebugger.IsConsoleApp(Project("Microsoft.NET.Sdk.Worker", "Exe")));
        Assert.False(DotNetDebugger.IsConsoleApp(Project("Microsoft.NET.Sdk", "WinExe")));
        Assert.False(DotNetDebugger.IsConsoleApp(Project("Microsoft.NET.Sdk", null)));

        var console = Project("Microsoft.NET.Sdk", "Exe");
        var web = Project("Microsoft.NET.Sdk.Web", "Exe");
        Assert.True(DotNetDebugger.UsesConsole(new(), console));
        Assert.False(DotNetDebugger.UsesConsole(new(), web));
        Assert.False(DotNetDebugger.UsesConsole(new() { ["console"] = "internalConsole" }, console));
        Assert.True(DotNetDebugger.UsesConsole(new() { ["console"] = "integratedTerminal" }, web));
        Assert.True(DotNetDebugger.UsesConsole(new() { ["console"] = "externalTerminal" }, null));
        Assert.False(DotNetDebugger.UsesConsole(new(), null));
    }

    /// <summary>Typed into the console, as its control hands the keyboard's input on.</summary>
    static void Type(Karvon.Terminal.TerminalSession console, string text) =>
        typeof(Exclr8.Terminal.TerminalControl).GetMethod("RaiseInput", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(console.Control, [Encoding.UTF8.GetBytes(text), InputLineOrigin.Typed]);

    [AvaloniaFact]
    public async Task Ctrl_F5_and_F5_run_a_console_app_in_its_console()
    {
        var dir = Directory.CreateTempSubdirectory("karvon-console-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "app.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
                </Project>
                """);
            var program = Path.Combine(dir, "Program.cs");
            File.WriteAllText(program, "Console.Write(\"Name? \");\nvar name = Console.ReadLine();\nConsole.WriteLine($\"Hello, {name}!\");\n");
            var vm = NewViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            await vm.OpenSolutionAsync(Path.Combine(dir, "app.csproj"));
            await WaitUntil(() => vm.StartupProject is not null, TimeSpan.FromSeconds(10));

            // Ctrl+F5: built, then run in its console, which reads what is typed; Enter after it exits closes it.
            var run = vm.StartWithoutDebuggingCommand.ExecuteAsync(null);
            await WaitUntil(() => vm.ProgramConsole?.ReadText().Contains("Name? ", StringComparison.Ordinal) == true, TimeSpan.FromSeconds(180));
            var console = vm.ProgramConsole!;
            Assert.True(console.ReadText().Contains("Name? ", StringComparison.Ordinal), vm.DebugOutput.Document.Text + "\n" + console.ReadText());
            Assert.Same(console, vm.ShownTerminal);
            Assert.Equal("app", vm.TerminalTitle);
            Type(console, "Bob\r");
            await run.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Contains("Hello, Bob!", console.ReadText());
            Assert.Contains("The program 'app' has exited with code 0", vm.DebugOutput.Document.Text);
            Assert.DoesNotContain("Hello, Bob!", vm.DebugOutput.Document.Text);
            vm.SwitchTerminalCommand.Execute(null);
            Assert.Same(vm.Terminal, vm.ShownTerminal);
            vm.SwitchTerminalCommand.Execute(null);
            Type(console, "\r");
            await WaitUntil(() => vm.ProgramConsole is null, TimeSpan.FromSeconds(5));
            Assert.Same(vm.Terminal, vm.ShownTerminal);

            // F5: in its console too, the debugger attached before Main (its first line's breakpoint hit), then to its end.
            vm.Breakpoints.Toggle(program, 1);
            var server = ClaudeOf(vm).ToolServer();
            var started = await ClaudeIdeToolsTests.Text(server, "debug_control", new { action = "start" });
            Assert.True(vm.Mode == Karvon.ViewModels.DebugMode.Break, started + "\n" + vm.DebugOutput.Document.Text);
            Assert.Contains("Program.cs:1", started);
            console = vm.ProgramConsole!;
            Assert.DoesNotContain("Name? ", console.ReadText());
            await vm.StartDebuggingCommand.ExecuteAsync(null);
            await WaitUntil(() => console.ReadText().Contains("Name? ", StringComparison.Ordinal), TimeSpan.FromSeconds(30));
            Type(console, "Ann\r");
            await WaitUntil(() => vm.Mode == Karvon.ViewModels.DebugMode.Design, TimeSpan.FromSeconds(30));
            Assert.True(vm.Mode == Karvon.ViewModels.DebugMode.Design, vm.DebugOutput.Document.Text);
            Assert.Contains("Hello, Ann!", console.ReadText());
            Assert.True(await vm.CloseSolutionCoreAsync());
            Assert.Null(vm.ProgramConsole);
            window.Close();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
