using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Karvon.Claude;
using Karvon.Claude.Tools;
using Karvon.Services;

namespace Karvon.Postman;

/// <summary>A variable of an environment: its name, the value its file has (none when it is Karvon's), the one Karvon keeps, and whether it is a secret.</summary>
public sealed record PostmanVariable(string Key, string Initial)
{
    /// <summary>The value Karvon keeps (typed here, or set by a script); null for the file's.</summary>
    public string? Current { get; init; }

    public bool Enabled { get; init; } = true;

    public bool IsSecret { get; init; }

    public string Value => Current ?? Initial;

    /// <summary>A secret by its type, or by its name.</summary>
    public bool HoldsSecret => IsSecret || PostmanCollection.IsSecretName(Key);
}

/// <summary>
/// A Postman environment (or the globals) in its file. Karvon keeps its values in the vault's sealed part (without a
/// passphrase, for this session), never in the file: no agent gets them, nor does a program an agent runs. A file that
/// came with values keeps them until the user moves them (<see cref="MoveToVaultAsync"/>).
/// </summary>
public sealed class PostmanEnvironment
{
    public const string Extension = ".postman_environment.json", GlobalsExtension = ".postman_globals.json";

    readonly SealedStore _store;
    Dictionary<string, object?>? _current;
    bool _agents;
    HashSet<string> _hosts = new(StringComparer.OrdinalIgnoreCase);

    PostmanEnvironment(string file, JsonObject root, SealedStore store)
    {
        File = file;
        Root = root;
        _store = store;
        // Whatever its name is, no agent reads it.
        SealedFiles.Add(file);
    }

    public string File { get; }

    public JsonObject Root { get; private set; }

    public DateTime Stamp { get; private set; }

    public string Id => Root.Text("id") ?? File;

    public string Name
    {
        get => Root.Text("name") ?? Path.GetFileName(File);
        set => Root["name"] = value;
    }

    public bool IsGlobals => Root.Text("_postman_variable_scope") == "globals" || File.EndsWith(GlobalsExtension, StringComparison.OrdinalIgnoreCase);

    public static bool Is(JsonObject root) => root["values"] is JsonArray && root["info"] is null;

    public static PostmanEnvironment Load(string file, SealedStore store)
    {
        var root = PostmanJson.Read(file);
        if (!Is(root))
            throw new PostmanException($"{Path.GetFileName(file)} is not a Postman environment.");
        var environment = new PostmanEnvironment(file, root, store) { Stamp = System.IO.File.GetLastWriteTimeUtc(file) };
        environment.HoldSecrets();
        return environment;
    }

    public static PostmanEnvironment Create(string file, string name, SealedStore store)
    {
        var environment = new PostmanEnvironment(file, new JsonObject
        {
            ["id"] = Guid.NewGuid().ToString(),
            ["name"] = name,
            ["values"] = new JsonArray(),
            ["_postman_variable_scope"] = "environment",
        }, store);
        environment.Save();
        return environment;
    }

    public bool IsStale => System.IO.File.Exists(File) && System.IO.File.GetLastWriteTimeUtc(File) != Stamp;

    public void Save()
    {
        PostmanJson.Write(File, Root);
        Stamp = System.IO.File.GetLastWriteTimeUtc(File);
    }

    public IReadOnlyList<PostmanVariable> Variables
    {
        get
        {
            var current = Current;
            var rows = Root.Objects("values").Select(v => new PostmanVariable(v.Text("key") ?? "", v.Text("value") ?? "")
            {
                Enabled = v["enabled"] is null || v.Flag("enabled"),
                IsSecret = v.Text("type") == "secret",
                Current = current.TryGetValue(v.Text("key") ?? "", out var value) ? PostmanScope.Text(value) : null,
            }).ToList();
            // What scripts set and the file has not.
            return [.. rows, .. current.Where(c => rows.All(r => r.Key != c.Key)).Select(c => new PostmanVariable(c.Key, "") { Current = PostmanScope.Text(c.Value) })];
        }
    }

    /// <summary>The rows as edited: the file gets their names, Karvon their values (a row's value in the file stays what it is).</summary>
    public void Put(string name, IReadOnlyList<PostmanVariable> variables)
    {
        var values = new JsonArray();
        foreach (var variable in variables)
        {
            // A row keeps what Karvon does not show of it.
            var row = Root.Objects("values").FirstOrDefault(v => v.Text("key") == variable.Key)?.DeepClone() as JsonObject ?? new JsonObject { ["key"] = variable.Key, ["value"] = "" };
            row["type"] = variable.IsSecret ? "secret" : row.Text("type") is { } type && type != "secret" ? type : "default";
            row["enabled"] = variable.Enabled;
            values.Add(row);
        }
        if (Name != name || !JsonNode.DeepEquals(values, Root["values"]))
        {
            Name = name;
            Root["values"] = values;
            Save();
        }
        _current = variables.Where(v => v.Current is not null).ToDictionary(v => v.Key, v => (object?)v.Current, StringComparer.Ordinal);
        Keep();
    }

