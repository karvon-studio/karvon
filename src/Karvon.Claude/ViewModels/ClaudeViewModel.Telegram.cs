using System.Text;
using Karvon.Claude;
using Karvon.Claude.Remote;
using Karvon.Claude.Tools;

namespace Karvon.ViewModels;

/// <summary>
/// The panel's conversations in Telegram (<see cref="TelegramRemote"/>): what they do and ask is told there, and what the
/// user answers or writes there is taken as if it were done here: a card answered, a prompt sent, a past conversation
/// opened again. Karvon's own questions about a destructive call are answered here alone.
/// </summary>
public partial class ClaudeViewModel : IRemoteHost
{
    /// <summary>Telegram, when the panel's conversations are shown there.</summary>
    public TelegramRemote? Remote { get; set; }

    /// <summary>Which panel it is for Telegram ("claude", "agents"): a topic's conversation is opened again in its own.</summary>
    public string RemoteKind { get; set; } = "claude";

    string IRemoteHost.Folder => _workingDirectory();

    /// <summary>What a conversation did, told to Telegram; and a question it asks, as its card says it.</summary>
    void Report(IAgentSession session, ClaudeEvent ev)
    {
        if (Remote is not { IsActive: true } remote)
            return;
        remote.Observe(this, session, RemoteKind, ev);
        if (ev is ClaudePermissionRequest request)
            ReportAsk(session, request);
    }

    void ReportAsk(IAgentSession? session, ClaudePermissionRequest request)
    {
        if (Remote is not { IsActive: true } remote || session is null)
            return;
        RemoteAsk ask;
        switch (request.ToolName)
        {
            case "AskUserQuestion":
                var questions = request.Input.TryGetProperty("questions", out var qs) && qs.ValueKind == System.Text.Json.JsonValueKind.Array
                    ? string.Join("\n", qs.EnumerateArray().Select(q => ClaudeJson.Str(q, "question")).OfType<string>())
                    : null;
                ask = new RemoteAsk(request.RequestId, $"{AgentName} asks you", questions, null, false, false, Answerable: false);
                break;
            case "ExitPlanMode":
                ask = new RemoteAsk(request.RequestId, $"{AgentName} has a plan for you to approve", ClaudeJson.Str(request.Input, "plan"), null, false, false, Answerable: false);
                break;
            default:
                var card = BuildPermission(request);
                var own = Tools?.Plan(request.ToolName, request.Input);
                var code = card.Code ?? (card.Diff.Count > 0 ? DiffText(card.Diff) : null);
                ask = new RemoteAsk(request.RequestId, (request.AgentId is null ? "" : "A subagent asks: ") + card.Question, code, card.Description,
                    card.HasSessionOption, OnlyHere: own?.Access == ToolAccess.Destructive);
                break;
        }
        remote.Asked(this, session, RemoteKind, ask);
    }

    static string DiffText(IReadOnlyList<DiffLine> diff) => string.Join("\n", diff.Select(l => l.Kind switch
    {
        DiffLineKind.Added => "+ " + l.Text,
        DiffLineKind.Removed => "- " + l.Text,
        DiffLineKind.Gap => "…",
        _ => "  " + l.Text,
    }));

    void RemoteAnswered(string requestId, string? outcome) => Remote?.Answered(requestId, outcome ?? "Answered in Karvon");

    // ---- What Telegram asks of it -----------------------------------------------------------------------------------

