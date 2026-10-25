using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Karvon.Claude.Tools;
using Karvon.Diagnostics;
using Karvon.DotNet.Diagnostics;
using Karvon.Modules;
using Karvon.Testing;
using Karvon.ViewModels;
using Karvon.Views;
using Karvon.Workspace;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>A test framework of the tests' that measures coverage: one test, passed, and an LCOV report of calc.js.</summary>
sealed class FakeCoverageFramework(string workspace) : TestFramework
{
    public override string Id => "fake-coverage";

    public override string Name => "Fake coverage";

    public override bool CanCollectCoverage => true;

    static readonly TestItem Test = new("adds", [new TestGroup(TestNodeKind.Class, "CalcTests")], "adds", null, 0) { FullName = "CalcTests.adds" };

    public override Task<TestDiscovery> DiscoverAsync(CancellationToken ct) => Task.FromResult(new TestDiscovery([Test]));

    public override Task RunAsync(IReadOnlyList<TestItem>? tests, bool debug, Action<TestItemResult> onResult, CancellationToken ct)
    {
        onResult(new TestItemResult("adds", TestState.Passed, TimeSpan.FromMilliseconds(3), null, null, null));
        return Task.CompletedTask;
    }

    public override async Task<(IReadOnlyList<string> Reports, string? Problem)> RunCoverageAsync(IReadOnlyList<TestItem>? tests, Action<TestItemResult> onResult, CancellationToken ct)
    {
        await RunAsync(tests, false, onResult, ct);
        var report = Path.Combine(workspace, "coverage", "lcov.info");
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        // calc.js: lines 1-2 ran, 3 and 4 did not, 5 ran with one of its two branches.
        await File.WriteAllTextAsync(report, "TN:\nSF:calc.js\nDA:1,4\nDA:2,4\nDA:3,0\nDA:4,0\nDA:5,2\nBRDA:5,0,0,2\nBRDA:5,0,1,0\nend_of_record\n", ct);
        return ([report], null);
    }
}

/// <summary>A module of the tests': the coverage framework, a run task with a child process, and a diagnostic of "sleep" processes.</summary>
sealed class FakeDiagnosticsModule : KarvonModule
{
    public override string Id => "fake-diagnostics";

    public override string Name => "Fake diagnostics";

    public override string Description => "Coverage and diagnostics for the tests.";

    public override IReadOnlyList<TestFramework> TestFrameworks(TestFrameworkStart start) => [new FakeCoverageFramework(start.Workspace)];

    public override IReadOnlyList<WorkspaceTask> WorkspaceTasks(string workspace) =>
        [new("busy", WorkspaceTaskKind.Run, "sh", ["-c", "sleep 30 & wait"], workspace) { Source = "fake" }];

    public override IReadOnlyList<ProcessDiagnostic> ProcessDiagnostics { get; } = [new CountSleep()];

    public sealed class CountSleep : ProcessDiagnostic
    {
        public override string Name => "Count Sheep";

        public override string Description => "The tests' diagnostic.";

        public int Asked { get; private set; }

        public override bool Applies(DiagnosedProcess process) => process.Name == "sleep";

        public override async Task<DiagnosticReport> RunAsync(DiagnosticRun run, CancellationToken ct)
        {
            if (await run.Confirm("Count", "Count the sheep?"))
                Asked++;
            run.Log("Counting...");
            return new DiagnosticReport("Sheep", $"{run.Process.Label} counted 2 sheep.", "Sheep", "Legs", "Weight", [new("Dolly", "white", "4", "70"), new("Shaun", "black", "4", "60")]);
        }
    }
}

/// <summary>Code coverage (its reports, the editor's margin, Code Coverage Results, test_coverage) and Diagnostic Tools.</summary>
public class CoverageDiagnosticsTests
{
    sealed class Dialogs : IDialogService
    {
        public int Confirmed { get; private set; }

        public Queue<string> Files { get; } = new();

        public Task<string?> PickSolutionAsync() => Task.FromResult<string?>(null);

        public Task<string?> PromptAsync(string title, string label, string initial) => Task.FromResult<string?>(null);

        public Task<bool?> ConfirmSaveAsync(string fileName) => Task.FromResult<bool?>(false);

        public Task<bool?> ConfirmAsync(string title, string message)
        {
            Confirmed++;
            return Task.FromResult<bool?>(true);
        }

