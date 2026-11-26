using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using Karvon.Claude.Tools;
using Karvon.Services;

namespace Karvon.Claude.Remote;

/// <summary>How a question asked in Telegram was answered there.</summary>
public enum RemoteAnswer
{
    Allow,
    AllowForSession,
    Deny,
}

/// <summary>
/// A question an agent asks the user, as Telegram shows it: what it asks, the command or file it is about, and whether it
/// may be answered there (<paramref name="OnlyHere"/>: Karvon warns about it, so it is answered at the computer).
/// <paramref name="Answerable"/> is false for what Telegram cannot answer (Claude's questions with options, a plan).
/// </summary>
public sealed record RemoteAsk(string RequestId, string Question, string? Code, string? Description, bool CanAllowForSession, bool OnlyHere,
    bool Answerable = true);

/// <summary>
/// An agent panel, as the remote control reaches it: its conversations answered, prompted, stopped, opened again, or a new
/// one begun, each as the user would in the panel.
/// </summary>
public interface IRemoteHost
{
    /// <summary>"Claude", "Codex"...</summary>
    string AgentName { get; }

    /// <summary>The folder its conversations work in.</summary>
    string Folder { get; }

    /// <summary>A question it shows answered; false when it waits no more.</summary>
    Task<bool> AnswerAsync(string requestId, RemoteAnswer answer);

    /// <summary>A prompt for a conversation it has running; false when it has not.</summary>
    Task<bool> PromptAsync(IAgentSession session, string text);

    /// <summary>A past conversation opened and prompted; false when it is not there.</summary>
    Task<bool> ResumeAsync(string sessionId, string text);

    Task InterruptAsync(IAgentSession session);

    Task NewConversationAsync(string text);
}

/// <summary>The group Karvon talks in and the one person whose say it takes there.</summary>
public sealed record RemotePairing(long ChatId, string ChatTitle, long UserId, string UserName, string BotName);

/// <summary>
/// Remote control over Telegram: each conversation of an agent panel has a topic in a group of the user's, where what it does
/// shows as it goes (one message, edited, without a sound), its questions come with Allow and Deny (with a sound), and its
/// answer when it ends; a message in its topic is a prompt for it, one in General begins a new conversation.
/// <para>
/// Only the person who paired it is heard, in that group alone; the bot's token, the pairing and the topics are kept in the
/// vault's sealed part, which no agent reaches. Karvon reaches Telegram (long polling); nothing reaches Karvon. What it
/// sends has its secrets hidden. One Karvon window at a time polls the bot (<see cref="LockFile"/>).
/// </para>
/// </summary>
public sealed class TelegramRemote : IAsyncDisposable
{
    const string TokenKey = "karvon-telegram/token";
    const string PairingKey = "karvon-telegram/pairing";
    const string TopicsKey = "karvon-telegram/topics";

    static TelegramRemote? _shared;

    /// <summary>The one of this Karvon Studio, over Karvon's vault.</summary>
    public static TelegramRemote Shared => _shared ??= new TelegramRemote(SealedStore.Karvons, Path.Combine(Settings.ConfigDirectory, "telegram.lock"));

    readonly SealedStore _store;
    readonly HttpClient _http;
    readonly Uri? _api;
    readonly Dictionary<string, Func<IRemoteHost>> _hosts = [];
    readonly List<Chat> _chats = [];
    readonly ConcurrentDictionary<string, Ask> _asks = new();
    readonly Lock _lock = new();
    FileStream? _owner;
    CancellationTokenSource? _polling;
    Task _poll = Task.CompletedTask;
    Timer? _ticker;
    string? _pairingCode;
    DateTime _pairingUntil;
    string? _problem;
    TelegramUser? _me;

    public TelegramRemote(SealedStore store, string lockFile, HttpMessageHandler? handler = null, Uri? api = null)
    {
        _store = store;
        LockFile = lockFile;
        _http = handler is null ? new HttpClient { Timeout = Timeout.InfiniteTimeSpan } : new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _api = api;
        if (Token is { } token)
            KnownSecrets.Seal(TokenKey, token);
    }

    /// <summary>The file a Karvon window holds while it polls the bot: the others leave it to that one.</summary>
    public string LockFile { get; }

    /// <summary>Whether it may be used at all (Settings); read as it is needed.</summary>
    public Func<bool> IsOn { get; set; } = () => true;

