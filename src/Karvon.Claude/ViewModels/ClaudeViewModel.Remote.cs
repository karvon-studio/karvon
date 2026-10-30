using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Karvon.Claude;

namespace Karvon.ViewModels;

/// <summary>
/// Remote Control, as in Claude Code: the conversation shown can be continued from claude.ai/code or the Claude app
/// while its CLI runs here. Prompts sent from there show in the panel as they are taken up, and a question Karvon shows
/// may be answered there. Once turned on it stays on for the conversations that follow, until it is turned off.
/// </summary>
public partial class ClaudeViewModel
{
    /// <summary>The CLI Remote Control was turned on for (the conversation shown, or one in the background).</summary>
    ClaudeSession? _remoteSession;

    /// <summary>"off", "connecting" or "on".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRemoteControlOn), nameof(IsRemoteControlConnecting), nameof(IsRemoteControlActive), nameof(RemoteControlTip))]
    public partial string RemoteControlState { get; set; } = "off";

    /// <summary>The conversation at claude.ai/code, once connected.</summary>
    [ObservableProperty]
    public partial string? RemoteControlUrl { get; set; }

    public bool IsRemoteControlOn => RemoteControlState == "on";

    public bool IsRemoteControlConnecting => RemoteControlState == "connecting";

    public bool IsRemoteControlActive => RemoteControlState != "off";

    public string RemoteControlTip => RemoteControlState switch
    {
        "on" => "Remote Control is on: continue this conversation at claude.ai/code or in the Claude app. Click to turn it off.",
        "connecting" => "Remote Control is connecting… Click to turn it off.",
        _ => "Remote Control: continue this conversation from claude.ai/code or the Claude app (/remote-control)",
    };

    [RelayCommand]
    public async Task ToggleRemoteControlAsync()
    {
        if (RemoteControlState != "off")
        {
            await TurnRemoteControlOffAsync();
            return;
        }
        SaveRemoteControl(true);
        CheckCli();
        if (CliPath is null)
            return;
        try
        {
            // A CLI that starts now turns it on itself (the setting is on).
            var session = await EnsureSessionAsync();
            await TurnRemoteControlOnAsync(session);
        }
        catch (Exception ex)
        {
            SaveRemoteControl(false);
            Items.Add(new NoticeChatItem("Claude Code could not be started: " + ex.Message, isError: true));
        }
    }

    /// <summary>The conversation at claude.ai/code, in the browser.</summary>
    [RelayCommand]
    void OpenRemoteControl()
    {
        if (RemoteControlUrl is { Length: > 0 } url)
            OpenUrlRequested?.Invoke(url);
    }

    /// <summary>A CLI just started: Remote Control comes back on when the user left it on.</summary>
    void ResumeRemoteControl(ClaudeSession session)
    {
        if (_settings.ClaudeRemoteControl && session.IsRemoteControlAvailable != false)
            _ = TurnRemoteControlOnAsync(session);
    }

    async Task TurnRemoteControlOnAsync(ClaudeSession session)
    {
        if (_remoteSession == session && RemoteControlState != "off")
            return;
        if (session.IsRemoteControlAvailable == false)
        {
            SaveRemoteControl(false);
            Items.Add(new NoticeChatItem("Remote Control needs a claude.ai sign-in (not an API key or a cloud provider).", isError: true));
            return;
        }
        _remoteSession = session;
        RemoteControlState = "connecting";
        try
        {
            var response = await session.SetRemoteControlAsync(true);
            var url = ClaudeJson.Str(response, "session_url");
            if (_remoteSession == session)
            {
                RemoteControlUrl = url;
                RemoteControlState = "on";
            }
            else if (_background.FirstOrDefault(b => b.Session == session) is { RemoteControl: not null } left)
            {
                // Its conversation went to the background meanwhile: it shows connected when opened again.
                left.RemoteControl = ("on", url);
            }
        }
        catch (InvalidOperationException ex)
        {
            if (_background.FirstOrDefault(b => b.Session == session) is { } left)
                left.RemoteControl = null;
            if (_remoteSession != session)
                return;
            ClearRemoteControl();
            SaveRemoteControl(false);
            Items.Add(new NoticeChatItem("Remote Control could not be turned on: " + ex.Message, isError: true));
        }
    }

    async Task TurnRemoteControlOffAsync()
    {
        SaveRemoteControl(false);
        var session = _remoteSession;
        ClearRemoteControl();
        if (session is { HasExited: false })
            await SendControlAsync(() => session.SetRemoteControlAsync(false));
    }

    /// <summary>The panel no longer shows Remote Control (turned off, or its CLI went away); the setting stays.</summary>
    void ClearRemoteControl()
    {
        _remoteSession = null;
        RemoteControlState = "off";
        RemoteControlUrl = null;
    }

    void SaveRemoteControl(bool on)
    {
        if (_settings.ClaudeRemoteControl == on)
            return;
        _settings.ClaudeRemoteControl = on;
        _settings.Save();
    }

    /// <summary>The connection to claude.ai changed on its own: it reconnects, or it is lost.</summary>
    void ApplyBridgeState(ClaudeBridgeState bridge)
    {
        if (RemoteControlState == "off")
            return;
        switch (bridge.State)
        {
            case "failed" or "policy_disabled":
                ClearRemoteControl();
                Items.Add(new NoticeChatItem("Remote Control disconnected" + (bridge.Detail is { Length: > 0 } detail ? ": " + detail : "."), isError: true));
                break;
            case "reconnecting":
                RemoteControlState = "connecting";
                break;
            case "connected" or "ready" when RemoteControlUrl is not null:
                RemoteControlState = "on";
                break;
        }
    }

    /// <summary>A prompt sent from claude.ai: shown like one typed here, and Claude is at work on it.</summary>
    void ApplyRemotePrompt(ClaudeRemotePrompt prompt)
    {
        var images = prompt.Images.Select((image, i) => new ClaudeAttachment($"image {i + 1}", image.MediaType, SafeBase64(image.Base64))).ToList();
        Items.Add(new UserChatItem(prompt.Text, null, images) { IsRemote = true });
        if (!IsRunning)
            BeginTurn();
    }

    /// <summary>A question shown here was answered on claude.ai: the card is closed, and Claude goes on.</summary>
    void ApplyAnsweredElsewhere(string requestId, string outcome = "Answered on another device")
    {
        foreach (var item in Items)
        {
            switch (item)
            {
                case PermissionChatItem { IsPending: true } p when p.Request.RequestId == requestId:
                    p.IsResolved = true;
                    p.Outcome = outcome;
                    // If it was allowed there, the call comes: Karvon's tool server does not ask again.
                    Approved(p.Request);
                    break;
                case QuestionChatItem { IsPending: true } q when q.Request.RequestId == requestId:
                    q.IsResolved = true;
                    q.Outcome = outcome;
                    break;
                case PlanChatItem { IsPending: true } plan when plan.Request.RequestId == requestId:
                    plan.IsResolved = true;
                    plan.Outcome = outcome;
                    break;
            }
        }
        UpdateStatus();
    }

    /// <summary>"/remote-control" (or "/rc"): on or off, as its argument says, else the other way round.</summary>
    async Task RemoteControlCommandAsync(string argument)
    {
        var on = argument switch { "on" => true, "off" => false, _ => RemoteControlState == "off" };
        if (on != (RemoteControlState != "off"))
            await ToggleRemoteControlAsync();
    }

    static ClaudeCommand RemoteControlCommand() =>
        new("remote-control", "Continue this conversation from claude.ai/code or the Claude app (on/off)", "[on|off]");
}
