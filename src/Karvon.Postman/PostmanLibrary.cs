using System.Text.Json;
using System.Text.Json.Nodes;
using Bengine.Introspection.Servers;

namespace Karvon.Postman;

/// <summary>
/// The collections and environments Karvon shows: those in the open folder (*.postman_collection.json and the like, as Postman
/// names what it exports), and the files opened from elsewhere, kept in postman.json beside Karvon's settings.
/// </summary>
public sealed class PostmanLibrary(Func<string?> workspace, string listFile, Func<ICredentialStore?> keyring)
{
    /// <summary>Folders that hold no one's collections: dependencies, build output, tools' own.</summary>
    static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
        { ".git", ".hg", ".svn", ".vs", ".vscode", ".idea", "node_modules", "bin", "obj", "target", "dist", "build", "vendor", ".venv", "venv", "__pycache__", "packages" };

    const int MaxDepth = 10, MaxEntries = 100_000;

    readonly List<PostmanCollection> _collections = [];
    readonly List<PostmanEnvironment> _environments = [];
    Task? _refreshing;
    bool _again;

    public IReadOnlyList<PostmanCollection> Collections => _collections;

    /// <summary>The environments, the globals aside.</summary>
    public IReadOnlyList<PostmanEnvironment> Environments => [.. _environments.Where(e => !e.IsGlobals)];

    public PostmanEnvironment? Globals => _environments.FirstOrDefault(e => e.IsGlobals);

    /// <summary>The files that could not be read, and why.</summary>
    public IReadOnlyList<(string File, string Message)> Problems { get; private set; } = [];

    /// <summary>What there is, or the environment chosen, changed.</summary>
    public event Action? Changed;

    // ---- The environment requests are sent with ---------------------------------------------------------------

    string? _activeId;

    /// <summary>Its id, kept with the solution.</summary>
    public string? ActiveId
    {
        get => _activeId;
        set
        {
            if (_activeId == value)
                return;
            _activeId = value;
            Changed?.Invoke();
        }
    }

    /// <summary>The environment chosen; null for none.</summary>
    public PostmanEnvironment? Active => _environments.FirstOrDefault(e => !e.IsGlobals && e.Id == _activeId);

