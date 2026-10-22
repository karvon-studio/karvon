using System.Diagnostics;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Karvon.ViewModels;
using Karvon.Views;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>What each part of Karvon costs in memory (a diagnostic: KARVON_MEMORY_PROBE=1).</summary>
public class MemoryProbe
{
    static (long Heap, long WorkingSet) Now()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var self = Process.GetCurrentProcess();
        return (GC.GetTotalMemory(true), self.WorkingSet64);
    }

    [AvaloniaFact]
    public async Task Each_part_costs()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("KARVON_MEMORY_PROBE") == "1", "A diagnostic.");
        var lines = new List<string>();
        var (heap, set) = Now();
        void Step(string what)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            var (h, s) = Now();
            lines.Add($"{what,-34} heap {h / 1048576.0,7:0.0} MB (+{(h - heap) / 1048576.0,6:0.0})   working set {s / 1048576.0,7:0.0} MB (+{(s - set) / 1048576.0,6:0.0})");
            (heap, set) = (h, s);
        }
        Step("test process");
        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        window.Show();
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(500));
        Step("main window, no solution");
        foreach (var (id, name) in new[]
        {
            (ToolWindows.SolutionExplorer, "Solution Explorer"), (ToolWindows.GitChanges, "Git Changes"), (ToolWindows.TestExplorer, "Test Explorer"),
            (Karvon.Database.DatabaseModule.ToolWindowId, "Database Explorer"), (Karvon.Docker.DockerModule.ToolWindowId, "Docker Explorer (fake engine)"),
            (Karvon.Servers.ServersModule.ToolWindowId, "Servers"), (Karvon.Mcp.McpModule.ToolWindowId, "MCP Servers"), (Karvon.Skills.SkillsModule.ToolWindowId, "Skills"), (Karvon.Claude.ClaudeModule.ToolWindowId, "Claude (empty)"),
        })
        {
            vm.ShowToolWindow(id);
            await WaitUntil(() => false, TimeSpan.FromMilliseconds(400));
            Step(name);
        }
        var answer = string.Join("\n\n", Enumerable.Range(1, 4).Select(p => $"Paragraph {p}: " + string.Join(" ", Enumerable.Repeat("some **bold** words and `code`", 12))))
            + "\n\n```csharp\n" + string.Join("\n", Enumerable.Range(1, 20).Select(l => $"var line{l} = Compute({l});")) + "\n```";
        for (var i = 0; i < 300; i++)
        {
            ClaudeOf(vm).Claude.Items.Add(new UserChatItem($"Question {i}", null));
            ClaudeOf(vm).Claude.Items.Add(new TextChatItem(answer));
        }
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(500));
        Step("Claude, 600 messages");
        window.Close();
        Assert.Fail("\n" + string.Join("\n", lines));
    }
}
