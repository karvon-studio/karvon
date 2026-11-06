using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Karvon.Claude;
using Karvon.ViewModels;
using Karvon.Views;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>Opening another conversation while Claude works on one leaves that one working, in the background.</summary>
public class ClaudeBackgroundTests
{
    /// <summary>A panel in a window, Confirm answering what <paramref name="answer"/> says and keeping the questions.</summary>
    static async Task<(ClaudeViewModel Vm, Window Window, List<string> Asked)> PanelAsync(string dir, Func<bool?> answer)
    {
        List<string> asked = [];
        var vm = new ClaudeViewModel(new Karvon.Services.Settings(), () => dir, () => null, () => Directory.EnumerateFiles(dir), () => FakeClaude.Executable)
        {
            Confirm = (_, question) =>
            {
                asked.Add(question);
                return Task.FromResult(answer());
            },
        };
        var window = new Window { Width = 560, Height = 900, Content = new ClaudeView { DataContext = vm } };
        window.Show();
        await WaitUntil(() => vm.Commands.Count > 0, TimeSpan.FromSeconds(10));
        return (vm, window, asked);
    }

    static Button DeleteButtonOf(Window window, string session) =>
        window.GetVisualDescendants().OfType<ListBoxItem>().Single(i => i.DataContext is ClaudeConversation c && c.SessionId == session)
            .GetVisualDescendants().OfType<Button>().Single();

    static void Click(Window window, Control control)
    {
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
    }

    [AvaloniaFact]
    public void The_panel_asks_before_deleting_through_the_window_of_Karvon()
    {
        // The panel as the module makes it: without this nothing would ask, and so nothing would be deleted.
        var claude = ClaudeOf(NewViewModel()).Claude;
        Assert.NotNull(claude.Confirm);
    }

    [AvaloniaFact]
    public async Task A_past_conversation_is_deleted_from_its_row_when_the_user_says_so()
    {
        var (dir, _) = FakeClaude.Workspace();
        FakeClaude.WriteTranscript(dir, "session-1");
        FakeClaude.WriteTranscript(dir, "session-2");
        var folder = Karvon.Claude.ClaudeCli.ProjectDirectory(dir);
        var transcript = Path.Combine(folder, "session-1.jsonl");
        // What Claude Code keeps beside a transcript.
        Directory.CreateDirectory(Path.Combine(folder, "session-1", "tool-results"));
        File.WriteAllText(Path.Combine(folder, "session-1", "tool-results", "big.txt"), "x");
        bool? answer = false;
        var (vm, window, asked) = await PanelAsync(dir, () => answer);
        vm.ToggleHistoryCommand.Execute(null);
        await WaitUntil(() => window.GetVisualDescendants().OfType<ListBoxItem>().Count() == 2, TimeSpan.FromSeconds(5));
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(200));

        // Clicked, it asks, with the conversation's title; no leaves all as it was, and the click opened nothing.
        Click(window, DeleteButtonOf(window, "session-1"));
        await WaitUntil(() => asked.Count == 1, TimeSpan.FromSeconds(5));
        Assert.StartsWith("Fix the build", asked[0]);
        Assert.True(File.Exists(transcript));
        Assert.Equal(2, vm.History.Count);
        Assert.True(vm.IsHistoryOpen);
        Assert.Empty(vm.Items);

        // Yes deletes it and what is beside it, the other conversation staying, and the list stays open.
        answer = true;
        Click(window, DeleteButtonOf(window, "session-1"));
        await WaitUntil(() => vm.History.Count == 1, TimeSpan.FromSeconds(5));
        Assert.False(File.Exists(transcript));
        Assert.False(Directory.Exists(Path.Combine(folder, "session-1")));
        Assert.Equal("session-2", vm.History.Single().SessionId);
        Assert.True(vm.IsHistoryOpen);
        Assert.Empty(vm.Items);

        // The conversation showing goes with it: the panel is empty, and sending starts another.
        await vm.ResumeConversationCommand.ExecuteAsync(vm.History.Single());
        Assert.NotEmpty(vm.Items);
        Assert.Equal("session-2", vm.SessionId);
        vm.ToggleHistoryCommand.Execute(null);
        await vm.DeleteConversationCommand.ExecuteAsync(vm.History.Single());
        Assert.Empty(vm.Items);
        Assert.Null(vm.SessionId);
        Assert.Empty(vm.History);
        Assert.True(vm.IsHistoryOpen);
        window.Close();
        await vm.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task A_conversation_Claude_works_on_stops_to_be_deleted_or_stays_in_the_background()
    {
        var (dir, _) = FakeClaude.Workspace();
        File.WriteAllText(Path.Combine(dir, ".fake-claude-sessions"), "0");
        var (vm, window, asked) = await PanelAsync(dir, () => true);
        vm.Prompt = "keep going in the background";
        await vm.SendCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.Items.OfType<TextChatItem>().LastOrDefault()?.Text == "Working on it", TimeSpan.FromSeconds(10));
        var first = vm.SessionId!;
        var transcript = FakeClaude.WriteTranscript(dir, first);

