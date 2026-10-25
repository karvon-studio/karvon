using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Karvon.Debugging;
using Karvon.Modules;
using Karvon.ViewModels;
using Karvon.Views;
using Karvon.Workspace;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>A module whose debugger is the tests' adapter (TestAssets/FakeDap/dap_breaker.py), logging what it is asked to a file.</summary>
sealed class FakeDebuggerModule(string log) : KarvonModule
{
    public override string Id => "fake-debugger";

    public override string Name => "Fake Debugger";

    public override string Description => "The tests' debugger.";

    public override IReadOnlyList<DebuggerInfo> Debuggers =>
    [
        new("fake", "Fake Debugger", ["fake"], start =>
        {
            var psi = new System.Diagnostics.ProcessStartInfo(TestTools.Python, ["-I", Path.Combine(AppContext.BaseDirectory, "TestAssets", "FakeDap", "dap_breaker.py")]);
            psi.Environment["FAKE_DAP_LOG"] = log;
            return Task.FromResult(DebugAdapter.Start("fake", psi));
        }),
    ];
}

/// <summary>The debugger's windows: a breakpoint's conditions, hit count and message, Breakpoints, Exception Settings, Immediate.</summary>
public class DebuggerWindowsTests
{
    [Fact]
    public void A_breakpoints_settings_go_with_it()
    {
        var store = new BreakpointStore();
        store.Toggle("a.cs", 3);
        store.Configure("a.cs", 6, new BreakpointSettings(Condition: "total > 5", HitCondition: ">= 2"));
        store.Configure("a.cs", 9, new BreakpointSettings(LogMessage: "total={total}"));
        store.Toggle("a.cs", 12);
        store.SetEnabled("a.cs", 12, false);
        Assert.Equal([new SourceBreakpoint(3), new SourceBreakpoint(6, "total > 5", ">= 2"), new SourceBreakpoint(9, LogMessage: "total={total}")],
            store.SourceBreakpoints("a.cs"));
        Assert.Equal("when total > 5, hit count >= 2", store.SettingsOf("a.cs", 6).Text);

        // Two lines inserted above: each breakpoint's settings with it.
        store.Replace("a.cs", [5, 8, 11, 14]);
        Assert.Equal(("total > 5", "total={total}", false), (store.SettingsOf("a.cs", 8).Condition, store.SettingsOf("a.cs", 11).LogMessage, store.SettingsOf("a.cs", 14).Enabled));
        Assert.Equal(BreakpointSettings.Default, store.SettingsOf("a.cs", 6));

        // Kept by the solution and read back; one deleted takes its settings with it.
        var lines = store.Snapshot();
        var settings = store.SettingsSnapshot();
        var again = new BreakpointStore();
        again.Load(lines, settings);
        Assert.Equal(store.SourceBreakpoints("a.cs"), again.SourceBreakpoints("a.cs"));
        again.Remove("a.cs", 8);
        again.Toggle("a.cs", 8);
        Assert.Equal(BreakpointSettings.Default, again.SettingsOf("a.cs", 8));
        again.Replace("a.cs", []);
        Assert.Empty(again.SettingsSnapshot());
    }