    /// <summary>A name added without a value (an agent's doing: the user gives it one).</summary>
    public bool AddName(string key, bool secret)
    {
        if (Variables.Any(v => v.Key == key))
            return false;
        Root.List("values").Add(new JsonObject { ["key"] = key, ["value"] = "", ["type"] = secret ? "secret" : "default", ["enabled"] = true });
        Save();
        return true;
    }

    // ---- The file's values ------------------------------------------------------------------------------------

    /// <summary>Its file has values in it, where any program reads them.</summary>
    public bool HasFileValues => Root.Objects("values").Any(v => v.Text("value") is { Length: > 0 });

    /// <summary>The file's values moved to the vault: the file keeps the names. Only where they are kept (<see cref="SealedStore.CanKeep"/>).</summary>
    public async Task MoveToVaultAsync()
    {
        if (!_store.CanKeep)
            throw new PostmanException("The vault has no passphrase, or is locked: the values stay where they are.");
        foreach (var row in Root.Objects("values"))
            if (row.Text("value") is { Length: > 0 } value && row.Text("key") is { } key)
                Own.TryAdd(key, value);
        Keep();
        await _store.Written;
        if (_store.Kept(StoreKey) is null)
            throw new PostmanException("The vault did not keep the values: they stay where they are.");
        foreach (var row in Root.Objects("values"))
            row["value"] = "";
        Save();
    }

    // ---- What Karvon keeps ------------------------------------------------------------------------------------

    string StoreKey => "postman/environment/" + Id;

    /// <summary>The vault is locked: its values are not here yet.</summary>
    public bool IsLocked => _store.IsLocked;

    /// <summary>What is typed here is kept when Karvon closes.</summary>
    public bool IsKept => _store.CanKeep;

    public IReadOnlyDictionary<string, object?> Current => Own;

    Dictionary<string, object?> Own
    {
        get
        {
            if (_current is null)
                Read(_store.Get(StoreKey));
            return _current!;
        }
    }

    void Read(string? kept)
    {
        _current = new Dictionary<string, object?>(StringComparer.Ordinal);
        (_agents, _hosts) = (false, new(StringComparer.OrdinalIgnoreCase));
        if (kept is not { Length: > 0 })
            return;
        try
        {
            var root = JsonNode.Parse(kept) as JsonObject;
            foreach (var (key, value) in root?["values"] as JsonObject ?? [])
                _current[key] = value is JsonValue v && v.TryGetValue<bool>(out var flag) ? flag : value is JsonValue n && n.TryGetValue<double>(out var number) ? number : value.Text();
            _agents = root.Flag("agents");
            foreach (var host in (root?["hosts"] as JsonArray ?? []).Select(h => h.Text()).OfType<string>())
                _hosts.Add(host);
        }
        // Not JSON: none kept.
        catch (JsonException)
        {
        }
    }

    /// <summary>The vault was opened, or given a passphrase: what is here for the session goes over what it keeps.</summary>
    public void Reopen()
    {
        if (!_store.CanKeep)
        {
            // Its passphrase was taken away: what is here is the session's.
            if (_current is not null)
                Keep();
            return;
        }
        var (session, agents, hosts) = (_current, _agents, _hosts);
        Read(_store.Kept(StoreKey));
        foreach (var (key, value) in session ?? [])
            _current![key] = value;
        _agents |= agents;
        _hosts.UnionWith(hosts);
        Keep();
    }

    public void Set(string key, object? value)
    {
        Own[key] = value;
        Keep();
    }

    public void Unset(string key)
    {
        if (Own.Remove(key))
            Keep();
    }

    void Keep()
    {
        HoldSecrets();
        var own = Own;
        if (own.Count == 0 && !_agents && _hosts.Count == 0)
        {
            _store.Delete(StoreKey);
            return;
        }
        var values = new JsonObject();
        foreach (var (key, value) in own)
            values[key] = value switch { bool flag => flag, double number => number, _ => PostmanScope.Text(value) };
        _store.Set(StoreKey, $"Karvon: {Name}'s values (Postman environment)", new JsonObject
        {
            ["values"] = values,
            ["agents"] = _agents,
            ["hosts"] = new JsonArray([.. _hosts.Select(h => (JsonNode)h)]),
        }.ToJsonString());
    }

