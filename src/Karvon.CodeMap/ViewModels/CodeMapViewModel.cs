using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Karvon.CodeMap.ViewModels;

/// <summary>A row of the map's tree: a folder, a file or a declaration, with its doc comment; its children made when it opens.</summary>
public sealed partial class CodeMapNode : ObservableObject
{
    readonly Func<CodeMapNode, IEnumerable<CodeMapNode>>? _children;
    bool _loaded;

    public CodeMapNode(string name, string? detail, string icon, string path, int line, Func<CodeMapNode, IEnumerable<CodeMapNode>>? children)
    {
        Name = name;
        Detail = detail;
        Icon = icon;
        Path = path;
        Line = line;
        _children = children;
        // A row that has children shows its arrow before they are made.
        if (children is not null)
            Children.Add(Placeholder);
    }

    static readonly CodeMapNode Placeholder = new("…", null, "Pending", "", 0, null);

    public string Name { get; }

    /// <summary>Its files' count (a folder's), its doc comment or its signature, in gray after it.</summary>
    public string? Detail { get; }

    public bool HasDetail => Detail is { Length: > 0 };

    /// <summary>A <c>VsIcon</c> kind.</summary>
    public string Icon { get; }

    /// <summary>The file it is in (a folder's own path, for a folder), relative to the workspace.</summary>
    public string Path { get; }

    public int Line { get; }

    public ObservableCollection<CodeMapNode> Children { get; } = [];

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value)
            Load();
    }

    public void Load()
    {
        if (_loaded || _children is null)
            return;
        _loaded = true;
        Children.Clear();
        foreach (var child in _children(this))
            Children.Add(child);
    }
}

/// <summary>A search's hit as the window lists it: what, where, and its doc comment or signature.</summary>
public sealed record CodeMapHitItem(string Icon, string Title, string Location, string? Detail, string Path, int Line)
{
    public bool HasDetail => Detail is { Length: > 0 };
}

/// <summary>
/// The Code Map window: a search of the map as you type (declarations and files, best first), the workspace's tree, and how
/// far the map is.
/// </summary>
public sealed partial class CodeMapViewModel : ObservableObject, IDisposable
{
    readonly CodeMapModule _module;
    readonly DispatcherTimer _searchTimer;
    readonly DispatcherTimer _reloadTimer;
    readonly DispatcherTimer _scanTimer;
    CodeMapWorkspace? _map;
    Dictionary<string, List<string>> _folders = [];
    Dictionary<string, List<string>> _filesIn = [];
    Dictionary<string, int> _counts = [];
    IReadOnlyDictionary<string, MapFile> _files = new Dictionary<string, MapFile>();

