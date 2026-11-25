using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Karvon.Ui;

namespace Karvon.Postman.ViewModels;

public enum PostmanNodeKind
{
    Collection,
    Folder,
    Request,
    /// <summary>The environments' heading.</summary>
    Environments,
    Environment,
    /// <summary>A file that could not be read.</summary>
    Problem,
}

/// <summary>A row of the Postman explorer.</summary>
public sealed partial class PostmanNode(PostmanNodeKind kind, string title, string key) : ObservableObject
{
    public PostmanNodeKind Kind => kind;

    public string Title => title;

    /// <summary>What it is, whatever was read again: an open row stays open.</summary>
    public string Key => key;

    public string? Detail { get; init; }

    public PostmanCollection? Collection { get; init; }

    public PostmanItem? Item { get; init; }

    public PostmanEnvironment? Environment { get; init; }

    public string? File { get; init; }

    public ObservableCollection<PostmanNode> Children { get; } = [];

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    public string Method => Item?.Method ?? "";

    /// <summary>The method as its badge has it (DELETE is DEL, as Postman's).</summary>
    public string MethodText => Method switch { "DELETE" => "DEL", "OPTIONS" => "OPT", "PATCH" => "PATCH", var m => m };

    public bool IsRequest => kind == PostmanNodeKind.Request;

    public bool IsFolder => kind == PostmanNodeKind.Folder;

    public bool IsCollection => kind == PostmanNodeKind.Collection;

    public bool IsEnvironment => kind == PostmanNodeKind.Environment;

    public bool IsProblem => kind == PostmanNodeKind.Problem;

    public bool IsHeading => kind == PostmanNodeKind.Environments;

    /// <summary>It can hold requests and folders, and be run.</summary>
    public bool IsContainer => IsFolder || IsCollection;

    public bool IsItem => IsFolder || IsRequest;

    public bool HasFile => IsCollection || IsEnvironment || IsProblem;

    public bool CanOpen => IsRequest || IsEnvironment;

    public bool CanRename => !IsHeading && !IsProblem;

    public bool CanDelete => !IsHeading && !IsOpened;

    /// <summary>The environment requests are sent with.</summary>
    public bool IsActive { get; init; }

    public bool CanUse => IsEnvironment && !IsActive && Environment is { IsGlobals: false };

    /// <summary>It is listed because it was opened from elsewhere.</summary>
    public bool IsOpened { get; init; }

    public string Icon => kind switch
    {
        PostmanNodeKind.Collection => "ToolPostman",
        PostmanNodeKind.Folder or PostmanNodeKind.Environments => "Folder",
        PostmanNodeKind.Environment => IsActive ? "Check" : "Globe",
        PostmanNodeKind.Problem => "Warning",
        _ => "",
    };

    public bool HasIcon => Icon.Length > 0;
}