    async Task<bool> IRemoteHost.AnswerAsync(string requestId, RemoteAnswer answer)
    {
        // Shown here: as its card's buttons would.
        if (Items.OfType<PermissionChatItem>().FirstOrDefault(p => p.IsPending && p.Request.RequestId == requestId) is { } card)
        {
            switch (answer)
            {
                case RemoteAnswer.Allow:
                    await AllowCommand.ExecuteAsync(card);
                    break;
                case RemoteAnswer.AllowForSession when card.HasSessionOption:
                    await AllowForSessionCommand.ExecuteAsync(card);
                    break;
                case RemoteAnswer.AllowForSession:
                    await AllowCommand.ExecuteAsync(card);
                    break;
                default:
                    await DenyCommand.ExecuteAsync(card);
                    break;
            }
            return true;
        }
        // In the background: answered there, and its card closed when it is shown again.
        if (_background.FirstOrDefault(b => b.Events.OfType<ClaudePermissionRequest>().Any(r => r.RequestId == requestId)
                && !b.Events.OfType<ClaudePermissionCancelled>().Any(c => c.RequestId == requestId)) is not { } conversation)
            return false;
        var request = conversation.Events.OfType<ClaudePermissionRequest>().First(r => r.RequestId == requestId);
        var outcome = answer == RemoteAnswer.Deny ? "Denied from Telegram" : "Allowed from Telegram";
        if (_asks.TryRemove(requestId, out var asked))
            asked.TrySetResult(answer != RemoteAnswer.Deny);
        else
            await conversation.Session.RespondToPermissionAsync(requestId, answer switch
            {
                RemoteAnswer.Allow => ClaudePermissionDecision.AllowOnce(request.Input),
                RemoteAnswer.AllowForSession => new ClaudePermissionDecision(true, request.Input, request.Suggestions),
                _ => new ClaudePermissionDecision(false, Message: "The user doesn't want to proceed with this tool use. Wait for their next instruction.", Interrupt: true),
            }, "the user, from Telegram");
        conversation.Events.Add(new ClaudePermissionCancelled(requestId, outcome));
        conversation.IsWaiting = conversation.Events.OfType<ClaudePermissionRequest>()
            .Any(r => !conversation.Events.OfType<ClaudePermissionCancelled>().Any(c => c.RequestId == r.RequestId));
        BackgroundChanged();
        return true;
    }

    async Task<bool> IRemoteHost.PromptAsync(IAgentSession session, string text)
    {
        text = RemoteText(text);
        if (session == _session && !session.HasExited)
        {
            ApplyRemotePrompt(new ClaudeRemotePrompt(text, []));
            await session.SendUserMessageAsync(SealedFiles.Unmention(text, _workingDirectory()));
            return true;
        }
        if (_background.FirstOrDefault(b => b.Session == session) is { } conversation && !session.HasExited)
        {
            conversation.Events.Add(new ClaudeRemotePrompt(text, []));
            await session.SendUserMessageAsync(SealedFiles.Unmention(text, _workingDirectory()));
            return true;
        }
        return false;
    }

    async Task<bool> IRemoteHost.ResumeAsync(string sessionId, string text)
    {
        if (sessionId.Length == 0 || sessionId is "." or ".." || sessionId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return false;
        var agent = _backend is null ? null : AgentTranscripts.Find(AgentsDirectory, _workingDirectory(), sessionId);
        var file = agent?.FilePath ?? Path.Combine(ClaudeCli.ProjectDirectory(_workingDirectory(), SelectedAccount?.ConfigDirectory), sessionId + ".jsonl");
        if (!File.Exists(file) || _backend is not null && agent is null)
            return false;
        await LoadConversationAsync(agent ?? new ClaudeConversation(sessionId, "", File.GetLastWriteTime(file), file));
        await SendPromptAsync(RemoteText(text), null, [], IsSlashCommand(text), remote: true);
        return true;
    }

    Task IRemoteHost.InterruptAsync(IAgentSession session) =>
        session == _session ? StopCommand.ExecuteAsync(null) : session.InterruptAsync();

    async Task IRemoteHost.NewConversationAsync(string text)
    {
        await NewConversationAsync();
        CheckCli();
        if (CliPath is null)
            return;
        await SendPromptAsync(RemoteText(text), null, [], IsSlashCommand(text), remote: true);
    }

    /// <summary>A prompt from Telegram, a secret in it hidden (it was not asked about here).</summary>
    string RemoteText(string text) => _settings.ClaudeKeepSecretsAway ? Redaction.Text(text) : text;
}
