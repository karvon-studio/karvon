using System.Text.Json;
using System.Text.Json.Nodes;
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
                List<RemoteQuestion> questions = request.Input.TryGetProperty("questions", out var qs) && qs.ValueKind == JsonValueKind.Array
                    ? [.. qs.EnumerateArray().Select(q => new RemoteQuestion(ClaudeJson.Str(q, "question") ?? "", ClaudeJson.Str(q, "header"), ClaudeJson.Bool(q, "multiSelect"),
                        q.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array
                            ? [.. opts.EnumerateArray().Select(o => new RemoteOption(ClaudeJson.Str(o, "label") ?? "", ClaudeJson.Str(o, "description")))]
                            : []))]
                    : [];
                ask = new RemoteAsk(request.RequestId, $"{AgentName} asks you", null, null, false, false, Answerable: questions.Count > 0, Questions: questions);
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
        if (WaitingInBackground(requestId) is not var (conversation, request))
            return false;
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
        AnsweredInBackground(conversation, requestId, outcome);
        return true;
    }

    async Task<bool> IRemoteHost.AnswerQuestionsAsync(string requestId, IReadOnlyList<RemoteReply> replies)
    {
        // Shown here: its options picked (or its own answer typed), and sent, as its card's button would.
        if (Items.OfType<QuestionChatItem>().FirstOrDefault(q => q.IsPending && q.Request.RequestId == requestId) is { } card)
        {
            foreach (var (entry, reply) in card.Questions.Zip(replies))
            {
                foreach (var option in entry.Options)
                    option.IsSelected = false;
                entry.Other = reply.Other ?? "";
                foreach (var i in reply.Picked.Where(i => i >= 0 && i < entry.Options.Count))
                    entry.Options[i].IsSelected = true;
            }
            await SubmitAnswersCommand.ExecuteAsync(card);
            return !card.IsPending;
        }
        if (WaitingInBackground(requestId) is not var (conversation, request))
            return false;
        var answers = new JsonObject();
        if (request.Input.TryGetProperty("questions", out var qs) && qs.ValueKind == JsonValueKind.Array)
            foreach (var (q, reply) in qs.EnumerateArray().Zip(replies))
            {
                var labels = q.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array
                    ? opts.EnumerateArray().Select(o => ClaudeJson.Str(o, "label") ?? "").ToList() : [];
                answers[ClaudeJson.Str(q, "question") ?? ""] = reply.Other is { Length: > 0 } other ? other.Trim()
                    : string.Join(", ", reply.Picked.Where(i => i >= 0 && i < labels.Count).Select(i => labels[i]));
            }
        var updated = JsonNode.Parse(request.Input.GetRawText()) as JsonObject ?? [];
        updated["answers"] = answers;
        using var doc = JsonDocument.Parse(updated.ToJsonString());
        await conversation.Session.RespondToPermissionAsync(requestId, ClaudePermissionDecision.AllowOnce(doc.RootElement.Clone()), "the user, from Telegram");
        AnsweredInBackground(conversation, requestId, "Answered from Telegram");
        return true;
    }

    /// <summary>The conversation in the background whose question this is, while it waits for its answer.</summary>
    (BackgroundConversation Conversation, ClaudePermissionRequest Request)? WaitingInBackground(string requestId) =>
        _background.FirstOrDefault(b => b.Events.OfType<ClaudePermissionRequest>().Any(r => r.RequestId == requestId)
            && !b.Events.OfType<ClaudePermissionCancelled>().Any(c => c.RequestId == requestId)) is { } conversation
            ? (conversation, conversation.Events.OfType<ClaudePermissionRequest>().First(r => r.RequestId == requestId))
            : null;

    /// <summary>Its card closed when it is shown again, saying how it was answered.</summary>
    void AnsweredInBackground(BackgroundConversation conversation, string requestId, string outcome)
    {
        conversation.Events.Add(new ClaudePermissionCancelled(requestId, outcome));
        conversation.IsWaiting = conversation.Events.OfType<ClaudePermissionRequest>()
            .Any(r => !conversation.Events.OfType<ClaudePermissionCancelled>().Any(c => c.RequestId == r.RequestId));
        BackgroundChanged();
    }

    async Task<bool> IRemoteHost.PromptAsync(IAgentSession session, string text, IReadOnlyList<RemoteImage> images)
    {
        text = RemoteText(text);
        List<ClaudeImage> sent = [.. images.Select(i => new ClaudeImage(i.MediaType, Convert.ToBase64String(i.Data)))];
        if (session == _session && !session.HasExited)
        {
            ApplyRemotePrompt(new ClaudeRemotePrompt(text, sent));
            await session.SendUserMessageAsync(SealedFiles.Unmention(text, _workingDirectory()), images: sent);
            return true;
        }
        if (_background.FirstOrDefault(b => b.Session == session) is { } conversation && !session.HasExited)
        {
            conversation.Events.Add(new ClaudeRemotePrompt(text, sent));
            await session.SendUserMessageAsync(SealedFiles.Unmention(text, _workingDirectory()), images: sent);
            return true;
        }
        return false;
    }

    static List<ClaudeAttachment> RemoteAttachments(IReadOnlyList<RemoteImage> images) => [.. images.Select(i => new ClaudeAttachment(i.Name, i.MediaType, i.Data))];

    /// <summary>A slash command, sent as one: with no images, and nothing after its line.</summary>
    static bool IsRemoteCommand(string text, IReadOnlyList<RemoteImage> images) => images.Count == 0 && IsSlashCommand(text);

    async Task<bool> IRemoteHost.ResumeAsync(string sessionId, string text, IReadOnlyList<RemoteImage> images)
    {
        if (sessionId.Length == 0 || sessionId is "." or ".." || sessionId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return false;
        var agent = _backend is null ? null : AgentTranscripts.Find(AgentsDirectory, _workingDirectory(), sessionId);
        var file = agent?.FilePath ?? Path.Combine(ClaudeCli.ProjectDirectory(_workingDirectory(), SelectedAccount?.ConfigDirectory), sessionId + ".jsonl");
        if (!File.Exists(file) || _backend is not null && agent is null)
            return false;
        await LoadConversationAsync(agent ?? new ClaudeConversation(sessionId, "", File.GetLastWriteTime(file), file));
        await SendPromptAsync(RemoteText(text), null, RemoteAttachments(images), IsRemoteCommand(text, images), remote: true);
        return true;
    }

    Task IRemoteHost.InterruptAsync(IAgentSession session) =>
        session == _session ? StopCommand.ExecuteAsync(null) : session.InterruptAsync();

    async Task IRemoteHost.NewConversationAsync(string text, IReadOnlyList<RemoteImage> images)
    {
        await NewConversationAsync();
        CheckCli();
        if (CliPath is null)
            return;
        await SendPromptAsync(RemoteText(text), null, RemoteAttachments(images), IsRemoteCommand(text, images), remote: true);
    }

    /// <summary>A prompt from Telegram, a secret in it hidden (it was not asked about here).</summary>
    string RemoteText(string text) => _settings.ClaudeKeepSecretsAway ? Redaction.Text(text) : text;
}
