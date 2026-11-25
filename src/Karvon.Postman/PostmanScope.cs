using System.Globalization;
using System.Text.RegularExpressions;

namespace Karvon.Postman;

/// <summary>
/// What agents set this session where a script sets a variable (pm.environment.set): theirs alone, over the user's, which
/// they neither see nor change; and their cookies.
/// </summary>
public sealed class PostmanAgentLayer
{
    readonly Dictionary<(string Owner, string Scope), Dictionary<string, object?>> _values = [];

    /// <summary>What was set in one place (an environment's, a collection's), null for a variable taken away.</summary>
    internal Dictionary<string, object?> Of(string owner, string scope)
    {
        if (!_values.TryGetValue((owner, scope), out var values))
            _values[(owner, scope)] = values = new(StringComparer.Ordinal);
        return values;
    }

    public PostmanCookies Cookies { get; } = new(null);
}

/// <summary>
/// The variables a request is sent with, as Postman finds them: its own (a script's, or given for this call), the run's data,
/// the environment's, the collection's, the globals; and the dynamic ones ($guid, $timestamp...). What scripts change is
/// put where it belongs once the request is done (<see cref="Apply"/>).
/// In an agent's call the environment's, the globals' and what the user's scripts set in the collection are held: a script
/// gets a variable's name ({{token}}) for its value, which is put in only as the request goes out.
/// </summary>
public sealed partial class PostmanScope
{
    public const string Local = "variables", Data = "iterationData", EnvironmentName = "environment", CollectionName = "collectionVariables", GlobalsName = "globals";

    /// <summary>A variable of a variable of a variable, no deeper.</summary>
    const int MaxDepth = 10;

    readonly PostmanCollection _collection;
    readonly PostmanEnvironment? _environment, _globals;
    readonly PostmanAgentLayer? _agent;
    readonly Dictionary<string, Dictionary<string, object?>> _scopes = new(StringComparer.Ordinal);

    /// <summary>The user's values in an agent's call, by their scope: what its scripts do not see.</summary>
    readonly Dictionary<string, Dictionary<string, object?>> _held = new(StringComparer.Ordinal);

    /// <summary>What was set (or unset) in the collection, the environment and the globals since they were read.</summary>
    readonly Dictionary<(string Scope, string Key), (bool Set, object? Value)> _changes = [];

    readonly Dictionary<string, (string Scope, string Value)> _used = new(StringComparer.Ordinal);

