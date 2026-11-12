using System.Text.Json;
using Bengine.Introspection.Servers;

namespace Karvon.Claude;

/// <summary>Where a model Karvon drives itself answers from: an OpenAI-compatible endpoint, with or without a key.</summary>
/// <param name="EnvironmentUrl">The variable that may name another address (OLLAMA_HOST...); null for none.</param>
/// <param name="EnvironmentKey">The variable that may hold the key (OPENROUTER_API_KEY); null when it needs none.</param>
/// <param name="Native">It is talked to in its own API (Ollama's, <see cref="OllamaChat"/>), not Chat Completions.</param>
/// <param name="StreamOptions">It takes <c>stream_options</c> (Mistral's refuses a field it does not know, and gives the usage anyway).</param>
/// <param name="MessagesApi">It is Anthropic's Messages API (<see cref="AnthropicChat"/>), not Chat Completions.</param>
public sealed record ModelProvider(string Id, string Name, string Description, string Url, string? EnvironmentUrl, string? EnvironmentKey,
    string MissingHint, string? InstallCommand, bool Native = false, bool StreamOptions = true, bool MessagesApi = false)
{
    public bool NeedsKey => EnvironmentKey is not null;

    /// <summary>The vault's entry for its key.</summary>
    public string KeyName => "karvon-agents/" + Id;

    public static readonly ModelProvider OpenRouter = new("openrouter", "OpenRouter", "Any model (GPT, Gemini, Qwen, DeepSeek, Kimi, GLM...) on your OpenRouter key",
        "https://openrouter.ai/api/v1", null, "OPENROUTER_API_KEY",
        "Add your OpenRouter API key (openrouter.ai/keys) in Choose Agent and Model: Karvon keeps it in its vault and sends it only to OpenRouter.", null);

    /// <summary>A provider on the user's key: kept in Karvon's vault and sent to it alone, or its variable's.</summary>
    static ModelProvider Keyed(string id, string name, string description, string url, string variable, string keys, bool streamOptions = true) =>
        new(id, name, description, url, null, variable,
            $"Add your {name} API key ({keys}) in Choose Agent and Model: Karvon keeps it in its vault and sends it only to {name}.", null,
            StreamOptions: streamOptions);

    /// <summary>Claude itself, through Anthropic's API on the user's key (not Claude Code: Karvon's own loop, as for the others).</summary>
    public static readonly ModelProvider Claude = Keyed("anthropic", "Claude API", "Claude's models through Anthropic's API, on your API key",
        "https://api.anthropic.com", "ANTHROPIC_API_KEY", "platform.claude.com/settings/keys") with
    {
        MessagesApi = true,
        MissingHint = "Add your Anthropic API key (platform.claude.com/settings/keys) in Choose Agent and Model: Karvon keeps it in its vault and sends it only to Anthropic.",
    };

    public static readonly ModelProvider BytePlus = Keyed("byteplus", "BytePlus ModelArk", "ByteDance's Seed and the other models of BytePlus ModelArk, on your key",
        "https://ark.ap-southeast.bytepluses.com/api/v3", "ARK_API_KEY", "console.byteplus.com");

    public static readonly ModelProvider DeepSeek = Keyed("deepseek", "DeepSeek", "DeepSeek's own models, on your DeepSeek key",
        "https://api.deepseek.com", "DEEPSEEK_API_KEY", "platform.deepseek.com/api_keys");

    public static readonly ModelProvider Groq = Keyed("groq", "Groq", "Open models on Groq's fast inference, on your Groq key",
        "https://api.groq.com/openai/v1", "GROQ_API_KEY", "console.groq.com/keys");

    public static readonly ModelProvider Mistral = Keyed("mistral", "Mistral", "Mistral AI's models, on your Mistral key",
        "https://api.mistral.ai/v1", "MISTRAL_API_KEY", "console.mistral.ai/api-keys", streamOptions: false);

    public static readonly ModelProvider Ollama = new("ollama", "Ollama", "A local model in Ollama, on this computer",
        "http://localhost:11434", "OLLAMA_HOST", null,
        "Install Ollama and pull a model that calls tools, then retry.", "ollama pull qwen3-coder", Native: true);

    public static readonly ModelProvider LmStudio = new("lmstudio", "LM Studio", "A local model in LM Studio's server, on this computer",
        "http://localhost:1234/v1", null, null,
        "Start LM Studio's local server with a model loaded, then retry.", null);

    /// <summary>The endpoint's address: the variable's when set (OLLAMA_HOST: a host, maybe without its scheme), else the usual one.</summary>
    public string Address()
    {
        if (EnvironmentUrl is not null && Environment.GetEnvironmentVariable(EnvironmentUrl) is { Length: > 0 } host)
        {
            var url = (host.Contains("://", StringComparison.Ordinal) ? host : "http://" + host).TrimEnd('/');
            return Native || url.EndsWith("/v1", StringComparison.Ordinal) ? url : url + "/v1";
        }
        return Url;
    }
}

