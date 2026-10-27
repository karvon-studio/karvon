using Anthropic.Exceptions;
using Avalonia.Controls.Templates;
using Bengine.Introspection.Servers;
using Karvon.Completions.ViewModels;
using Karvon.Completions.Views;
using Karvon.Editor;
using Karvon.Modules;
using Karvon.ViewModels;

namespace Karvon.Completions;

/// <summary>
/// Inline Completions: an AI's suggestion in gray after the caret as you type, as Visual Studio's Copilot shows one (the
/// editor's part is <see cref="EditorHost"/>'s): from Claude through Anthropic's API, or from a local model through Ollama.
/// Its status bar item says where they come from and opens its settings (Tools > Inline Completions too); the API key stays
/// in the system keyring.
/// </summary>
public sealed class CompletionsModule : KarvonModule
{
    readonly ModuleStatusItem _status = new() { Icon = "Sparkle", IsVisible = true };
    readonly CompletionsOverlay _overlay = new();
    CompletionSettings? _settings;
    CompletionSecrets? _secrets;
    string _settingsFile = Path.Combine(Services.Settings.ConfigDirectory, "completions.json");
    ICredentialStore? _keyring = Services.Keyring.System;
    Func<string?>? _environmentKey;
    HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    ICompletionSource? _source;
    string? _sourceFor;
    string? _problem;
    OutputChannel? _output;

    public CompletionsModule() => _status.Click = () => _ = ShowSettingsAsync();

    public override string Id => "completions";

    public override string Name => "Inline Completions";

    public override string Icon => "robot";

    public override string Description =>
        "An AI's code suggestions in the editor as you type, in gray after the caret: Tab takes one, Ctrl+Right its next word, "
        + "Esc dismisses it, Alt+\\ asks for one. From Claude through Anthropic's API (your API key, kept in the system keyring) "
        + "or from a local model through Ollama.";

    public override ModuleStatusItem StatusItem => _status;

    public override IReadOnlyList<ModuleMainMenuItem> MainMenus => [new("Tools", new("Inline _Completions...", ShowSettingsAsync) { After = "Settings Sync" })];

    public override IDataTemplate OverlayTemplate => Template<CompletionsOverlay>(_ => new CompletionsOverlayView());

    public override Func<InlineCompletionRequest, CancellationToken, Task<string?>>? InlineCompletions => CompleteAsync;

    /// <summary>Where the settings are kept (the tests' own).</summary>
    internal string SettingsFile
    {
        get => _settingsFile;
        set
        {
            _settingsFile = value;
            _settings = null;
            Reset();
        }
    }

    internal ICredentialStore? Keyring
    {
        get => _keyring;
        set
        {
            _keyring = value;
            _secrets = null;
            Reset();
        }
    }

    /// <summary>The environment's ANTHROPIC_API_KEY (the tests' own).</summary>
    internal Func<string?>? EnvironmentKey
    {
        get => _environmentKey;
        set
        {
            _environmentKey = value;
            _secrets = null;
            Reset();
        }
    }

    /// <summary>What reaches Anthropic's API and Ollama (the tests' fake).</summary>
    internal HttpClient Http
    {
        get => _http;
        set
        {
            _http = value;
            Reset();
        }
    }

    public CompletionSettings Settings => _settings ??= CompletionSettings.Load(SettingsFile);

    CompletionSecrets Secrets => _secrets ??= new(_keyring, _environmentKey);

    internal CompletionsOverlay Dialogs => _overlay;

    protected override void Started() => UpdateStatus();

    /// <summary>What was made from the settings and the key goes; it is made again as they are now.</summary>
    void Reset()
    {
        _sourceFor = null;
        _problem = null;
        if (IsStarted)
            UpdateStatus();
    }