    /// <summary>Whether a question may be answered from Telegram (Settings).</summary>
    public Func<bool> Approves { get; set; } = () => true;

    /// <summary>Whether what an agent does shows as it goes (Settings); else only its questions and its answer.</summary>
    public Func<bool> ShowsProgress { get; set; } = () => true;

    /// <summary>How often a conversation's status message is edited at most.</summary>
    public TimeSpan EditEvery { get; set; } = TimeSpan.FromSeconds(4);

    /// <summary>Its state changed (set up, paired, polling, a problem): Settings reads it again.</summary>
    public event Action? Changed;

    /// <summary>The bot's token, from the vault's sealed part.</summary>
    public string? Token => _store.Get(TokenKey);

    public RemotePairing? Pairing
    {
        get
        {
            try
            {
                return _store.Get(PairingKey) is { } json ? JsonSerializer.Deserialize<RemotePairing>(json) : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    /// <summary>A Karvon window polls the bot: this one.</summary>
    public bool IsPolling => _owner is not null && _polling is { IsCancellationRequested: false };

    /// <summary>Conversations are shown in Telegram now: it is set up, paired, on, and this window polls.</summary>
    public bool IsActive => IsPolling && Pairing is not null && IsOn();

    /// <summary>"@bot": the bot's name, once Telegram said it.</summary>
    public string? BotName => Pairing?.BotName ?? _me?.Display;

    /// <summary>The code to send in the group, while it is waited for.</summary>
    public string? PairingCode => _pairingCode is not null && DateTime.UtcNow < _pairingUntil ? _pairingCode : null;

    /// <summary>What it is now, for Settings.</summary>
    public string State =>
        Token is null ? _store.IsLocked ? "Off: the vault is locked" : "Not set up"
        : _problem is { } problem ? problem
        : !IsOn() ? "Off"
        : _owner is null && _polling is not null ? "Used by another Karvon window"
        : Pairing is { } pairing ? $"On: {pairing.BotName} in “{pairing.ChatTitle}”, for {pairing.UserName}"
        : PairingCode is { } code ? $"Waiting for “/pair {code}” in your group"
        : "Not paired with a group yet";

    /// <summary>Kept until Karvon closes: the vault has no passphrase.</summary>
    public bool IsForSession => !_store.CanKeep;

    /// <summary>An agent panel's conversations, by its kind ("claude", "agents"): made when Telegram first needs it.</summary>
    public void AddHost(string kind, Func<IRemoteHost> host) => _hosts[kind] = host;

    // ---- Setting it up ------------------------------------------------------------------------------------------

    /// <summary>The bot's token checked with Telegram and kept; the problem with it, or null.</summary>
    public async Task<string?> SetTokenAsync(string token)
    {
        token = token.Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(token, @"^\d{5,}:[A-Za-z0-9_-]{30,}$"))
            return "That is not a bot's token: @BotFather gives one as 123456789:AA…";
        try
        {
            var me = await new TelegramBot(token, _http, _api).GetMeAsync();
            if (!me.IsBot)
                return "That token is not a bot's.";
            await StopAsync();
            KnownSecrets.Seal(TokenKey, token);
            _store.Set(TokenKey, "Telegram bot", token);
            _store.Delete(PairingKey);
            _store.Delete(TopicsKey);
            _me = me;
            _problem = null;
            Start();
            return null;
        }
        catch (TelegramException ex)
        {
            return ex.Code == 401 ? "Telegram does not know that token." : ex.Message;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return "Telegram could not be reached: " + ex.Message;
        }
    }

    /// <summary>A code for the user to send in their group, valid for 15 minutes: whoever sends it there is the one heard.</summary>
    public string BeginPairing()
    {
        // Another group: the one it had is let go.
        _store.Delete(PairingKey);
        _store.Delete(TopicsKey);
        lock (_lock)
            _chats.Clear();
        const string alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
        _pairingCode = string.Concat(Enumerable.Range(0, 6).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]));
        _pairingUntil = DateTime.UtcNow.AddMinutes(15);
        Start();
        Changed?.Invoke();
        return _pairingCode;
    }

