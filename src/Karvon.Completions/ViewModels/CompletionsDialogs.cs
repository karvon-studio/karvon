using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Karvon.Ui;

namespace Karvon.Completions.ViewModels;

/// <summary>The module's dialogs over the window, one at a time.</summary>
public sealed partial class CompletionsOverlay : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDialog))]
    public partial OverlayDialog? Dialog { get; set; }

    public bool HasDialog => Dialog is not null;

    /// <summary>A dialog over the window: it, when confirmed; else null.</summary>
    public async Task<T?> ShowAsync<T>(T dialog) where T : OverlayDialog
    {
        while (Dialog is { } open)
            await open.Result;
        Dialog = dialog;
        try
        {
            return await dialog.Result ? dialog : null;
        }
        finally
        {
            Dialog = null;
        }
    }
}

/// <summary>
/// Tools > Inline Completions: whether suggestions come as you type, and from where: Claude (a model, Anthropic's API key,
/// kept in the system keyring) or a local model through Ollama (its address and model).
/// </summary>
public sealed partial class CompletionsSettingsDialog : OverlayDialog
{
    public const string ConsoleSite = "https://console.anthropic.com/settings/keys";

    public CompletionsSettingsDialog(CompletionSettings settings, bool hasKey, bool canKeep, bool hasEnvironmentKey) : base("Inline Completions", "Save")
    {
        HasKey = hasKey;
        CanKeep = canKeep;
        HasEnvironmentKey = hasEnvironmentKey;
        Enabled = settings.Enabled;
        ProviderIndex = (int)settings.Provider;
        ClaudeModels = settings.ClaudeModel is { Length: > 0 } model && !CompletionSettings.ClaudeModels.Contains(model)
            ? [.. CompletionSettings.ClaudeModels, model]
            : CompletionSettings.ClaudeModels;
        ClaudeModel = settings.ClaudeModel;
        OllamaUrl = settings.OllamaUrl;
        OllamaModel = settings.OllamaModel;
    }

    public static IReadOnlyList<string> Providers { get; } = ["Claude: Anthropic's API (an API key)", "Ollama: a model on this machine"];

    public IReadOnlyList<string> ClaudeModels { get; }

    public bool HasKey { get; }

    public bool CanKeep { get; }

    public bool HasEnvironmentKey { get; }

    [ObservableProperty]
    public partial bool Enabled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsClaude), nameof(IsOllama), nameof(Problem))]
    public partial int ProviderIndex { get; set; }

    public CompletionProvider Provider => (CompletionProvider)ProviderIndex;

    public bool IsClaude => Provider == CompletionProvider.Claude;

    public bool IsOllama => Provider == CompletionProvider.Ollama;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problem))]
    public partial string ClaudeModel { get; set; }

    [ObservableProperty]
    public partial string Key { get; set; } = "";

    [ObservableProperty]
    public partial bool Keep { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problem))]
    public partial string OllamaUrl { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problem))]
    public partial string OllamaModel { get; set; }

    /// <summary>The key is to be forgotten (Forget Key).</summary>
    public bool Forget { get; private set; }

    public string KeyPlaceholder =>
        HasKey ? "Saved; type a new one to change it"
        : HasEnvironmentKey ? "ANTHROPIC_API_KEY's, or type one"
        : "sk-ant-...";

    public string? Problem =>
        IsClaude && ClaudeModel.Trim().Length == 0 ? "The model: a Claude model's id (claude-opus-5-5)."
        : IsOllama && !(Uri.TryCreate(OllamaUrl.Trim(), UriKind.Absolute, out var address) && address.Scheme is "http" or "https")
            ? "Ollama's address, with its http://: http://localhost:11434, or where it runs."
        : IsOllama && OllamaModel.Trim().Length == 0 ? "The model: one Ollama has pulled (ollama pull qwen2.5-coder:1.5b)."
        : null;

    public override bool CanConfirm => Problem is null;

    [RelayCommand]
    void ForgetKey()
    {
        // Only with the form as it can be saved: a Forget left set would forget the key on a later Save.
        if (Problem is not null)
            return;
        Forget = true;
        Key = "";
        Confirm();
    }

    /// <summary>A page to open in the browser (where to get a key).</summary>
    public event Action<string>? OpenSite;

    [RelayCommand]
    void ShowSite() => OpenSite?.Invoke(ConsoleSite);
}
