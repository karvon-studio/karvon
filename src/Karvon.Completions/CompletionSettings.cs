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
    /// <summary>The fastest and cheapest: a suggestion is asked for at every pause in typing.</summary>
    public const string DefaultClaudeModel = "claude-haiku-5-5";
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

    /// <summary>Off until the user turns them on: they send the code around the caret to the provider, on the user's key.</summary>
    public bool Enabled { get; set; }

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
/// the ANTHROPIC_API_KEY the environment has. Never in a file. The keyring is read once it answers (a locked one is
/// asked again a while later), off the window's thread (<see cref="ReadAsync"/>): it may wait, or ask the user.
/// </summary>
public sealed class CompletionSecrets(ICredentialStore? keyring, Func<string?>? environment = null)
{
    const string KeyName = "karvon-completions/anthropic";

    /// <summary>How long a keyring that did not answer (locked, timed out) is left alone.</summary>
    static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(30);

    readonly Func<string?> _environment = environment ?? (() => Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"));
    readonly Lock _lock = new();
    string? _session;
    bool _read;
    DateTime _failedAt = DateTime.MinValue;

    public bool HasKeyring => keyring is not null;

    /// <summary>The keyring was read (or there is none): <see cref="ApiKey"/> asks it nothing more.</summary>
    public bool IsRead => keyring is null || _read;

    /// <summary>A key Karvon keeps (the keyring's, or this session's), not the environment's.</summary>
    public bool HasOwnKey => Own() is not null;

    public bool HasEnvironmentKey => _environment() is { Length: > 0 };

    public string? ApiKey => Own() ?? EnvironmentKey();

    /// <summary>The key as far as it is known without asking the keyring: this session's (or the keyring's, read), else the environment's.</summary>
    public string? KnownKey => _session ?? EnvironmentKey();

    string? EnvironmentKey() => _environment() is { Length: > 0 } key ? key : null;

    /// <summary>The keyring read on a thread of its own.</summary>
    public Task ReadAsync() => IsRead ? Task.CompletedTask : Task.Run(Own);

    string? Own()
    {
        lock (_lock)
        {
            if (!_read && keyring is not null && DateTime.UtcNow - _failedAt > RetryAfter)
            {
                try
                {
                    _session ??= keyring.Get(KeyName) is { Length: > 0 } kept ? kept : null;
                    _read = true;
                }
                // Locked, timed out, not there (D-Bus, Keychain): no key from it now, asked again later.
                catch (Exception)
                {
                    _failedAt = DateTime.UtcNow;
                }
            }
            return _session;
        }
    }

    /// <summary>The key for this session, and kept in the keyring (or no longer there): whether the keyring did as asked.</summary>
    public bool Set(string key, bool keep)
    {
        lock (_lock)
        {
            _session = key;
            _read = true;
        }
        try
        {
            if (keep)
                return keyring?.Set(KeyName, "Karvon: Anthropic API key (inline completions)", key) == true;
            keyring?.Delete(KeyName);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The key gone, from the keyring too: whether the keyring did as asked.</summary>
    public bool Forget()
    {
        lock (_lock)
        {
            _session = null;
            _read = true;
        }
        try
        {
            keyring?.Delete(KeyName);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
