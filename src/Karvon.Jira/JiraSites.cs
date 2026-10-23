using System.Text.Json;
using System.Text.Json.Serialization;
using Bengine.Introspection.Servers;

namespace Karvon.Jira;

/// <summary>
/// The Jira sites added in Karvon (jira.json in Karvon's settings folder, the user's alone): what is not secret. A site's
/// token or password is never in it.
/// </summary>
public sealed class JiraSiteList(string file)
{
    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string File => file;

    public IReadOnlyList<JiraSite> Sites
    {
        get
        {
            try
            {
                return System.IO.File.Exists(file) ? JsonSerializer.Deserialize<List<JiraSite>>(System.IO.File.ReadAllText(file), Options) ?? [] : [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return [];
            }
        }
    }

    /// <summary>A site added, or changed (by its id).</summary>
    public void Save(JiraSite site) => Write([.. Sites.Where(s => s.Id != site.Id), site]);

    public void Remove(string id) => Write([.. Sites.Where(s => s.Id != id)]);

    void Write(List<JiraSite> sites)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        System.IO.File.WriteAllText(file, JsonSerializer.Serialize(sites, Options));
        if (!OperatingSystem.IsWindows())
            System.IO.File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}

/// <summary>
/// The sites' tokens and passwords: in the system keyring when the user keeps them there, else for this session only
/// (asked again the next time Karvon Studio starts). Never in a file, never in what Claude reads.
/// </summary>
public sealed class JiraSecrets(ICredentialStore? keyring)
{
    readonly Dictionary<string, string> _session = new(StringComparer.Ordinal);

    public bool HasKeyring => keyring is not null;

    static string Key(string site) => "karvon-jira/" + site;

    public string? Get(string site)
    {
        lock (_session)
            if (_session.TryGetValue(site, out var secret))
                return secret;
        if (keyring?.Get(Key(site)) is { Length: > 0 } kept)
        {
            lock (_session)
                _session[site] = kept;
            return kept;
        }
        return null;
    }

    public void Set(JiraSite site, string secret, bool keep)
    {
        lock (_session)
            _session[site.Id] = secret;
        if (keep)
            keyring?.Set(Key(site.Id), $"Karvon: Jira {site.Name} ({site.Url})", secret);
        else
            keyring?.Delete(Key(site.Id));
    }

    public void Forget(string site)
    {
        lock (_session)
            _session.Remove(site);
        keyring?.Delete(Key(site));
    }
}
