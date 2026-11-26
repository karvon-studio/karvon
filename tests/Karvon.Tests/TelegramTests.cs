using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Karvon.Claude;
using Karvon.Claude.Remote;
using Karvon.Claude.Tools;
using Karvon.ViewModels;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>
/// Telegram's Bot API on this machine, as Karvon's remote control uses it: the calls it is sent are kept, and the updates a
/// test gives it are what getUpdates answers. It knows one token.
/// </summary>
sealed class FakeTelegram : IDisposable
{
    public const string Token = "123456789:AAH-fake-token-for-karvon-tests-0123456789";

    readonly HttpListener _listener = new();
    /// <summary>The updates not yet confirmed (by a later offset): as Telegram, it gives them again until they are.</summary>
    readonly List<JsonObject> _updates = [];
    readonly SemaphoreSlim _arrived = new(0);
    int _ids = 10;
    long _updateIds = 1;

    public FakeTelegram()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        Url = new Uri($"http://127.0.0.1:{port}/");
        _listener.Prefixes.Add(Url.ToString());
        _listener.Start();
        _ = Task.Run(ServeAsync);
    }

    public Uri Url { get; }

    /// <summary>Whether the bot may manage the group's topics.</summary>
    public bool Admin { get; set; } = true;

    public ConcurrentQueue<(string Method, JsonObject Args)> Calls { get; } = new();

    public IEnumerable<JsonObject> CallsOf(string method) => Calls.Where(c => c.Method == method).Select(c => c.Args);

    public const long Group = -1001234;
    public const long Me = 42;

    public void Message(long chat, long from, string? text, int? thread = null, string type = "supergroup", bool forum = true, DateTimeOffset? sent = null,
        string? caption = null, byte[]? photo = null, (string Name, byte[] Data)? document = null, string? album = null, bool voice = false)
    {
        var message = new JsonObject
        {
            ["message_id"] = Interlocked.Increment(ref _ids),
            ["chat"] = new JsonObject { ["id"] = chat, ["type"] = type, ["title"] = "Karvon", ["is_forum"] = forum },
            ["from"] = new JsonObject { ["id"] = from, ["is_bot"] = false, ["first_name"] = "Nodir", ["username"] = from == Me ? "me" : "someone" },
            ["message_thread_id"] = thread,
            ["is_topic_message"] = thread is not null,
            ["date"] = (sent ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds(),
        };
        if (text is not null)
            message["text"] = text;
        if (caption is not null)
            message["caption"] = caption;
        if (album is not null)
            message["media_group_id"] = album;
        if (photo is not null)
            message["photo"] = new JsonArray(
                new JsonObject { ["file_id"] = File([1, 2, 3]), ["file_size"] = 3 },
                new JsonObject { ["file_id"] = File(photo), ["file_size"] = photo.Length });
        if (document is var (name, data))
            message["document"] = new JsonObject { ["file_id"] = File(data), ["file_name"] = name, ["file_size"] = data.Length };
        if (voice)
            message["voice"] = new JsonObject { ["file_id"] = File([0]), ["duration"] = 2 };
        Add(new JsonObject { ["message"] = message });
    }

    /// <summary>The files people sent, by their ids.</summary>
    readonly ConcurrentDictionary<string, byte[]> _files = new();

    string File(byte[] data)
    {
        var id = "file" + Interlocked.Increment(ref _ids);
        _files[id] = data;
        return id;
    }

    public void Press(long from, string data, long chat = Group) => Add(new JsonObject
    {
        ["callback_query"] = new JsonObject
        {
            ["id"] = "cb" + Interlocked.Increment(ref _ids),
            ["from"] = new JsonObject { ["id"] = from, ["is_bot"] = false, ["first_name"] = "Nodir" },
            ["message"] = new JsonObject { ["message_id"] = 1, ["chat"] = new JsonObject { ["id"] = chat, ["type"] = "supergroup" } },
            ["data"] = data,
        },
    });

    void Add(JsonObject update)
    {
        update["update_id"] = Interlocked.Increment(ref _updateIds);
        lock (_updates)
            _updates.Add(update);
        _arrived.Release();
    }

    async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                return;
            }
            _ = Task.Run(() => AnswerAsync(context));
        }
    }

    async Task AnswerAsync(HttpListenerContext context)
    {
        var path = context.Request.Url!.AbsolutePath.Trim('/');
        // A file fetched: file/bot<token>/<its path>.
        if (path.StartsWith("file/bot" + Token + "/files/", StringComparison.Ordinal) && _files.TryGetValue(path.Split('/')[^1], out var bytes))
        {
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
            return;
        }
        var slash = path.IndexOf('/');
        var token = path[3..slash];
        var method = path[(slash + 1)..];
        using var reader = new StreamReader(context.Request.InputStream);
        var body = await reader.ReadToEndAsync();
        var args = context.Request.ContentType?.StartsWith("application/json", StringComparison.Ordinal) == true
            ? JsonNode.Parse(body) as JsonObject ?? [] : new JsonObject { ["raw"] = body };
        JsonNode? result;
        if (token != Token)
        {
            await Reply(context, new JsonObject { ["ok"] = false, ["error_code"] = 401, ["description"] = "Unauthorized" });
            return;
        }
        if (method != "getUpdates")
            Calls.Enqueue((method, args));
        switch (method)
        {
            case "getMe":
                result = new JsonObject { ["id"] = 999, ["is_bot"] = true, ["first_name"] = "Karvon", ["username"] = "karvon_test_bot" };
                break;
            case "getUpdates":
                var offset = args["offset"]?.GetValue<long>() ?? 0;
                JsonArray Pending()
                {
                    lock (_updates)
                    {
                        _updates.RemoveAll(u => u["update_id"]!.GetValue<long>() < offset);
                        return [.. _updates.Select(u => u.DeepClone())];
                    }
                }
                var all = Pending();
                if (all.Count == 0)
                {
                    await _arrived.WaitAsync(TimeSpan.FromSeconds(Math.Min(args["timeout"]?.GetValue<int>() ?? 0, 2)));
                    all = Pending();
                }
                result = all;
                break;
            case "sendMessage":
            case "sendDocument":
                var sent = Interlocked.Increment(ref _ids);
                args["_id"] = sent;
                result = new JsonObject { ["message_id"] = sent };
                break;
            case "createForumTopic":
                result = new JsonObject { ["message_thread_id"] = 500 + Interlocked.Increment(ref _ids), ["name"] = args["name"]?.GetValue<string>() };
                break;
            case "getFile":
                var fileId = args["file_id"]!.GetValue<string>();
                result = new JsonObject { ["file_id"] = fileId, ["file_path"] = "files/" + fileId, ["file_size"] = _files.TryGetValue(fileId, out var file) ? file.Length : 0 };
                break;
            case "getChatMember":
                result = new JsonObject { ["status"] = Admin ? "administrator" : "member", ["can_manage_topics"] = Admin };
                break;
            default:
                result = true;
                break;
        }
        await Reply(context, new JsonObject { ["ok"] = true, ["result"] = result });
    }

    static async Task Reply(HttpListenerContext context, JsonObject json)
    {
        var bytes = Encoding.UTF8.GetBytes(json.ToJsonString());
        context.Response.ContentType = "application/json";
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    public void Dispose() => _listener.Close();
}