    /// <summary>A suggestion for a request from where the settings say, or null (off, no key, an error: said in its status and Output).</summary>
    async Task<string?> CompleteAsync(InlineCompletionRequest request, CancellationToken ct)
    {
        if (!Settings.Enabled || Source() is not { } source)
            return null;
        // The file as the model sees it: its path in the folder open.
        var relative = Workbench.Workspace is { } workspace ? Path.GetRelativePath(workspace, request.FilePath) : "..";
        var path = relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? Path.GetFileName(request.FilePath) : relative.Replace('\\', '/');
        try
        {
            var text = await source.CompleteAsync(request, path, ct);
            Problem(null);
            return text;
        }
        // Typing went on: whatever the request ended with, it was cancelled, and that is no problem.
        catch (Exception) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        catch (AnthropicUnauthorizedException)
        {
            Problem("Anthropic did not take the API key: set another in Tools > Inline Completions.");
        }
        catch (AnthropicRateLimitException)
        {
            Problem("Anthropic's rate limit for the key was reached: suggestions come again in a moment.");
        }
        catch (Exception ex) when (ex is AnthropicException or HttpRequestException or TaskCanceledException or TimeoutException or System.Text.Json.JsonException)
        {
            var why = ex is TaskCanceledException or TimeoutException ? "no answer within 30 seconds" : ex.Message;
            Problem(Settings.Provider == CompletionProvider.Ollama
                ? $"Ollama at {Settings.OllamaUrl} did not answer ({why}). Is it running, with {Settings.OllamaModel} pulled?"
                : $"Claude did not answer: {why}");
        }
        return null;
    }

    /// <summary>The source the settings say, made again when they (or the key) change; null without a key for Claude.</summary>
    ICompletionSource? Source()
    {
        var settings = Settings;
        string key;
        if (settings.Provider == CompletionProvider.Ollama)
            key = $"ollama {settings.OllamaUrl} {settings.OllamaModel}";
        else if (Secrets.ApiKey is { } apiKey)
            key = $"claude {settings.ClaudeModel} {apiKey.GetHashCode()}";
        else
            return null;
        if (key != _sourceFor)
        {
            _source = settings.Provider == CompletionProvider.Ollama
                ? new OllamaCompletionSource(Http, settings.OllamaUrl, settings.OllamaModel)
                : new ClaudeCompletionSource(Secrets.ApiKey!, settings.ClaudeModel, Http);
            _sourceFor = key;
        }
        return _source;
    }

    /// <summary>What went wrong last (null: the last request went through): in the status bar's tooltip, and once in Output.</summary>
    void Problem(string? problem)
    {
        if (problem == _problem)
            return;
        _problem = problem;
        if (problem is not null)
        {
            _output ??= Workbench.Output("Inline Completions");
            _output.Append($"[{DateTime.Now:HH:mm:ss}] {problem}");
        }
        UpdateStatus();
    }

    void UpdateStatus()
    {
        var settings = Settings;
        var from = settings.Provider == CompletionProvider.Claude ? "Claude" : "Ollama";
        var noKey = settings.Provider == CompletionProvider.Claude && Secrets.ApiKey is null;
        // Turned off, it is out of the way: Tools > Inline Completions turns it on again.
        _status.IsVisible = settings.Enabled;
        _status.Text = noKey ? "Inline Completions" : _problem is not null ? $"{from}: error" : from;
        _status.ToolTip = noKey
            ? "Inline completions need Anthropic's API key, or a local model through Ollama: click to set them up."
            : $"Inline completions from {from} ({settings.Model}): Tab takes a suggestion, Ctrl+Right its next word, Esc dismisses it, "
              + "Alt+\\ asks for one. Click to change."
              + (_problem is { } problem ? "\n\n" + problem : "");
    }

    /// <summary>Tools > Inline Completions (and the status bar item): on or off, the provider, its model, the key.</summary>
    public async Task ShowSettingsAsync()
    {
        var dialog = new CompletionsSettingsDialog(Settings, Secrets.HasOwnKey, Secrets.HasKeyring, Secrets.HasEnvironmentKey);
        dialog.OpenSite += Workbench.OpenUrl;
        // Nothing of the module's is made over the window before it is needed.
        Overlay ??= _overlay;
        if (await _overlay.ShowAsync(dialog) is not { } form)
            return;
        var settings = Settings;
        settings.Enabled = form.Enabled;
        settings.Provider = form.Provider;
        settings.ClaudeModel = form.ClaudeModel.Trim();
        settings.OllamaUrl = form.OllamaUrl.Trim();
        settings.OllamaModel = form.OllamaModel.Trim();
        try
        {
            settings.Save(SettingsFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Workbench.Status($"Inline Completions' settings were not saved: {ex.Message}");
        }
        if (form.Forget)
            Secrets.Forget();
        else if (form.Key.Trim() is { Length: > 0 } key)
            Secrets.Set(key, form.Keep);
        Reset();
    }

    public override void Dispose() => _http.Dispose();
}
