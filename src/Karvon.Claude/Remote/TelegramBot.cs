using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Karvon.Claude.Remote;

/// <summary>What Telegram said no to: its error code and description ("Bad Request: message is not modified").</summary>
public sealed class TelegramException(string message, int code) : Exception(message)
{
    public int Code => code;
}

public sealed record TelegramUser(long Id, string? UserName, string FirstName, bool IsBot)
{
    /// <summary>"@name", or the first name where there is no user name.</summary>
    public string Display => UserName is { Length: > 0 } name ? "@" + name : FirstName;
}

public sealed record TelegramChat(long Id, string Type, string? Title, bool IsForum)
{
    public bool IsGroup => Type is "group" or "supergroup";
}

/// <summary>A message; <paramref name="ThreadId"/> is its topic's when <paramref name="IsTopicMessage"/> (else it is in General).</summary>
public sealed record TelegramMessage(int MessageId, TelegramChat Chat, TelegramUser? From, int? ThreadId, bool IsTopicMessage, string? Text);

/// <summary>An inline button pressed.</summary>
public sealed record TelegramCallback(string Id, TelegramUser From, TelegramMessage? Message, string? Data);

public sealed record TelegramUpdate(long UpdateId, TelegramMessage? Message, TelegramCallback? Callback);

/// <summary>An inline button: what it says, and what pressing it sends back (64 bytes at most).</summary>
public sealed record TelegramButton(string Text, string Data);

/// <summary>
/// The few calls of Telegram's Bot API that Karvon's remote control makes: its messages (sent, edited, with buttons), a
/// group's topics, files, and the updates it is sent (long polling: Karvon reaches Telegram, nothing reaches Karvon).
/// The token is in each call's address only: no error, log or exception says it.
/// </summary>
public sealed class TelegramBot(string token, HttpClient http, Uri? api = null)
{
    readonly Uri _api = api ?? new Uri("https://api.telegram.org/");

    /// <summary>A text message holds this many characters at most.</summary>
    public const int MaxText = 4096;

    /// <summary>A file's caption holds this many.</summary>
    public const int MaxCaption = 1024;

    public async Task<TelegramUser> GetMeAsync(CancellationToken ct = default) =>
        User(await CallAsync("getMe", [], ct))!;

    /// <summary>The updates after <paramref name="offset"/>, waiting up to <paramref name="wait"/> for one to come.</summary>
    public async Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(long offset, TimeSpan wait, CancellationToken ct = default)
    {
        var result = await CallAsync("getUpdates", new JsonObject
        {
            ["offset"] = offset,
            ["timeout"] = (int)wait.TotalSeconds,
            ["allowed_updates"] = new JsonArray("message", "callback_query"),
        }, ct, wait + TimeSpan.FromSeconds(15));
        return result is JsonArray updates ? [.. updates.Select(Update).OfType<TelegramUpdate>()] : [];
    }

    /// <summary>A message in HTML (Telegram's few tags), in a topic when <paramref name="threadId"/> is given; its id.</summary>
    public async Task<int> SendMessageAsync(long chatId, int? threadId, string html, IReadOnlyList<IReadOnlyList<TelegramButton>>? buttons = null,
        bool silent = false, CancellationToken ct = default)
    {
        var args = new JsonObject
        {
            ["chat_id"] = chatId,
            ["text"] = html,
            ["parse_mode"] = "HTML",
            ["disable_notification"] = silent,
            ["link_preview_options"] = new JsonObject { ["is_disabled"] = true },
        };
        if (threadId is { } thread)
            args["message_thread_id"] = thread;
        if (buttons is { Count: > 0 })
            args["reply_markup"] = Keyboard(buttons);
        var sent = await CallAsync("sendMessage", args, ct);
        return sent?["message_id"]?.GetValue<int>() ?? 0;
    }

    /// <summary>A message's text changed, its buttons with it (none when none are given); the same text again is no error.</summary>
    public async Task EditMessageAsync(long chatId, int messageId, string html, IReadOnlyList<IReadOnlyList<TelegramButton>>? buttons = null,
        CancellationToken ct = default)
    {
        var args = new JsonObject
        {
            ["chat_id"] = chatId,
            ["message_id"] = messageId,
            ["text"] = html,
            ["parse_mode"] = "HTML",
            ["link_preview_options"] = new JsonObject { ["is_disabled"] = true },
        };
        if (buttons is { Count: > 0 })
            args["reply_markup"] = Keyboard(buttons);
        try
        {
            await CallAsync("editMessageText", args, ct);
        }
        catch (TelegramException ex) when (ex.Code == 400 && ex.Message.Contains("not modified", StringComparison.OrdinalIgnoreCase))
        {
        }
    }

