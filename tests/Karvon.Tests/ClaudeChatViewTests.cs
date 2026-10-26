using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Karvon.Claude;
using Karvon.ViewModels;
using Karvon.Views;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>How the Claude panel shows a conversation: where it opens, folded summaries and prompts, scroll bars that hide nothing.</summary>
public class ClaudeChatViewTests
{
    static string Json(object value) => JsonSerializer.Serialize(value);

    /// <summary>A long conversation that was compacted, with a pasted log in it.</summary>
    static string WriteLongTranscript()
    {
        var dir = Path.Combine(Path.GetTempPath(), "karvon-chat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var lines = new List<string>
        {
            Json(new { type = "system", subtype = "compact_boundary", content = "Conversation compacted" }),
            Json(new { type = "user", isCompactSummary = true, message = new { role = "user", content = "This session is being continued from a previous conversation.\n\n## Summary\n- The build was fixed.\n- Tests pass." } }),
        };
        for (var i = 1; i <= 30; i++)
        {
            lines.Add(Json(new { type = "user", message = new { role = "user", content = $"Question {i}" } }));
            lines.Add(Json(new { type = "assistant", message = new { id = $"m{i}", role = "assistant", content = new[] { new { type = "text", text = $"Answer {i}: " + string.Join(" ", Enumerable.Repeat("words", 40)) } } } }));
        }
        var log = string.Join("\n", Enumerable.Range(1, 60).Select(n => $"at Shop.Orders.Handler.Step{n}() in /src/Orders/Handler.cs:line {n}"));
        lines.Add(Json(new { type = "user", message = new { role = "user", content = "Why does this fail?\n" + log } }));
        lines.Add(Json(new { type = "assistant", message = new { id = "last", role = "assistant", content = new[] { new { type = "text", text = "The last answer." } } } }));
        var path = Path.Combine(dir, "long.jsonl");
        File.WriteAllLines(path, lines);
        return path;
    }

    [AvaloniaFact]
    public async Task An_old_conversation_opens_at_its_end_with_its_summary_and_long_prompts_folded()
    {
        var (dir, _) = FakeClaude.Workspace();
        var vm = new ClaudeViewModel(new Karvon.Services.Settings(), () => dir, () => null, () => [], () => FakeClaude.Executable);
        var view = new ClaudeView { DataContext = vm };
        var window = new Window { Width = 520, Height = 760, Content = view };
        window.Show();
        await WaitUntil(() => vm.Commands.Count > 0, TimeSpan.FromSeconds(10));

        await vm.ResumeConversationAsync(new ClaudeConversation("long", "", DateTime.Now, WriteLongTranscript()));
        var scroll = view.FindControl<ScrollViewer>("ChatScroll")!;
        bool AtEnd() => scroll.Extent.Height > scroll.Viewport.Height && scroll.Offset.Y >= scroll.Extent.Height - scroll.Viewport.Height - 2;
        await WaitUntil(AtEnd, TimeSpan.FromSeconds(5));
        var jump = view.FindControl<Button>("JumpToBottomButton")!;
        Assert.False(jump.IsVisible);

        // The compaction is one folded line; its summary opens under it.
        var compact = Assert.IsType<CompactChatItem>(vm.Items[0]);
        Assert.Contains("The build was fixed", compact.Summary);
        Assert.False(compact.IsExpanded);
        Assert.DoesNotContain(vm.Items.OfType<UserChatItem>(), u => u.Text.Contains("being continued", StringComparison.Ordinal));
        vm.ToggleExpandCommand.Execute(compact);
        Assert.True(compact.IsExpanded);

        // A pasted log shows its first lines until it is opened.
        var pasted = vm.Items.OfType<UserChatItem>().Last();
        Assert.True(pasted.IsLong);
        Assert.EndsWith(" …", pasted.ShownText);
        Assert.Equal(8, pasted.ShownText.Split('\n').Length);
        vm.ToggleExpandCommand.Execute(pasted);
        Assert.Equal(pasted.Text, pasted.ShownText);
        vm.ToggleExpandCommand.Execute(pasted);
        Assert.False(vm.Items.OfType<UserChatItem>().First().IsLong);
        await WaitUntil(AtEnd, TimeSpan.FromSeconds(5));

        // Scrolled up with the wheel: the conversation stays where the user is, and a button goes back to the end. (The
        // offset moving by itself, as the layout settles, is not the user's scrolling: the conversation still follows.)
        for (var i = 0; i < 6; i++)
            window.MouseWheel(new Point(200, 300), new Vector(0, 3));
        await WaitUntil(() => jump.IsVisible, TimeSpan.FromSeconds(5));
        var read = scroll.Offset.Y;
        Assert.True(read < scroll.Extent.Height - scroll.Viewport.Height - 100, $"scrolled to {read}");
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(400));
        Assert.Equal(read, scroll.Offset.Y);
        Snapshot(window, "claude-jump-to-bottom");
        jump.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntil(() => AtEnd() && !jump.IsVisible, TimeSpan.FromSeconds(5));

        // The last message keeps some room under it.
        var last = view.GetVisualDescendants().OfType<MarkdownView>().Last(m => m.Markdown == "The last answer.");
        var bottom = last.TranslatePoint(new Point(0, last.Bounds.Height), scroll)!.Value.Y;
        Assert.InRange(scroll.Viewport.Height - bottom, 20, 80);
        await vm.DisposeAsync();
        window.Close();
    }

