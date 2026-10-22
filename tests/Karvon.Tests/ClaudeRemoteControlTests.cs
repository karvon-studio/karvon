using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Karvon.Claude;
using Karvon.ViewModels;
using Karvon.Views;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>Bypass permissions in the Modes menu, and Remote Control (the conversation continued from claude.ai).</summary>
public class ClaudeRemoteControlTests
{
    static List<JsonElement> ControlRequests(string log, string subtype) =>
        FakeClaude.Log(log).Where(e => e.Kind == "in" && e.Data.GetProperty("type").GetString() == "control_request")
            .Select(e => e.Data.GetProperty("request")).Where(r => r.GetProperty("subtype").GetString() == subtype).ToList();

    [AvaloniaFact]
    public async Task Bypass_permissions_is_chosen_in_the_menu_never_by_Shift_Tab()
    {
        var (dir, log) = FakeClaude.Workspace();
        var settings = new Karvon.Services.Settings();
        var vm = new ClaudeViewModel(settings, () => dir, () => null, () => [], () => FakeClaude.Executable);
        var window = new Window { Width = 560, Height = 900, Content = new ClaudeView { DataContext = vm } };
        window.Show();
        vm.WarmUp();
        await WaitUntil(() => vm.Commands.Count > 0, TimeSpan.FromSeconds(10));

        // The CLI is started so that bypass can be chosen during the session; it is the menu's last mode, in red.
        var argv = FakeClaude.Log(log).Last(e => e.Kind == "argv").Data.EnumerateArray().Select(a => a.GetString()).ToList();
        Assert.Contains("--allow-dangerously-skip-permissions", argv);
        Assert.DoesNotContain("--permission-mode", argv);
        Assert.Equal(["Manual", "Edit automatically", "Plan", "Auto", "Bypass permissions"], vm.Modes.Select(m => m.Label));
        Assert.Equal([false, false, false, false, true], vm.Modes.Select(m => m.IsRisky));

        // Shift+Tab goes round the other modes only.
        List<string> seen = [];
        for (var i = 0; i < 4; i++)
        {
            vm.CycleModeCommand.Execute(null);
            seen.Add(vm.PermissionMode);
        }
        Assert.Equal(["acceptEdits", "plan", "auto", "default"], seen);

        // Chosen in the menu, it is sent to the CLI and the mode chip turns red.
        vm.ToggleModesCommand.Execute(null);
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(250));
        Snapshot(window, "claude-modes-bypass");
        vm.SelectModeCommand.Execute(vm.Modes.Single(m => m.Value == "bypassPermissions"));
        Assert.Equal(("bypassPermissions", "Bypass permissions", "ShieldOff"), (vm.PermissionMode, vm.ModeLabel, vm.ModeIcon));
        Assert.Equal("bypassPermissions", settings.ClaudePermissionMode);
        await WaitUntil(() => ControlRequests(log, "set_permission_mode").Any(r => r.GetProperty("mode").GetString() == "bypassPermissions"), TimeSpan.FromSeconds(5));
        var chip = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ModeButton");
        Assert.Contains("bypass", chip.Classes);
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(150));
        Snapshot(window, "claude-bypass");
        // The CLI says the mode back (system/status): it stays as chosen, and no error shows.
        Assert.Equal("bypassPermissions", vm.PermissionMode);
        Assert.Empty(vm.Items.OfType<NoticeChatItem>());

        // From bypass, Shift+Tab leaves it.
        vm.CycleModeCommand.Execute(null);
        Assert.Equal("default", vm.PermissionMode);
        Assert.DoesNotContain("bypass", chip.Classes);

        // The next window starts in bypass when it was left in it.
        vm.SelectModeCommand.Execute(vm.Modes.Single(m => m.Value == "bypassPermissions"));
        await vm.DisposeAsync();
        var next = new ClaudeViewModel(settings, () => dir, () => null, () => [], () => FakeClaude.Executable);
        Assert.Equal("bypassPermissions", next.PermissionMode);
        next.Prompt = "hello";
        await next.SendCommand.ExecuteAsync(null);
        await WaitUntil(() => !next.IsRunning && next.Items.OfType<TextChatItem>().Any(), TimeSpan.FromSeconds(10));
        argv = FakeClaude.Log(log).Last(e => e.Kind == "argv").Data.EnumerateArray().Select(a => a.GetString()).ToList();
        Assert.Equal("bypassPermissions", argv[argv.IndexOf("--permission-mode") + 1]);
        await next.DisposeAsync();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Remote_control_lets_a_conversation_go_on_from_claude_ai()
    {
        var (dir, log) = FakeClaude.Workspace();
        File.WriteAllText(Path.Combine(dir, ".fake-claude-sessions"), "0");
        var settings = new Karvon.Services.Settings();
        var vm = new ClaudeViewModel(settings, () => dir, () => null, () => [], () => FakeClaude.Executable);
        string? opened = null;
        vm.OpenUrlRequested += url => opened = url;
        var window = new Window { Width = 560, Height = 900, Content = new ClaudeView { DataContext = vm } };
        window.Show();
        vm.WarmUp();
        await WaitUntil(() => vm.Commands.Count > 0, TimeSpan.FromSeconds(10));
        var pill = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "RemoteControlPill");
        Assert.False(pill.IsVisible);
        // The CLI does not list /remote-control when headless; Karvon offers it.
        vm.Prompt = "/remo";
        Assert.Equal("/remote-control", Assert.Single(vm.Suggestions).Label);
        vm.Prompt = "";

        // Turned on, the panel shows the conversation's link at claude.ai, and the setting keeps it on.
        await vm.ToggleRemoteControlCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.IsRemoteControlOn, TimeSpan.FromSeconds(10));
        Assert.Equal("https://claude.ai/code/session_fake-session-1", vm.RemoteControlUrl);
        Assert.True(settings.ClaudeRemoteControl);
        Assert.True(Assert.Single(ControlRequests(log, "remote_control")).GetProperty("enabled").GetBoolean());
        Assert.True(pill.IsVisible);
        vm.OpenRemoteControlCommand.Execute(null);
        Assert.Equal(vm.RemoteControlUrl, opened);

        // A prompt typed in the Claude app shows as sent from there, and Claude works on it here too. Karvon's own prompt,
        // echoed by the CLI, shows once.
        vm.Prompt = "I'll continue on my phone";
        await vm.SendCommand.ExecuteAsync(null);
        await WaitUntil(() => !vm.IsRunning && vm.Items.OfType<TextChatItem>().Any(t => t.Text == "All 12 tests passed."), TimeSpan.FromSeconds(10));
        Assert.Single(vm.Items.OfType<UserChatItem>(), u => u.Text == "I'll continue on my phone" && !u.IsRemote);
        var remote = vm.Items.OfType<UserChatItem>().Single(u => u.IsRemote);
        Assert.Equal("Run the tests from my phone", remote.Text);
        Assert.True(vm.Items.IndexOf(remote) < vm.Items.IndexOf(vm.Items.OfType<TextChatItem>().Last()));
        // The approval it needed was given on the phone: the card here is closed, and the phone's mode shows here.
        var approval = vm.Items.OfType<PermissionChatItem>().Single();
        Assert.Equal((false, "Answered on another device"), (approval.IsPending, approval.Outcome));
        Assert.Empty(FakeClaude.Log(log).Where(e => e.Kind == "decision"));
        Assert.Equal("acceptEdits", vm.PermissionMode);
        Assert.Empty(ControlRequests(log, "set_permission_mode"));
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(150));
        Snapshot(window, "claude-remote-control");

        // /remote-control turns it off (and the setting with it).
        vm.Prompt = "/remote-control";
        await vm.SendCommand.ExecuteAsync(null);
        Assert.Equal("off", vm.RemoteControlState);
        Assert.False(settings.ClaudeRemoteControl);
        Assert.False(pill.IsVisible);
        await WaitUntil(() => ControlRequests(log, "remote_control").Count == 2, TimeSpan.FromSeconds(5));
        Assert.False(ControlRequests(log, "remote_control")[1].GetProperty("enabled").GetBoolean());
        Assert.DoesNotContain(FakeClaude.Log(log), e => e.Kind == "in" && e.Data.GetProperty("type").GetString() == "user"
            && e.Data.GetProperty("message").GetProperty("content").ToString() == "/remote-control");

        // Left on, it comes back on for the next conversation's CLI.
        vm.Prompt = "/rc on";
        await vm.SendCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.IsRemoteControlOn, TimeSpan.FromSeconds(10));
        await vm.NewConversationCommand.ExecuteAsync(null);
        Assert.Equal("off", vm.RemoteControlState);
        vm.Prompt = "hello";
        await vm.SendCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.IsRemoteControlOn && !vm.IsRunning, TimeSpan.FromSeconds(10));
        Assert.Equal("https://claude.ai/code/session_fake-session-2", vm.RemoteControlUrl);

        // A connection that is lost says so; the setting stays for the next conversation.
        vm.Prompt = "bridge";
        await vm.SendCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.RemoteControlState == "off", TimeSpan.FromSeconds(10));
        Assert.Equal("Remote Control disconnected: The connection to claude.ai was lost", vm.Items.OfType<NoticeChatItem>().Last().Text);
        Assert.True(vm.Items.OfType<NoticeChatItem>().Last().IsError);
        Assert.True(settings.ClaudeRemoteControl);
        await vm.DisposeAsync();
        window.Close();
    }

    [Fact]
    public void Remote_control_events_are_read_from_the_cli()
    {
        static ClaudeEvent One(string json)
        {
            using var doc = JsonDocument.Parse(json);
            return Assert.Single(ClaudeProtocol.Parse(doc.RootElement).ToList());
        }

        Assert.Equal(new ClaudePermissionCancelled("r1"), One("""{"type":"control_cancel_request","request_id":"r1"}"""));
        Assert.Equal(new ClaudeModeChanged("plan"), One("""{"type":"system","subtype":"status","status":null,"permissionMode":"plan"}"""));
        Assert.Equal(new ClaudeBridgeState("failed", "gone"), One("""{"type":"system","subtype":"bridge_state","state":"failed","detail":"gone"}"""));
        using (var doc = JsonDocument.Parse("""{"type":"system","subtype":"status","status":"requesting"}"""))
            Assert.Empty(ClaudeProtocol.Parse(doc.RootElement));

        // A slash command sent from claude.ai comes back expanded; it shows as typed.
        Assert.Equal("/effort high", ClaudeProtocol.PromptText("<command-name>/effort</command-name>\n<command-message>effort</command-message>\n<command-args>high</command-args>"));
        Assert.Equal("/compact", ClaudeProtocol.PromptText("<command-name>/compact</command-name>\n<command-args></command-args>"));
        Assert.Equal("Fix it", ClaudeProtocol.PromptText("Fix it\n<ide_context>\nThe user has a.cs open in the editor.\n</ide_context>"));
        Assert.Null(ClaudeProtocol.PromptText("<local-command-caveat>Caveat</local-command-caveat>"));
    }
}
