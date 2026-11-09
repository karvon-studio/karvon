using Bengine.Introspection.Servers;

namespace Karvon.Claude;

/// <summary>Where a model Karvon drives itself answers from: an OpenAI-compatible endpoint, with or without a key.</summary>
/// <param name="EnvironmentUrl">The variable that may name another address (OLLAMA_HOST...); null for none.</param>
/// <param name="EnvironmentKey">The variable that may hold the key (OPENROUTER_API_KEY); null when it needs none.</param>
public sealed record ModelProvider(string Id, string Name, string Description, string Url, string? EnvironmentUrl, string? EnvironmentKey,
    string MissingHint, string? InstallCommand)
{
    public bool NeedsKey => EnvironmentKey is not null;

    /// <summary>The vault's entry for its key.</summary>
    public string KeyName => "karvon-agents/" + Id;

    public static readonly ModelProvider OpenRouter = new("openrouter", "OpenRouter", "Any model (GPT, Gemini, Qwen, DeepSeek, Kimi, GLM...) on your OpenRouter key",
        "https://openrouter.ai/api/v1", null, "OPENROUTER_API_KEY",
        "Paste your OpenRouter API key (openrouter.ai/keys): Karvon keeps it in its vault and sends it only to OpenRouter.", null);

    public static readonly ModelProvider Ollama = new("ollama", "Ollama", "A local model in Ollama, on this computer",
        "http://localhost:11434/v1", "OLLAMA_HOST", null,
        "Install Ollama and pull a model that calls tools, then retry.", "ollama pull qwen3-coder");

    public static readonly ModelProvider LmStudio = new("lmstudio", "LM Studio", "A local model in LM Studio's server, on this computer",
        "http://localhost:1234/v1", null, null,
        "Start LM Studio's local server with a model loaded, then retry.", null);

    /// <summary>The endpoint's address: the variable's when set (Ollama's OLLAMA_HOST has no /v1), else the usual one.</summary>
    public string Address()
    {
        if (EnvironmentUrl is not null && Environment.GetEnvironmentVariable(EnvironmentUrl) is { Length: > 0 } host)
        {
            var url = host.Contains("://", StringComparison.Ordinal) ? host : "http://" + host;
            return url.TrimEnd('/') + (url.TrimEnd('/').EndsWith("/v1", StringComparison.Ordinal) ? "" : "/v1");
        }
        return Url;
    }
}

/// <summary>
/// A model Karvon drives itself (<see cref="ModelSession"/>) in the panel, from one provider: OpenRouter on the user's key,
/// or a local Ollama or LM Studio. The key is kept in Karvon's vault (<paramref name="keyring"/>), or comes from the
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

    public async Task<IAgentSession> StartAsync(AgentStartOptions options, CancellationToken cancellationToken = default) =>
        await ModelSession.StartAsync(new ChatCompletions(http ?? ModelSession.Client, options.Executable, Key()), provider.Name, options, cancellationToken);
}
