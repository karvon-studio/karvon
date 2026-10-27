using System.Net;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Karvon.Completions;
using Karvon.Completions.ViewModels;
using Karvon.Editor;
using Karvon.Views;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>The tests' Anthropic API and Ollama: each request (its body read), answered as the test says.</summary>
sealed class FakeCompletionsHttp(Func<HttpRequestMessage, string, HttpResponseMessage> answer) : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (Requests)
            Requests.Add((request, body));
        return answer(request, body);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>A Messages API answer: a thinking block with nothing shown of it, then the text.</summary>
    public static HttpResponseMessage Message(string text, string stop = "end_turn") => Json(JsonSerializer.Serialize(new
    {
        id = "msg_test",
        type = "message",
        role = "assistant",
        model = "claude-opus-5-5",
        content = new object[] { new { type = "thinking", thinking = "", signature = "sig" }, new { type = "text", text } },
        stop_reason = stop,
        stop_sequence = (string?)null,
        usage = new { input_tokens = 120, output_tokens = 12 },
    }));
}

/// <summary>Inline completions from Claude and from Ollama: what is asked, what comes back, the module's settings and status.</summary>
public class CompletionsTests
{
    static T Named<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    static readonly InlineCompletionRequest Request = new("/work/src/Hello.cs", "class Hello\n{\n    void Say() => Console.", "\n}\n");

