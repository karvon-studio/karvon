using System.Text.Json;
using System.Text.Json.Serialization;
using Bengine.Introspection.Servers;

namespace Karvon.Completions;

/// <summary>Where suggestions come from.</summary>
public enum CompletionProvider
{
    /// <summary>Claude, through Anthropic's API (an API key).</summary>
    Claude,

    /// <summary>A local model through Ollama (a fill-in-the-middle one: Qwen2.5-Coder...).</summary>
    Ollama,
}

/// <summary>
/// Inline completions' settings, in completions.json beside Karvon's: whether they are on, where they come from and the
/// model. Never the API key (<see cref="CompletionSecrets"/>).
/// </summary>
public sealed class CompletionSettings
{
    public const string DefaultClaudeModel = "claude-opus-5-5";
    public const string DefaultOllamaUrl = "http://localhost:11434";
    public const string DefaultOllamaModel = "qwen2.5-coder:1.5b";

    /// <summary>The Claude models offered, the fastest last; another can be typed.</summary>
    public static IReadOnlyList<string> ClaudeModels { get; } = ["claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5"];

    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public bool Enabled { get; set; } = true;

    public CompletionProvider Provider { get; set; } = CompletionProvider.Claude;

    public string ClaudeModel { get; set; } = DefaultClaudeModel;

    public string OllamaUrl { get; set; } = DefaultOllamaUrl;

    public string OllamaModel { get; set; } = DefaultOllamaModel;

    /// <summary>The model suggestions come from now.</summary>
    [JsonIgnore]
    public string Model => Provider == CompletionProvider.Claude ? ClaudeModel : OllamaModel;

    public static CompletionSettings Load(string file)
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<CompletionSettings>(File.ReadAllText(file), Options) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    public void Save(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(this, Options));
    }
}

/// <summary>
/// Anthropic's API key: in the system keyring when the user keeps it there, else for this session only; without either,
/// the ANTHROPIC_API_KEY the environment has. Never in a file.
/// </summary>
public sealed class CompletionSecrets(ICredentialStore? keyring, Func<string?>? environment = null)
{
    const string KeyName = "karvon-completions/anthropic";

    readonly Func<string?> _environment = environment ?? (() => Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"));
    string? _session;
    bool _read;

    public bool HasKeyring => keyring is not null;

    /// <summary>A key Karvon keeps (the keyring's, or this session's), not the environment's.</summary>
    public bool HasOwnKey => Own() is not null;

    public bool HasEnvironmentKey => _environment() is { Length: > 0 };

    public string? ApiKey => Own() ?? (_environment() is { Length: > 0 } key ? key : null);

    string? Own()
    {
        if (!_read)
        {
            _read = true;
            try
            {
                _session ??= keyring?.Get(KeyName) is { Length: > 0 } kept ? kept : null;
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
            {
            }
        }
        return _session;
    }

    public void Set(string key, bool keep)
    {
        _session = key;
        _read = true;
        if (keep)
            keyring?.Set(KeyName, "Karvon: Anthropic API key (inline completions)", key);
        else
            keyring?.Delete(KeyName);
    }

    public void Forget()
    {
        _session = null;
        _read = true;
        keyring?.Delete(KeyName);
    }
}
