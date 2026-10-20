using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using VSL.ViewModels;
using VSL.Views;
using static VSL.Tests.UiTests;

namespace VSL.Tests;

/// <summary>Opening another conversation while Claude works on one leaves that one working, in the background.</summary>
public class ClaudeBackgroundTests
{
    [AvaloniaFact]
    public async Task A_conversation_left_while_Claude_works_keeps_working()
    {
        var (dir, _) = FakeClaude.Workspace();
        File.WriteAllText(Path.Combine(dir, ".fake-claude-sessions"), "0");
        var vm = new ClaudeViewModel(new VSL.Services.Settings(), () => dir, () => null, () => Directory.EnumerateFiles(dir), () => FakeClaude.Executable);
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
}