public class TelegramTests
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    /// <summary>Whether a message the bot sent was taken away.</summary>
    static bool Deleted(FakeTelegram telegram, JsonObject sent) =>
        telegram.CallsOf("deleteMessage").Any(d => d["message_id"]!.GetValue<int>() == sent["_id"]!.GetValue<int>());

    /// <summary>Waits for it, and fails when it did not come.</summary>
    static async Task Until(Func<bool> condition, TimeSpan timeout)
    {
        await WaitUntil(condition, timeout);
        Assert.True(condition(), "It did not come in time.");
    }

    static TelegramRemote NewRemote(FakeTelegram telegram) =>
        new(PostmanTests.Sealed(out _), Path.Combine(PostmanTests.Folder(), "telegram.lock"), api: telegram.Url) { EditEvery = TimeSpan.FromMilliseconds(200) };

    /// <summary>Set up and paired with the fake's group, by its user <see cref="FakeTelegram.Me"/>.</summary>
    static async Task<TelegramRemote> PairedAsync(FakeTelegram telegram)
    {
        var remote = NewRemote(telegram);
        Assert.Null(await remote.SetTokenAsync(FakeTelegram.Token));
        var code = remote.BeginPairing();
        telegram.Message(FakeTelegram.Group, FakeTelegram.Me, "/pair " + code);
        await Until(() => remote.Pairing is not null, Wait);
        Assert.NotNull(remote.Pairing);
        return remote;
    }

    [AvaloniaFact]
    public async Task A_token_is_checked_and_kept_sealed_and_the_group_paired_by_its_code_from_the_one_who_sends_it()
    {
        using var telegram = new FakeTelegram();
        await using var remote = NewRemote(telegram);
        Assert.Equal("Not set up", remote.State);
        Assert.StartsWith("That is not a bot's token", await remote.SetTokenAsync("hello"));
        Assert.Equal("Telegram does not know that token.", await remote.SetTokenAsync("123456789:AAH-another-token-not-the-fakes-0123456789"));
        Assert.Null(await remote.SetTokenAsync(FakeTelegram.Token));
        // Sealed: hidden from every agent wherever it would show.
        Assert.DoesNotContain(FakeTelegram.Token, Redaction.Text("the token is " + FakeTelegram.Token));
        Assert.Equal("Not paired with a group yet", remote.State);

        var code = remote.BeginPairing();
        Assert.Matches("^[A-Z2-9]{6}$", code);
        // Not in a group, a group without topics, a bot that may not manage them, a wrong code: not paired, and told why.
        telegram.Message(500, FakeTelegram.Me, "/pair " + code, type: "private", forum: false);
        telegram.Message(FakeTelegram.Group, FakeTelegram.Me, "/pair " + code, forum: false);
        await Until(() => telegram.CallsOf("sendMessage").Count() == 2, Wait);
        telegram.Admin = false;
        telegram.Message(FakeTelegram.Group, FakeTelegram.Me, code);
        telegram.Message(FakeTelegram.Group, FakeTelegram.Me, "/pair WRONG1");
        await Until(() => telegram.CallsOf("sendMessage").Count() == 3, Wait);
        Assert.Collection(telegram.CallsOf("sendMessage").Select(m => m["text"]!.GetValue<string>()),
            t => Assert.StartsWith("Send it in a group", t),
            t => Assert.StartsWith("Turn Topics on", t),
            t => Assert.StartsWith("Make me an administrator", t));
        Assert.Null(remote.Pairing);

        telegram.Admin = true;
        telegram.Message(FakeTelegram.Group, FakeTelegram.Me, "/pair@karvon_test_bot " + code.ToLowerInvariant());
        await Until(() => remote.Pairing is not null, Wait);
        Assert.Equal(new RemotePairing(FakeTelegram.Group, "Karvon", FakeTelegram.Me, "@me", "@karvon_test_bot"), remote.Pairing);
        Assert.Contains("hears @me alone", telegram.CallsOf("sendMessage").Last()["text"]!.GetValue<string>());
        Assert.Equal("On: @karvon_test_bot in “Karvon”, for @me", remote.State);
        Assert.Null(remote.PairingCode);

        await remote.ForgetAsync();
        Assert.Equal((null, null), (remote.Token, remote.Pairing));
    }

    [AvaloniaFact]
    public async Task A_conversation_has_its_topic_its_progress_its_questions_answered_there_and_its_answer()
    {
        using var telegram = new FakeTelegram();
        await using var remote = await PairedAsync(telegram);
        var (dir, log) = FakeClaude.Workspace();
        var vm = new ClaudeViewModel(new Karvon.Services.Settings(), () => dir, () => null, () => [], () => FakeClaude.Executable) { Remote = remote };
        remote.AddHost("claude", () => vm, () => dir);

        vm.Prompt = "run bash please";
        await vm.SendCommand.ExecuteAsync(null);
        await Until(() => telegram.CallsOf("sendMessage").Any(m => m["reply_markup"] is not null), Wait);
        var topic = telegram.CallsOf("createForumTopic").Single();
        Assert.Equal($"Claude · {Path.GetFileName(dir)} · run bash please", topic["name"]!.GetValue<string>());
        var ask = telegram.CallsOf("sendMessage").Single(m => m["reply_markup"] is not null);
        Assert.Contains("Allow Claude to run this command?", ask["text"]!.GetValue<string>());
        Assert.Contains("<pre>echo hi</pre>", ask["text"]!.GetValue<string>());
        Assert.False(ask["disable_notification"]!.GetValue<bool>());
        var buttons = ask["reply_markup"]!["inline_keyboard"]![0]!.AsArray().Select(b => (b!["text"]!.GetValue<string>(), b["callback_data"]!.GetValue<string>())).ToList();
        Assert.Equal(["✅ Allow", "Allow for the session", "❌ Deny"], buttons.Select(b => b.Item1));
        // The status message goes without a sound, and is edited as it goes.
        Assert.Contains(telegram.CallsOf("sendMessage"), m => m["disable_notification"]!.GetValue<bool>() && m["text"]!.GetValue<string>().Contains("Working"));
        await Until(() => telegram.CallsOf("editMessageText").Any(e => e["text"]!.GetValue<string>().StartsWith("✋ <b>Waiting for you</b>")), Wait);

        // Someone else in the group, or the right one elsewhere: not heard.
        telegram.Press(7, buttons[0].Item2);
        telegram.Press(FakeTelegram.Me, buttons[0].Item2, chat: -999);
        await Until(() => telegram.CallsOf("answerCallbackQuery").Count() == 2, Wait);
        Assert.All(telegram.CallsOf("answerCallbackQuery"), a => Assert.Equal("Not yours to answer.", a["text"]!.GetValue<string>()));
        Assert.Contains(vm.Items, i => i is PermissionChatItem { IsPending: true });

        telegram.Press(FakeTelegram.Me, buttons[0].Item2);
        await Until(() => vm.Items.OfType<PermissionChatItem>().Any(p => !p.IsPending) && !vm.IsRunning, Wait);
        Assert.Equal("allow", FakeClaude.LastDecision(log).GetProperty("behavior").GetString());
        Assert.Equal("Allowed", vm.Items.OfType<PermissionChatItem>().Single().Outcome);
        // Answered: its message goes, so a turn leaves one message in the chat.
        await Until(() => Deleted(telegram, ask), Wait);
        // One message a turn: its answer in place of the status message, which goes.
        var status = telegram.CallsOf("sendMessage").Single(m => m["disable_notification"]!.GetValue<bool>() && m["text"]!.GetValue<string>().Contains("Working"));
        await Until(() => telegram.CallsOf("sendMessage").Any(m => m["text"]!.GetValue<string>().StartsWith("✅ <b>Done</b>")) && Deleted(telegram, status), Wait);
        var done = telegram.CallsOf("sendMessage").Single(m => m["text"]!.GetValue<string>().StartsWith("✅ <b>Done</b>"));
        // What the turn did and asked, folded in it; then its answer.
        Assert.Matches(@"^✅ <b>Done</b> · \d+s\n<blockquote expandable>▸ Bash: echo hi\n🔐 Allow Claude to run this command\? — <i>Allowed from Telegram</i></blockquote>\n\nok$",
            done["text"]!.GetValue<string>());
        Assert.False(done["disable_notification"]!.GetValue<bool>());
        Assert.Single(telegram.CallsOf("sendMessage"), m => m["disable_notification"]!.GetValue<bool>() && m["text"]!.GetValue<string>().Contains("Working"));
        // All of it in the conversation's topic.
        Assert.Single(telegram.CallsOf("sendMessage").Where(m => m["message_thread_id"] is not null).Select(m => m["message_thread_id"]!.GetValue<int>()).Distinct());
        // Pressed again: it waits no more.
        telegram.Press(FakeTelegram.Me, buttons[2].Item2);
        await Until(() => telegram.CallsOf("answerCallbackQuery").Count() == 4, Wait);
        Assert.Equal("It does not wait any more.", telegram.CallsOf("answerCallbackQuery").Last()["text"]!.GetValue<string>());

        // A message in its topic is a prompt for it; answered in Karvon, its Telegram message goes too.
        var thread = done["message_thread_id"]!.GetValue<int>();
        telegram.Message(FakeTelegram.Group, FakeTelegram.Me, "bash once more", thread);
        await Until(() => vm.Items.OfType<UserChatItem>().Any(u => u.IsRemote && u.Text == "bash once more"), Wait);
        await Until(() => vm.Items.OfType<PermissionChatItem>().Count(p => p.IsPending) == 1, Wait);
        await vm.DenyCommand.ExecuteAsync(vm.Items.OfType<PermissionChatItem>().Single(p => p.IsPending));
        await Until(() => Deleted(telegram, telegram.CallsOf("sendMessage").Last(m => m["reply_markup"] is not null)), Wait);
        Assert.Single(telegram.CallsOf("createForumTopic"));
    }

    [AvaloniaFact]
    public async Task Claudes_questions_are_answered_by_their_options_or_by_a_text_of_ones_own()
    {
        using var telegram = new FakeTelegram();
        await using var remote = await PairedAsync(telegram);
        var (dir, _) = FakeClaude.Workspace();
        var vm = new ClaudeViewModel(new Karvon.Services.Settings(), () => dir, () => null, () => [], () => FakeClaude.Executable) { Remote = remote };
        remote.AddHost("claude", () => vm, () => dir);

        vm.Prompt = "ask me which database";
        await vm.SendCommand.ExecuteAsync(null);
        await Until(() => telegram.CallsOf("sendMessage").Any(m => m["reply_markup"] is not null), Wait);
        var asked = telegram.CallsOf("sendMessage").Single(m => m["reply_markup"] is not null);
        var text = asked["text"]!.GetValue<string>();
        Assert.StartsWith("❓ <b>Claude asks you</b>\n\n<b>Database</b>: Which database?\n1. <b>PostgreSQL</b> — Relational server\n2. <b>SQLite</b> — A single file", text);
        Assert.EndsWith("Pick one, or write your own answer in this topic.</i>", text);
        var buttons = asked["reply_markup"]!["inline_keyboard"]!.AsArray().Select(row => row![0]!).ToList();
        Assert.Equal(["1. PostgreSQL", "2. SQLite"], buttons.Select(b => b["text"]!.GetValue<string>()));

        telegram.Press(FakeTelegram.Me, buttons[1]["callback_data"]!.GetValue<string>());
        await Until(() => vm.Items.OfType<QuestionChatItem>().Any(q => !q.IsPending) && !vm.IsRunning, Wait);
        Assert.Contains(vm.Items.OfType<AnswerChatItem>(), a => a.Text.Contains("You picked SQLite."));
        await Until(() => Deleted(telegram, asked), Wait);

        // A text written in the topic while it asks: the user's own answer, not a prompt.
        vm.Prompt = "ask once more";
        await vm.SendCommand.ExecuteAsync(null);
        await Until(() => telegram.CallsOf("sendMessage").Count(m => m["reply_markup"] is not null) == 2, Wait);
        telegram.Message(FakeTelegram.Group, FakeTelegram.Me, "MySQL, please", asked["message_thread_id"]!.GetValue<int>());
        await Until(() => vm.Items.OfType<QuestionChatItem>().All(q => !q.IsPending) && !vm.IsRunning, Wait);
        Assert.Contains(vm.Items.OfType<AnswerChatItem>(), a => a.Text.Contains("You picked MySQL, please."));
        Assert.DoesNotContain(vm.Items.OfType<UserChatItem>(), u => u.Text == "MySQL, please");
    }

    [AvaloniaFact]
    public async Task Photos_and_files_go_with_the_prompt_their_caption_is()
    {
        using var telegram = new FakeTelegram();
        await using var remote = await PairedAsync(telegram);
        remote.AlbumWait = TimeSpan.FromMilliseconds(300);
        remote.FilesFolder = PostmanTests.Folder();
        var (dir, log) = FakeClaude.Workspace();
        var vm = new ClaudeViewModel(new Karvon.Services.Settings(), () => dir, () => null, () => [], () => FakeClaude.Executable) { Remote = remote };
        remote.AddHost("claude", () => vm, () => dir);
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
        UserChatItem? Sent(string start) => vm.Items.OfType<UserChatItem>().LastOrDefault(u => u.IsRemote && u.Text.StartsWith(start, StringComparison.Ordinal));
        JsonElement LastContent() => FakeClaude.Log(log).Last(e => e.Kind == "in" && e.Data.GetProperty("type").GetString() == "user").Data.GetProperty("message").GetProperty("content");

        // A photo in General, its caption the prompt: a new conversation, the photo (its largest size) with it.
        telegram.Message(FakeTelegram.Group, FakeTelegram.Me, null, caption: "what is on it", photo: png);
        await Until(() => Sent("what is on it") is { Images.Count: 1 } && !vm.IsRunning, Wait);
        Assert.Contains(LastContent().EnumerateArray(), b => b.GetProperty("type").GetString() == "image");
        await Until(() => telegram.CallsOf("createForumTopic").Any() && telegram.CallsOf("sendMessage").Any(m => m["message_thread_id"] is not null), Wait);
        var thread = telegram.CallsOf("sendMessage").First(m => m["message_thread_id"] is not null)["message_thread_id"]!.GetValue<int>();

        // Two photos sent at once in its topic: one prompt.
        telegram.Message(FakeTelegram.Group, FakeTelegram.Me, null, thread, caption: "both of them", photo: png, album: "a1");
        telegram.Message(FakeTelegram.Group, FakeTelegram.Me, null, thread, photo: png, album: "a1");
        await Until(() => Sent("both of them") is { Images.Count: 2 } && !vm.IsRunning, Wait);

        // A text file in the prompt, whole; another file saved, the prompt saying where.
        telegram.Message(FakeTelegram.Group, FakeTelegram.Me, null, thread, caption: "check the log", document: ("app.log", "line 1\nERROR here\n"u8.ToArray()));
        await Until(() => Sent("check the log") is not null && !vm.IsRunning, Wait);
        Assert.Equal("check the log\n\n<file name=\"app.log\">\nline 1\nERROR here\n</file>", Sent("check the log")!.Text);
        telegram.Message(FakeTelegram.Group, FakeTelegram.Me, null, thread, caption: "and this one", document: ("data.bin", [0, 1, 2, 255]));
        await Until(() => Sent("and this one") is not null && !vm.IsRunning, Wait);
        var saved = Sent("and this one")!.Text.Split("it is saved at ")[1];
        Assert.StartsWith(remote.FilesFolder, saved);
        Assert.Equal([0, 1, 2, 255], File.ReadAllBytes(saved));

        // A voice message: said it is not taken.
        telegram.Message(FakeTelegram.Group, FakeTelegram.Me, null, thread, voice: true);
        await Until(() => telegram.CallsOf("sendMessage").Any(m => m["text"]!.GetValue<string>().StartsWith("Karvon takes text, photos and files")), Wait);
    }

    [AvaloniaFact]
    public async Task A_message_in_General_begins_a_new_conversation_and_a_destructive_call_is_answered_at_the_computer()
    {
        using var telegram = new FakeTelegram();
        await using var remote = await PairedAsync(telegram);
        var (dir, _) = FakeClaude.Workspace();
        var vm = new ClaudeViewModel(new Karvon.Services.Settings(), () => dir, () => null, () => [], () => FakeClaude.Executable) { Remote = remote };
        remote.AddHost("claude", () => vm, () => dir);

        telegram.Message(FakeTelegram.Group, 7, "not from me");
        // Sent while Karvon was closed: not done, and said so.
        telegram.Message(FakeTelegram.Group, FakeTelegram.Me, "an old one", sent: DateTimeOffset.UtcNow.AddHours(-3));
        telegram.Message(FakeTelegram.Group, FakeTelegram.Me, "say hello");
        await Until(() => vm.Items.OfType<UserChatItem>().Any(u => u.IsRemote && u.Text == "say hello") && !vm.IsRunning, Wait);
        Assert.DoesNotContain(vm.Items.OfType<UserChatItem>(), u => u.Text is "not from me" or "an old one");
        Assert.Contains(telegram.CallsOf("sendMessage"), m => m["text"]!.GetValue<string>().StartsWith("Karvon was not running when this was sent"));
        await Until(() => telegram.CallsOf("createForumTopic").Any(), Wait);
        Assert.EndsWith("· say hello", telegram.CallsOf("createForumTopic").Single()["name"]!.GetValue<string>());

        // Karvon warns about it: no buttons in Telegram.
        var session = new QuietSession();
        remote.Asked(vm, session, "claude", new RemoteAsk("req-1", "Allow Claude to drop the table?", "DROP TABLE orders", null, false, OnlyHere: true));
        await Until(() => telegram.CallsOf("sendMessage").Any(m => m["text"]!.GetValue<string>().Contains("DROP TABLE")), Wait);
        var asked = telegram.CallsOf("sendMessage").Single(m => m["text"]!.GetValue<string>().Contains("DROP TABLE"));
        Assert.Null(asked["reply_markup"]);
        Assert.Contains("answer it at the computer", asked["text"]!.GetValue<string>());
    }

    [AvaloniaFact]
    public async Task Two_windows_use_one_bot_each_given_its_own_and_the_other_takes_it_over_when_the_first_closes()
    {
        using var telegram = new FakeTelegram();
        var firstStore = PostmanTests.Sealed(out var vault);
        var secondStore = new Karvon.Services.SealedStore(() => vault);
        var lockFile = Path.Combine(PostmanTests.Folder(), "telegram.lock");
        var first = new TelegramRemote(firstStore, lockFile, api: telegram.Url) { EditEvery = TimeSpan.FromMilliseconds(200), LinkRetry = TimeSpan.FromMilliseconds(200) };
        await using var second = new TelegramRemote(secondStore, lockFile, api: telegram.Url) { EditEvery = TimeSpan.FromMilliseconds(200), LinkRetry = TimeSpan.FromMilliseconds(200) };
        Assert.Null(await first.SetTokenAsync(FakeTelegram.Token));
        await Until(() => first.IsPolling, Wait);
        await firstStore.Written;
        second.Start();
        await Until(() => second.IsLinked, Wait);
        Assert.False(second.IsPolling);

        // Paired from the second window: the first, which polls, hears the code.
        var code = second.BeginPairing();
        await secondStore.Written;
        telegram.Message(FakeTelegram.Group, FakeTelegram.Me, "/pair " + code);
        await Until(() => first.Pairing is not null && second.Pairing is not null, Wait);
        Assert.EndsWith(", through another Karvon window", second.State);
        Assert.DoesNotContain("through", first.State);

        var (dirA, _) = FakeClaude.Workspace();
        var (dirB, logB) = FakeClaude.Workspace();
        var a = new ClaudeViewModel(new Karvon.Services.Settings(), () => dirA, () => null, () => [], () => FakeClaude.Executable) { Remote = first };
        var b = new ClaudeViewModel(new Karvon.Services.Settings(), () => dirB, () => null, () => [], () => FakeClaude.Executable) { Remote = second };
        first.AddHost("claude", () => a, () => dirA);
        second.AddHost("claude", () => b, () => dirB);

        // The second window's question, answered in Telegram: handed to it by the first.
        b.Prompt = "run bash in the second window";
        await b.SendCommand.ExecuteAsync(null);
        await Until(() => telegram.CallsOf("sendMessage").Any(m => m["reply_markup"] is not null), Wait);
        var ask = telegram.CallsOf("sendMessage").Single(m => m["reply_markup"] is not null);
        var thread = ask["message_thread_id"]!.GetValue<int>();
        await Task.Delay(300, TestContext.Current.CancellationToken);
        telegram.Press(FakeTelegram.Me, ask["reply_markup"]!["inline_keyboard"]![0]![0]!["callback_data"]!.GetValue<string>());
        await Until(() => b.Items.OfType<PermissionChatItem>().Any(p => !p.IsPending) && !b.IsRunning, Wait);
        Assert.Equal("allow", FakeClaude.LastDecision(logB).GetProperty("behavior").GetString());

        // A message in its topic goes to it, not to the first window's panel.
        telegram.Message(FakeTelegram.Group, FakeTelegram.Me, "say hi from the topic", thread);
        await Until(() => b.Items.OfType<UserChatItem>().Any(u => u.IsRemote && u.Text == "say hi from the topic") && !b.IsRunning, Wait);
        Assert.Empty(a.Items);

        // The first window closes: the second takes the bot over.
        await first.DisposeAsync();
        await Until(() => second.IsPolling, Wait);
        Assert.False(second.IsLinked);
        telegram.Message(FakeTelegram.Group, FakeTelegram.Me, "and now", thread);
        await Until(() => b.Items.OfType<UserChatItem>().Any(u => u.IsRemote && u.Text == "and now"), Wait);
    }

    [Fact]
    public void An_agents_Markdown_is_shown_as_Telegram_shows_HTML()
    {
        var html = TelegramMarkdown.ToHtml("""
            Faqat o'qiyman. Qoidalar:

            - **O'qish:** faqat `SELECT`, <hech> narsa & boshqa
              * *ichki* ~~eski~~
            ## Keyin
            ```sql
            SELECT 1 < 2;
            ```
            | a | b |
            |---|---|
            > Iqtibos [docs](https://a.dev/x?a=1&b=2)
            ---
            `__init__` and __init__ stay
            """);
        Assert.Equal("""
            Faqat o'qiyman. Qoidalar:

            • <b>O'qish:</b> faqat <code>SELECT</code>, &lt;hech&gt; narsa &amp; boshqa
              • <i>ichki</i> <s>eski</s>
            <b>Keyin</b>
            <pre><code class="language-sql">SELECT 1 &lt; 2;</code></pre>
            <pre>| a | b |
            |---|---|</pre>
            <blockquote>Iqtibos <a href="https://a.dev/x?a=1&amp;b=2">docs</a></blockquote>
            ──────────
            <code>__init__</code> and __init__ stay
            """, html);
        // Cut inside a code block: the block is closed.
        Assert.Equal("<pre>x = 1</pre>", TelegramMarkdown.ToHtml("```\nx = 1"));
    }

    [AvaloniaFact]
    public async Task Its_window_sets_it_up_step_by_step()
    {
        using var telegram = new FakeTelegram();
        await using var remote = NewRemote(telegram);
        var owner = new Avalonia.Controls.Window { Width = 900, Height = 800 };
        owner.Show();
        var window = new Karvon.Views.TelegramSetupWindow(remote);
        _ = window.ShowDialog(owner);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        T Find<T>(string name) where T : Avalonia.Controls.Control =>
            Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<T>().Single(c => c.Name == name);
        Assert.Equal("", Find<Avalonia.Controls.TextBox>("Code").Text);

        Find<Avalonia.Controls.TextBox>("Token").Text = "not a token";
        Find<Avalonia.Controls.Button>("Check").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        await Until(() => Find<Avalonia.Controls.TextBlock>("TokenState").Text?.StartsWith("That is not") == true, Wait);
        Find<Avalonia.Controls.TextBox>("Token").Text = FakeTelegram.Token;
        Find<Avalonia.Controls.Button>("Check").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        await Until(() => Find<Avalonia.Controls.TextBox>("Code").Text?.StartsWith("/pair ") == true, Wait);
        Assert.Equal("/pair " + remote.PairingCode, Find<Avalonia.Controls.TextBox>("Code").Text);
        Assert.StartsWith("✓ The bot is set up", Find<Avalonia.Controls.TextBlock>("TokenState").Text);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Snapshot(window, "telegram-setup");

        telegram.Message(FakeTelegram.Group, FakeTelegram.Me, "/pair " + remote.PairingCode);
        await Until(() => Find<Avalonia.Controls.TextBlock>("PairState").Text?.StartsWith("✓ Paired") == true, Wait);
        Assert.Equal("✓ Paired with “Karvon”: Karvon hears @me alone there.", Find<Avalonia.Controls.TextBlock>("PairState").Text);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Snapshot(window, "telegram-setup-paired");
        window.Close();
        owner.Close();
    }

    /// <summary>A conversation that does nothing: for what Telegram is told about one.</summary>
    sealed class QuietSession : IAgentSession
    {
        public event Action<ClaudeEvent>? Event;

        public string? SessionId => "quiet";

        public string? WorkingDirectory => null;

        public bool HasExited => false;

        public JsonElement Initialization => default;

        public IReadOnlyList<string> HiddenVariables => [];

        public ClaudeAudit? Audit => null;

        public Task SendUserMessageAsync(string text, IEnumerable<string>? extraBlocks = null, IEnumerable<ClaudeImage>? images = null)
        {
            Event?.Invoke(new ClaudeNotice(text, false));
            return Task.CompletedTask;
        }

        public Task RespondToPermissionAsync(string requestId, ClaudePermissionDecision decision, string by = "the user") => Task.CompletedTask;

        public Task InterruptAsync() => Task.CompletedTask;

        public Task SetPermissionModeAsync(string mode) => Task.CompletedTask;

        public Task SetModelAsync(string? model) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
