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
    readonly ConcurrentQueue<JsonObject> _updates = new();
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

    public void Message(long chat, long from, string text, int? thread = null, string type = "supergroup", bool forum = true) => Add(new JsonObject
    {
        ["message"] = new JsonObject
        {
            ["message_id"] = Interlocked.Increment(ref _ids),
            ["chat"] = new JsonObject { ["id"] = chat, ["type"] = type, ["title"] = "Karvon", ["is_forum"] = forum },
            ["from"] = new JsonObject { ["id"] = from, ["is_bot"] = false, ["first_name"] = "Nodir", ["username"] = from == Me ? "me" : "someone" },
            ["text"] = text,
            ["message_thread_id"] = thread,
            ["is_topic_message"] = thread is not null,
        },
    });

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
        _updates.Enqueue(update);
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
                var wait = args["timeout"]?.GetValue<int>() ?? 0;
                await _arrived.WaitAsync(TimeSpan.FromSeconds(Math.Min(wait, 2)));
                var all = new JsonArray();
                while (_updates.TryDequeue(out var update))
                    all.Add(update);
                result = all;
                break;
            case "sendMessage":
            case "sendDocument":
                result = new JsonObject { ["message_id"] = Interlocked.Increment(ref _ids) };
                break;
            case "createForumTopic":
                result = new JsonObject { ["message_thread_id"] = 500 + Interlocked.Increment(ref _ids), ["name"] = args["name"]?.GetValue<string>() };
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

    static TelegramRemote NewRemote(FakeTelegram telegram) =>
        new(PostmanTests.Sealed(out _), Path.Combine(PostmanTests.Folder(), "telegram.lock"), api: telegram.Url) { EditEvery = TimeSpan.FromMilliseconds(200) };

    /// <summary>Set up and paired with the fake's group, by its user <see cref="FakeTelegram.Me"/>.</summary>
    static async Task<TelegramRemote> PairedAsync(FakeTelegram telegram)
    {
        var remote = NewRemote(telegram);
        Assert.Null(await remote.SetTokenAsync(FakeTelegram.Token));
        var code = remote.BeginPairing();
        telegram.Message(FakeTelegram.Group, FakeTelegram.Me, "/pair " + code);
        await WaitUntil(() => remote.Pairing is not null, Wait);
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
        await WaitUntil(() => telegram.CallsOf("sendMessage").Count() == 2, Wait);
        telegram.Admin = false;
        telegram.Message(FakeTelegram.Group, FakeTelegram.Me, code);
        telegram.Message(FakeTelegram.Group, FakeTelegram.Me, "/pair WRONG1");
        await WaitUntil(() => telegram.CallsOf("sendMessage").Count() == 3, Wait);
        Assert.Collection(telegram.CallsOf("sendMessage").Select(m => m["text"]!.GetValue<string>()),
            t => Assert.StartsWith("Send it in a group", t),
            t => Assert.StartsWith("Turn Topics on", t),
            t => Assert.StartsWith("Make me an administrator", t));
        Assert.Null(remote.Pairing);

        telegram.Admin = true;
        telegram.Message(FakeTelegram.Group, FakeTelegram.Me, "/pair@karvon_test_bot " + code.ToLowerInvariant());
        await WaitUntil(() => remote.Pairing is not null, Wait);
        Assert.Equal(new RemotePairing(FakeTelegram.Group, "Karvon", FakeTelegram.Me, "@me", "@karvon_test_bot"), remote.Pairing);
        Assert.Contains("hears @me alone", telegram.CallsOf("sendMessage").Last()["text"]!.GetValue<string>());
        Assert.Equal("On: @karvon_test_bot in “Karvon”, for @me", remote.State);
        Assert.Null(remote.PairingCode);

        // Another window of Karvon leaves the bot to this one.
        await using var other = new TelegramRemote(PostmanTests.Sealed(out _), remote.LockFile, api: telegram.Url);
        Assert.Null(await other.SetTokenAsync(FakeTelegram.Token));
        Assert.False(other.IsPolling);
        Assert.Equal("Used by another Karvon window", other.State);

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
        remote.AddHost("claude", () => vm);

        vm.Prompt = "run bash please";
        await vm.SendCommand.ExecuteAsync(null);
        await WaitUntil(() => telegram.CallsOf("sendMessage").Any(m => m["reply_markup"] is not null), Wait);
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
        await WaitUntil(() => telegram.CallsOf("editMessageText").Any(e => e["text"]!.GetValue<string>().StartsWith("✋ <b>Waiting for you</b>")), Wait);

        // Someone else in the group, or the right one elsewhere: not heard.
        telegram.Press(7, buttons[0].Item2);
        telegram.Press(FakeTelegram.Me, buttons[0].Item2, chat: -999);
        await WaitUntil(() => telegram.CallsOf("answerCallbackQuery").Count() == 2, Wait);
        Assert.All(telegram.CallsOf("answerCallbackQuery"), a => Assert.Equal("Not yours to answer.", a["text"]!.GetValue<string>()));
        Assert.Contains(vm.Items, i => i is PermissionChatItem { IsPending: true });

        telegram.Press(FakeTelegram.Me, buttons[0].Item2);
        await WaitUntil(() => vm.Items.OfType<PermissionChatItem>().Any(p => !p.IsPending) && !vm.IsRunning, Wait);
        Assert.Equal("allow", FakeClaude.LastDecision(log).GetProperty("behavior").GetString());
        Assert.Equal("Allowed", vm.Items.OfType<PermissionChatItem>().Single().Outcome);
        await WaitUntil(() => telegram.CallsOf("editMessageText").Any(e => e["text"]!.GetValue<string>().Contains("Allowed from Telegram")), Wait);
        await WaitUntil(() => telegram.CallsOf("sendMessage").Any(m => m["text"]!.GetValue<string>().Contains("is done")), Wait);
        var done = telegram.CallsOf("sendMessage").Last(m => m["text"]!.GetValue<string>().Contains("is done"));
        Assert.StartsWith("✅ <b>Claude</b> is done", done["text"]!.GetValue<string>());
        // All of it in the conversation's topic.
        Assert.Single(telegram.CallsOf("sendMessage").Where(m => m["message_thread_id"] is not null).Select(m => m["message_thread_id"]!.GetValue<int>()).Distinct());
        // Pressed again: it waits no more.
        telegram.Press(FakeTelegram.Me, buttons[2].Item2);
        await WaitUntil(() => telegram.CallsOf("answerCallbackQuery").Count() == 4, Wait);
        Assert.Equal("It does not wait any more.", telegram.CallsOf("answerCallbackQuery").Last()["text"]!.GetValue<string>());

        // A message in its topic is a prompt for it; answered in Karvon, its Telegram message says so.
        var thread = done["message_thread_id"]!.GetValue<int>();
        telegram.Message(FakeTelegram.Group, FakeTelegram.Me, "bash once more", thread);
        await WaitUntil(() => vm.Items.OfType<UserChatItem>().Any(u => u.IsRemote && u.Text == "bash once more"), Wait);
        await WaitUntil(() => vm.Items.OfType<PermissionChatItem>().Count(p => p.IsPending) == 1, Wait);
        await vm.DenyCommand.ExecuteAsync(vm.Items.OfType<PermissionChatItem>().Single(p => p.IsPending));
        await WaitUntil(() => telegram.CallsOf("editMessageText").Any(e => e["text"]!.GetValue<string>().Contains("Denied in Karvon")), Wait);
        Assert.Single(telegram.CallsOf("createForumTopic"));
    }

    [AvaloniaFact]
    public async Task A_message_in_General_begins_a_new_conversation_and_a_destructive_call_is_answered_at_the_computer()
    {
        using var telegram = new FakeTelegram();
        await using var remote = await PairedAsync(telegram);
        var (dir, _) = FakeClaude.Workspace();
        var vm = new ClaudeViewModel(new Karvon.Services.Settings(), () => dir, () => null, () => [], () => FakeClaude.Executable) { Remote = remote };
        remote.AddHost("claude", () => vm);

        telegram.Message(FakeTelegram.Group, 7, "not from me");
        telegram.Message(FakeTelegram.Group, FakeTelegram.Me, "say hello");
        await WaitUntil(() => vm.Items.OfType<UserChatItem>().Any(u => u.IsRemote && u.Text == "say hello") && !vm.IsRunning, Wait);
        Assert.DoesNotContain(vm.Items.OfType<UserChatItem>(), u => u.Text == "not from me");
        await WaitUntil(() => telegram.CallsOf("createForumTopic").Any(), Wait);
        Assert.EndsWith("· say hello", telegram.CallsOf("createForumTopic").Single()["name"]!.GetValue<string>());

        // Karvon warns about it: no buttons in Telegram.
        var session = new QuietSession();
        remote.Asked(vm, session, "claude", new RemoteAsk("req-1", "Allow Claude to drop the table?", "DROP TABLE orders", null, false, OnlyHere: true));
        await WaitUntil(() => telegram.CallsOf("sendMessage").Any(m => m["text"]!.GetValue<string>().Contains("DROP TABLE")), Wait);
        var asked = telegram.CallsOf("sendMessage").Single(m => m["text"]!.GetValue<string>().Contains("DROP TABLE"));
        Assert.Null(asked["reply_markup"]);
        Assert.Contains("answer it at the computer", asked["text"]!.GetValue<string>());
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
        await WaitUntil(() => Find<Avalonia.Controls.TextBlock>("TokenState").Text?.StartsWith("That is not") == true, Wait);
        Find<Avalonia.Controls.TextBox>("Token").Text = FakeTelegram.Token;
        Find<Avalonia.Controls.Button>("Check").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        await WaitUntil(() => Find<Avalonia.Controls.TextBox>("Code").Text?.StartsWith("/pair ") == true, Wait);
        Assert.Equal("/pair " + remote.PairingCode, Find<Avalonia.Controls.TextBox>("Code").Text);
        Assert.StartsWith("✓ The bot is set up", Find<Avalonia.Controls.TextBlock>("TokenState").Text);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Snapshot(window, "telegram-setup");

        telegram.Message(FakeTelegram.Group, FakeTelegram.Me, "/pair " + remote.PairingCode);
        await WaitUntil(() => Find<Avalonia.Controls.TextBlock>("PairState").Text?.StartsWith("✓ Paired") == true, Wait);
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