/// <summary>
/// A model Karvon drives itself (<see cref="ModelSession"/>) in the panel, from one provider: Claude (Anthropic's API), OpenRouter,
/// BytePlus ModelArk, DeepSeek, Groq or Mistral on the user's key, or a local Ollama or LM Studio. The key is kept in Karvon's vault (<paramref name="keyring"/>), or comes from the
/// provider's variable.
/// </summary>
public sealed class ModelBackend(ModelProvider provider, ICredentialStore? keyring = null, HttpClient? http = null) : IAgentBackend
{
    string? _key;
    bool _read;

    public ModelProvider Provider => provider;

    public string Id => provider.Id;

    public string Name => provider.Name;

    public string Description => provider.Description;

    public string MissingTitle => provider.NeedsKey ? $"{provider.Name} needs an API key" : $"{provider.Name} is not set up";

    public string MissingHint => provider.MissingHint;

    public string? InstallCommand => provider.InstallCommand;

    public bool NeedsKey => provider.NeedsKey;

    /// <summary>Karvon's loop has every mode of the panel's but auto (a classifier of Claude's).</summary>
    public IReadOnlyList<string> Modes => CodexSession.Modes;

    /// <summary>The endpoint's address; null when it needs a key and has none.</summary>
    public string? Find() => !provider.NeedsKey || Key() is not null ? provider.Address() : null;

    string? Key()
    {
        if (!_read && keyring is not null)
        {
            try
            {
                _key ??= keyring.Get(provider.KeyName) is { Length: > 0 } kept ? kept : null;
                _read = true;
            }
            // A locked or missing keyring gives none now.
            catch (Exception)
            {
            }
        }
        return _key ?? (provider.EnvironmentKey is { } name && Environment.GetEnvironmentVariable(name) is { Length: > 0 } fromEnvironment ? fromEnvironment : null);
    }

    /// <summary>The user's key: used from now on, and kept in the vault when there is one.</summary>
    public bool SetKey(string key)
    {
        key = key.Trim();
        if (key.Length == 0)
            return false;
        _key = key;
        try
        {
            keyring?.Set(provider.KeyName, $"Karvon: the {provider.Name} API key", key);
        }
        catch (Exception)
        {
            // Kept for this run of Karvon only.
        }
        return true;
    }

    public async Task<IReadOnlyList<AgentModel>> ModelsAsync(string executable, string workingDirectory, CancellationToken cancellationToken = default)
    {
        try
        {
            var models = await Endpoint(executable).ModelsAsync(cancellationToken);
            return [.. models.Select(m => new AgentModel(m.Id, m.Name, m.Description))];
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException || ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException($"{provider.Name} did not answer at {executable}: {ex.Message}", ex);
        }
    }

    public async Task<IAgentSession> StartAsync(AgentStartOptions options, CancellationToken cancellationToken = default) =>
        await ModelSession.StartAsync(Endpoint(options.Executable, options.Effort), provider.Name, options, cancellationToken);

    /// <summary>Its endpoint at <paramref name="url"/>: Ollama's own API, Anthropic's Messages API, or Chat Completions, with the key.</summary>
    IChatEndpoint Endpoint(string url, string? effort = null) =>
        provider.Native ? new OllamaChat(http ?? ModelSession.Client, url)
        : provider.MessagesApi ? new AnthropicChat(url, Key(), effort)
        : new ChatCompletions(http ?? ModelSession.Client, url, Key(), provider.StreamOptions);
}
