using System.Globalization;
using System.Text.RegularExpressions;

namespace Karvon.Postman;

/// <summary>
/// The variables a request is sent with, as Postman finds them: its own (a script's, or given for this call), the run's data,
/// the environment's, the collection's, the globals; and the dynamic ones ($guid, $timestamp...). What scripts change is
/// put where it belongs once the request is done (<see cref="Apply"/>).
/// </summary>
public sealed partial class PostmanScope
{
    public const string Local = "variables", Data = "iterationData", EnvironmentName = "environment", CollectionName = "collectionVariables", GlobalsName = "globals";

    /// <summary>A variable of a variable of a variable, no deeper.</summary>
    const int MaxDepth = 10;

    readonly PostmanCollection _collection;
    readonly PostmanEnvironment? _environment, _globals;
    readonly Dictionary<string, Dictionary<string, object?>> _scopes = new(StringComparer.Ordinal);

    /// <summary>What was set (or unset) in the collection, the environment and the globals since they were read.</summary>
    readonly Dictionary<(string Scope, string Key), (bool Set, object? Value)> _changes = [];

    public PostmanScope(PostmanCollection collection, PostmanEnvironment? environment, PostmanEnvironment? globals,
        IReadOnlyDictionary<string, string>? local = null, IReadOnlyDictionary<string, string>? data = null)
    {
        _collection = collection;
        _environment = environment;
        _globals = globals;
        _scopes[Local] = Copy(local);
        _scopes[Data] = Copy(data);
        _scopes[EnvironmentName] = Values(environment);
        _scopes[GlobalsName] = Values(globals);
        var own = collection.Variables.Where(v => v.Enabled).GroupBy(v => v.Key).ToDictionary(g => g.Key, g => (object?)g.Last().Value, StringComparer.Ordinal);
        foreach (var (key, value) in collection.Current)
            own[key] = value;
        _scopes[CollectionName] = own;
    }

    static Dictionary<string, object?> Copy(IReadOnlyDictionary<string, string>? values) =>
        values?.ToDictionary(v => v.Key, v => (object?)v.Value, StringComparer.Ordinal) ?? new(StringComparer.Ordinal);

    static Dictionary<string, object?> Values(PostmanEnvironment? environment)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var variable in environment?.Variables.Where(v => v.Enabled) ?? [])
            values[variable.Key] = environment!.Current.TryGetValue(variable.Key, out var current) ? current : variable.Initial;
        return values;
    }

    public string? EnvironmentTitle => _environment?.Name;

    /// <summary>A value as it is put in a text.</summary>
    public static string Text(object? value) => value switch
    {
        null => "",
        string text => text,
        bool flag => flag ? "true" : "false",
        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    public bool TryGet(string scope, string key, out object? value)
    {
        // pm.variables is all of them, the nearest first.
        foreach (var name in scope == Local ? [Local, Data, EnvironmentName, CollectionName, GlobalsName] : new[] { scope })
            if (_scopes[name].TryGetValue(key, out value))
                return true;
        value = null;
        return false;
    }

    public void Set(string scope, string key, object? value)
    {
        _scopes[scope][key] = value;
        _changes[(scope, key)] = (true, value);
    }

    public void Unset(string scope, string key)
    {
        _scopes[scope].Remove(key);
        _changes[(scope, key)] = (false, null);
    }

    public IReadOnlyDictionary<string, object?> All(string scope)
    {
        if (scope != Local)
            return _scopes[scope];
        var all = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var name in new[] { GlobalsName, CollectionName, EnvironmentName, Data, Local })
            foreach (var (key, value) in _scopes[name])
                all[key] = value;
        return all;
    }

    /// <summary>What scripts changed, put in the environment, the collection and the globals (on the window's thread).</summary>
    public void Apply()
    {
        foreach (var ((scope, key), (set, value)) in _changes)
            switch (scope)
            {
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

    /// <summary>A text with its {{variables}} put in; one that has no value stays as it is, its name added to <paramref name="missing"/>.</summary>
    public string Resolve(string text, ICollection<string>? missing = null) => Resolve(text, missing, 0);

    string Resolve(string text, ICollection<string>? missing, int depth) => depth > MaxDepth || !text.Contains("{{", StringComparison.Ordinal) ? text
        : Reference().Replace(text, m =>
        {
            var name = m.Groups[1].Value.Trim();
            if (TryGet(Local, name, out var value))
                return Resolve(Text(value), missing, depth + 1);
            if (name.StartsWith('$') && Dynamic(name[1..]) is { } made)
                return made;
            if (missing?.Contains(name) == false)
                missing.Add(name);
            return m.Value;
        });

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
