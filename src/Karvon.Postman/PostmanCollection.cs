using System.Text.Json.Nodes;

namespace Karvon.Postman;

/// <summary>An item of a collection: a folder (which has items) or a request, with the folders it is in.</summary>
public sealed class PostmanItem(PostmanCollection collection, PostmanItem? parent, JsonObject node)
{
    public PostmanCollection Collection => collection;

    public PostmanItem? Parent => parent;

    public JsonObject Node => node;

    public bool IsFolder => node["item"] is JsonArray;

    public string Name
    {
        get => node.Text("name") ?? "";
        set => node["name"] = value;
    }

    public string Method => (node["request"] as JsonObject)?.Text("method")?.ToUpperInvariant() is { Length: > 0 } method ? method : "GET";

    public IReadOnlyList<PostmanItem> Items => [.. node.Objects("item").Select(i => new PostmanItem(collection, this, i))];

    /// <summary>Its folders' names and its own, as a tool names it ("Orders/Create").</summary>
    public string Path => parent is null ? Name : parent.Path + "/" + Name;

    /// <summary>Its requests (a folder's, those of its folders too; a request's, itself), in their order.</summary>
    public IEnumerable<PostmanItem> Requests => IsFolder ? Items.SelectMany(i => i.Requests) : [this];

    public PostmanRequest Read() => PostmanRequest.Read(node);

    /// <summary>The sign-in it is sent with: its own, else its nearest folder's, else the collection's.</summary>
    public PostmanAuth? EffectiveAuth() => PostmanAuth.Read((node["request"] as JsonObject)?["auth"]) ?? PostmanAuth.Read(node["auth"])
        ?? (parent is not null ? parent.EffectiveAuth() : PostmanAuth.Read(collection.Root["auth"]));

    /// <summary>The scripts that run with it, the collection's first, then its folders', then its own.</summary>
    public IEnumerable<string> Scripts(string listen) =>
        (parent?.Scripts(listen) ?? [PostmanRequest.Script(collection.Root, listen)]).Append(PostmanRequest.Script(node, listen)).Where(s => s.Trim().Length > 0);

    /// <summary>The array it is in.</summary>
    internal JsonArray Siblings => (JsonArray)(parent?.Node ?? collection.Root)["item"]!;
}

/// <summary>
/// A Postman collection (v2.0, v2.1) in its file, kept as its JSON: what Karvon does not show (examples, ids, protocol
/// options) stays as it was. Its variables' current values (what scripts set) are this session's, never the file's.
/// </summary>
public sealed class PostmanCollection
{
    public const string Extension = ".postman_collection.json";

    const string Schema = "https://schema.getpostman.com/json/collection/v2.1.0/collection.json";

    PostmanCollection(string file, JsonObject root)
    {
        File = file;
        Root = root;
        if (root["item"] is not JsonArray)
            root["item"] = new JsonArray();
    }

    public string File { get; }

    public JsonObject Root { get; private set; }

    /// <summary>When the file was last read or written here: one changed since is read again.</summary>
    public DateTime Stamp { get; private set; }

    public string Name
    {
        get => Root["info"].Text("name") ?? Path.GetFileName(File);
        set => ((JsonObject)Root["info"]!)["name"] = value;
    }

    public IReadOnlyList<PostmanItem> Items => [.. Root.Objects("item").Select(i => new PostmanItem(this, null, i))];

    public IEnumerable<PostmanItem> Requests => Items.SelectMany(i => i.Requests);

    public IReadOnlyList<PostmanPair> Variables => [.. PostmanRequest.Pairs(Root, "variable")];

    /// <summary>What scripts set in it this session, over <see cref="Variables"/>.</summary>
    public Dictionary<string, object?> Current { get; } = new(StringComparer.Ordinal);

    /// <summary>Whether a JSON file is a collection Karvon reads.</summary>
    public static bool Is(JsonObject root) => root["info"] is JsonObject info && (info.Text("schema")?.Contains("/collection/v2", StringComparison.Ordinal) == true || root["item"] is JsonArray);

    public static PostmanCollection Load(string file)
    {
        var root = PostmanJson.Read(file);
        if (!Is(root))
            throw new PostmanException(root["requests"] is JsonArray || root["order"] is JsonArray
                ? $"{Path.GetFileName(file)} is a v1 collection: export it from Postman as v2.1."
                : $"{Path.GetFileName(file)} is not a Postman collection.");
        var collection = new PostmanCollection(file, root) { Stamp = System.IO.File.GetLastWriteTimeUtc(file) };
        collection.HoldSecrets();
        return collection;
    }

    /// <summary>A name a secret goes by, a header's too (X-Api-Key).</summary>
    internal static bool IsSecretName(string name) => Claude.Tools.Redaction.IsSecretName(name.Replace('-', '_')) || name.Equals("Authorization", StringComparison.OrdinalIgnoreCase);

