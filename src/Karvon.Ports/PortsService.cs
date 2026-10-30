using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bengine.Introspection.Servers;

namespace Karvon.Ports;

/// <summary>
/// The tunnel service ports are forwarded through (ports.json in Karvon's settings, the user's alone): a jprq server that
/// multiplexes a tunnel over one connection (as tulki.uz's does), named by its config page (https://tulki.uz/config.json,
/// which gives its domain and event port) or by its event port itself (host:4321). Its API key is never in the file.
/// </summary>
public sealed class PortsSettings(string file)
{
    public const string DefaultService = "https://tulki.uz/config.json";

    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    sealed class Data
    {
        public string? Service { get; set; }
    }

    public string File => file;

    public string Service
    {
        get
        {
            try
            {
                return System.IO.File.Exists(file) && JsonSerializer.Deserialize<Data>(System.IO.File.ReadAllText(file), Options)?.Service is { Length: > 0 } service
                    ? service
                    : DefaultService;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return DefaultService;
            }
        }
        set
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            System.IO.File.WriteAllText(file, JsonSerializer.Serialize(new Data { Service = value.Trim() }, Options));
        }
    }

    /// <summary>The service's name as the user knows it: its config page's host ("tulki.uz"), or the event port's host.</summary>
    public static string NameOf(string service) =>
        Uri.TryCreate(service, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.Host : service.Split(':')[0];

    /// <summary>Where the service's site is (it says how to get a key): its config page's origin; none for a bare event port.</summary>
    public static string? SiteOf(string service) =>
        Uri.TryCreate(service, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.GetLeftPart(UriPartial.Authority) + "/" : null;
}

/// <summary>A jprq server's event port, and the domain its HTTP tunnels are under.</summary>
public sealed record JprqEndpoint(string Domain, string Host, int Port)
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    sealed record Config([property: JsonPropertyName("domain")] string? Domain, [property: JsonPropertyName("events")] string? Events);

    /// <summary>The service's endpoint: its config page read (as jprq's client reads it), or host:port as given.</summary>
    public static async Task<JprqEndpoint> ResolveAsync(string service, CancellationToken ct)
    {
        if (Uri.TryCreate(service, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            Config? config;
            try
            {
                config = await Http.GetFromJsonAsync<Config>(uri, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new IOException($"{uri.Host} does not answer ({uri}): {ex.Message}");
            }
            if (config?.Events is not { Length: > 0 } events || Parse(events) is not { } at)
                throw new IOException($"{uri} does not say the service's event port.");
            return new JprqEndpoint(config.Domain ?? at.Host, at.Host, at.Port);
        }
        return Parse(service) is { } endpoint
            ? new JprqEndpoint(endpoint.Host, endpoint.Host, endpoint.Port)
            : throw new IOException($"\"{service}\" is not a tunnel service: give its config page (https://...) or its event port (host:4321).");
    }

    static (string Host, int Port)? Parse(string hostPort)
    {
        var colon = hostPort.LastIndexOf(':');
        return colon > 0 && int.TryParse(hostPort[(colon + 1)..], out var port) && port is > 0 and < 65536
            ? (hostPort[..colon].Trim('[', ']'), port)
            : null;
    }
}

/// <summary>
/// The services' API keys: in the system keyring when the user keeps them there, else for this session only (asked
/// again the next time Karvon Studio starts). Never in a file, never in what Claude reads.
/// </summary>
public sealed class PortsSecrets(ICredentialStore? keyring)
{
    readonly Dictionary<string, string> _session = new(StringComparer.Ordinal);

    public bool HasKeyring => keyring is not null;

    static string Key(string service) => "karvon-ports/" + PortsSettings.NameOf(service);

    public string? Get(string service)
    {
        lock (_session)
            if (_session.TryGetValue(service, out var key))
                return key;
        if (keyring?.Get(Key(service)) is { Length: > 0 } kept)
        {
            lock (_session)
                _session[service] = kept;
            return kept;
        }
        return null;
    }

    public void Set(string service, string key, bool keep)
    {
        lock (_session)
            _session[service] = key;
        Karvon.Claude.Tools.KnownSecrets.Add(Key(service), key);
        if (keep)
            keyring?.Set(Key(service), $"Karvon: tunnel service {PortsSettings.NameOf(service)}", key);
        else
            keyring?.Delete(Key(service));
    }

    public void Forget(string service)
    {
        lock (_session)
            _session.Remove(service);
        keyring?.Delete(Key(service));
    }
}
