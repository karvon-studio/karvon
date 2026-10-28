using Karvon.Claude;

namespace Karvon.ViewModels;

/// <summary>
/// Conversations left while Claude works on them keep working: opening another conversation (or a new one) moves the
/// running one to the background with its CLI, as it does one whose background tasks (a command, an agent) still run in
/// its CLI. What the CLI sends meanwhile waits, in order, and is replayed onto the conversation as it was left when it
/// is opened again; a question for the user (an approval, a plan) asks for attention and shows in Past conversations.
/// One that finishes while away, with no background task left, lets its CLI go: its transcript then holds all of it,
/// and opening it again reads that.
/// </summary>
public partial class ClaudeViewModel
{
    readonly List<BackgroundConversation> _background = [];

    /// <summary>How many tasks Claude runs in the background of the conversation shown: they end with its CLI.</summary>
    int _backgroundTasks;

    sealed class BackgroundConversation(ClaudeSession session)
    {
        public ClaudeSession Session { get; } = session;

        /// <summary>Claude was working on it when it was left (else only its background tasks ran).</summary>
        public bool IsRunning { get; init; }

        /// <summary>How many tasks Claude runs in its background.</summary>
        public int Tasks { get; set; }

        public string? SessionId { get; set; }

        public string? SessionCwd { get; init; }

        public List<ChatItem> Items { get; init; } = [];

        public Dictionary<string, ToolChatItem> Tools { get; init; } = [];

        public DateTime TurnStarted { get; init; }

        public int Tokens { get; init; }

        public bool Thinking { get; init; }

        public bool Interrupting { get; init; }

        public int? ContextTokens { get; init; }

        /// <summary>What the CLI sent while the conversation was not shown.</summary>
        public List<ClaudeEvent> Events { get; } = [];

        /// <summary>Claude waits for the user (an approval, an answer, a plan).</summary>
        public bool IsWaiting { get; set; }

        /// <summary>Remote Control as it was ("on" or "connecting", with the claude.ai link), or null when it was off.</summary>
        public (string State, string? Url)? RemoteControl { get; set; }
    }

    /// <summary>How many conversations Claude is working on in the background.</summary>
    public int BackgroundCount => _background.Count;

    public bool HasBackground => _background.Count > 0;

    /// <summary>
    /// Moves the conversation shown to the background if Claude is working on it or runs tasks in its background; false
    /// when it does neither.
    /// </summary>
    bool MoveToBackground()
    {
        if (_session is not { HasExited: false } session || !IsRunning && _backgroundTasks == 0)
            return false;
        _background.Add(new BackgroundConversation(session)
        {
            IsRunning = IsRunning,
            Tasks = _backgroundTasks,
            SessionId = session.SessionId ?? _resumeId,
            SessionCwd = _sessionCwd,
            Items = [.. Items],
            Tools = new(_tools),
            TurnStarted = _turnStarted,
            Tokens = _tokens,
            Thinking = _thinking,
            Interrupting = _interrupting,
            ContextTokens = ContextTokens,
            IsWaiting = Items.Any(i => i is PermissionChatItem { IsPending: true } or QuestionChatItem { IsPending: true } or PlanChatItem { IsPending: true }),
            RemoteControl = _remoteSession == session ? (RemoteControlState, RemoteControlUrl) : null,
        });
        _session = null;
        _backgroundTasks = 0;
        if (_remoteSession == session)
            ClearRemoteControl();
        BackgroundChanged();
        return true;
    }

    /// <summary>Shows a conversation Claude works on in the background again, with what happened meanwhile.</summary>
    bool BringBack(string sessionId)
    {
        if (_background.FirstOrDefault(b => b.SessionId == sessionId) is not { } conversation)
            return false;
        _background.Remove(conversation);
        _session = conversation.Session;
        _sessionCwd = conversation.SessionCwd;
        _resumeId = conversation.SessionId;
        _backgroundTasks = conversation.Tasks;
        foreach (var item in conversation.Items)
            Items.Add(item);
        foreach (var (id, tool) in conversation.Tools)
            _tools[id] = tool;
        ContextTokens = conversation.ContextTokens;
        if (conversation.RemoteControl is var (state, url))
        {
            _remoteSession = conversation.Session;
            RemoteControlUrl = url;
            RemoteControlState = state;
        }
        if (conversation.IsRunning)
        {
            BeginTurn();
            _turnStarted = conversation.TurnStarted;
            _tokens = conversation.Tokens;
            _thinking = conversation.Thinking;
            _interrupting = conversation.Interrupting;
            UpdateStatus();
        }
        foreach (var ev in conversation.Events)
            Apply(ev, live: true);
        OnPropertyChanged(nameof(SessionId));
        BackgroundChanged();
        return true;
    }

    /// <summary>What the CLI of a conversation in the background sends: kept for when it is shown again.</summary>
    void HandleInBackground(ClaudeSession session, ClaudeEvent ev)
    {
        if (_background.FirstOrDefault(b => b.Session == session) is not { } conversation)
            return;
        conversation.Events.Add(ev);
        switch (ev)
        {
            case ClaudeInit init:
                conversation.SessionId = init.SessionId;
                break;
            case ClaudePermissionRequest:
                conversation.IsWaiting = true;
                BackgroundChanged();
                AttentionRequested?.Invoke();
                break;
            case ClaudePermissionCancelled:
                // Answered on claude.ai (Remote Control): Claude goes on.
                conversation.IsWaiting = false;
                BackgroundChanged();
                break;
            case ClaudeToolResults:
                // It may have edited files open in the editor.
                FilesChanged?.Invoke();
                break;
            case ClaudeBackgroundTasks tasks:
                conversation.Tasks = tasks.Descriptions.Count;
                break;
            case ClaudeResult when conversation.Tasks > 0:
                // Done for now, but its background tasks run on in its CLI, and what they report starts a turn of its own.
                break;
            case ClaudeResult:
                // Done: its transcript has the whole turn, which is what opening it again shows.
                _background.Remove(conversation);
                _ = conversation.Session.DisposeAsync().AsTask();
                BackgroundChanged();
                break;
            case ClaudeExited:
                _background.Remove(conversation);
                BackgroundChanged();
                break;
        }
    }

    void BackgroundChanged()
    {
        OnPropertyChanged(nameof(BackgroundCount));
        OnPropertyChanged(nameof(HasBackground));
        if (IsHistoryOpen)
            RefreshHistory();
    }

    /// <summary>A past conversation, marked when Claude works on it in the background (or waits there for the user).</summary>
    ClaudeConversation WithStatus(ClaudeConversation conversation) =>
        _background.FirstOrDefault(b => b.SessionId == conversation.SessionId) is { } running
            ? conversation with { Status = running.IsWaiting ? "Waiting for you" : "Working" }
            : conversation;

    async Task StopBackgroundAsync()
    {
        var all = _background.ToList();
        _background.Clear();
        foreach (var conversation in all)
            await conversation.Session.DisposeAsync();
        BackgroundChanged();
    }
}