        public Task<NewItemChoice?> ChooseNewItemAsync(string title, string folder, ItemTemplate initial) => Task.FromResult<NewItemChoice?>(null);

        public Task<IReadOnlyList<string>> PickFilesAsync(string title) => Task.FromResult<IReadOnlyList<string>>(Files.Count > 0 ? [Files.Dequeue()] : []);
    }

    static string NewFolder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "karvon-coverage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Cobertura_and_LCOV_reports_are_read_and_merged()
    {
        var dir = NewFolder();
        Directory.CreateDirectory(Path.Combine(dir, "src"));
        File.WriteAllText(Path.Combine(dir, "src", "Cart.cs"), "");
        var cobertura = CoverageReport.Cobertura($"""
            <?xml version="1.0" encoding="utf-8"?>
            <coverage line-rate="0.5" version="1.9">
              <sources><source>{dir}/src/</source></sources>
              <packages><package name="Shop"><classes>
                <class name="Shop.Cart" filename="Cart.cs">
                  <lines>
                    <line number="3" hits="2" branch="false" />
                    <line number="4" hits="1" branch="true" condition-coverage="50% (1/2)" />
                    <line number="5" hits="0" branch="false" />
                    <line number="6" hits="1" branch="true" condition-coverage="100% (2/2)" />
                  </lines>
                </class>
              </classes></package></packages>
            </coverage>
            """, dir);
        var cart = Assert.Single(cobertura.Files);
        Assert.Equal(Path.Combine(dir, "src", "Cart.cs"), cart.Path);
        Assert.Equal(new LineCoverage(2, false), cart.Lines[3]);
        Assert.Equal(new LineCoverage(1, true), cart.Lines[4]);
        Assert.False(cart.Lines[5].IsCovered);
        Assert.False(cart.Lines[6].Partial);
        Assert.Equal(75, cart.Percent);

        var lcov = CoverageReport.Lcov("SF:src/Cart.cs\nDA:5,3\nDA:7,0\nend_of_record\nSF:web/app.js\nDA:1,1\nDA:2,1\nBRDA:2,0,0,1\nBRDA:2,0,1,-\nend_of_record\n", dir);
        var app = lcov.For(Path.Combine(dir, "web", "app.js"))!;
        Assert.True(app.Lines[2].Partial);
        Assert.False(app.Lines[1].Partial);

        // Merged: a line either covers is covered, its hits summed.
        cobertura.Merge(lcov);
        var merged = cobertura.For(Path.Combine(dir, "src", "Cart.cs"))!;
        Assert.Equal(3, merged.Lines[5].Hits);
        Assert.Equal(5, merged.Total);
        Assert.Equal(2, cobertura.Files.Count);
        Assert.Equal(6, cobertura.Covered);
        Assert.Equal("3-5, 9, 12-14", TestCoverageTool.Ranges([12, 3, 4, 5, 9, 13, 14]));
        Assert.Equal("none", TestCoverageTool.Ranges([]));
    }