/// <summary>An environment of the choice above the tree, or none.</summary>
public sealed record PostmanEnvironmentChoice(string? Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// The Postman tool window: the collections (their folders and requests) and the environments, the one requests are sent
/// with, a search of the requests; what is made, renamed, copied and removed here is written to its file.
/// </summary>
public sealed partial class PostmanExplorerViewModel : ObservableObject, IDisposable
{
    readonly IPostmanHost _host;

    /// <summary>The rows that are open, as they were before a search opened them all.</summary>
    HashSet<string> _open = new(StringComparer.Ordinal);
    bool _choosing, _filtered;

    public PostmanExplorerViewModel(IPostmanHost host)
    {
        _host = host;
        _host.Library.Changed += Show;
        Show();
    }

    PostmanLibrary Library => _host.Library;

    public ObservableCollection<PostmanNode> Roots { get; } = [];

    [ObservableProperty]
    public partial PostmanNode? Selected { get; set; }

    [ObservableProperty]
    public partial string Filter { get; set; } = "";

    partial void OnFilterChanged(string value) => Show();

    public bool IsEmpty => Library.Collections.Count == 0 && Library.Environments.Count == 0 && Library.Problems.Count == 0;

    public ObservableCollection<PostmanEnvironmentChoice> Environments { get; } = [];

    [ObservableProperty]
    public partial PostmanEnvironmentChoice? Environment { get; set; }

    partial void OnEnvironmentChanged(PostmanEnvironmentChoice? value)
    {
        if (!_choosing && value is not null)
            Library.ActiveId = value.Id;
    }

    /// <summary>The tree as the files are now, what was open kept open.</summary>
    void Show()
    {
        if (!_filtered)
            _open = [.. All(Roots).Where(n => n.IsExpanded).Select(n => n.Key)];
        var open = _open;
        _filtered = Filter.Trim().Length > 0;
        var selected = Selected?.Key;
        var first = Roots.Count == 0;
        Roots.Clear();
        foreach (var collection in Library.Collections)
        {
            var node = new PostmanNode(PostmanNodeKind.Collection, collection.Name, "collection:" + collection.File)
            {
                Collection = collection,
                File = collection.File,
                Detail = collection.File,
                IsOpened = Library.IsOpened(collection.File),
            };
            foreach (var item in collection.Items)
                if (Node(item) is { } child)
                    node.Children.Add(child);
            if (Filter.Trim().Length == 0 || node.Children.Count > 0 || Matches(collection.Name))
                Roots.Add(node);
            node.IsExpanded = Filter.Trim().Length > 0 || open.Contains(node.Key) || (first && Library.Collections.Count == 1);
        }
        if (Library.Environments.Count > 0 || Library.Globals is not null)
        {
            var heading = new PostmanNode(PostmanNodeKind.Environments, "Environments", "environments");
            foreach (var environment in Library.Environments.Concat(Library.Globals is { } globals ? [globals] : []))
                heading.Children.Add(new PostmanNode(PostmanNodeKind.Environment, environment.Name, "environment:" + environment.File)
                {
                    Environment = environment,
                    File = environment.File,
                    Detail = environment.File,
                    IsActive = !environment.IsGlobals && environment.Id == Library.ActiveId,
                    IsOpened = Library.IsOpened(environment.File),
                });
            heading.IsExpanded = first || open.Contains(heading.Key);
            if (Filter.Trim().Length == 0)
                Roots.Add(heading);
        }
        foreach (var (file, message) in Library.Problems)
            Roots.Add(new PostmanNode(PostmanNodeKind.Problem, Path.GetFileName(file), "problem:" + file) { File = file, Detail = message, IsOpened = Library.IsOpened(file) });
        Selected = All(Roots).FirstOrDefault(n => n.Key == selected);
        OnPropertyChanged(nameof(IsEmpty));

        _choosing = true;
        Environments.Clear();
        Environments.Add(new PostmanEnvironmentChoice(null, "No environment"));
        foreach (var environment in Library.Environments)
            Environments.Add(new PostmanEnvironmentChoice(environment.Id, environment.Name));
        Environment = Environments.FirstOrDefault(e => e.Id == Library.ActiveId) ?? Environments[0];
        _choosing = false;

        PostmanNode? Node(PostmanItem item)
        {
            var path = item.Collection.PathOf(item);
            var node = new PostmanNode(item.IsFolder ? PostmanNodeKind.Folder : PostmanNodeKind.Request, item.Name, $"item:{item.Collection.File}:{path}")
            {
                Item = item,
                Collection = item.Collection,
                Detail = item.IsFolder ? null : item.Read().Url,
            };
            if (item.IsFolder)
            {
                foreach (var child in item.Items)
                    if (Node(child) is { } shown)
                        node.Children.Add(shown);
                node.IsExpanded = Filter.Trim().Length > 0 || open.Contains(node.Key);
                return Filter.Trim().Length == 0 || node.Children.Count > 0 || Matches(item.Name) ? node : null;
            }
            return Matches($"{item.Method} {item.Name} {node.Detail}") ? node : null;
        }

        bool Matches(string text) => Filter.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    static IEnumerable<PostmanNode> All(IEnumerable<PostmanNode> nodes) => nodes.SelectMany(n => All(n.Children).Prepend(n));

    // ---- Dialogs ----------------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDialog))]
    public partial OverlayDialog? Dialog { get; set; }

    public bool HasDialog => Dialog is not null;

    async Task<T?> ShowAsync<T>(T dialog) where T : OverlayDialog
    {
        while (Dialog is { } open)
            await open.Result;
        Dialog = dialog;
        try
        {
            return await dialog.Result ? dialog : null;
        }
        finally
        {
            Dialog = null;
        }
    }

    async Task<string?> AskNameAsync(string title, string confirm, string initial = "") =>
        await ShowAsync(new InputDialog(title, "Name", "", confirm) { Text = initial }) is { } named ? named.Text.Trim() : null;

    // ---- What can be done -------------------------------------------------------------------------------------

    [RelayCommand]
    Task RefreshAsync() => Library.RefreshAsync();

    [RelayCommand]
    async Task NewCollectionAsync()
    {
        if (await AskNameAsync("New Collection", "Create") is { } name)
            await DoAsync(() => Library.CreateCollectionAsync(name));
    }

    [RelayCommand]
    async Task NewEnvironmentAsync()
    {
        if (await AskNameAsync("New Environment", "Create") is { } name)
            await DoAsync(async () => _host.Open(await Library.CreateEnvironmentAsync(name)));
    }

    [RelayCommand]
    async Task OpenFilesAsync()
    {
        if (await _host.PickFilesAsync("Open Postman collections and environments") is { Count: > 0 } files)
            await DoAsync(async () =>
            {
                if (await Library.OpenAsync(files) is { } problem)
                    _host.Status(problem);
            });
    }

    /// <summary>A row opened: a request, an environment; a collection or a folder opens in the tree.</summary>
    [RelayCommand]
    public void Open(PostmanNode? node)
    {
        switch (node)
        {
            case { IsRequest: true, Item: { } item }:
                _host.Open(item);
                break;
            case { IsEnvironment: true, Environment: { } environment }:
                _host.Open(environment);
                break;
            case { IsProblem: true, File: { } file }:
                _host.OpenFile(file);
                break;
            case not null:
                node.IsExpanded = !node.IsExpanded;
                break;
        }
    }

    [RelayCommand]
    void OpenFile(PostmanNode? node)
    {
        if (node?.File is { } file)
            _host.OpenFile(file);
    }

    [RelayCommand]
    void Run(PostmanNode? node)
    {
        if (node is { IsContainer: true, Collection: { } collection })
            _ = _host.ShowRun(collection, node.Item).RunAsync();
    }

    [RelayCommand]
    async Task NewRequestAsync(PostmanNode? node)
    {
        node ??= Selected;
        if (Place(node) is not var (collection, folder))
            return;
        if (await AskNameAsync("New Request", "Create") is { } name)
            Change(collection, () =>
            {
                var item = collection.Add(folder, name, isFolder: false);
                return () => _host.Open(item);
            }, folder);
    }

    [RelayCommand]
    async Task NewFolderAsync(PostmanNode? node)
    {
        node ??= Selected;
        if (Place(node) is not var (collection, folder))
            return;
        if (await AskNameAsync("New Folder", "Create") is { } name)
            Change(collection, () =>
            {
                collection.Add(folder, name, isFolder: true);
                return null;
            }, folder);
    }

    /// <summary>Where a new item of a row goes: in a collection or a folder, beside a request; the only collection without a row.</summary>
    (PostmanCollection Collection, PostmanItem? Folder)? Place(PostmanNode? node) => node switch
    {
        { IsCollection: true, Collection: { } collection } => (collection, null),
        { IsFolder: true, Item: { } folder } => (folder.Collection, folder),
        { IsRequest: true, Item: { } request } => (request.Collection, request.Parent),
        _ => Library.Collections is [var only] ? (only, null) : null,
    };

    [RelayCommand]
    async Task RenameAsync(PostmanNode? node)
    {
        switch (node)
        {
            case { IsItem: true, Item: { } item } when await AskNameAsync("Rename", "Rename", item.Name) is { } name:
                Change(item.Collection, () =>
                {
                    item.Name = name;
                    return null;
                }, item);
                break;
            case { IsCollection: true, Collection: { } collection } when await AskNameAsync("Rename Collection", "Rename", collection.Name) is { } name:
                Change(collection, () =>
                {
                    collection.Name = name;
                    return null;
                });
                break;
            case { IsEnvironment: true, Environment: { } environment } when await AskNameAsync("Rename Environment", "Rename", environment.Name) is { } name:
                await DoAsync(() =>
                {
                    environment.Put(name, environment.Variables);
                    Library.RaiseChanged();
                    return Task.CompletedTask;
                });
                break;
        }
    }

    [RelayCommand]
    void Duplicate(PostmanNode? node)
    {
        if (node is { IsItem: true, Item: { } item })
            Change(item.Collection, () =>
            {
                var copy = item.Collection.Duplicate(item);
                return copy.IsFolder ? null : () => _host.Open(copy);
            }, item);
    }

    [RelayCommand]
    async Task DeleteAsync(PostmanNode? node)
    {
        switch (node)
        {
            case { IsItem: true, Item: { } item }:
                var requests = item.Requests.Count();
                if (await ShowAsync(new ConfirmDialog(item.IsFolder ? "Delete Folder" : "Delete Request",
                        item.IsFolder && requests > 0 ? $"Delete {item.Name} and its {(requests == 1 ? "request" : $"{requests} requests")} from {item.Collection.Name}?" : $"Delete {item.Name} from {item.Collection.Name}?", "Delete", isDanger: true)) is null)
                    return;
                Change(item.Collection, () =>
                {
                    item.Collection.Remove(item);
                    foreach (var document in _host.Documents.OfType<PostmanRequestDocument>().Where(d => item.Requests.Any(d.Is)).ToList())
                        _host.CloseDocument(document);
                    return null;
                }, item);
                break;
            case { HasFile: true, IsOpened: true, File: { } opened }:
                await DoAsync(() => Library.ForgetAsync(opened));
                break;
            case { HasFile: true, File: { } file }:
                if (await ShowAsync(new ConfirmDialog("Delete File", $"Delete {Path.GetFileName(file)}?\n\n{file}", "Delete", isDanger: true)) is null)
                    return;
                await DoAsync(() =>
                {
                    File.Delete(file);
                    return Library.RefreshAsync();
                });
                break;
        }
    }

    [RelayCommand]
    void Use(PostmanNode? node)
    {
        if (node is { IsEnvironment: true, Environment: { IsGlobals: false } environment })
            Library.ActiveId = environment.Id;
    }

    /// <summary>
    /// A change of a collection, written to its file. One changed on disk meanwhile is read again instead, and the change is to
    /// be made on what it has now.
    /// </summary>
    void Change(PostmanCollection collection, Func<Action?> change, PostmanItem? on = null)
    {
        try
        {
            if (collection.IsStale || (on is not null && collection.Of(on.Node) is null))
            {
                _host.Status($"{collection.Name} was changed meanwhile: it was read again, do it again.");
                _ = Library.RefreshAsync();
                return;
            }
            var after = change();
            Library.Save(collection);
            after?.Invoke();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PostmanException)
        {
            _host.Status($"{collection.Name} was not saved: {ex.Message}");
        }
    }

    async Task DoAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PostmanException)
        {
            _host.Status(ex.Message);
        }
    }

    public void Dispose() => _host.Library.Changed -= Show;
}