        // In the background: no button, and the command leaves it alone.
        await vm.NewConversationCommand.ExecuteAsync(null);
        Assert.Equal(1, vm.BackgroundCount);
        vm.ToggleHistoryCommand.Execute(null);
        await WaitUntil(() => vm.History.Any(h => h.SessionId == first && h.HasStatus), TimeSpan.FromSeconds(10));
        await WaitUntil(() => window.GetVisualDescendants().OfType<ListBoxItem>().Any(), TimeSpan.FromSeconds(5));
        Assert.False(DeleteButtonOf(window, first).IsVisible);
        await vm.DeleteConversationCommand.ExecuteAsync(vm.History.Single(h => h.SessionId == first));
        Assert.Empty(asked);
        Assert.True(File.Exists(transcript));

        // Showing, while Claude works on it: the question says it stops, and it does.
        await vm.ResumeConversationCommand.ExecuteAsync(vm.History.Single(h => h.SessionId == first));
        Assert.True(vm.IsRunning);
        vm.ToggleHistoryCommand.Execute(null);
        await WaitUntil(() => vm.History.Any(h => h.SessionId == first), TimeSpan.FromSeconds(5));
        await vm.DeleteConversationCommand.ExecuteAsync(vm.History.Single(h => h.SessionId == first));
        Assert.Contains("Claude is working on it, and stops.", Assert.Single(asked));
        Assert.False(vm.IsRunning);
        Assert.Equal(0, vm.BackgroundCount);
        Assert.False(File.Exists(transcript));
        Assert.Empty(vm.History);
        window.Close();
        await vm.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task A_message_streamed_without_the_internal_id_shows_once()
    {
        var (dir, _) = FakeClaude.Workspace();
        var vm = new ClaudeViewModel(new Karvon.Services.Settings(), () => dir, () => null, () => Directory.EnumerateFiles(dir), () => FakeClaude.Executable);
        var window = new Window { Width = 560, Height = 900, Content = new ClaudeView { DataContext = vm } };
        window.Show();
        await WaitUntil(() => vm.Commands.Count > 0, TimeSpan.FromSeconds(10));

        // As an older Claude Code streams it: no api_message_id, only message_start's id.
        vm.Prompt = "olderstream";
        await vm.SendCommand.ExecuteAsync(null);
        await WaitUntil(() => !vm.IsRunning, TimeSpan.FromSeconds(10));
        Assert.Equal(["Streamed by an older version."], vm.Items.OfType<TextChatItem>().Select(t => t.Text));
        window.Close();
        await vm.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task PowerShell_shows_as_the_command_it_runs()
    {
        var (dir, _) = FakeClaude.Workspace();
        var vm = new ClaudeViewModel(new Karvon.Services.Settings(), () => dir, () => null, () => Directory.EnumerateFiles(dir), () => FakeClaude.Executable);
        var window = new Window { Width = 560, Height = 900, Content = new ClaudeView { DataContext = vm } };
        window.Show();
        await WaitUntil(() => vm.Commands.Count > 0, TimeSpan.FromSeconds(10));

        // Windows' shell tool: its card and its row read as Bash's do, not as JSON.
        vm.Prompt = "powershell";
        await vm.SendCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.Items.OfType<PermissionChatItem>().Any(c => c.IsPending), TimeSpan.FromSeconds(10));
        var card = vm.Items.OfType<PermissionChatItem>().Single(c => c.IsPending);
        Assert.Equal("Allow Claude to run this command?", card.Question);
        Assert.StartsWith("winget install", card.Code);
        var row = Assert.Single(vm.Items.OfType<ToolChatItem>());
        Assert.Equal(("PowerShell", "winget install --id LLVM.LLVM --exact"), (row.Title, row.Target));
        Assert.StartsWith("winget install", row.Code);

        await vm.AllowCommand.ExecuteAsync(card);
        await WaitUntil(() => !vm.IsRunning, TimeSpan.FromSeconds(10));
        Assert.Equal("Found LLVM", row.Summary);
        window.Close();
        await vm.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task A_conversation_left_while_Claude_works_keeps_working()
    {
        var (dir, _) = FakeClaude.Workspace();
        File.WriteAllText(Path.Combine(dir, ".fake-claude-sessions"), "0");
        var vm = new ClaudeViewModel(new Karvon.Services.Settings(), () => dir, () => null, () => Directory.EnumerateFiles(dir), () => FakeClaude.Executable);
        var window = new Window { Width = 560, Height = 900, Content = new ClaudeView { DataContext = vm } };
        window.Show();
        await WaitUntil(() => vm.Commands.Count > 0, TimeSpan.FromSeconds(10));

        // Claude is working on the first conversation...
        vm.Prompt = "keep going in the background";
        await vm.SendCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.Items.OfType<TextChatItem>().LastOrDefault()?.Text == "Working on it", TimeSpan.FromSeconds(10));
        Assert.True(vm.IsRunning);
        var first = vm.SessionId!;

        // ...when the user starts another one: the first keeps working, and the new one answers meanwhile.
        await vm.NewConversationCommand.ExecuteAsync(null);
        Assert.Empty(vm.Items);
        Assert.False(vm.IsRunning);
        Assert.Equal(1, vm.BackgroundCount);
        vm.Prompt = "hello";
        await vm.SendCommand.ExecuteAsync(null);
        await WaitUntil(() => !vm.IsRunning && vm.Items.OfType<TextChatItem>().Any(), TimeSpan.FromSeconds(10));
        Assert.NotEqual(first, vm.SessionId);
        Assert.Equal(1, vm.BackgroundCount);

        // The first one goes on and asks to run a command: Past conversations says it waits for the user.
        File.WriteAllText(Path.Combine(dir, ".fake-claude-go"), "");
        FakeClaude.WriteTranscript(dir, first);
        vm.ToggleHistoryCommand.Execute(null);
        await WaitUntil(() => vm.History.Any(h => h.SessionId == first && h.Status == "Waiting for you"), TimeSpan.FromSeconds(10));
        Assert.Equal("Waiting for you", vm.History.Single(h => h.SessionId == first).Status);
        Snapshot(window, "claude-background");

        // Opening it shows it as it was left, with what happened meanwhile; approved, it finishes.
        await vm.ResumeConversationCommand.ExecuteAsync(vm.History.Single(h => h.SessionId == first));
        Assert.Equal(0, vm.BackgroundCount);
        Assert.True(vm.IsRunning);
        Assert.Equal(first, vm.SessionId);
        Assert.Equal("keep going in the background", vm.Items.OfType<UserChatItem>().First().Text);
        Assert.Contains(vm.Items.OfType<TextChatItem>(), t => t.Text == "Working on it");
        var approval = vm.Items.OfType<PermissionChatItem>().Single(p => p.IsPending);
        await vm.AllowCommand.ExecuteAsync(approval);
        await WaitUntil(() => !vm.IsRunning, TimeSpan.FromSeconds(10));
        Assert.Equal("Finished in the background.", vm.Items.OfType<TextChatItem>().Last().Text);
        await vm.DisposeAsync();
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_conversation_left_while_its_background_task_runs_keeps_it_running()
    {
        var (dir, _) = FakeClaude.Workspace();
        File.WriteAllText(Path.Combine(dir, ".fake-claude-sessions"), "0");
        var vm = new ClaudeViewModel(new Karvon.Services.Settings(), () => dir, () => null, () => Directory.EnumerateFiles(dir), () => FakeClaude.Executable);
        await WaitUntil(() => vm.Commands.Count > 0, TimeSpan.FromSeconds(10));

        // Claude's turn is over, but the command it started in the background runs on in its CLI...
        vm.Prompt = "run the longjob";
        await vm.SendCommand.ExecuteAsync(null);
        await WaitUntil(() => !vm.IsRunning && vm.Items.OfType<TextChatItem>().Any(t => t.Text == "It runs in the background."), TimeSpan.FromSeconds(10));
        var first = vm.SessionId!;

        // ...so opening another conversation leaves that CLI running, in the background.
        await vm.NewConversationCommand.ExecuteAsync(null);
        Assert.Equal(1, vm.BackgroundCount);
        FakeClaude.WriteTranscript(dir, first);
        vm.ToggleHistoryCommand.Execute(null);
        await WaitUntil(() => vm.History.Any(h => h.SessionId == first && h.Status == "Working"), TimeSpan.FromSeconds(10));

        // Opened again, it is as it was left: Claude is not working on it.
        await vm.ResumeConversationCommand.ExecuteAsync(vm.History.Single(h => h.SessionId == first));
        Assert.Equal(0, vm.BackgroundCount);
        Assert.False(vm.IsRunning);
        Assert.Contains(vm.Items.OfType<TextChatItem>(), t => t.Text == "It runs in the background.");

        // The command ends, and Claude takes up what it reported in a turn of its own, which is shown as a turn.
        File.WriteAllText(Path.Combine(dir, ".fake-claude-job-done"), "");
        await WaitUntil(() => vm.Items.OfType<TextChatItem>().Any(t => t.Text == "The job finished."), TimeSpan.FromSeconds(10));
        Assert.True(vm.IsRunning);

        // Left during that turn, it goes on in the background; once it ends, with no task left, its CLI is let go.
        await vm.NewConversationCommand.ExecuteAsync(null);
        Assert.Equal(1, vm.BackgroundCount);
        File.WriteAllText(Path.Combine(dir, ".fake-claude-job-go"), "");
        await WaitUntil(() => vm.BackgroundCount == 0, TimeSpan.FromSeconds(10));
        Assert.True(File.Exists(Path.Combine(dir, ".fake-claude-job-finished")));
        await vm.DisposeAsync();
    }
}
