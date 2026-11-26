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
/// <paramref name="Answerable"/> is false for what Telegram cannot answer (a plan); <paramref name="Questions"/> are Claude's
/// questions with options, answered by them.
/// </summary>
public sealed record RemoteAsk(string RequestId, string Question, string? Code, string? Description, bool CanAllowForSession, bool OnlyHere,
    bool Answerable = true, IReadOnlyList<RemoteQuestion>? Questions = null);

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

    /// <summary>Questions with options it shows answered, one reply each; false when they wait no more.</summary>
    Task<bool> AnswerQuestionsAsync(string requestId, IReadOnlyList<RemoteReply> replies);

    /// <summary>A prompt (with the images sent with it) for a conversation it has running; false when it has not.</summary>
    Task<bool> PromptAsync(IAgentSession session, string text, IReadOnlyList<RemoteImage> images);

    /// <summary>A past conversation opened and prompted; false when it is not there.</summary>
    Task<bool> ResumeAsync(string sessionId, string text, IReadOnlyList<RemoteImage> images);

    Task InterruptAsync(IAgentSession session);

    Task NewConversationAsync(string text, IReadOnlyList<RemoteImage> images);
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
public sealed partial class TelegramRemote : IAsyncDisposable
{
    const string TokenKey = "karvon-telegram/token";
    const string PairingKey = "karvon-telegram/pairing";
    const string TopicsKey = "karvon-telegram/topics";
    const string CodeKey = "karvon-telegram/pairing-code";
    const string PausedKey = "karvon-telegram/paused";

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
    bool _wasPaired;
    string? _problem;
    TelegramUser? _me;

    public TelegramRemote(SealedStore store, string lockFile, HttpMessageHandler? handler = null, Uri? api = null)
    {
        _store = store;
        LockFile = lockFile;
        _http = handler is null ? new HttpClient { Timeout = Timeout.InfiniteTimeSpan } : new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _api = api;
        IsOn = () => !IsPaused;
        if (Token is { } token)
            KnownSecrets.Seal(TokenKey, token);
    }

    /// <summary>The file a Karvon window holds while it polls the bot: the others leave it to that one.</summary>
    public string LockFile { get; }

    /// <summary>Whether it is used now: not turned off (<see cref="IsPaused"/>).</summary>
    public Func<bool> IsOn { get; set; }

    /// <summary>Whether a question may be answered from Telegram (Settings).</summary>
    public Func<bool> Approves { get; set; } = () => true;

    /// <summary>Whether what an agent does shows as it goes (Settings); else only its questions and its answer.</summary>
    public Func<bool> ShowsProgress { get; set; } = () => true;

    /// <summary>How old a message may be and still be done: an older one was sent while Karvon was not there.</summary>
    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromMinutes(2);

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

    /// <summary>
    /// Turned off, in every Karvon window (it is kept sealed, where they all read it): nothing is told in Telegram, what is
    /// there of the turns that run goes, and what is written there is answered that it is off. The bot stays set up.
    /// </summary>
    public bool IsPaused
    {
        get => _store.Get(PausedKey) is not null;
        set
        {
            if (value == IsPaused)
                return;
            if (value)
            {
                _store.Set(PausedKey, "Telegram turned off", "1");
                Hush();
            }
            else
            {
                _store.Delete(PausedKey);
                Start();
            }
            Changed?.Invoke();
        }
    }

    /// <summary>What Telegram shows of the turns that run (their status, their questions) taken away: it is off.</summary>
    void Hush()
    {
        foreach (var (key, ask) in _asks.ToList())
        {
            ask.Done = true;
            _asks.TryRemove(key, out _);
            var id = ask.MessageId;
            ask.MessageId = null;
            if (id is { } message)
                Enqueue(ask.Chat, (bot, pairing) => bot.DeleteMessageAsync(pairing.ChatId, message));
        }
        lock (_lock)
            foreach (var chat in _chats)
            {
                chat.Waiting = 0;
                chat.Running = false;
                if (chat.StatusId is { } id)
                {
                    chat.StatusId = null;
                    Enqueue(chat, (bot, pairing) => bot.DeleteMessageAsync(pairing.ChatId, id));
                }
            }
        Announce();
    }

