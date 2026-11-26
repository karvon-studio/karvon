namespace Karvon.Claude.Remote;

/// <summary>One of a question's options, as the agent gave it.</summary>
public sealed record RemoteOption(string Label, string? Description);

/// <summary>A question with options (Claude's AskUserQuestion): one is picked, or several when <paramref name="MultiSelect"/>.</summary>
public sealed record RemoteQuestion(string Question, string? Header, bool MultiSelect, IReadOnlyList<RemoteOption> Options);

/// <summary>The answer to one question: the options picked (by their place), or a text of the user's own.</summary>
public sealed record RemoteReply(IReadOnlyList<int> Picked, string? Other);

/// <summary>
/// An agent's questions with options, answered in Telegram: one message a question, its options as buttons (ticked, then
/// Done, where several may be picked), or a text written in the topic as the user's own answer. The answers go to the agent
/// once the last is given; each question's message goes once it is answered.
/// </summary>
public sealed partial class TelegramRemote
{
    /// <summary>The question it is on, asked in its topic.</summary>
    void SendQuestion(Ask ask)
    {
        var html = QuestionHtml(ask);
        ask.Html = html;
        var buttons = QuestionButtons(ask);
        Enqueue(ask.Chat, async (bot, pairing) =>
        {
            await EnsureTopicAsync(bot, pairing, ask.Chat);
            if (ask.Done)
                return;
            ask.MessageId = await bot.SendMessageAsync(pairing.ChatId, ask.Chat.ThreadId, html + QuestionHint(ask), buttons);
            if (ask.Done)
                await bot.DeleteMessageAsync(pairing.ChatId, ask.MessageId.Value);
        });
    }

    static string QuestionHtml(Ask ask)
    {
        var questions = ask.Request.Questions!;
        var question = questions[ask.Current];
        var html = $"❓ <b>{Html(ask.Request.Question)}</b>" + (questions.Count > 1 ? $" ({ask.Current + 1}/{questions.Count})" : "") + "\n\n"
            + (question.Header is { Length: > 0 } header ? $"<b>{Html(header)}</b>: " : "") + Inline(Clip(question.Question, 1000));
        for (var i = 0; i < question.Options.Count; i++)
            html += $"\n{i + 1}. <b>{Html(Clip(question.Options[i].Label, 200))}</b>"
                + (question.Options[i].Description is { Length: > 0 } description ? " — " + Inline(Clip(description, 300)) : "");
        return html;
    }

    /// <summary>A line of the agent's, its Markdown shown, its secrets hidden.</summary>
    static string Inline(string text) => TelegramMarkdown.Inline(Claude.Tools.Redaction.Text(text.Replace('\n', ' ')));

    static string QuestionHint(Ask ask) => ask.Request.Questions![ask.Current].MultiSelect
        ? "\n\n<i>Tick any, then Done; or write your own answer in this topic.</i>"
        : "\n\n<i>Pick one, or write your own answer in this topic.</i>";

    static IReadOnlyList<IReadOnlyList<TelegramButton>> QuestionButtons(Ask ask)
    {
        var question = ask.Request.Questions![ask.Current];
        var rows = question.Options.Select((option, i) => (IReadOnlyList<TelegramButton>)
            [new TelegramButton((question.MultiSelect ? ask.Picked.Contains(i) ? "☑ " : "☐ " : "") + $"{i + 1}. " + Clip(option.Label, 50), $"q:{ask.Key}:{i}")]).ToList();
        if (question.MultiSelect)
            rows.Add([new TelegramButton("✔ Done", $"q:{ask.Key}:d")]);
        return rows;
    }

    /// <summary>The question of a conversation that waits for its answer, when one does.</summary>
    Ask? QuestionOf(Chat chat) => _asks.Values.FirstOrDefault(a => a.Chat == chat && !a.Done && a.Request.Questions is { Count: > 0 } questions && a.Current < questions.Count);

    /// <summary>An option's button: picked (or ticked, where several may be).</summary>
    async Task OnPickAsync(TelegramBot bot, TelegramCallback callback, string data)
    {
        var parts = data.Split(':');
        if (parts.Length != 3 || !_asks.TryGetValue(parts[1], out var ask) || ask.Done || ask.Request.Questions is not { } questions || ask.Current >= questions.Count)
        {
            await bot.AnswerCallbackAsync(callback.Id, "It does not wait any more.");
            return;
        }
        if (!Approves() || !IsOn())
        {
            await bot.AnswerCallbackAsync(callback.Id, "This one is answered in Karvon.");
            return;
        }
        var question = questions[ask.Current];
        if (parts[2] == "d")
        {
            if (ask.Picked.Count == 0)
            {
                await bot.AnswerCallbackAsync(callback.Id, "Tick one at least.");
                return;
            }
            await bot.AnswerCallbackAsync(callback.Id);
            await ReplyAsync(ask, new RemoteReply([.. ask.Picked.Order()], null));
            return;
        }
        if (!int.TryParse(parts[2], out var index) || index < 0 || index >= question.Options.Count)
        {
            await bot.AnswerCallbackAsync(callback.Id, "It does not wait any more.");
            return;
        }
        if (question.MultiSelect)
        {
            if (!ask.Picked.Add(index))
                ask.Picked.Remove(index);
            await bot.AnswerCallbackAsync(callback.Id);
            var html = ask.Html + QuestionHint(ask);
            var buttons = QuestionButtons(ask);
            Enqueue(ask.Chat, async (bot, pairing) =>
            {
                if (ask.MessageId is { } id && !ask.Done)
                    await bot.EditMessageAsync(pairing.ChatId, id, html, buttons);
            });
            return;
        }
        await bot.AnswerCallbackAsync(callback.Id, Clip(question.Options[index].Label, 150));
        await ReplyAsync(ask, new RemoteReply([index], null));
    }

    /// <summary>One question answered: its message says how, and the next is asked; after the last, the agent gets them all.</summary>
    async Task ReplyAsync(Ask ask, RemoteReply reply)
    {
        var questions = ask.Request.Questions!;
        var question = questions[ask.Current];
        ask.Replies.Add(reply);
        ask.Picked.Clear();
        // Answered: its message goes (the chat kept to one message a turn), and the next is asked.
        var asked = ask.MessageId;
        ask.MessageId = null;
        Enqueue(ask.Chat, async (bot, pairing) =>
        {
            if (asked is { } id)
                await bot.DeleteMessageAsync(pairing.ChatId, id);
        });
        ask.Current++;
        if (ask.Current < questions.Count)
        {
            SendQuestion(ask);
            return;
        }
        ask.Outcome = "Answered from Telegram";
        if (await ask.Chat.Host.AnswerQuestionsAsync(ask.Request.RequestId, ask.Replies))
            Answered(ask.Request.RequestId, ask.Outcome);
        else
        {
            ask.Outcome = null;
            Enqueue(ask.Chat, (bot, pairing) => bot.SendMessageAsync(pairing.ChatId, ask.Chat.ThreadId, "It does not wait for an answer any more.", silent: true));
        }
    }
}