    [AvaloniaFact]
    public async Task Analyzing_coverage_colors_the_lines_and_lists_the_files()
    {
        var dir = NewFolder();
        var calc = Path.Combine(dir, "calc.js");
        File.WriteAllText(calc, "function add(a, b) {\n  return a + b;\n}\nfunction sub(a, b) { return a - b; }\nconst x = add(1, 2) > 2 ? 1 : 0;\n");
        var vm = NewViewModel(modules: [.. Karvon.BuiltInModules.Create(), new FakeDiagnosticsModule()]);
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        window.Show();
        await vm.OpenSolutionAsync(dir);
        var host = vm.OpenDocument(calc)!.Host;
        Assert.Null(host.Coverage);

        await WaitUntil(() => vm.AnalyzeCodeCoverageCommand.CanExecute(null), TimeSpan.FromSeconds(10));
        await vm.AnalyzeCodeCoverageCommand.ExecuteAsync(null);
        Assert.True(vm.HasCoverage, vm.TestSummary);
        Assert.Equal(1, vm.PassedCount);
        Assert.StartsWith("Covered 60% (3 of 5 lines, 1 files)", vm.TestSummary);
        var row = Assert.Single(vm.CoverageRows);
        Assert.Equal(("calc.js", "60%", "3 / 5"), (row.Name, row.Percent, row.Lines));
        Assert.True(window.IsToolWindowShowing(ToolWindows.CoverageResults));

        // The open editor's lines colored, in a margin of their own.
        Assert.Equal(5, host.Coverage!.Total);
        Assert.True(host.Coverage.Lines[5].Partial);
        Assert.Contains(host.PrimaryView.TextArea.LeftMargins, m => m.GetType().Name == "CoverageMargin");
        Snapshot(window, "coverage-results");

        // test_coverage: the files, then a file's lines not covered.
        var tool = IdeClaudeTools.Create(vm).Single(t => t.Name == "test_coverage");
        var all = (await tool.CallAsync(JsonSerializer.SerializeToElement(new { }), CancellationToken.None)).Text;
        Assert.Contains("60%  3/5  calc.js", all);
        var file = (await tool.CallAsync(JsonSerializer.SerializeToElement(new { file = "calc.js" }), CancellationToken.None)).Text;
        Assert.Contains("Not covered: 3-4", file);
        Assert.Contains("Partly covered (some branches only): 5", file);

        // Coloring off: none in the editors; on again, and in an editor opened after.
        vm.ShowCoverageColoring = false;
        Assert.Null(host.Coverage);
        vm.ShowCoverageColoring = true;
        Assert.NotNull(host.Coverage);
        await vm.CloseDocumentCommand.ExecuteAsync(vm.Documents.Single());
        vm.OpenCoverageFileCommand.Execute(row);
        Assert.NotNull(vm.Editors.Single().Host.Coverage);

        vm.ClearCoverageCommand.Execute(null);
        Assert.False(vm.HasCoverage);
        Assert.Null(vm.Editors.Single().Host.Coverage);

        // A report of the tests' own tool, loaded: Cobertura's this time.
        var xml = Path.Combine(dir, "coverage.cobertura.xml");
        File.WriteAllText(xml, $"""<coverage><sources><source>{dir}</source></sources><packages><package><classes><class filename="calc.js"><lines><line number="2" hits="1" /></lines></class></classes></package></packages></coverage>""");
        var dialogs = new Dialogs();
        dialogs.Files.Enqueue(xml);
        vm.Dialogs = dialogs;
        await vm.LoadCoverageReportCommand.ExecuteAsync(null);
        Assert.Equal(1, vm.Coverage!.Total);
        Assert.True(vm.Editors.Single().Host.Coverage!.Lines[2].IsCovered);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Diagnostic_Tools_follow_the_running_programs_processes()
    {
        var dir = NewFolder();
        File.WriteAllText(Path.Combine(dir, "readme.md"), "# busy\n");
        var module = new FakeDiagnosticsModule();
        var vm = NewViewModel(modules: [.. Karvon.BuiltInModules.Create(), module]);
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        window.Show();
        var dialogs = new Dialogs();
        vm.Dialogs = dialogs;
        await vm.OpenSolutionAsync(dir);
        vm.StartupItem = vm.StartupItems.Single(i => i.Name == "busy");

        var run = vm.StartWithoutDebuggingCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.MemoryHistory.Count >= 2 && vm.CpuHistory.Count >= 1 && vm.DiagnosticProcesses.Count == 2, TimeSpan.FromSeconds(20));
        Assert.Equal(["sh", "sleep"], vm.DiagnosticProcesses.Select(p => p.Name));
        Assert.Contains("sleep 30", vm.DiagnosticProcesses[1].CommandLine);
        Assert.EndsWith(" MB", vm.MemoryText);
        Assert.InRange(vm.CpuHistory[^1], 0, 100);
        // The process a diagnostic looks at is chosen: its buttons show.
        Assert.Equal("sleep", vm.SelectedDiagnosticProcess!.Name);
        var diagnostic = Assert.Single(vm.AvailableDiagnostics);
        vm.ShowDiagnosticToolsCommand.Execute(null);
        Assert.True(window.IsToolWindowShowing(ToolWindows.DiagnosticTools));

        await vm.RunDiagnosticCommand.ExecuteAsync(diagnostic);
        Assert.Equal(1, dialogs.Confirmed);
        Assert.Equal(1, ((FakeDiagnosticsModule.CountSleep)diagnostic).Asked);
        Assert.Equal(["Dolly", "Shaun"], vm.DiagnosticReport!.Rows.Select(r => r.Name));
        // What it said as it went, posted, never hides what it found.
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(100));
        Assert.Equal($"sleep ({vm.SelectedDiagnosticProcess.Pid}) counted 2 sheep.", vm.DiagnosticsMessage);
        Assert.False(vm.HasTimedDiagnostics);
        Snapshot(window, "diagnostic-tools");

