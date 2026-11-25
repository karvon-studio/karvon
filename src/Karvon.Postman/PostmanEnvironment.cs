using System.Text.Json;
using System.Text.Json.Nodes;
using Bengine.Introspection.Servers;
using Karvon.Claude.Tools;

namespace Karvon.Postman;

/// <summary>A variable of an environment: its name, the value its file has (shared), the one this machine has over it, and whether it is a secret.</summary>
public sealed record PostmanVariable(string Key, string Initial)
{
    /// <summary>This machine's value (typed here, or set by a script); null for the file's.</summary>
    public string? Current { get; init; }

    public bool Enabled { get; init; } = true;

    public bool IsSecret { get; init; }

    public string Value => Current ?? Initial;

    /// <summary>A secret by its type, or by its name.</summary>
    public bool HoldsSecret => IsSecret || Redaction.IsSecretName(Key);
}

/// <summary>
/// A Postman environment (or the globals) in its file. Its current values are this machine's: they go to Karvon's vault
/// (without one, they are this session's), never into the file, which a repository may hold.
/// </summary>
public sealed class PostmanEnvironment
{
    public const string Extension = ".postman_environment.json", GlobalsExtension = ".postman_globals.json";

    readonly ICredentialStore? _keyring;
    Dictionary<string, object?>? _current;

    PostmanEnvironment(string file, JsonObject root, ICredentialStore? keyring)
    {
        File = file;
        Root = root;
        _keyring = keyring;
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

    public static PostmanEnvironment Load(string file, ICredentialStore? keyring)
    {
        var root = PostmanJson.Read(file);
        if (!Is(root))
            throw new PostmanException($"{Path.GetFileName(file)} is not a Postman environment.");
        var environment = new PostmanEnvironment(file, root, keyring) { Stamp = System.IO.File.GetLastWriteTimeUtc(file) };
        environment.HoldSecrets();
        return environment;
    }

    public static PostmanEnvironment Create(string file, string name, ICredentialStore? keyring)
    {
        var environment = new PostmanEnvironment(file, new JsonObject
        {
            ["id"] = Guid.NewGuid().ToString(),
            ["name"] = name,
            ["values"] = new JsonArray(),
            ["_postman_variable_scope"] = "environment",
        }, keyring);
        environment.Save();
        return environment;
    }

    public bool IsStale => System.IO.File.Exists(File) && System.IO.File.GetLastWriteTimeUtc(File) != Stamp;

    public void Reload()
    {
        var read = Load(File, _keyring);
        Root = read.Root;
        Stamp = read.Stamp;
        HoldSecrets();
    }

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

    /// <summary>The rows as edited: the file's part saved when it changed, the current values kept.</summary>
    public void Put(string name, IReadOnlyList<PostmanVariable> variables)
    {
        var values = new JsonArray();
        foreach (var variable in variables.Where(v => v.Initial.Length > 0 || v.Current is null || Root.Objects("values").Any(o => o.Text("key") == v.Key)))
        {
            // A row keeps what Karvon does not show of it.
            var row = Root.Objects("values").FirstOrDefault(v => v.Text("key") == variable.Key)?.DeepClone() as JsonObject ?? new JsonObject { ["key"] = variable.Key };
            if (row.Text("value") != variable.Initial)
                row["value"] = variable.Initial;
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

    // ---- The current values -----------------------------------------------------------------------------------

    string KeyringKey => "karvon-postman/" + Id;

    /// <summary>This machine's values, read from the keyring the first time.</summary>
    public IReadOnlyDictionary<string, object?> Current => Own;

    Dictionary<string, object?> Own
    {
        get
        {
            if (_current is not null)
                return _current;
            _current = new Dictionary<string, object?>(StringComparer.Ordinal);
            try
            {
                if (_keyring?.Get(KeyringKey) is { Length: > 0 } kept && JsonNode.Parse(kept) is JsonObject values)
                    foreach (var (key, value) in values)
                        _current[key] = value.Text();
            }
            // Locked, not there, not JSON: none kept.
            catch (Exception)
            {
            }
            return _current;
        }
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

    /// <summary>The last write to the keyring (it may wait, or ask the user: never on the window's thread).</summary>
    internal Task Kept { get; private set; } = Task.CompletedTask;

    void Keep()
    {
        HoldSecrets();
        if (_keyring is not { } keyring)
            return;
        var values = Own.ToDictionary(v => v.Key, v => PostmanScope.Text(v.Value));
        var label = $"Karvon: {Name}'s values (Postman environment)";
        Kept = Kept.ContinueWith(_ =>
        {
            try
            {
                if (values.Count == 0)
                    keyring.Delete(KeyringKey);
                else
                    keyring.Set(KeyringKey, label, JsonSerializer.Serialize(values));
            }
            // No keyring now: they are this session's.
            catch (Exception)
            {
            }
        }, TaskScheduler.Default);
    }

    /// <summary>Its secrets are Karvon's to hide: told by their values in what goes to Claude.</summary>
    void HoldSecrets()
    {
        foreach (var variable in Variables.Where(v => v.HoldsSecret))
        {
            KnownSecrets.Add($"karvon-postman/{Name}/{variable.Key}", variable.Initial);
            KnownSecrets.Add($"karvon-postman/{Name}/{variable.Key}", variable.Current);
        }
    }
}