    /// <summary>Its secrets hidden in whatever goes to an agent, with no way back (those a text may not hold by chance).</summary>
    void HoldSecrets()
    {
        foreach (var variable in Variables.Where(v => v.HoldsSecret))
            foreach (var value in new[] { variable.Initial, variable.Current }.OfType<string>().Where(LooksSecret))
                KnownSecrets.Seal($"Postman {Name}/{variable.Key}", value);
    }

    /// <summary>Not a word or a default a file holds by chance ("postgres", "localhost:5432"): long and mixed, or a token's form.</summary>
    internal static bool LooksSecret(string value)
    {
        if (value.Contains("{{", StringComparison.Ordinal) || value.Contains("://", StringComparison.Ordinal))
            return false;
        var classes = new[] { value.Any(char.IsAsciiLetterLower), value.Any(char.IsAsciiLetterUpper), value.Any(char.IsAsciiDigit), value.Any(c => !char.IsAsciiLetterOrDigit(c)) }.Count(c => c);
        return value.Length >= 12 && classes >= 3 || value.Length >= 20 && classes >= 2 || Redaction.HoldsSecret(value);
    }

    // ---- Where it may go --------------------------------------------------------------------------------------

    /// <summary>Agents may send requests with it (never see it), to <see cref="Hosts"/> alone. The user turns it on, once no value is in the file.</summary>
    public bool AgentsMaySend
    {
        get
        {
            _ = Own;
            return _agents && !HasFileValues;
        }
        set
        {
            _ = Own;
            _agents = value;
            Keep();
        }
    }

    /// <summary>The hosts the user added.</summary>
    public IReadOnlyCollection<string> AddedHosts
    {
        get
        {
            _ = Own;
            return _hosts;
        }
        set
        {
            _ = Own;
            _hosts = new HashSet<string>(value.Select(v => v.Trim()).Where(v => v.Length > 0), StringComparer.OrdinalIgnoreCase);
            Keep();
        }
    }

    public void AddHost(string host)
    {
        _ = Own;
        if (_hosts.Add(host))
            Keep();
    }

    /// <summary>
    /// Where its values may go: the hosts of its own addresses, and those the user added. Of the values Karvon keeps alone:
    /// what is written in the file says nothing of where they go.
    /// </summary>
    public IReadOnlyList<string> Hosts
    {
        get
        {
            var hosts = new List<string>(AddedHosts);
            var off = Root.Objects("values").Where(v => v["enabled"] is not null && !v.Flag("enabled")).Select(v => v.Text("key")).ToHashSet();
            var values = Own.Where(v => !off.Contains(v.Key)).ToDictionary(v => v.Key, v => PostmanScope.Text(v.Value), StringComparer.Ordinal);
            string Put(string text, int depth = 0) => depth > 4 ? text
                : System.Text.RegularExpressions.Regex.Replace(text, @"\{\{([^{}]+)\}\}", m => values.TryGetValue(m.Groups[1].Value.Trim(), out var v) ? Put(v, depth + 1) : m.Value);
            foreach (var value in values.Values.Select(v => Put(v).Trim()))
            {
                if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri is { Scheme: "http" or "https" or "ws" or "wss", Host.Length: > 0 })
                    hosts.Add(uri.Authority);
                else if (HostName().IsMatch(value) && Uri.TryCreate("http://" + value, UriKind.Absolute, out var bare))
                    hosts.Add(bare.Authority);
            }
            return [.. hosts.Distinct(StringComparer.OrdinalIgnoreCase)];
        }
    }

    /// <summary>A host written alone: localhost, api.example.com, with its port or a path.</summary>
    static System.Text.RegularExpressions.Regex HostName() => HostPattern;

    static readonly System.Text.RegularExpressions.Regex HostPattern = new(@"^(?:localhost|(?:[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\.)+[a-z]{2,}|\d{1,3}(?:\.\d{1,3}){3})(?::\d+)?(?:/\S*)?$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Whether its values may go to <paramref name="uri"/>; a host listed without a port is any of the usual ones.</summary>
    public bool Reaches(Uri uri) => Hosts.Any(h => h.Equals(uri.Authority, StringComparison.OrdinalIgnoreCase)
        || h.Equals($"{uri.Host}:{uri.Port.ToString(CultureInfo.InvariantCulture)}", StringComparison.OrdinalIgnoreCase)
        || (h.Equals(uri.Host, StringComparison.OrdinalIgnoreCase) && uri.IsDefaultPort));
}