    public CodeMapViewModel(CodeMapModule module)
    {
        _module = module;
        _searchTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background, (_, _) =>
        {
            _searchTimer!.Stop();
            Search();
        });
        _reloadTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) =>
        {
            _reloadTimer!.Stop();
            Reload();
        });
        // While the window shows, the map follows what changed on disk without a save in Karvon (a checkout, another editor).
        _scanTimer = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) =>
        {
            if (_module.IsShowing)
                _ = _map?.Indexer.ScanAsync();
        });
        _scanTimer.Start();
        SetMap(module.Map);
    }

    public ObservableCollection<CodeMapNode> Roots { get; } = [];

    public ObservableCollection<CodeMapHitItem> Results { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearching))]
    public partial string Query { get; set; } = "";

    public bool IsSearching => Query.Trim().Length > 0;

    [ObservableProperty]
    public partial bool HasNoResults { get; set; }

    /// <summary>The map: its files and declarations, or how far its first scan is.</summary>
    [ObservableProperty]
    public partial string Status { get; set; } = "";

    partial void OnQueryChanged(string value)
    {
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    /// <summary>The map of another workspace (or none).</summary>
    public void SetMap(CodeMapWorkspace? map)
    {
        if (_map is not null)
            _map.Changed -= OnMapChanged;
        _map = map;
        if (map is not null)
            map.Changed += OnMapChanged;
        Reload();
    }

    void OnMapChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (!_reloadTimer.IsEnabled)
            _reloadTimer.Start();
    });

    /// <summary>The tree and the status as the map is now, the folders and files that were open kept open.</summary>
    public void Reload()
    {
        var expanded = new HashSet<string>(StringComparer.Ordinal);
        void Collect(IEnumerable<CodeMapNode> nodes)
        {
            foreach (var node in nodes.Where(n => n.IsExpanded))
            {
                expanded.Add(node.Path + "#" + node.Name);
                Collect(node.Children);
            }
        }
        Collect(Roots);
        Roots.Clear();
        if (_map is { } map)
        {
            _files = map.Store.Files();
            _folders = [];
            _filesIn = [];
            _counts = [];
            foreach (var path in _files.Keys)
            {
                var folder = Parent(path);
                List(_filesIn, folder).Add(path);
                for (var f = folder; f.Length > 0; f = Parent(f))
                    _counts[f] = _counts.GetValueOrDefault(f) + 1;
                for (var f = folder; f.Length > 0; f = Parent(f))
                {
                    var subfolders = List(_folders, Parent(f));
                    if (subfolders.Contains(f))
                        break;
                    subfolders.Add(f);
                }
            }
            foreach (var node in ChildrenOf(""))
                Roots.Add(node);
            Expand(Roots);
        }
        UpdateStatus();
        if (IsSearching)
            Search();

        void Expand(IEnumerable<CodeMapNode> nodes)
        {
            foreach (var node in nodes)
                if (expanded.Contains(node.Path + "#" + node.Name))
                {
                    node.IsExpanded = true;
                    Expand(node.Children);
                }
        }
    }

    static List<string> List(Dictionary<string, List<string>> lists, string key) =>
        lists.TryGetValue(key, out var list) ? list : lists[key] = [];

    /// <summary>A folder's rows: its folders, then its files.</summary>
    IEnumerable<CodeMapNode> ChildrenOf(string folder)
    {
        foreach (var sub in _folders.GetValueOrDefault(folder, []).Order(StringComparer.Ordinal))
            yield return new CodeMapNode(System.IO.Path.GetFileName(sub), Files(_counts.GetValueOrDefault(sub)), "Folder", sub, 0, _ => ChildrenOf(sub));
        foreach (var path in _filesIn.GetValueOrDefault(folder, []).Order(StringComparer.Ordinal))
        {
            var file = _files[path];
            yield return new CodeMapNode(System.IO.Path.GetFileName(path), null, "FileCode", path, 1,
                file.Outlined ? node => SymbolsOf(node.Path) : null);
        }
    }

    /// <summary>A file's declarations, each type's members under it.</summary>
    IEnumerable<CodeMapNode> SymbolsOf(string path)
    {
        var symbols = _map?.Store.Symbols(path) ?? [];
        return Level(0, 0, symbols.Count);

        IEnumerable<CodeMapNode> Level(int depth, int from, int to)
        {
            for (var i = from; i < to; i++)
            {
                var symbol = symbols[i];
                if (symbol.Depth != depth)
                    continue;
                var end = i + 1;
                while (end < to && symbols[end].Depth > depth)
                    end++;
                var (first, last) = (i + 1, end);
                yield return new CodeMapNode(symbol.Name, symbol.Doc ?? symbol.Detail, IconOf(symbol.Kind), path, symbol.Line,
                    last > first ? _ => Level(depth + 1, first, last) : null);
            }
        }
    }

    void Search()
    {
        Results.Clear();
        if (_map is not { } map || !IsSearching)
        {
            HasNoResults = false;
            return;
        }
        foreach (var hit in map.Store.Search(Query, limit: 60))
            Results.Add(hit.IsFile
                ? new CodeMapHitItem("FileCode", hit.Name, hit.Path, null, hit.Path, 1)
                : new CodeMapHitItem(IconOf(hit.Kind), (hit.Container is { } c ? c + "." : "") + hit.Name, $"{hit.Path}:{hit.Line}",
                    hit.Doc ?? hit.Detail, hit.Path, hit.Line));
        HasNoResults = Results.Count == 0;
    }

    void UpdateStatus()
    {
        if (_map is not { } map)
        {
            Status = "Open a solution or a folder: its map is made here.";
            return;
        }
        var (done, total) = map.Indexer.Progress;
        Status = total > 0
            ? $"Reading the workspace: {done:N0} of {total:N0} files…"
            : $"{Files(_files.Count)} · {map.Store.SymbolCount():N0} declarations";
    }

    [RelayCommand]
    void Refresh() => RefreshMap();

    [RelayCommand]
    void CollapseAll()
    {
        void Collapse(IEnumerable<CodeMapNode> nodes)
        {
            foreach (var node in nodes)
            {
                Collapse(node.Children);
                node.IsExpanded = false;
            }
        }
        Collapse(Roots);
    }

    /// <summary>A row's file in the editor, at its line (a folder opens in the tree).</summary>
    public void Open(string path, int line)
    {
        if (line > 0)
            _module.OpenFile(path, line);
    }

    /// <summary>Refresh: what changed read, and the files waiting for their language's server asked about again.</summary>
    void RefreshMap()
    {
        _map?.Indexer.RetryWaiting();
        _ = _map?.Indexer.ScanAsync();
    }

    static string Parent(string path) => path.LastIndexOf('/') is var slash and > 0 ? path[..slash] : "";

    static string Files(int count) => count == 1 ? "1 file" : $"{count:N0} files";

    /// <summary>A declaration's kind as a <c>VsIcon</c> kind.</summary>
    static string IconOf(string kind) => kind switch
    {
        "class" => "Class",
        "struct" => "Struct",
        "interface" => "Interface",
        "enum" => "Enum",
        "enum member" => "EnumMember",
        "method" or "function" or "constructor" => "Method",
        "property" => "Property",
        "field" => "Field",
        "event" => "Event",
        "constant" => "Constant",
        "operator" => "Operator",
        "namespace" or "module" or "package" => "Namespace",
        "variable" => "Local",
        _ => "Snippet",
    };

    public void Dispose()
    {
        _searchTimer.Stop();
        _reloadTimer.Stop();
        _scanTimer.Stop();
        if (_map is not null)
            _map.Changed -= OnMapChanged;
    }
}