    public PostmanScope(PostmanCollection collection, PostmanEnvironment? environment, PostmanEnvironment? globals,
        IReadOnlyDictionary<string, string>? local = null, IReadOnlyDictionary<string, object?>? data = null, PostmanAgentLayer? agent = null)
    {
        (_collection, _environment, _globals, _agent) = (collection, environment, globals, agent);
        _scopes[Local] = local?.ToDictionary(v => v.Key, v => (object?)v.Value, StringComparer.Ordinal) ?? new(StringComparer.Ordinal);
        _scopes[Data] = data?.ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal) ?? new(StringComparer.Ordinal);
        var own = collection.Variables.Where(v => v.Enabled).GroupBy(v => v.Key).ToDictionary(g => g.Key, g => (object?)g.Last().Value, StringComparer.Ordinal);
        if (agent is null)
        {
            _scopes[EnvironmentName] = Values(environment);
            _scopes[GlobalsName] = Values(globals);
            foreach (var (key, value) in collection.Current)
                own[key] = value;
            _scopes[CollectionName] = own;
            return;
        }
        _held[EnvironmentName] = Values(environment);
        _held[GlobalsName] = Values(globals);
        _held[CollectionName] = new(collection.Current, StringComparer.Ordinal);
        _scopes[EnvironmentName] = new(StringComparer.Ordinal);
        _scopes[GlobalsName] = new(StringComparer.Ordinal);
        _scopes[CollectionName] = own;
        foreach (var scope in new[] { EnvironmentName, GlobalsName, CollectionName })
        {
            // What an agent sees of the user's is its name.
            foreach (var key in _held[scope].Keys)
                _scopes[scope][key] = Named(key);
            foreach (var (key, value) in agent.Of(Owner(scope), scope))
            {
                _held[scope].Remove(key);
                if (value is null)
                    _scopes[scope].Remove(key);
                else
                    _scopes[scope][key] = value;
            }
        }
    }

    string Owner(string scope) => scope switch { EnvironmentName => _environment?.Id ?? "", GlobalsName => _globals?.Id ?? "", _ => _collection.File };

    static string Named(string key) => "{{" + key + "}}";

    static Dictionary<string, object?> Values(PostmanEnvironment? environment)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var variable in environment?.Variables.Where(v => v.Enabled) ?? [])
            values[variable.Key] = environment!.Current.TryGetValue(variable.Key, out var current) ? current : variable.Initial;
        return values;
    }

    /// <summary>An agent's call: its scripts see names for the user's values.</summary>
    public bool IsAgents => _agent is not null;

    public string? EnvironmentTitle => _environment?.Name;

    public PostmanEnvironment? Environment => _environment;

    /// <summary>The user's values that were read or put in so far, by their names (the environment's, the globals', a sign-in's token): where they go is asked.</summary>
    public IReadOnlyDictionary<string, (string Scope, string Value)> Used => _used;

    /// <summary>A value that goes into the request and is the user's as the environment's are (an access token, a cookie a script read).</summary>
    public void Use(string name, string value) => _used[name] = (EnvironmentName, value);

    /// <summary>The user's, read or put in by the user's own call: the environment's, the globals', what scripts set in the collection.</summary>
    void Note(string scope, string key, object? value)
    {
        if (_agent is null && (scope is EnvironmentName or GlobalsName || (scope == CollectionName && _collection.Current.ContainsKey(key))))
            _used[key] = (scope, Text(value));
    }

    /// <summary>A value as it is put in a text.</summary>
    public static string Text(object? value) => value switch
    {
        null => "",
        string text => text,
        bool flag => flag ? "true" : "false",
        System.Text.Json.Nodes.JsonNode node => PostmanJson.Text(node) ?? node.ToJsonString(),
        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    static readonly string[] Nearest = [Local, Data, EnvironmentName, CollectionName, GlobalsName];

    /// <summary>A variable's value as a script may have it.</summary>
    public bool TryGet(string scope, string key, out object? value)
    {
        // pm.variables is all of them, the nearest first.
        foreach (var name in scope == Local ? Nearest : [scope])
            if (_scopes[name].TryGetValue(key, out value))
            {
                Note(name, key, value);
                return true;
            }
        value = null;
        return false;
    }

    public void Set(string scope, string key, object? value)
    {
        _scopes[scope][key] = value;
        if (_held.TryGetValue(scope, out var held))
            held.Remove(key);
        _changes[(scope, key)] = (true, value);
    }

    public void Unset(string scope, string key)
    {
        _scopes[scope].Remove(key);
        if (_held.TryGetValue(scope, out var held))
            held.Remove(key);
        _changes[(scope, key)] = (false, null);
    }

    /// <summary>All of a scope, as a script may have it.</summary>
    public IReadOnlyDictionary<string, object?> All(string scope)
    {
        foreach (var name in scope == Local ? Nearest : [scope])
            foreach (var (key, value) in _scopes[name])
                Note(name, key, value);
        if (scope != Local)
            return _scopes[scope];
        var all = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var name in Nearest.Reverse())
            foreach (var (key, value) in _scopes[name])
                all[key] = value;
        return all;
    }

    /// <summary>What scripts changed, put in the environment, the collection and the globals (on the window's thread); an agent's, in its own layer.</summary>
    public void Apply()
    {
        foreach (var ((scope, key), (set, value)) in _changes)
            switch (scope)
            {
                case EnvironmentName or GlobalsName or CollectionName when _agent is not null:
                    _agent.Of(Owner(scope), scope)[key] = set ? value ?? "" : null;
                    break;
                case EnvironmentName when set:
                    _environment?.Set(key, value);
                    break;
                case EnvironmentName:
                    _environment?.Unset(key);
                    break;
                case GlobalsName when set:
                    _globals?.Set(key, value);
                    break;
                case GlobalsName:
                    _globals?.Unset(key);
                    break;
                case CollectionName when set:
                    _collection.Current[key] = value;
                    break;
                case CollectionName:
                    _collection.Current.Remove(key);
                    break;
            }
        _changes.Clear();
    }

    // ---- Texts ------------------------------------------------------------------------------------------------

    /// <summary>A text with its {{variables}} put in, as a script may have it; one that has no value stays as it is, its name added to <paramref name="missing"/>.</summary>
    public string Resolve(string text, ICollection<string>? missing = null) => Resolve(text, missing, 0, held: false);

    /// <summary>The same as the request goes out: the user's values too, each noted as used.</summary>
    public string Put(string text, ICollection<string>? missing = null) => Resolve(text, missing, 0, held: true);

    string Resolve(string text, ICollection<string>? missing, int depth, bool held) => depth > MaxDepth || !text.Contains("{{", StringComparison.Ordinal) ? text
        : Reference().Replace(text, m =>
        {
            var name = m.Groups[1].Value.Trim();
            foreach (var scope in Nearest)
            {
                if (_held.TryGetValue(scope, out var theirs) && theirs.TryGetValue(name, out var kept))
                {
                    if (!held)
                        return m.Value;
                    _used[name] = (scope, Text(kept));
                    return Resolve(Text(kept), missing, depth + 1, held);
                }
                if (_scopes[scope].TryGetValue(name, out var value))
                {
                    Note(scope, name, value);
                    return Resolve(Text(value), missing, depth + 1, held);
                }
            }
            if (name.StartsWith('$') && Dynamic(name[1..]) is { } made)
                return made;
            if (missing?.Contains(name) == false)
                missing.Add(name);
            return m.Value;
        });

    /// <summary>The user's variables a text names, in an agent's call.</summary>
    public IEnumerable<string> HeldIn(string text) => Reference().Matches(text).Select(m => m.Groups[1].Value.Trim())
        .Where(name => Nearest.Select(s => _held.TryGetValue(s, out var theirs) && theirs.ContainsKey(name) ? true : _scopes[s].ContainsKey(name) ? false : (bool?)null).FirstOrDefault(f => f is not null) == true);

    [GeneratedRegex(@"\{\{([^{}]+)\}\}")]
    private static partial Regex Reference();

    static readonly string[] FirstNames = ["Alice", "Bruno", "Chen", "Dilnoza", "Emma", "Farhod", "Grace", "Hugo", "Ines", "Jamshid"];
    static readonly string[] LastNames = ["Adams", "Baker", "Carter", "Diaz", "Evans", "Fischer", "Garcia", "Hill", "Ito", "Karimov"];
    static readonly string[] Nouns = ["alarm", "bus", "card", "driver", "feed", "matrix", "panel", "port", "sensor", "system"];
    static readonly string[] Cities = ["Tashkent", "Samarkand", "Berlin", "Lisbon", "Oslo", "Porto", "Quito", "Riga", "Seoul", "Turin"];
    static readonly string[] Colors = ["red", "green", "blue", "orange", "purple", "teal", "white", "black"];

    static string Pick(string[] values) => values[Random.Shared.Next(values.Length)];

    static string Letters(int length) => new([.. Enumerable.Range(0, length).Select(_ => (char)('a' + Random.Shared.Next(26)))]);

    /// <summary>Postman's dynamic variables, those most used; null for one Karvon does not make.</summary>
    static string? Dynamic(string name) => name switch
    {
        "guid" or "randomUUID" => Guid.NewGuid().ToString(),
        "timestamp" => DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
        "isoTimestamp" => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
        "randomInt" => Random.Shared.Next(0, 1001).ToString(CultureInfo.InvariantCulture),
        "randomBoolean" => Random.Shared.Next(2) == 0 ? "false" : "true",
        "randomAlphaNumeric" => "0123456789abcdefghijklmnopqrstuvwxyz"[Random.Shared.Next(36)].ToString(),
        "randomHexColor" => $"#{Random.Shared.Next(0x1000000):x6}",
        "randomColor" => Pick(Colors),
        "randomFirstName" => Pick(FirstNames),
        "randomLastName" => Pick(LastNames),
        "randomFullName" => $"{Pick(FirstNames)} {Pick(LastNames)}",
        "randomUserName" => $"{Pick(FirstNames).ToLowerInvariant()}{Random.Shared.Next(10, 100)}",
        "randomEmail" or "randomExampleEmail" => $"{Pick(FirstNames).ToLowerInvariant()}.{Pick(LastNames).ToLowerInvariant()}{Random.Shared.Next(100)}@example.com",
        "randomPassword" => Letters(10) + Random.Shared.Next(10, 100),
        "randomWord" or "randomNoun" => Pick(Nouns),
        "randomWords" => $"{Pick(Nouns)} {Pick(Nouns)} {Pick(Nouns)}",
        "randomCity" => Pick(Cities),
        "randomCountryCode" => Pick(["UZ", "DE", "PT", "NO", "EC", "LV", "KR", "IT"]),
        "randomPhoneNumber" => $"{Random.Shared.Next(200, 1000)}-{Random.Shared.Next(200, 1000)}-{Random.Shared.Next(1000, 10000)}",
        "randomIP" => $"192.0.2.{Random.Shared.Next(1, 255)}",
        "randomUrl" => $"https://{Letters(8)}.example.com",
        "randomDomainName" => $"{Letters(8)}.example.com",
        "randomPrice" => (Random.Shared.Next(0, 100000) / 100.0).ToString("0.00", CultureInfo.InvariantCulture),
        "randomDateFuture" => DateTime.UtcNow.AddDays(Random.Shared.Next(1, 365)).ToString("O", CultureInfo.InvariantCulture),
        "randomDatePast" => DateTime.UtcNow.AddDays(-Random.Shared.Next(1, 365)).ToString("O", CultureInfo.InvariantCulture),
        "randomDateRecent" => DateTime.UtcNow.AddMinutes(-Random.Shared.Next(1, 1440)).ToString("O", CultureInfo.InvariantCulture),
        _ => null,
    };
}