        vm.SelectedDiagnosticProcess = vm.DiagnosticProcesses[0];
        Assert.Empty(vm.AvailableDiagnostics);
        vm.StopCommand.Execute(null);
        await run.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Empty(vm.DiagnosticProcesses);
        Assert.Equal("The program has ended: its last minute is shown.", vm.DiagnosticsMessage);
        window.Close();
    }

    static (string Dir, Func<IReadOnlyList<string>> Calls) FakeTools()
    {
        var dir = Path.Combine(Path.GetTempPath(), "karvon-fake-diag-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var tool in new[] { "dotnet-gcdump", "dotnet-trace" })
        {
            var script = Path.Combine(dir, tool + ".py");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "TestAssets", "FakeDiagnostics", tool + ".py"), script);
            TestTools.Command(script, dir, new Dictionary<string, string> { ["FAKE_DIAG_LOG"] = dir });
        }
        return (dir, () => File.Exists(Path.Combine(dir, "calls.txt")) ? File.ReadAllLines(Path.Combine(dir, "calls.txt")) : []);
    }

    [Fact]
    public async Task A_dotnet_programs_memory_snapshot_and_CPU_profile_are_read_and_their_files_deleted()
    {
        var (tools, calls) = FakeTools();
        // The tests' own process: a .NET program, listening for diagnostics.
        var self = ProcessUsage.Processes(Environment.ProcessId).First();
        Assert.True(DiagnosticsTools.IsDotNetProgram(self), self.CommandLine);
        Assert.True(DiagnosticsTools.IsSdk("/usr/share/dotnet/dotnet run --project Web"));
        Assert.True(DiagnosticsTools.IsSdk("/usr/share/dotnet/dotnet /usr/share/dotnet/sdk/10.0.100/MSBuild.dll -nodemode:1"));
        Assert.False(DiagnosticsTools.IsSdk("/usr/share/dotnet/dotnet exec bin/Debug/net10.0/Web.dll"));
        var log = new List<string>();
        var run = new DiagnosticRun(self, TimeSpan.FromSeconds(5), log.Add, (_, _) => Task.FromResult(false));
        Environment.SetEnvironmentVariable("KARVON_DOTNET_DIAGNOSTICS", tools);
        try
        {
            var memory = await new MemorySnapshot().RunAsync(run, TestContext.Current.CancellationToken);
            Assert.StartsWith($"1,163,002 bytes in 20,925 objects on {self.Label}'s GC heap", memory.Summary);
            Assert.Equal(("Object Type", "Count", "Size (Bytes)"), (memory.NameHeader, memory.FirstHeader, memory.SecondHeader));
            Assert.Equal(new DiagnosticRow("System.String[] (Bytes > 100K)", "System.Private.CoreLib.dll", "1", "262,168"), memory.Rows[0]);
            Assert.Equal(new DiagnosticRow("System.String", "System.Private.CoreLib.dll", "20,645", "41,290"), memory.Rows[1]);
            Assert.Equal(5, memory.Rows.Count);

            var cpu = await new CpuProfile().RunAsync(run, TestContext.Current.CancellationToken);
            Assert.Equal($"100 ms of CPU in 1 thread of {self.Label} over 5 seconds: its functions by Total (with what they call) and Self.", cpu.Summary);
            Assert.Equal(
                [new("Shop.Total()", "app", "100.0%", "60.0%"), new("Program.Main()", "app", "100.0%", "0.0%"), new DiagnosticRow("Shop.Tax()", "app", "40.0%", "40.0%")],
                cpu.Rows);
        }
        finally
        {
            Environment.SetEnvironmentVariable("KARVON_DOTNET_DIAGNOSTICS", "off");
        }
        var made = calls();
        Assert.StartsWith($"dotnet-gcdump collect -p {Environment.ProcessId} -o ", made[0]);
        Assert.StartsWith("dotnet-gcdump report ", made[1]);
        Assert.Matches($@"^dotnet-trace collect -p {Environment.ProcessId} --duration 00:00:00:05 --format speedscope -o \S+\.nettrace$", made[2]);
        // Read and gone: the dump, the trace and its speedscope file.
        var dump = made[0].Split(' ')[^1];
        var trace = made[2].Split(' ')[^1];
        Assert.False(File.Exists(dump));
        Assert.False(File.Exists(trace));
        Assert.False(File.Exists(Path.ChangeExtension(trace, ".speedscope.json")));

        // Turned off (as the tests have them otherwise): no tool, and nothing is installed.
        var off = await Assert.ThrowsAsync<InvalidOperationException>(() => new MemorySnapshot().RunAsync(run, TestContext.Current.CancellationToken));
        Assert.Equal("dotnet-gcdump is turned off (KARVON_DOTNET_DIAGNOSTICS).", off.Message);
    }
}

