using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Karvon.ViewModels;

namespace Karvon.Postman.ViewModels;

/// <summary>What the Postman windows need of Karvon.</summary>
public interface IPostmanHost
{
    PostmanLibrary Library { get; }

    string? Workspace { get; }

    IEnumerable<DocumentTab> Documents { get; }

    PostmanRequestDocument? Open(PostmanItem item);

    PostmanEnvironmentDocument Open(PostmanEnvironment environment);

    PostmanRunDocument ShowRun(PostmanCollection collection, PostmanItem? folder);

    /// <summary>A document out of the window, without asking.</summary>
    void CloseDocument(DocumentTab document);

    Task<bool?> ConfirmAsync(string title, string message);

    Task<IReadOnlyList<string>> PickFilesAsync(string title);

    void OpenFile(string path);

    void Status(string text);
}

/// <summary>A document of the module's: what is changed in it is saved by Ctrl+S, or asked about when it closes.</summary>
public abstract class PostmanDocument(IPostmanHost host) : DocumentTab
{
    protected IPostmanHost Host => host;

    /// <summary>Its name in the question before it closes.</summary>
    protected abstract string SavedName { get; }

    /// <summary>Closed by the tab's ×: asked about first when it has changes.</summary>
    public override bool CloseByOwner()
    {
        if (!IsDirty)
            return false;
        _ = CloseAsync();
        return true;
    }

    /// <summary>Closed, its changes saved or not as the user says; false when they cancelled.</summary>
    public async Task<bool> CloseAsync()
    {
        if (IsDirty)
        {
            var answer = await host.ConfirmAsync("Save changes", $"Save the changes to {SavedName}?");
            if (answer is null || (answer == true && !Save()))
                return false;
        }
        host.CloseDocument(this);
        return true;
    }
}

/// <summary>A row of a request's: a key, a value, whether it is sent.</summary>
public sealed partial class PostmanRow : ObservableObject
{
    readonly PostmanRows _rows;

    public PostmanRow(PostmanRows rows, PostmanPair pair)
    {
        _rows = rows;
        Key = pair.Key;
        Value = pair.Value;
        Enabled = pair.Enabled;
        Description = pair.Description;
        IsFile = pair.IsFile;
        Ready = true;
    }

    bool Ready { get; }

    [ObservableProperty]
    public partial bool Enabled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial string Key { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial string Value { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial string Description { get; set; }

    /// <summary>A form's field that is a file, its path the value.</summary>
    [ObservableProperty]
    public partial bool IsFile { get; set; }

    public bool IsEmpty => Key.Length == 0 && Value.Length == 0 && Description.Length == 0;

    public bool CanRemove => !IsEmpty && !_rows.HasFixedKeys;

    public PostmanPair Pair => new(Key, Value) { Enabled = Enabled, Description = Description, IsFile = IsFile };

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!Ready || e.PropertyName == nameof(CanRemove))
            return;
        if (e.PropertyName == nameof(IsEmpty))
            OnPropertyChanged(nameof(CanRemove));
        else
            _rows.OnEdited(this);
    }
}

/// <summary>Rows as a table edits them: an empty one is always last, to type the next in (not where the keys are given: a path's variables).</summary>
public sealed partial class PostmanRows(bool fixedKeys = false, bool canBeFiles = false) : ObservableObject
{
    bool _loading;

    public ObservableCollection<PostmanRow> Rows { get; } = [];

    public bool HasFixedKeys => fixedKeys;

    public bool CanBeFiles => canBeFiles;

    public bool IsEmpty => Rows.All(r => r.IsEmpty);

    public int Count => Rows.Count(r => !r.IsEmpty && r.Enabled);

    /// <summary>A row was changed, added or removed (by the user).</summary>
    public event Action? Edited;

    public IReadOnlyList<PostmanPair> Pairs => [.. Rows.Where(r => !r.IsEmpty).Select(r => r.Pair)];

    public void Load(IEnumerable<PostmanPair> pairs)
    {
        _loading = true;
        Rows.Clear();
        foreach (var pair in pairs)
            Rows.Add(new PostmanRow(this, pair));
        AddEmpty();
        _loading = false;
        Counted();
    }

    void AddEmpty()
    {
        if (!fixedKeys && (Rows.Count == 0 || !Rows[^1].IsEmpty))
            Rows.Add(new PostmanRow(this, new PostmanPair("", "")));
    }

    internal void OnEdited(PostmanRow row)
    {
        if (_loading)
            return;
        if (row == Rows.LastOrDefault())
            AddEmpty();
        Counted();
        Edited?.Invoke();
    }

    [RelayCommand]
    void Remove(PostmanRow? row)
    {
        if (row is null || fixedKeys || !Rows.Remove(row))
            return;
        AddEmpty();
        Counted();
        Edited?.Invoke();
    }

    void Counted()
    {
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(IsEmpty));
    }
}