    [AvaloniaFact]
    public async Task The_debugger_gets_the_breakpoints_settings_and_the_windows_follow_it()
    {
        var dir = Path.Combine(Path.GetTempPath(), "karvon-debug-windows-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, ".vscode"));
        var program = Path.Combine(dir, "app.cs");
        File.WriteAllText(program, string.Join('\n', Enumerable.Range(1, 12).Select(i => $"var line{i} = {i};")) + "\n");
        File.WriteAllText(VsCodeLaunch.FileOf(dir), """{ "configurations": [ { "name": "App", "type": "fake", "request": "launch", "program": "${workspaceFolder}/app.cs" } ] }""");
        var log = Path.Combine(dir, "dap.log");
        var vm = NewViewModel(modules: [.. Karvon.BuiltInModules.Create(), new FakeDebuggerModule(log)]);
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        window.Show();
        await vm.OpenSolutionAsync(dir);
        var doc = vm.OpenDocument(program)!;

        // A conditional breakpoint, a tracepoint, one off: the editor's menu opens their settings in the Breakpoints window.
        doc.Host.ToggleBreakpoint(3);
        vm.EditBreakpoint(program, 3);
        Assert.Equal((program, 3), (vm.SelectedBreakpoint?.File, vm.SelectedBreakpoint?.Line));
        vm.BreakpointCondition = "total > 5";
        vm.BreakpointHitCondition = ">= 2";
        vm.ApplyBreakpointSettingsCommand.Execute(null);
        vm.EditBreakpoint(program, 6, logpoint: true);
        Assert.Equal("Reached line 6", vm.BreakpointLogMessage);
        vm.BreakpointLogMessage = "total is {total}";
        vm.ApplyBreakpointSettingsCommand.Execute(null);
        doc.Host.ToggleBreakpoint(9);
        vm.BreakpointItems.Single(b => b.Line == 9).IsEnabled = false;
        Assert.Equal(["⊕", "◆", "○"], vm.BreakpointItems.Select(b => b.Glyph));
        Assert.Equal(["when total > 5, hit count >= 2", "logs 'total is {total}'", ""], vm.BreakpointItems.Select(b => b.Conditions));

        // F5: the debugger gets them (the one off not), and its exception kinds; it stops at the first.
        await vm.StartDebuggingCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.Mode == DebugMode.Break && vm.Locals.Count == 1, TimeSpan.FromSeconds(20));
        JsonNode? Last(string command) => File.ReadAllLines(log).Select(l => JsonNode.Parse(l)!).LastOrDefault(r => r["command"]!.GetValue<string>() == command)?["arguments"];
        static string Json(JsonNode node) => node.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        var set = Last("setBreakpoints")!;
        Assert.Equal("""[{"condition":"total > 5","hitCondition":">= 2","line":3},{"line":6,"logMessage":"total is {total}"}]""", Json(set["breakpoints"]!));
        Assert.Equal("""["user-unhandled"]""", Json(Last("setExceptionBreakpoints")!["filters"]!));

        // Exception Settings: the debugger's kinds, its default checked; one checked more is told to it at once and kept.
        Assert.Equal([("All Exceptions", false), ("User-Unhandled Exceptions", true)], vm.ExceptionFilters.Select(f => (f.Label, f.IsChecked)));
        Assert.Equal("fake", vm.ExceptionDebugger);
        vm.ExceptionFilters[0].IsChecked = true;
        await WaitUntil(() => Json(Last("setExceptionBreakpoints")!["filters"]!) == """["all","user-unhandled"]""", TimeSpan.FromSeconds(10));
        Assert.Equal(["all", "user-unhandled"], vm.Settings.ExceptionBreakpoints["fake"]);

        // Immediate: an expression where it stopped; an assignment, Locals following; an error said as the debugger says it.
        await vm.EvaluateImmediate("total");
        await vm.EvaluateImmediate("total = 7");
        await vm.EvaluateImmediate("nope");
        Assert.Equal([("total", "42  (int)", false), ("total = 7", "7  (int)", false), ("nope", "error CS0103: The name 'nope' does not exist in the current context", true)],
            vm.Immediate.Select(e => (e.Expression, e.Result, e.IsError)));
        await WaitUntil(() => vm.Locals.FirstOrDefault()?.Value == "7", TimeSpan.FromSeconds(10));
        Assert.Equal(["total", "total = 7", "nope"], vm.ImmediateHistory);
        vm.ShowToolWindow(ToolWindows.Breakpoints);
        Snapshot(window, "debug-breakpoints");
        vm.ShowToolWindow(ToolWindows.Immediate);
        Snapshot(window, "debug-immediate");

        // A breakpoint's condition changed while debugging: the debugger gets it at once.
        vm.SelectedBreakpoint = vm.BreakpointItems.Single(b => b.Line == 3);
        Assert.Equal("total > 5", vm.BreakpointCondition);
        vm.BreakpointCondition = "total > 40";
        vm.ApplyBreakpointSettingsCommand.Execute(null);
        await WaitUntil(() => Last("setBreakpoints")!["breakpoints"]![0]!["condition"]?.GetValue<string>() == "total > 40", TimeSpan.FromSeconds(10));

        await vm.StopCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.Mode == DebugMode.Design, TimeSpan.FromSeconds(10));
        // Not stopped: the Immediate window says so.
        await vm.EvaluateImmediate("total");
        Assert.True(vm.Immediate[^1].IsError);
        window.Close();
    }
}