    public PostmanEnvironment? FindEnvironment(string name) => Environments.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || e.Id == name);

    public PostmanCollection? FindCollection(string name) => _collections.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? _collections.FirstOrDefault(c => Path.GetFileName(c.File).Equals(name, StringComparison.OrdinalIgnoreCase) || c.File == name);

    // ---- The files --------------------------------------------------------------------------------------------

    /// <summary>The files read (again): the new ones, those changed, those gone. Called and done on the window's thread.</summary>
    public Task RefreshAsync()
    {
        if (_refreshing is { IsCompleted: false })
        {
            _again = true;
            return _refreshing;
        }
        return _refreshing = RunAsync();

        async Task RunAsync()
        {
            do
            {
                _again = false;
                await ReadAsync();
            }
            while (_again);
            Changed?.Invoke();
        }
    }

    bool _loaded;

    /// <summary>
    /// What there is, read now (a plan of Claude's does not wait for a thread): all of it the first time, or when asked; then
    /// the files changed since.
    /// </summary>
    public void ReadNow(bool all = false)
    {
        if (_loaded && !all && !_collections.Any(c => c.IsStale) && !_environments.Any(e => e.IsStale))
            return;
        Put(Read(workspace(), _collections.ToDictionary(c => c.File), _environments.ToDictionary(e => e.File), keyring()));
        Changed?.Invoke();
    }

    async Task ReadAsync()
    {
        var root = workspace();
        var (collections, environments, store) = (_collections.ToDictionary(c => c.File), _environments.ToDictionary(e => e.File), keyring());
        Put(await Task.Run(() => Read(root, collections, environments, store)));
    }

    sealed record Found(List<PostmanCollection> Collections, List<PostmanEnvironment> Environments, List<(string, string)> Problems);

    Found Read(string? root, Dictionary<string, PostmanCollection> knownCollections, Dictionary<string, PostmanEnvironment> knownEnvironments, ICredentialStore? store)
    {
        var collections = new List<PostmanCollection>();
        var environments = new List<PostmanEnvironment>();
        var problems = new List<(string, string)>();
        foreach (var file in Files(root).Concat(Opened()).Distinct())
            try
            {
                if (knownCollections.GetValueOrDefault(file) is { IsStale: false } collection && File.Exists(file))
                    collections.Add(collection);
                else if (knownEnvironments.GetValueOrDefault(file) is { IsStale: false } environment && File.Exists(file))
                    environments.Add(environment);
                else if (IsEnvironmentFile(file))
                    environments.Add(PostmanEnvironment.Load(file, store));
                else
                    collections.Add(PostmanCollection.Load(file));
            }
            catch (Exception ex) when (ex is PostmanException or IOException or UnauthorizedAccessException)
            {
                problems.Add((file, ex.Message));
            }
        return new Found(collections, environments, problems);
    }

    void Put(Found read)
    {
        // What scripts set in a collection stays this session, its file read again or not.
        foreach (var collection in read.Collections)
            if (_collections.FirstOrDefault(c => c.File == collection.File) is { } old && old != collection)
                foreach (var (key, value) in old.Current)
                    collection.Current[key] = value;
        _loaded = true;
        _collections.Clear();
        _collections.AddRange(read.Collections.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase));
        _environments.Clear();
        _environments.AddRange(read.Environments.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase));
        Problems = read.Problems;
    }

    public static bool IsCollectionFile(string path) => path.EndsWith(PostmanCollection.Extension, StringComparison.OrdinalIgnoreCase);

    public static bool IsPostmanFile(string path) => IsCollectionFile(path) || path.EndsWith(PostmanEnvironment.Extension, StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(PostmanEnvironment.GlobalsExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a file is an environment's (by its name, else by what is in it): the rest are collections.</summary>
    static bool IsEnvironmentFile(string file) => !IsCollectionFile(file) && (IsPostmanFile(file) || PostmanEnvironment.Is(PostmanJson.Read(file)));

    /// <summary>The folder's Postman files, links not followed.</summary>
    static IEnumerable<string> Files(string? root)
    {
        if (root is null || !Directory.Exists(root))
            return [];
        var found = new List<string>();
        var entries = 0;
        void Read(string folder, int depth)
        {
            try
            {
                foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
                {
                    if (++entries > MaxEntries)
                        return;
                    if (entry is DirectoryInfo)
                    {
                        if (depth < MaxDepth && !Skipped.Contains(entry.Name) && !entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                            Read(entry.FullName, depth + 1);
                    }
                    else if (IsPostmanFile(entry.Name))
                        found.Add(entry.FullName);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        Read(root, 0);
        return found;
    }

    /// <summary>The files opened from elsewhere, those that are still there.</summary>
    IReadOnlyList<string> Opened()
    {
        try
        {
            return File.Exists(listFile) && JsonNode.Parse(File.ReadAllText(listFile))?["files"] is JsonArray files
                ? [.. files.Select(f => f.Text()).OfType<string>().Where(File.Exists)] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    void SaveOpened(IEnumerable<string> files)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(listFile)!);
        File.WriteAllText(listFile, new JsonObject { ["files"] = new JsonArray([.. files.Distinct().Select(f => (JsonNode)f)]) }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Whether a file is listed because it was opened, not because it is in the folder.</summary>
    public bool IsOpened(string file) => Opened().Contains(file) && !InWorkspace(file);

    bool InWorkspace(string file) => workspace() is { } root && Path.GetFullPath(file).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    /// <summary>Files opened from anywhere: those that are Postman's, listed from now on; the others are why they are not.</summary>
    public async Task<string?> OpenAsync(IEnumerable<string> files)
    {
        var opened = Opened().ToList();
        string? problem = null;
        foreach (var file in files.Select(Path.GetFullPath))
            try
            {
                var root = PostmanJson.Read(file);
                if (PostmanEnvironment.Is(root))
                    _ = PostmanEnvironment.Load(file, null);
                else
                    _ = PostmanCollection.Load(file);
                if (!(InWorkspace(file) && IsPostmanFile(file)))
                    opened.Add(file);
            }
            catch (Exception ex) when (ex is PostmanException or IOException or UnauthorizedAccessException)
            {
                problem = ex.Message;
            }
        SaveOpened(opened);
        await RefreshAsync();
        return problem;
    }

    /// <summary>A file opened from elsewhere listed no more; it stays where it is.</summary>
    public Task ForgetAsync(string file)
    {
        SaveOpened(Opened().Where(f => f != file));
        return RefreshAsync();
    }

    /// <summary>A file for a new collection or environment: the folder's postman folder (Karvon's own without one), by its name.</summary>
    string NewFile(string name, string extension)
    {
        var folder = workspace() is { } root ? Path.Combine(root, "postman") : Path.Combine(Path.GetDirectoryName(listFile)!, "postman");
        var plain = string.Concat(name.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '/' or '\\' or ':' ? '-' : c));
        for (var i = 1; ; i++)
        {
            var file = Path.Combine(folder, (i == 1 ? plain : $"{plain} {i}") + extension);
            if (!File.Exists(file))
                return file;
        }
    }

    public async Task<PostmanCollection> CreateCollectionAsync(string name)
    {
        var file = PostmanCollection.Create(NewFile(name, PostmanCollection.Extension), name.Trim()).File;
        await ListAsync(file);
        return _collections.First(c => c.File == file);
    }

    public async Task<PostmanEnvironment> CreateEnvironmentAsync(string name)
    {
        var file = PostmanEnvironment.Create(NewFile(name, PostmanEnvironment.Extension), name.Trim(), keyring()).File;
        await ListAsync(file);
        return _environments.First(e => e.File == file);
    }

    Task ListAsync(string file)
    {
        if (!InWorkspace(file))
            SaveOpened(Opened().Append(file));
        return RefreshAsync();
    }

    /// <summary>A collection changed and saved; one changed on disk meanwhile is read again first, and the change made there.</summary>
    public void Save(PostmanCollection collection)
    {
        collection.Save();
        Changed?.Invoke();
    }

    /// <summary>A change was made and saved elsewhere than in <see cref="Save"/> (an environment's).</summary>
    public void RaiseChanged() => Changed?.Invoke();
}