    [Fact]
    public async Task Claude_is_asked_for_the_text_at_the_marker_with_little_thinking_and_its_fallback()
    {
        var api = new FakeCompletionsHttp((_, _) => FakeCompletionsHttp.Message("```csharp\nWriteLine(\"hi\");\n```"));
        var source = new ClaudeCompletionSource("sk-ant-test", "claude-opus-5-5", new HttpClient(api));

        Assert.Equal("WriteLine(\"hi\");", await source.CompleteAsync(Request, "src/Hello.cs", TestContext.Current.CancellationToken));

        var (request, body) = Assert.Single(api.Requests);
        Assert.Equal("api.anthropic.com", request.RequestUri!.Host);
        Assert.Equal("/v1/messages", request.RequestUri.AbsolutePath);
        // The API version the SDK speaks takes the key as a bearer token.
        Assert.Equal("Bearer sk-ant-test", request.Headers.Authorization?.ToString());
        Assert.Contains("server-side-fallback-2026-07-01", string.Join(",", request.Headers.GetValues("anthropic-beta")));
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.Equal("claude-opus-5-5", root.GetProperty("model").GetString());
        Assert.Equal("low", root.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.Equal("default", root.GetProperty("fallbacks").GetString());
        Assert.Equal(ClaudeCompletionSource.SystemPrompt, root.GetProperty("system").GetString());
        Assert.False(root.TryGetProperty("temperature", out _));
        Assert.False(root.TryGetProperty("thinking", out _));
        Assert.Equal("user", Assert.Single(root.GetProperty("messages").EnumerateArray()).GetProperty("role").GetString());
        Assert.Equal("<file path=\"src/Hello.cs\">\nclass Hello\n{\n    void Say() => Console.<CURSOR/>\n}\n\n</file>", PromptOf(body));
    }

    /// <summary>The user's message a Messages API request sends.</summary>
    static string? PromptOf(string body)
    {
        using var json = JsonDocument.Parse(body);
        var content = json.RootElement.GetProperty("messages")[0].GetProperty("content");
        return content.ValueKind == JsonValueKind.String ? content.GetString() : content[0].GetProperty("text").GetString();
    }

    [Fact]
    public async Task A_declined_or_cut_off_answer_gives_no_line_it_cannot_stand_behind()
    {
        var answers = new Queue<HttpResponseMessage>([
            FakeCompletionsHttp.Message("", "refusal"),
            FakeCompletionsHttp.Message("WriteLine(1);\n    var x = Compu", "max_tokens"),
        ]);
        var source = new ClaudeCompletionSource("sk-ant-test", "claude-haiku-5-5", new HttpClient(new FakeCompletionsHttp((_, _) => answers.Dequeue())));
        Assert.Null(await source.CompleteAsync(Request, "Hello.cs", TestContext.Current.CancellationToken));
        Assert.Equal("WriteLine(1);", await source.CompleteAsync(Request, "Hello.cs", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Ollama_fills_in_the_middle_with_the_text_before_and_after_the_caret()
    {
        var ollama = new FakeCompletionsHttp((_, _) => FakeCompletionsHttp.Json("""{"model":"qwen2.5-coder:1.5b","response":"WriteLine();\n","done":true,"done_reason":"stop"}"""));
        var source = new OllamaCompletionSource(new HttpClient(ollama), "http://localhost:11434/", "qwen2.5-coder:1.5b");

        Assert.Equal("WriteLine();", await source.CompleteAsync(Request, "src/Hello.cs", TestContext.Current.CancellationToken));
        var (request, body) = Assert.Single(ollama.Requests);
        Assert.Equal("http://localhost:11434/api/generate", request.RequestUri!.ToString());
        using var json = JsonDocument.Parse(body);
        Assert.Equal("qwen2.5-coder:1.5b", json.RootElement.GetProperty("model").GetString());
        Assert.Equal(Request.Prefix, json.RootElement.GetProperty("prompt").GetString());
        Assert.Equal(Request.Suffix, json.RootElement.GetProperty("suffix").GetString());
        Assert.False(json.RootElement.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public void A_models_text_is_cleaned_into_a_suggestion()
    {
        Assert.Equal("a();\nb();", CompletionText.Clean("```js\na();\nb();\n```"));
        Assert.Equal("x", CompletionText.Clean("x<CURSOR/>"));
        Assert.Null(CompletionText.Clean("```\n```"));
        Assert.Null(CompletionText.Clean("  "));
        Assert.Equal(CompletionText.MaxLines, CompletionText.Clean(string.Join("\n", Enumerable.Range(0, 50)))!.Split('\n').Length);
    }

    [AvaloniaFact]
    public async Task The_module_suggests_from_Claude_in_the_editor_says_its_problems_and_keeps_its_settings()
    {
        EditorHost.InlineCompletionDelay = TimeSpan.FromMilliseconds(50);
        var status = HttpStatusCode.OK;
        var api = new FakeCompletionsHttp((request, _) => request.RequestUri!.Host == "api.anthropic.com"
            ? status == HttpStatusCode.OK
                ? FakeCompletionsHttp.Message("WriteLine(\"hi\");")
                : FakeCompletionsHttp.Json("""{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}""", status)
            : FakeCompletionsHttp.Json("""{"response":"Write(\"local\");","done":true}"""));
        var dir = Directory.CreateTempSubdirectory("karvon-completions-").FullName;
        var file = Path.Combine(dir, "Hello.cs");
        File.WriteAllText(file, "Console\n");
        var vm = NewViewModel();
        var completions = vm.Module<CompletionsModule>()!;
        // No key yet: the status bar says so, and nothing is asked.
        Assert.Equal("Inline Completions", completions.StatusItem.Text);
        completions.EnvironmentKey = () => "sk-ant-env";
        completions.Http = new HttpClient(api);
        Assert.Equal("Claude", completions.StatusItem.Text);
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        window.Show();
        await vm.OpenSolutionAsync(dir);
        var host = vm.OpenDocument(file)!.Host;
        await WaitUntil(() => host.Editor.TextArea.IsEffectivelyVisible, TimeSpan.FromSeconds(5));
        host.Editor.TextArea.Focus();
        host.Editor.CaretOffset = "Console".Length;

        host.Editor.TextArea.PerformTextInput(".");
        await WaitUntil(() => host.InlineSuggestion is not null, TimeSpan.FromSeconds(5));
        Assert.Equal("WriteLine(\"hi\");", host.InlineSuggestion);
        var (_, body) = Assert.Single(api.Requests);
        Assert.StartsWith("<file path=\"Hello.cs\">\nConsole.<CURSOR/>", PromptOf(body));
        Assert.Equal("Bearer sk-ant-env", api.Requests[0].Request.Headers.Authorization?.ToString());

        // A key Anthropic does not take: no suggestion, the status bar and Output say why.
        host.DismissInlineSuggestion();
        status = HttpStatusCode.Unauthorized;
        Assert.False(await host.RequestInlineCompletionAsync());
        Assert.Equal("Claude: error", completions.StatusItem.Text);
        Assert.Contains("did not take the API key", completions.StatusItem.ToolTip);
        Assert.Contains("did not take the API key", vm.OutputChannels.Single(c => c.Name == "Inline Completions").Document.Text);

        // Tools > Inline Completions: Ollama instead, kept in completions.json; its suggestions come from there.
        var shown = completions.ShowSettingsAsync();
        var dialog = Assert.IsType<CompletionsSettingsDialog>(completions.Dialogs.Dialog);
        // Over the window: the key's box is there, the Ollama boxes are not while Claude is chosen.
        Dispatcher.UIThread.RunJobs();
        Assert.True(Named<TextBox>(window, "KeyBox").IsEffectivelyVisible);
        Assert.False(Named<TextBox>(window, "OllamaModelBox").IsEffectivelyVisible);
        Assert.True(dialog.IsClaude);
        Assert.Equal("claude-opus-5-5", dialog.ClaudeModel);
        dialog.ProviderIndex = 1;
        dialog.OllamaModel = "qwen2.5-coder:7b";
        dialog.Confirm();
        await shown;
        Assert.Equal("Ollama", completions.StatusItem.Text);
        Assert.Contains("\"provider\": \"ollama\"", File.ReadAllText(completions.SettingsFile));
        Assert.Equal(CompletionProvider.Ollama, CompletionSettings.Load(completions.SettingsFile).Provider);
        Assert.True(await host.RequestInlineCompletionAsync());
        Assert.Equal("Write(\"local\");", host.InlineSuggestion);
        Assert.Contains("qwen2.5-coder:7b", api.Requests[^1].Body);

        // Turned off: nothing is asked, and its status bar item goes.
        host.DismissInlineSuggestion();
        shown = completions.ShowSettingsAsync();
        dialog = Assert.IsType<CompletionsSettingsDialog>(completions.Dialogs.Dialog);
        dialog.Enabled = false;
        dialog.Confirm();
        await shown;
        var asked = api.Requests.Count;
        Assert.False(await host.RequestInlineCompletionAsync());
        Assert.Equal(asked, api.Requests.Count);
        Assert.False(completions.StatusItem.IsVisible);
        window.Close();
    }
}