    DateTime _toldOff;

    /// <summary>Conversations are shown in Telegram now: it is set up, paired, on, and this window polls (or another hands it its own).</summary>
    public bool IsActive => (IsPolling || IsLinked) && Pairing is not null && IsOn();

    /// <summary>"@bot": the bot's name, once Telegram said it.</summary>
    public string? BotName => Pairing?.BotName ?? _me?.Display;

    /// <summary>The code to send in the group, while it is waited for (kept sealed: the window that polls may be another).</summary>
    public string? PairingCode
    {
        get
        {
            var kept = _store.Get(CodeKey)?.Split(' ');
            return kept is [var code, var until] && long.TryParse(until, out var ticks) && DateTime.UtcNow.Ticks < ticks ? code : null;
        }
    }

    /// <summary>What it is now, for Settings.</summary>
    public string State =>
        Token is null ? _store.IsLocked ? "Off: the vault is locked" : "Not set up"
        : _problem is { } problem ? problem
        : !IsOn() ? "Off"
        : Pairing is { } pairing ? $"On: {pairing.BotName} in “{pairing.ChatTitle}”, for {pairing.UserName}"
            + (IsPolling ? "" : IsLinked ? ", through another Karvon window" : ", waiting for the Karvon window that uses it")
        : PairingCode is { } code ? $"Waiting for “/pair {code}” in your group"
        : "Not paired with a group yet";

    /// <summary>Kept until Karvon closes: the vault has no passphrase.</summary>
    public bool IsForSession => !_store.CanKeep;