    /// <summary>The layout's warnings of a control measured again and again in one pass.</summary>
    sealed class LayoutCycles : Avalonia.Logging.ILogSink
    {
        public int Count;

        public List<string> Sources { get; } = [];

        public bool IsEnabled(Avalonia.Logging.LogEventLevel level, string area) => area == Avalonia.Logging.LogArea.Layout;

        public void Log(Avalonia.Logging.LogEventLevel level, string area, object? source, string messageTemplate) => Log(level, area, source, messageTemplate, []);

        public void Log(Avalonia.Logging.LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
        {
            if (!messageTemplate.Contains("cycle", StringComparison.Ordinal))
                return;
            Count++;
            Sources.Add(string.Join("/", propertyValues.Select(v => v?.GetType().Name)));
        }
    }

    [AvaloniaFact]
    public async Task An_answer_streams_at_the_end_of_a_long_conversation_without_a_layout_loop()
    {
        // A long conversation of messages as unlike as Claude's (paragraphs, wide code blocks, tables, lists), whose heights
        // a virtualizing panel guessed: at its end, as an answer streamed, the guesses and the end chased each other, the
        // conversation jumped up and Avalonia ended Karvon ("Infinite layout loop detected").
        var cycles = new LayoutCycles();
        var sink = Avalonia.Logging.Logger.Sink;
        Avalonia.Logging.Logger.Sink = cycles;
        try
        {
            var (dir, _) = FakeClaude.Workspace();
            var vm = new ClaudeViewModel(new Karvon.Services.Settings(), () => dir, () => null, () => [], () => FakeClaude.Executable);
            var view = new ClaudeView { DataContext = vm };
            var window = new Window { Width = 480, Height = 700, Content = view };
            window.Show();
            await WaitUntil(() => vm.Commands.Count > 0, TimeSpan.FromSeconds(10));
            var random = new Random(7);
            string Message(int i) => (i % 4) switch
            {
                0 => "Some text " + string.Join(" ", Enumerable.Repeat("word", random.Next(5, 120))),
                1 => "```kotlin\n" + string.Join("\n", Enumerable.Range(0, random.Next(2, 30)).Select(_ => new string('x', random.Next(10, 160)))) + "\n```",
                2 => "| A | B | C |\n|---|---|---|\n" + string.Join("\n", Enumerable.Range(0, random.Next(1, 8)).Select(_ => $"| {new string('a', random.Next(1, 60))} | b | {new string('c', random.Next(1, 80))} |")),
                _ => "- one\n- " + new string('y', random.Next(1, 200)) + "\n\n> " + new string('z', random.Next(1, 150)),
            };
            for (var i = 0; i < 250; i++)
                vm.Items.Add(new TextChatItem(Message(i)));
            var scroll = view.FindControl<ScrollViewer>("ChatScroll")!;
            bool AtEnd() => scroll.Offset.Y >= scroll.Extent.Height - scroll.Viewport.Height - 1;
            await WaitUntil(AtEnd, TimeSpan.FromSeconds(5));
            Assert.Equal(ClaudeView.ShownStep, view.ShownItems.Count);
            Assert.Same(vm.Items[^1], view.ShownItems[^1]);

            var ups = 0;
            var last = scroll.Offset.Y;
            scroll.ScrollChanged += (_, e) =>
            {
                // Up and away from the end (the end itself coming nearer, as the content gets shorter, is no jump).
                if (ReferenceEquals(e.Source, scroll) && scroll.Offset.Y < last - 1 && !AtEnd())
                    ups++;
                last = scroll.Offset.Y;
            };
            foreach (var width in new[] { 470, 455, 500, 399, 620 })
            {
                window.Width = width;
                var answer = new TextChatItem("");
                vm.Items.Add(answer);
                for (var step = 0; step < 40; step++)
                {
                    answer.Text += step % 7 == 0 ? "\n\n```\n" + new string('q', random.Next(5, 200)) + "\n```\n\n" : " streamed words " + new string('w', random.Next(1, 30));
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                }
                await WaitUntil(AtEnd, TimeSpan.FromSeconds(5));
                Assert.True(AtEnd(), $"at {scroll.Offset.Y} of {scroll.Extent.Height - scroll.Viewport.Height}, {width} wide");
            }
            Assert.True(cycles.Count == 0, $"{cycles.Count} layout cycles: {string.Join(", ", cycles.Sources.Distinct())}");
            Assert.True(ups == 0, $"it went up {ups} times");
            Assert.Equal(ClaudeView.ShownStep, view.ShownItems.Count);

            // Show earlier messages: a hundred more above, the message read before where it was on screen.
            var first = view.ShownItems[0];
            var container = view.FindControl<ItemsControl>("ChatItems")!.ContainerFromItem(first)!;
            scroll.Offset = new Vector(0, 0);
            await WaitUntil(() => false, TimeSpan.FromMilliseconds(200));
            var top = container.TranslatePoint(default, scroll)!.Value.Y;
            view.FindControl<Button>("ShowEarlierButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => view.ShownItems.Count == 2 * ClaudeView.ShownStep, TimeSpan.FromSeconds(5));
            await WaitUntil(() => false, TimeSpan.FromMilliseconds(200));
            Assert.Same(vm.Items[^(2 * ClaudeView.ShownStep)], view.ShownItems[0]);
            Assert.InRange(container.TranslatePoint(default, scroll)!.Value.Y - top, -2, 2);
            await vm.DisposeAsync();
            window.Close();
        }
        finally
        {
            Avalonia.Logging.Logger.Sink = sink;
        }
    }

    [AvaloniaFact]
    public async Task A_wide_code_block_or_table_scrolls_under_its_last_line_not_over_it()
    {
        var markdown = "```\n" + string.Join("\n", Enumerable.Range(1, 3).Select(i => $"line {i} " + new string('x', 120))) + "\n```\n\n"
            + "| Name | Region | Count | Total | Comment |\n|---|---|---|---|---|\n"
            + string.Join("\n", Enumerable.Range(1, 4).Select(i => $"| row {i} | a region with a long name | {i * 10} | {i * 100} | a comment that is long enough to overflow |"));
        var view = new MarkdownView { Markdown = markdown };
        var window = new Window { Width = 300, Height = 600, Content = new ScrollViewer { Content = view } };
        window.Show();
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(200));
        Snapshot(window, "markdown-scroll-bars");

        var sideways = view.GetVisualDescendants().OfType<ScrollViewer>().Where(s => s.Extent.Width > s.Viewport.Width + 1).ToList();
        Assert.Equal(2, sideways.Count);
        foreach (var scroller in sideways)
        {
            var bar = scroller.GetVisualDescendants().OfType<ScrollBar>().Single(b => b.Orientation == Orientation.Horizontal);
            var content = scroller.GetVisualDescendants().OfType<ScrollContentPresenter>().First();
            Assert.True(bar.IsVisible);
            var barTop = bar.TranslatePoint(default, scroller)!.Value.Y;
            var contentBottom = content.TranslatePoint(new Point(0, content.Bounds.Height), scroller)!.Value.Y;
            Assert.True(contentBottom <= barTop + 0.5, $"The scroll bar (top {barTop}) covers the content (bottom {contentBottom}).");
        }
        window.Close();
    }
}
