using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Avalonia.Threading;
using Karvon.ViewModels;
using Karvon.Views;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>Coming back to the Claude tool window with a long conversation in it.</summary>
public class ClaudeTabSwitchTests
{
    static void Time(Action switchTo)
    {
        switchTo();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task Switching_back_to_a_long_conversation_is_quick()
    {
        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        window.Show();
        var answer = string.Join("\n\n", Enumerable.Range(1, 4).Select(p => $"Paragraph {p}: " + string.Join(" ", Enumerable.Repeat("some **bold** words and `code`", 12))))
            + "\n\n```csharp\n" + string.Join("\n", Enumerable.Range(1, 20).Select(l => $"var line{l} = Compute({l});")) + "\n```\n\n- one\n- two\n- three";
        for (var i = 0; i < 150; i++)
        {
            ClaudeOf(vm).Claude.Items.Add(new UserChatItem($"Question {i}", null));
            ClaudeOf(vm).Claude.Items.Add(new TextChatItem(answer));
        }
        vm.ShowToolWindow(Karvon.Claude.ClaudeModule.ToolWindowId);
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(500));
        var scroll = window.GetVisualDescendants().OfType<ClaudeView>().Single().FindControl<ScrollViewer>("ChatScroll")!;
        var scrolls = 0;
        scroll.ScrollChanged += (_, e) =>
        {
            if (ReferenceEquals(e.Source, scroll))
                scrolls++;
        };
        var items = window.GetVisualDescendants().OfType<ClaudeView>().Single().FindControl<ItemsControl>("ChatItems")!;

        // Only the messages in view are made, at the end of the conversation.
        var made = items.GetRealizedContainers().Count();
        Assert.InRange(made, 1, 60);
        Assert.Contains(items.GetRealizedContainers(), c => c.DataContext == ClaudeOf(vm).Claude.Items[^1]);

        // Away and back: still at the end, without laying out every message again.
        Time(() => vm.ShowToolWindow(ToolWindows.SolutionExplorer));
        scrolls = 0;
        Time(() => vm.ShowToolWindow(Karvon.Claude.ClaudeModule.ToolWindowId));
        Assert.InRange(scrolls, 0, 10);
        Assert.InRange(items.GetRealizedContainers().Count(), 1, 60);
        await WaitUntil(() => scroll.Offset.Y >= scroll.Extent.Height - scroll.Viewport.Height - 2, TimeSpan.FromSeconds(2));
        Assert.True(scroll.Offset.Y >= scroll.Extent.Height - scroll.Viewport.Height - 2, $"{scroll.Offset.Y} of {scroll.Extent.Height - scroll.Viewport.Height}");
        window.Close();
    }
}