    /// <summary>
    /// An agent panel's conversations, by its kind ("claude", "agents"): made when Telegram first needs it. Its folder says
    /// which window opens a past conversation again.
    /// </summary>
    public void AddHost(string kind, Func<IRemoteHost> host, Func<string> folder)
    {
        _hosts[kind] = host;
        _folders[kind] = folder;
    }

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
        var code = string.Concat(Enumerable.Range(0, 6).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]));
        _store.Set(CodeKey, "Telegram pairing", code + " " + DateTime.UtcNow.AddMinutes(15).Ticks);
        Start();
        Changed?.Invoke();
        return code;
    }

    /// <summary>The bot, its group and its topics forgotten here (the topics stay in Telegram).</summary>
    public async Task ForgetAsync()
    {
        await StopAsync();
        _store.Delete(TokenKey);
        _store.Delete(PairingKey);
        _store.Delete(TopicsKey);
        _store.Delete(CodeKey);
        _store.Delete(LinkKey);
        _problem = null;
        _me = null;
        lock (_lock)
            _chats.Clear();
        _asks.Clear();
        Changed?.Invoke();
    }

    /// <summary>Polls the bot when it is set up and on and no other Karvon window does; else gets its own from the one that does.</summary>
    public void Start()
    {
        if (Token is null || !IsOn() || _polling is { IsCancellationRequested: false })
            return;
        _polling = new CancellationTokenSource();
        _ticker ??= new Timer(_ => Tick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        var cancel = _polling.Token;
        _poll = Task.Run(() => RunAsync(cancel), CancellationToken.None);
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
        _link = null;
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

    /// <summary>An update: another window's handed to it (by the one that polls), this one's taken here.</summary>
    async Task HandleAsync(TelegramBot bot, TelegramUpdate update, bool forwarded = false)
    {
        try
        {
            if (!forwarded && await ForwardAsync(update))
                return;
            if (update.Callback is { } callback)
                await OnButtonAsync(bot, callback);
            else if (update.Message is { } message)
                await OnMessageAsync(bot, message);
        }
        catch (Exception ex) when (ex is TelegramException or HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            _problem = "Telegram: " + ex.Message;
            Changed?.Invoke();
        }
    }

    async Task OnMessageAsync(TelegramBot bot, TelegramMessage message)
    {
        if (Pairing is not { } pairing)
        {
            if (message.Text is { Length: > 0 })
                await TryPairAsync(bot, message);
            return;
        }
        // Only the one who paired it, in that group.
        if (message.Chat.Id != pairing.ChatId || message.From?.Id != pairing.UserId)
            return;
        int? where = message.IsTopicMessage ? message.ThreadId : null;
        if (!IsOn())
        {
            if (DateTime.UtcNow - _toldOff > TimeSpan.FromMinutes(1))
            {
                _toldOff = DateTime.UtcNow;
                await bot.SendMessageAsync(pairing.ChatId, where, "Karvon's remote control is off: turn it on with the Telegram button in Karvon (by Settings).", silent: true);
            }
            return;
        }
        // Sent while no Karvon window was there to take it: not done now, unasked, hours later.
        if (message.Date > 0 && DateTimeOffset.UtcNow.ToUnixTimeSeconds() - message.Date > StaleAfter.TotalSeconds)
        {
            await bot.SendMessageAsync(pairing.ChatId, where, "Karvon was not running when this was sent, so it was not done. Send it again if it still stands.", silent: true);
            return;
        }
        if (message.IsOther)
        {
            await bot.SendMessageAsync(pairing.ChatId, where, "Karvon takes text, photos and files here; that one was not sent.", silent: true);
            return;
        }
        // Several photos or files sent at once: one prompt, once they have all come.
        if (!Gathered(bot, message))
            await PromptedAsync(bot, [message]);
    }

    /// <summary>What the user sent (a text, or photos and files with their caption): a prompt, a command, an answer.</summary>
    async Task PromptedAsync(TelegramBot bot, IReadOnlyList<TelegramMessage> messages)
    {
        if (Pairing is not { } pairing || messages.Count == 0)
            return;
        var first = messages[0];
        int? where = first.IsTopicMessage ? first.ThreadId : null;
        var hasFiles = messages.Any(m => m.Photo is not null || m.Document is not null);
        var (text, images, problem) = await GatherAsync(bot, messages);
        if (problem is not null)
        {
            await bot.SendMessageAsync(pairing.ChatId, where, problem + " Nothing was sent.", silent: true);
            return;
        }
        if (text.Length == 0 && images.Count == 0)
            return;
        if (!first.IsTopicMessage || first.ThreadId is not { } thread)
        {
            if (!hasFiles && text.StartsWith('/') && !text.StartsWith("/new", StringComparison.Ordinal))
            {
                await bot.SendMessageAsync(pairing.ChatId, null, "A message here begins a new conversation; one in a conversation's topic goes to it, and /stop there stops it.", silent: true);
                return;
            }
            text = !hasFiles && text.StartsWith("/new", StringComparison.Ordinal) ? text[4..].Trim() : text;
            if (text.Length == 0 && images.Count == 0 || HostOf("claude") is not { } host)
                return;
            await host.NewConversationAsync(text, images);
            return;
        }
        Chat? chat;
        lock (_lock)
            chat = _chats.FirstOrDefault(c => c.ThreadId == thread);
        if (!hasFiles && chat is not null && !text.StartsWith('/') && QuestionOf(chat) is { } question && Approves())
        {
            await ReplyAsync(question, new RemoteReply([], text));
            return;
        }
        if (!hasFiles && (text == "/stop" || text.StartsWith("/stop@", StringComparison.Ordinal)))
        {
            if (chat is { Running: true })
                await chat.Host.InterruptAsync(chat.Session);
            else
                await bot.SendMessageAsync(pairing.ChatId, thread, "It is not working on anything.", silent: true);
            return;
        }
        if (chat is not null && await chat.Host.PromptAsync(chat.Session, text, images))
            return;
        // Not running here now: opened again in its panel, and prompted.
        if (Topics().FirstOrDefault(t => t.Value.Thread == thread) is { Key: { } sessionId, Value: var topic }
            && HostOf(topic.Kind) is { } owner && await owner.ResumeAsync(sessionId, text, images))
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
        _store.Delete(CodeKey);
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
        if (callback.Data?.StartsWith("q:", StringComparison.Ordinal) == true)
        {
            await OnPickAsync(bot, callback, callback.Data);
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

        /// <summary>What the turn did and asked, in HTML lines: its answer's message keeps them, folded.</summary>
        public List<string> History { get; } = [];

        public int Waiting { get; set; }

        public bool Dirty { get; set; }

        public DateTime Edited { get; set; }

        /// <summary>Its Telegram calls, one after another (a message is edited after it was sent).</summary>
        public Task Queue { get; set; } = Task.CompletedTask;
    }

    sealed class Ask(Chat chat, RemoteAsk request, string key)
    {
        public Chat Chat { get; } = chat;

        public RemoteAsk Request { get; } = request;

        /// <summary>What its buttons send back, after "a:", "q:"...</summary>
        public string Key { get; } = key;

        /// <summary>The question it is on, of its <see cref="RemoteAsk.Questions"/>.</summary>
        public int Current { get; set; }

        public List<RemoteReply> Replies { get; } = [];

        /// <summary>The options ticked so far, where several may be.</summary>
        public HashSet<int> Picked { get; } = [];

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
                        Remember(init.SessionId, thread, kind, host.Folder);
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
                        if (use.Name is not ("AskUserQuestion" or "ExitPlanMode"))
                            chat.History.Add("▸ " + Html(chat.Step));
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
        var ask = new Ask(chat, request, key);
        _asks[key] = ask;
        Announce();
        lock (_lock)
        {
            chat.Waiting++;
            chat.Dirty = true;
        }
        if (request.Questions is { Count: > 0 } && Approves())
        {
            SendQuestion(ask);
            return;
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
            // Answered in Karvon while it was being sent: it goes at once.
            if (ask.Done)
                await bot.DeleteMessageAsync(pairing.ChatId, ask.MessageId.Value);
        });
    }

    /// <summary>A question answered (in Karvon, on another device, from Telegram): its message goes, the chat kept to one a turn.</summary>
    public void Answered(string requestId, string outcome)
    {
        if (_asks.FirstOrDefault(a => a.Value.Request.RequestId == requestId) is not { Key: { } key, Value: { } ask } || ask.Done)
            return;
        ask.Done = true;
        ask.Outcome ??= outcome;
        _asks.TryRemove(key, out _);
        Announce();
        lock (_lock)
        {
            ask.Chat.Waiting = Math.Max(0, ask.Chat.Waiting - 1);
            ask.Chat.Dirty = true;
            // Its message goes; the answer's keeps what it asked and how it was answered.
            if (ask.Request.Questions is { Count: > 0 } questions && ask.Replies.Count > 0)
                foreach (var (question, reply) in questions.Zip(ask.Replies))
                    ask.Chat.History.Add("❓ " + Html(Clip(question.Question, 120)) + " — <b>"
                        + Html(Clip(reply.Other ?? string.Join(", ", reply.Picked.Where(i => i < question.Options.Count).Select(i => question.Options[i].Label)), 120)) + "</b>");
            else
                ask.Chat.History.Add((ask.Request.Questions is { Count: > 0 } ? "❓ " : "🔐 ") + Html(Clip(ask.Request.Question, 150)) + " — <i>" + Html(ask.Outcome) + "</i>");
        }
        Enqueue(ask.Chat, async (bot, pairing) =>
        {
            if (ask.MessageId is { } id)
                await bot.DeleteMessageAsync(pairing.ChatId, id);
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
        chat.History.Clear();
        chat.StatusId = null;
        if (!ShowsProgress())
            return;
        var html = StatusHtml(chat);
        Enqueue(chat, async (bot, pairing) =>
        {
            await EnsureTopicAsync(bot, pairing, chat);
            var id = await bot.SendMessageAsync(pairing.ChatId, chat.ThreadId, html, silent: true);
            chat.Edited = DateTime.Now;
            // Over before it was sent: it goes at once.
            if (chat.Running)
                chat.StatusId = id;
            else
                await bot.DeleteMessageAsync(pairing.ChatId, id);
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
        var statusId = chat.StatusId;
        chat.StatusId = null;
        var history = chat.History.ToList();
        // One message a turn: its answer (with a sound) in place of the status message, which goes.
        Enqueue(chat, async (bot, pairing) =>
        {
            await EnsureTopicAsync(bot, pairing, chat);
            var html = TelegramMarkdown.ToHtml(answer);
            if (status.Length + Folded(history, 1200).Length + html.Length < 4000)
            {
                var head = status + Folded(history, 1200);
                await SendFormattedAsync(() => bot.SendMessageAsync(pairing.ChatId, chat.ThreadId, head + (answer.Length > 0 ? "\n\n" + html : "")),
                    () => bot.SendMessageAsync(pairing.ChatId, chat.ThreadId, head + (answer.Length > 0 ? "\n\n" + Html(answer) : "")));
            }
            else
            {
                // A long answer: all of it as a file, its start under it (and what the turn did, as much as a caption holds).
                var head = status + Folded(history, 300);
                await SendFormattedAsync(
                    () => bot.SendDocumentAsync(pairing.ChatId, chat.ThreadId, "answer.md", Encoding.UTF8.GetBytes(answer),
                        head + "\n\n" + TelegramMarkdown.ToHtml(Clip(answer, 400)) + "\n\n<i>All of it is in the file.</i>"),
                    () => bot.SendDocumentAsync(pairing.ChatId, chat.ThreadId, "answer.md", Encoding.UTF8.GetBytes(answer),
                        head + "\n\n" + Html(Clip(answer, 400)) + "\n\n<i>All of it is in the file.</i>"));
            }
            if (statusId is { } id)
                await bot.DeleteMessageAsync(pairing.ChatId, id);
        });
    }

    /// <summary>
    /// What a turn did and asked, folded under its answer's first line (Telegram opens it on a tap): its last lines, as
    /// many as <paramref name="max"/> characters hold.
    /// </summary>
    static string Folded(IReadOnlyList<string> history, int max)
    {
        if (history.Count == 0)
            return "";
        List<string> lines = [];
        var length = 0;
        foreach (var line in history.Reverse())
        {
            if (length + line.Length > max && lines.Count > 0)
            {
                lines.Insert(0, $"… {history.Count - lines.Count} before");
                break;
            }
            lines.Insert(0, line);
            length += line.Length + 1;
        }
        return "\n<blockquote expandable>" + string.Join("\n", lines) + "</blockquote>";
    }

    /// <summary>A message with the agent's Markdown in it; as plain text where Telegram cannot read what it was made into.</summary>
    static async Task SendFormattedAsync(Func<Task> formatted, Func<Task> plain)
    {
        try
        {
            await formatted();
        }
        catch (TelegramException ex) when (ex.Code == 400 && ex.Message.Contains("parse", StringComparison.OrdinalIgnoreCase))
        {
            await plain();
        }
    }

    /// <summary>The status messages of the conversations that work, edited when something changed (not more often than <see cref="EditEvery"/>).</summary>
    void Tick()
    {
        // Paired, or let go, in another window.
        if ((Pairing is not null) != _wasPaired)
        {
            _wasPaired = !_wasPaired;
            Changed?.Invoke();
        }
        Announce();
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
        if (use.Name is "AskUserQuestion" or "ExitPlanMode")
            return use.Name == "AskUserQuestion" ? "Asking you" : "Proposing a plan";
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
            Remember(id, chat.ThreadId.Value, chat.Kind, chat.Host.Folder);
        }
        Announce();
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

    /// <summary>A conversation's topic, of an agent panel's kind, in a folder.</summary>
    public sealed record Topic(int Thread, string Kind, string? Folder = null);

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

    void Remember(string sessionId, int thread, string kind, string folder)
    {
        var topics = Topics();
        topics[sessionId] = new Topic(thread, kind, Folder(folder));
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