    /// <summary>The secrets written in it (a sign-in's, a variable's and a header's by their names) are Karvon's to hide from what goes to Claude.</summary>
    void HoldSecrets()
    {
        void Hold(string name, string? value)
        {
            if (value?.Contains("{{", StringComparison.Ordinal) == false)
                Claude.Tools.KnownSecrets.Add($"karvon-postman/{Name}/{name}", value);
        }
        foreach (var variable in Variables.Where(v => IsSecretName(v.Key)))
            Hold(variable.Key, variable.Value);
        void Auth(JsonNode? node)
        {
            foreach (var (key, value) in PostmanAuth.Read(node)?.Values ?? new Dictionary<string, string>())
                if (key is "token" or "password" or "value" or "accessToken" or "refreshToken" or "clientSecret" or "secretKey" or "sessionToken")
                    Hold(key, value);
        }
        void Items(IEnumerable<JsonObject> items)
        {
            foreach (var item in items)
            {
                Auth(item["auth"]);
                Auth((item["request"] as JsonObject)?["auth"]);
                foreach (var header in PostmanRequest.Pairs(item["request"], "header").Where(h => IsSecretName(h.Key)))
                    Hold(header.Key, header.Value);
                Items(item.Objects("item"));
            }
        }
        Auth(Root["auth"]);
        Items(Root.Objects("item"));
    }

    public static PostmanCollection Create(string file, string name)
    {
        var collection = new PostmanCollection(file, new JsonObject
        {
            ["info"] = new JsonObject { ["_postman_id"] = Guid.NewGuid().ToString(), ["name"] = name, ["schema"] = Schema },
            ["item"] = new JsonArray(),
        });
        collection.Save();
        return collection;
    }

    /// <summary>The file is not as it was read or written here (a checkout, another program): it is to be read again.</summary>
    public bool IsStale => System.IO.File.Exists(File) && System.IO.File.GetLastWriteTimeUtc(File) != Stamp;

    /// <summary>The file read again, in place of what was here (items held before are of no use any more).</summary>
    public void Reload()
    {
        var read = Load(File);
        Root = read.Root;
        Stamp = read.Stamp;
    }

    public void Save()
    {
        PostmanJson.Write(File, Root);
        Stamp = System.IO.File.GetLastWriteTimeUtc(File);
    }

    /// <summary>The item of a path ("Orders/Create"; "Orders/Create#2" is the second of that name), or of a request's name alone when only one has it.</summary>
    public PostmanItem? Find(string path)
    {
        path = path.Trim().Trim('/');
        if (path.Length == 0)
            return null;
        static IEnumerable<PostmanItem> All(IEnumerable<PostmanItem> items) => items.SelectMany(i => All(i.Items).Prepend(i));
        var all = All(Items).ToList();
        var number = 1;
        if (path.LastIndexOf('#') is > 0 and var hash && int.TryParse(path[(hash + 1)..], out var n) && n > 0 && all.All(i => i.Path != path))
            (path, number) = (path[..hash], n);
        if (all.Where(i => i.Path.Equals(path, StringComparison.OrdinalIgnoreCase)).ElementAtOrDefault(number - 1) is { } found)
            return found;
        return all.Where(i => i.Name.Equals(path, StringComparison.OrdinalIgnoreCase)).ToList() is [var only] ? only : null;
    }

    /// <summary>An item's path as <see cref="Find"/> tells it from others of the same.</summary>
    public string PathOf(PostmanItem item)
    {
        static IEnumerable<PostmanItem> All(IEnumerable<PostmanItem> items) => items.SelectMany(i => All(i.Items).Prepend(i));
        var same = All(Items).Where(i => i.Path == item.Path).ToList();
        var at = same.FindIndex(i => i.Node == item.Node);
        return at > 0 ? $"{item.Path}#{at + 1}" : item.Path;
    }

    /// <summary>An item as it is now (held since before a change here), or null when it is gone.</summary>
    public PostmanItem? Of(JsonObject node)
    {
        static IEnumerable<PostmanItem> All(IEnumerable<PostmanItem> items) => items.SelectMany(i => All(i.Items).Prepend(i));
        return All(Items).FirstOrDefault(i => i.Node == node);
    }

    /// <summary>A new request or folder, at the end of a folder (or of the collection).</summary>
    public PostmanItem Add(PostmanItem? folder, string name, bool isFolder)
    {
        var node = new JsonObject { ["name"] = name };
        if (isFolder)
            node["item"] = new JsonArray();
        else
            new PostmanRequest().Write(node);
        ((JsonArray)(folder?.Node ?? Root)["item"]!).Add(node);
        return new PostmanItem(this, folder, node);
    }

    /// <summary>A copy of an item after it.</summary>
    public PostmanItem Duplicate(PostmanItem item)
    {
        var node = (JsonObject)item.Node.DeepClone();
        // Two items of one id would be one item to Postman.
        static void NewIds(JsonObject item)
        {
            if (item.ContainsKey("id"))
                item["id"] = Guid.NewGuid().ToString();
            item.Remove("_postman_id");
            foreach (var child in item.Objects("item"))
                NewIds(child);
        }
        NewIds(node);
        node["name"] = item.Name + " Copy";
        item.Siblings.Insert(item.Siblings.IndexOf(item.Node) + 1, node);
        return new PostmanItem(this, item.Parent, node);
    }

    public void Remove(PostmanItem item) => item.Siblings.Remove(item.Node);
}