/// <summary>
/// The real tools: coverlet's collector on a solution's tests, dotnet-gcdump and dotnet-trace on the tests' own process.
/// By themselves, as the sampling profiler stops every thread a thousand times a second (the windows of the tests beside
/// it would be slowed).
/// </summary>
[CollectionDefinition("Live dotnet diagnostics", DisableParallelization = true)]
public sealed class LiveDotNetDiagnosticsCollection;

[Collection("Live dotnet diagnostics")]
public class LiveDotNetDiagnosticsTests
{
    [Fact]
    public async Task Live_dotnet_diagnostics_read_the_tests_own_process()
    {
        var tools = Environment.GetEnvironmentVariable("KARVON_LIVE_DOTNET_DIAGNOSTICS");
        Assert.SkipUnless(tools is { Length: > 0 } && Directory.Exists(tools),
            "Set KARVON_LIVE_DOTNET_DIAGNOSTICS to a folder with dotnet-gcdump and dotnet-trace (dotnet tool install --tool-path) to read this process with them.");
        var self = ProcessUsage.Processes(Environment.ProcessId).First();
        var run = new DiagnosticRun(self, TimeSpan.FromSeconds(2), _ => { }, (_, _) => Task.FromResult(false));
        Environment.SetEnvironmentVariable("KARVON_DOTNET_DIAGNOSTICS", tools);
        try
        {
            var memory = await new MemorySnapshot().RunAsync(run, TestContext.Current.CancellationToken);
            Assert.Contains(memory.Rows, r => r.Name == "System.String");
            using var busy = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            var spin = Task.Run(() => { long n = 0; while (!busy.IsCancellationRequested) n++; return n; });
            var cpu = await new CpuProfile().RunAsync(run, TestContext.Current.CancellationToken);
            await spin;
            Assert.NotEmpty(cpu.Rows);
            Assert.Contains(" ms of CPU in ", cpu.Summary);
        }
        finally
        {
            Environment.SetEnvironmentVariable("KARVON_DOTNET_DIAGNOSTICS", "off");
        }
    }

    [AvaloniaFact]
    public async Task Live_coverlet_measures_a_solutions_tests()
    {
        var solution = Environment.GetEnvironmentVariable("KARVON_LIVE_COVERAGE_SOLUTION");
        Assert.SkipUnless(solution is { Length: > 0 } && File.Exists(solution),
            "Set KARVON_LIVE_COVERAGE_SOLUTION to a solution whose test project has coverlet.collector (dotnet new xunit's has), with a class library it tests.");
        var vm = NewViewModel();
        await vm.OpenSolutionAsync(solution);
        await WaitUntil(() => vm.AnalyzeCodeCoverageCommand.CanExecute(null), TimeSpan.FromMinutes(1));
        await vm.AnalyzeCodeCoverageCommand.ExecuteAsync(null);
        Assert.True(vm.HasCoverage, vm.TestSummary);
        Assert.True(vm.PassedCount > 0, vm.TestSummary);
        // The tested library's files, not the tests' own (coverlet leaves those out).
        Assert.NotEmpty(vm.Coverage!.Files);
        Assert.All(vm.Coverage.Files, f => Assert.True(File.Exists(f.Path), f.Path));
        Assert.Contains(vm.Coverage.Files, f => f.Covered > 0 && f.Covered < f.Total);
    }
}