    /// <summary>A file sent in a topic, with a caption in HTML.</summary>
    public async Task SendDocumentAsync(long chatId, int? threadId, string fileName, byte[] content, string? captionHtml, bool silent = false,
        CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent(chatId.ToString(System.Globalization.CultureInfo.InvariantCulture)), "chat_id" },
            { new StringContent(silent ? "true" : "false"), "disable_notification" },
        };
        if (threadId is { } thread)
            form.Add(new StringContent(thread.ToString(System.Globalization.CultureInfo.InvariantCulture)), "message_thread_id");
        if (captionHtml is { Length: > 0 })
        {
            form.Add(new StringContent(captionHtml), "caption");
            form.Add(new StringContent("HTML"), "parse_mode");
        }
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("text/markdown");
        form.Add(file, "document", fileName);
        await SendAsync("sendDocument", form, ct, TimeSpan.FromSeconds(60));
    }

    /// <summary>A topic of a forum group, made; its thread's id.</summary>
    public async Task<int> CreateTopicAsync(long chatId, string name, CancellationToken ct = default)
    {
        var topic = await CallAsync("createForumTopic", new JsonObject { ["chat_id"] = chatId, ["name"] = TopicName(name) }, ct);
        return topic?["message_thread_id"]?.GetValue<int>() ?? throw new TelegramException("Telegram made no topic.", 0);
    }

    public Task RenameTopicAsync(long chatId, int threadId, string name, CancellationToken ct = default) =>
        CallAsync("editForumTopic", new JsonObject { ["chat_id"] = chatId, ["message_thread_id"] = threadId, ["name"] = TopicName(name) }, ct);

    public Task DeleteTopicAsync(long chatId, int threadId, CancellationToken ct = default) =>
        CallAsync("deleteForumTopic", new JsonObject { ["chat_id"] = chatId, ["message_thread_id"] = threadId }, ct);

    /// <summary>Whether <paramref name="userId"/> is an administrator of the chat who may manage its topics.</summary>
    public async Task<bool> CanManageTopicsAsync(long chatId, long userId, CancellationToken ct = default)
    {
        var member = await CallAsync("getChatMember", new JsonObject { ["chat_id"] = chatId, ["user_id"] = userId }, ct);
        return member?["status"]?.GetValue<string>() switch
        {
            "creator" => true,
            "administrator" => member["can_manage_topics"]?.GetValue<bool>() == true,
            _ => false,
        };
    }

    /// <summary>A pressed button answered (its spinner stopped), with a short note shown over the chat.</summary>
    public Task AnswerCallbackAsync(string callbackId, string? text = null, CancellationToken ct = default)
    {
        var args = new JsonObject { ["callback_query_id"] = callbackId };
        if (text is { Length: > 0 })
            args["text"] = text;
        return CallAsync("answerCallbackQuery", args, ct);
    }

    static string TopicName(string name) => name.Length <= 128 ? name : name[..127] + "…";

    static JsonObject Keyboard(IReadOnlyList<IReadOnlyList<TelegramButton>> rows) => new()
    {
        ["inline_keyboard"] = new JsonArray([.. rows.Select(row => (JsonNode)new JsonArray([.. row.Select(b => (JsonNode)new JsonObject { ["text"] = b.Text, ["callback_data"] = b.Data })]))]),
    };

    Task<JsonNode?> CallAsync(string method, JsonObject args, CancellationToken ct, TimeSpan? timeout = null) =>
        SendAsync(method, new StringContent(args.ToJsonString(), Encoding.UTF8, "application/json"), ct, timeout ?? TimeSpan.FromSeconds(30));

    /// <summary>A call, waited for again (a few times) when Telegram asks to slow down.</summary>
    async Task<JsonNode?> SendAsync(string method, HttpContent content, CancellationToken ct, TimeSpan timeout)
    {
        var body = await content.ReadAsByteArrayAsync(ct);
        var headers = content.Headers.ToList();
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_api, $"./bot{token}/{method}")) { Content = new ByteArrayContent(body) };
            foreach (var (name, values) in headers)
                request.Content.Headers.TryAddWithoutValidation(name, values);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, cts.Token);
            }
            catch (HttpRequestException ex)
            {
                // Its message never has the address (with the token) in it, but its inner one might.
                throw new TelegramException("Telegram could not be reached: " + ex.Message, 0);
            }
            using (response)
            {
                var text = await response.Content.ReadAsStringAsync(ct);
                JsonNode? json;
                try
                {
                    json = JsonNode.Parse(text);
                }
                catch (JsonException)
                {
                    throw new TelegramException($"Telegram answered {(int)response.StatusCode} without its JSON.", (int)response.StatusCode);
                }
                if (json?["ok"]?.GetValue<bool>() == true)
                    return json["result"];
                var code = json?["error_code"]?.GetValue<int>() ?? (int)response.StatusCode;
                var description = json?["description"]?.GetValue<string>() ?? response.ReasonPhrase ?? "error";
                if (code == (int)HttpStatusCode.TooManyRequests && attempt < 3 && json?["parameters"]?["retry_after"]?.GetValue<int>() is { } wait && wait <= 60)
                {
                    await Task.Delay(TimeSpan.FromSeconds(wait), ct);
                    continue;
                }
                throw new TelegramException(description, code);
            }
        }
    }

    // ---- What Telegram sends ------------------------------------------------------------------------------------

    static TelegramUser? User(JsonNode? node) => node is null ? null : new(
        node["id"]?.GetValue<long>() ?? 0,
        node["username"]?.GetValue<string>(),
        node["first_name"]?.GetValue<string>() ?? "",
        node["is_bot"]?.GetValue<bool>() ?? false);

    static TelegramChat? Chat(JsonNode? node) => node is null ? null : new(
        node["id"]?.GetValue<long>() ?? 0,
        node["type"]?.GetValue<string>() ?? "",
        node["title"]?.GetValue<string>(),
        node["is_forum"]?.GetValue<bool>() ?? false);

    static TelegramMessage? Message(JsonNode? node) => node is null || Chat(node["chat"]) is not { } chat ? null : new(
        node["message_id"]?.GetValue<int>() ?? 0,
        chat,
        User(node["from"]),
        node["message_thread_id"]?.GetValue<int>(),
        node["is_topic_message"]?.GetValue<bool>() ?? false,
        node["text"]?.GetValue<string>());

    static TelegramUpdate? Update(JsonNode? node)
    {
        if (node is null)
            return null;
        var callback = node["callback_query"] is { } c && User(c["from"]) is { } from
            ? new TelegramCallback(c["id"]?.GetValue<string>() ?? "", from, Message(c["message"]), c["data"]?.GetValue<string>())
            : null;
        return new(node["update_id"]?.GetValue<long>() ?? 0, Message(node["message"]), callback);
    }
}