    /// <summary>The bot, its group and its topics forgotten here (the topics stay in Telegram).</summary>
    public async Task ForgetAsync()
    {
        await StopAsync();
        _store.Delete(TokenKey);
        _store.Delete(PairingKey);
        _store.Delete(TopicsKey);
        _pairingCode = null;
        _problem = null;
        _me = null;
        lock (_lock)
            _chats.Clear();
        _asks.Clear();
        Changed?.Invoke();
    }

    /// <summary>Polls the bot when it is set up and on, and no other Karvon window does.</summary>
    public void Start()
    {
        if (Token is null || !IsOn() || _polling is { IsCancellationRequested: false })
            return;
        _polling = new CancellationTokenSource();
        _ticker ??= new Timer(_ => Tick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        TryPoll();
        Changed?.Invoke();
    }

    /// <summary>Polls when no other Karvon window does (else it is tried again each tick: that window may close).</summary>
    void TryPoll()
    {
        lock (_lock)
        {
            if (_polling is not { IsCancellationRequested: false } polling || _owner is not null || !TakeLock())
                return;
            var cancel = polling.Token;
            _poll = Task.Run(() => PollAsync(cancel), CancellationToken.None);
        }
        Changed?.Invoke();
    }

    public async Task StopAsync()
    {
        _polling?.Cancel();
        try
        {
            await _poll;
        }
        catch (OperationCanceledException)
        {
        }
        _polling = null;
        _owner?.Dispose();
        _owner = null;
        Changed?.Invoke();
    }

    bool TakeLock()
    {
        if (_owner is not null)
            return true;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LockFile)!);
            _owner = new FileStream(LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    // ---- Polling ------------------------------------------------------------------------------------------------

    async Task PollAsync(CancellationToken cancel)
    {
        long offset = 0;
        while (!cancel.IsCancellationRequested)
        {
            if (Token is not { } token)
                return;
            var bot = Bot(token);
            try
            {
                _me ??= await bot.GetMeAsync(cancel);
                var updates = await bot.GetUpdatesAsync(offset, TimeSpan.FromSeconds(30), cancel);
                if (_problem is not null)
                {
                    _problem = null;
                    Changed?.Invoke();
                }
                foreach (var update in updates)
                {
                    offset = Math.Max(offset, update.UpdateId + 1);
                    await OnUIAsync(() => HandleAsync(bot, update));
                }
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is TelegramException or HttpRequestException or TaskCanceledException)
            {
                _problem = ex is TelegramException { Code: 401 } ? "Telegram does not know the bot's token any more: set it up again."
                    : ex is TelegramException { Code: 409 } ? "Another program polls this bot: Karvon waits for it to stop."
                    : "Telegram could not be reached: " + ex.Message;
                Changed?.Invoke();
                if (ex is TelegramException { Code: 401 })
                    return;
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(ex is TelegramException { Code: 409 } ? 30 : 5), cancel);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    static Task OnUIAsync(Func<Task> work) =>
        Dispatcher.UIThread.CheckAccess() ? work() : Dispatcher.UIThread.InvokeAsync(work);

    TelegramBot Bot(string token) => new(token, _http, _api);

    async Task HandleAsync(TelegramBot bot, TelegramUpdate update)
    {
        try
        {
            if (update.Callback is { } callback)
                await OnButtonAsync(bot, callback);
            else if (update.Message is { Text: { Length: > 0 } } message)
                await OnMessageAsync(bot, message);
        }
        catch (Exception ex) when (ex is TelegramException or HttpRequestException or TaskCanceledException)
        {
            _problem = "Telegram: " + ex.Message;
            Changed?.Invoke();
        }
    }

    async Task OnMessageAsync(TelegramBot bot, TelegramMessage message)
    {
        if (Pairing is not { } pairing)
        {
            await TryPairAsync(bot, message);
            return;
        }
        // Only the one who paired it, in that group.
        if (message.Chat.Id != pairing.ChatId || message.From?.Id != pairing.UserId)
            return;
        var text = message.Text!.Trim();
        if (!IsOn())
            return;
        if (!message.IsTopicMessage || message.ThreadId is not { } thread)
        {
            if (text.StartsWith('/') && !text.StartsWith("/new", StringComparison.Ordinal))
            {
                await bot.SendMessageAsync(pairing.ChatId, null, "A message here begins a new conversation; one in a conversation's topic goes to it, and /stop there stops it.", silent: true);
                return;
            }
            text = text.StartsWith("/new", StringComparison.Ordinal) ? text[4..].Trim() : text;
            if (text.Length == 0 || HostOf("claude") is not { } host)
                return;
            await host.NewConversationAsync(text);
            return;
        }
        Chat? chat;
        lock (_lock)
            chat = _chats.FirstOrDefault(c => c.ThreadId == thread);
        if (text == "/stop" || text.StartsWith("/stop@", StringComparison.Ordinal))
        {
            if (chat is { Running: true })
                await chat.Host.InterruptAsync(chat.Session);
            else
                await bot.SendMessageAsync(pairing.ChatId, thread, "It is not working on anything.", silent: true);
            return;
        }
        if (chat is not null && await chat.Host.PromptAsync(chat.Session, text))
            return;
        // Not running here now: opened again in its panel, and prompted.
        if (Topics().FirstOrDefault(t => t.Value.Thread == thread) is { Key: { } sessionId, Value: var topic }
            && HostOf(topic.Kind) is { } owner && await owner.ResumeAsync(sessionId, text))
            return;
        await bot.SendMessageAsync(pairing.ChatId, thread, "This conversation is not in Karvon any more. A message in General begins a new one.", silent: true);
    }

    async Task TryPairAsync(TelegramBot bot, TelegramMessage message)
    {
        if (PairingCode is not { } code || message.From is not { IsBot: false } from)
            return;
        var words = message.Text!.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var said = words.Length == 2 && words[0].Split('@')[0] == "/pair" ? words[1] : words.Length == 1 ? words[0] : null;
        if (!string.Equals(said, code, StringComparison.OrdinalIgnoreCase))
            return;
        if (!message.Chat.IsGroup)
        {
            await bot.SendMessageAsync(message.Chat.Id, null, "Send it in a group of yours with Topics on, where I am an administrator who may manage topics.");
            return;
        }
        if (!message.Chat.IsForum)
        {
            await bot.SendMessageAsync(message.Chat.Id, null, "Turn Topics on in this group's settings first, then send the code again.");
            return;
        }
        _me ??= await bot.GetMeAsync();
        if (!await bot.CanManageTopicsAsync(message.Chat.Id, _me.Id))
        {
            await bot.SendMessageAsync(message.Chat.Id, null, "Make me an administrator of this group who may manage topics, then send the code again.");
            return;
        }
        var pairing = new RemotePairing(message.Chat.Id, message.Chat.Title ?? "the group", from.Id, from.Display, _me.Display);
        _store.Set(PairingKey, "Telegram group", JsonSerializer.Serialize(pairing));
        _pairingCode = null;
        await bot.SendMessageAsync(message.Chat.Id, message.IsTopicMessage ? message.ThreadId : null,
            $"Karvon Studio is paired with this group, and hears {Html(from.Display)} alone here. Each conversation gets a topic of its own; a message in General begins a new one.");
        Changed?.Invoke();
    }

    async Task OnButtonAsync(TelegramBot bot, TelegramCallback callback)
    {
        if (Pairing is not { } pairing || callback.From.Id != pairing.UserId || callback.Message?.Chat.Id != pairing.ChatId)
        {
            await bot.AnswerCallbackAsync(callback.Id, "Not yours to answer.");
            return;
        }
        if (callback.Data is not { Length: > 2 } data || data[1] != ':' || !_asks.TryGetValue(data[2..], out var ask) || ask.Done)
        {
            await bot.AnswerCallbackAsync(callback.Id, "It does not wait any more.");
            return;
        }
        if (ask.Request.OnlyHere || !Approves() || !IsOn())
        {
            await bot.AnswerCallbackAsync(callback.Id, "This one is answered in Karvon.");
            return;
        }
        var answer = data[0] switch { 'a' => RemoteAnswer.Allow, 's' => RemoteAnswer.AllowForSession, _ => RemoteAnswer.Deny };
        ask.Outcome = answer switch
        {
            RemoteAnswer.Allow => "Allowed from Telegram",
            RemoteAnswer.AllowForSession => "Allowed for the session from Telegram",
            _ => "Denied from Telegram",
        };
        if (await ask.Chat.Host.AnswerAsync(ask.Request.RequestId, answer))
        {
            await bot.AnswerCallbackAsync(callback.Id, ask.Outcome.Replace(" from Telegram", "", StringComparison.Ordinal));
            Answered(ask.Request.RequestId, ask.Outcome);
        }
        else
        {
            ask.Outcome = null;
            await bot.AnswerCallbackAsync(callback.Id, "It does not wait any more.");
        }
    }

    IRemoteHost? HostOf(string kind) => _hosts.TryGetValue(kind, out var host) ? host() : null;

    // ---- What the agent panels tell it ----------------------------------------------------------------------------

    sealed class Chat(IRemoteHost host, IAgentSession session, string kind)
    {
        public IRemoteHost Host { get; } = host;

        public IAgentSession Session { get; } = session;

        public string Kind { get; } = kind;

        public string? SessionId { get; set; }

        public int? ThreadId { get; set; }

        public string Title { get; set; } = "";

        public bool Named { get; set; }

        public int? StatusId { get; set; }

        public bool Running { get; set; }

        public DateTime Started { get; set; }

        public int Tokens { get; set; }

        public string? Step { get; set; }

        public bool Thinking { get; set; }

        public HashSet<string> Files { get; } = new(StringComparer.Ordinal);

        public int Waiting { get; set; }

        public bool Dirty { get; set; }

        public DateTime Edited { get; set; }

        /// <summary>Its Telegram calls, one after another (a message is edited after it was sent).</summary>
        public Task Queue { get; set; } = Task.CompletedTask;
    }

    sealed class Ask(Chat chat, RemoteAsk request)
    {
        public Chat Chat { get; } = chat;

        public RemoteAsk Request { get; } = request;

        public int? MessageId { get; set; }

        public string Html { get; set; } = "";

        public bool Done { get; set; }

        /// <summary>How it is being answered from Telegram: what its message says once it is.</summary>
        public string? Outcome { get; set; }
    }

    Chat ChatOf(IRemoteHost host, IAgentSession session, string kind)
    {
        lock (_lock)
        {
            if (_chats.FirstOrDefault(c => c.Session == session) is { } chat)
                return chat;
            chat = new Chat(host, session, kind) { SessionId = session.SessionId };
            if (chat.SessionId is { } id && Topics().TryGetValue(id, out var topic))
            {
                chat.ThreadId = topic.Thread;
                chat.Named = true;
            }
            _chats.Add(chat);
            return chat;
        }
    }

    /// <summary>The user sent a prompt in the panel: the conversation's topic is named by its first.</summary>
    public void Prompted(IRemoteHost host, IAgentSession session, string kind, string text)
    {
        if (!IsActive)
            return;
        var chat = ChatOf(host, session, kind);
        if (chat.Title.Length == 0)
            chat.Title = FirstLine(text, 60);
    }

    /// <summary>What an agent did (any thread is fine: it is noted, and Telegram told in order).</summary>
    public void Observe(IRemoteHost host, IAgentSession session, string kind, ClaudeEvent ev)
    {
        if (!IsActive)
            return;
        var chat = ChatOf(host, session, kind);
        lock (_lock)
        {
            switch (ev)
            {
                case ClaudeInit init:
                    chat.SessionId = init.SessionId;
                    if (chat.ThreadId is null && Topics().TryGetValue(init.SessionId, out var topic))
                    {
                        chat.ThreadId = topic.Thread;
                        chat.Named = true;
                    }
                    else if (chat.ThreadId is { } thread)
                        Remember(init.SessionId, thread, kind);
                    return;
                case ClaudeUserText { ParentToolUseId: null } user when chat.Title.Length == 0 && !user.Text.StartsWith('[') && !user.Text.StartsWith('<'):
                    chat.Title = FirstLine(ClaudeHistory.StripContext(user.Text), 60);
                    return;
                case ClaudePermissionCancelled cancelled:
                    Answered(cancelled.RequestId, cancelled.Outcome ?? "Answered on another device");
                    return;
                case ClaudeResult result:
                    if (chat.Running)
                        EndTurn(chat, result.IsError, result.Text, result.DurationMs);
                    return;
                case ClaudeExited exited:
                    if (chat.Running)
                        EndTurn(chat, true, exited.Stderr.Length > 0 ? exited.Stderr : "The agent stopped.", (long)(DateTime.Now - chat.Started).TotalMilliseconds);
                    lock (_lock)
                        _chats.Remove(chat);
                    return;
            }
            if (!chat.Running && ev is ClaudeBlockStart or ClaudeAssistant or ClaudeTokens or ClaudeToolResults or ClaudeRemotePrompt)
                StartTurn(chat);
            switch (ev)
            {
                case ClaudeTokens tokens:
                    chat.Tokens = tokens.OutputTokens;
                    break;
                case ClaudeBlockStart block:
                    chat.Thinking = block.BlockType == "thinking";
                    break;
                case ClaudeAssistant assistant:
                    foreach (var use in assistant.Blocks.OfType<ClaudeToolUseBlock>())
                    {
                        chat.Step = Step(use);
                        if (use.Name is "Edit" or "MultiEdit" or "Write" or "NotebookEdit"
                            && (ClaudeProtocol.Str(use.Input, "file_path") ?? ClaudeProtocol.Str(use.Input, "notebook_path")) is { } file)
                            chat.Files.Add(file);
                    }
                    if (assistant.Blocks.Any(b => b is ClaudeTextBlock))
                        chat.Thinking = false;
                    break;
            }
            chat.Dirty = true;
        }
    }

    /// <summary>An agent asks the user: shown in its topic, with Allow and Deny when it may be answered there.</summary>
    public void Asked(IRemoteHost host, IAgentSession session, string kind, RemoteAsk request)
    {
        if (!IsActive || _asks.Values.Any(a => a.Request.RequestId == request.RequestId))
            return;
        var chat = ChatOf(host, session, kind);
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
        var ask = new Ask(chat, request);
        _asks[key] = ask;
        lock (_lock)
        {
            chat.Waiting++;
            chat.Dirty = true;
        }
        var html = $"🔐 <b>{Html(Clip(request.Question, 300))}</b>"
            + (request.Code is { Length: > 0 } code ? $"\n<pre>{Html(Clip(code, 2500))}</pre>" : "")
            + (request.Description is { Length: > 0 } description ? "\n" + Html(Clip(description, 600)) : "");
        ask.Html = html;
        IReadOnlyList<IReadOnlyList<TelegramButton>>? buttons = null;
        if (!request.Answerable)
            html += "\n\n<i>Answer it in Karvon.</i>";
        else if (request.OnlyHere || !Approves())
            html += "\n\n<i>" + (request.OnlyHere ? "Karvon warns about this one: answer it at the computer." : "Answer it in Karvon.") + "</i>";
        else
            buttons = [[new("✅ Allow", "a:" + key), .. request.CanAllowForSession ? [new TelegramButton("Allow for the session", "s:" + key)] : Array.Empty<TelegramButton>(), new("❌ Deny", "d:" + key)]];
        Enqueue(chat, async (bot, pairing) =>
        {
            await EnsureTopicAsync(bot, pairing, chat);
            if (ask.Done)
                return;
            ask.MessageId = await bot.SendMessageAsync(pairing.ChatId, chat.ThreadId, html, buttons);
            // Answered in Karvon while it was being sent.
            if (ask.Done)
                await bot.EditMessageAsync(pairing.ChatId, ask.MessageId.Value, ask.Html + $"\n\n<i>{Html(ask.Outcome ?? "Answered")}</i>");
        });
    }

    /// <summary>A question answered (in Karvon, on another device, from Telegram): its message says how, without its buttons.</summary>
    public void Answered(string requestId, string outcome)
    {
        if (_asks.FirstOrDefault(a => a.Value.Request.RequestId == requestId) is not { Key: { } key, Value: { } ask } || ask.Done)
            return;
        ask.Done = true;
        ask.Outcome ??= outcome;
        _asks.TryRemove(key, out _);
        lock (_lock)
        {
            ask.Chat.Waiting = Math.Max(0, ask.Chat.Waiting - 1);
            ask.Chat.Dirty = true;
        }
        Enqueue(ask.Chat, async (bot, pairing) =>
        {
            if (ask.MessageId is { } id)
                await bot.EditMessageAsync(pairing.ChatId, id, ask.Html + $"\n\n<i>{Html(ask.Outcome)}</i>");
        });
    }

    /// <summary>A conversation deleted in Karvon: its topic goes too.</summary>
    public void Deleted(string sessionId)
    {
        if (Pairing is not { } pairing || Token is not { } token || !Topics().TryGetValue(sessionId, out var topic))
            return;
        Forget(sessionId);
        lock (_lock)
            _chats.RemoveAll(c => c.SessionId == sessionId);
        _ = Run(() => Bot(token).DeleteTopicAsync(pairing.ChatId, topic.Thread));
    }

    void StartTurn(Chat chat)
    {
        chat.Running = true;
        chat.Started = DateTime.Now;
        chat.Tokens = 0;
        chat.Step = null;
        chat.Thinking = false;
        chat.Files.Clear();
        chat.StatusId = null;
        if (!ShowsProgress())
            return;
        var html = StatusHtml(chat);
        Enqueue(chat, async (bot, pairing) =>
        {
            await EnsureTopicAsync(bot, pairing, chat);
            chat.StatusId = await bot.SendMessageAsync(pairing.ChatId, chat.ThreadId, html, silent: true);
            chat.Edited = DateTime.Now;
        });
    }

    void EndTurn(Chat chat, bool isError, string? text, long durationMs)
    {
        chat.Running = false;
        chat.Dirty = false;
        var took = Took(TimeSpan.FromMilliseconds(durationMs));
        var files = chat.Files.Count;
        var status = (isError ? "❌ <b>Stopped</b>" : "✅ <b>Done</b>") + $" · {took}" + (files > 0 ? $" · {files} {(files == 1 ? "file" : "files")} changed" : "");
        var answer = Redaction.Text(text ?? "").Trim();
        Enqueue(chat, async (bot, pairing) =>
        {
            await EnsureTopicAsync(bot, pairing, chat);
            if (chat.StatusId is { } id)
                await bot.EditMessageAsync(pairing.ChatId, id, status);
            var head = (isError ? "❌ " : "✅ ") + $"<b>{Html(chat.Host.AgentName)}</b> " + (isError ? "stopped" : "is done") + $" ({took}).";
            if (answer.Length == 0)
                await bot.SendMessageAsync(pairing.ChatId, chat.ThreadId, head);
            else if (head.Length + answer.Length < 3500)
                await bot.SendMessageAsync(pairing.ChatId, chat.ThreadId, head + "\n\n" + Html(answer));
            else
            {
                // A long answer: its start here, all of it as a file.
                await bot.SendMessageAsync(pairing.ChatId, chat.ThreadId, head + "\n\n" + Html(Clip(answer, 1500)) + "\n\n<i>All of it is in the file below.</i>");
                await bot.SendDocumentAsync(pairing.ChatId, chat.ThreadId, "answer.md", Encoding.UTF8.GetBytes(answer), null, silent: true);
            }
        });
    }

    /// <summary>The status messages of the conversations that work, edited when something changed (not more often than <see cref="EditEvery"/>).</summary>
    void Tick()
    {
        if (_owner is null && _polling is { IsCancellationRequested: false })
            TryPoll();
        if (!IsActive || !ShowsProgress())
            return;
        List<(Chat Chat, string Html)> due = [];
        lock (_lock)
            foreach (var chat in _chats)
                if (chat.Running && chat.StatusId is not null && DateTime.Now - chat.Edited >= EditEvery
                    && (chat.Dirty || DateTime.Now - chat.Edited >= TimeSpan.FromSeconds(30)))
                {
                    chat.Dirty = false;
                    chat.Edited = DateTime.Now;
                    due.Add((chat, StatusHtml(chat)));
                }
        foreach (var (chat, html) in due)
            Enqueue(chat, async (bot, pairing) =>
            {
                if (chat.Running && chat.StatusId is { } id)
                    await bot.EditMessageAsync(pairing.ChatId, id, html);
            });
    }

    /// <summary>"Waiting for you · 1m 20s · 12.3k tokens", what it does now, and how many files it changed.</summary>
    static string StatusHtml(Chat chat)
    {
        var verb = chat.Waiting > 0 ? "✋ <b>Waiting for you</b>" : chat.Thinking ? "💭 <b>Thinking</b>" : "⏳ <b>Working</b>";
        var line = verb + " · " + Took(DateTime.Now - chat.Started) + (chat.Tokens > 0 ? $" · {(chat.Tokens >= 1000 ? $"{chat.Tokens / 1000.0:0.0}k" : chat.Tokens)} tokens" : "");
        if (chat.Step is { } step)
            line += "\n" + Html(step);
        if (chat.Files.Count > 0)
            line += $"\n{chat.Files.Count} {(chat.Files.Count == 1 ? "file" : "files")} changed: " + Html(string.Join(", ", chat.Files.Take(5).Select(Path.GetFileName)) + (chat.Files.Count > 5 ? "…" : ""));
        return line;
    }

    /// <summary>A tool call as one line: "Edit Program.cs", "Bash: dotnet test".</summary>
    static string Step(ClaudeToolUseBlock use)
    {
        string? Str(string name) => ClaudeProtocol.Str(use.Input, name);
        var name = use.Name.StartsWith("mcp__", StringComparison.Ordinal) ? use.Name.Split("__").Last() : use.Name;
        var detail = Str("file_path") is { } file ? Path.GetFileName(file)
            : Str("command") is { } command ? FirstLine(command, 80)
            : Str("pattern") ?? Str("url") ?? Str("query") ?? Str("description");
        return Redaction.Text(detail is { Length: > 0 } ? $"{name}: {FirstLine(detail, 80)}" : name);
    }

    async Task EnsureTopicAsync(TelegramBot bot, RemotePairing pairing, Chat chat)
    {
        var name = $"{chat.Host.AgentName} · {Path.GetFileName(chat.Host.Folder.TrimEnd('/', '\\'))}" + (chat.Title.Length > 0 ? " · " + chat.Title : "");
        name = Redaction.Text(name);
        if (chat.ThreadId is { } thread)
        {
            if (!chat.Named && chat.Title.Length > 0)
            {
                chat.Named = true;
                await bot.RenameTopicAsync(pairing.ChatId, thread, name);
            }
            return;
        }
        chat.ThreadId = await bot.CreateTopicAsync(pairing.ChatId, name);
        chat.Named = chat.Title.Length > 0;
        if ((chat.SessionId ?? chat.Session.SessionId) is { } id)
        {
            chat.SessionId = id;
            Remember(id, chat.ThreadId.Value, chat.Kind);
        }
    }

    /// <summary>Telegram told in the chat's order, by this window's bot; what goes wrong is shown in Settings.</summary>
    void Enqueue(Chat chat, Func<TelegramBot, RemotePairing, Task> work)
    {
        if (Token is not { } token || Pairing is not { } pairing)
            return;
        var bot = Bot(token);
        lock (_lock)
            chat.Queue = chat.Queue.ContinueWith(_ => Run(() => work(bot, pairing)), TaskScheduler.Default).Unwrap();
    }

    async Task Run(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception ex) when (ex is TelegramException or HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            _problem = "Telegram: " + ex.Message;
            Changed?.Invoke();
        }
    }

    // ---- The topics, by conversation --------------------------------------------------------------------------------

    public sealed record Topic(int Thread, string Kind);

    Dictionary<string, Topic> Topics()
    {
        try
        {
            return _store.Get(TopicsKey) is { } json ? JsonSerializer.Deserialize<Dictionary<string, Topic>>(json) ?? [] : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    void Remember(string sessionId, int thread, string kind)
    {
        var topics = Topics();
        topics[sessionId] = new Topic(thread, kind);
        _store.Set(TopicsKey, "Telegram topics", JsonSerializer.Serialize(topics));
    }

    void Forget(string sessionId)
    {
        var topics = Topics();
        if (topics.Remove(sessionId))
            _store.Set(TopicsKey, "Telegram topics", JsonSerializer.Serialize(topics));
    }

    // ---- Text ---------------------------------------------------------------------------------------------------

    /// <summary>Text as Telegram's HTML shows it, its secrets hidden.</summary>
    static string Html(string text) => WebUtility.HtmlEncode(Redaction.Text(text)).Replace("&#39;", "'", StringComparison.Ordinal).Replace("&quot;", "\"", StringComparison.Ordinal);

    static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    static string FirstLine(string text, int max) => Clip(text.Trim().Split('\n')[0].Trim(), max);

    static string Took(TimeSpan time) => time.TotalHours >= 1 ? $"{(int)time.TotalHours}h {time.Minutes}m"
        : time.TotalMinutes >= 1 ? $"{(int)time.TotalMinutes}m {time.Seconds}s" : $"{Math.Max(0, (int)time.TotalSeconds)}s";

    public async ValueTask DisposeAsync()
    {
        _ticker?.Dispose();
        await StopAsync();
        _http.Dispose();
    }
}
