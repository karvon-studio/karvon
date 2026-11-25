using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Karvon.Postman.ViewModels;

/// <summary>A variable of an environment, as its row: the file's value, and this machine's over it.</summary>
public sealed partial class PostmanVariableRow : ObservableObject
{
    readonly Action<PostmanVariableRow> _edited;

    public PostmanVariableRow(PostmanVariable variable, Action<PostmanVariableRow> edited)
    {
        Key = variable.Key;
        Initial = variable.Initial;
        Current = variable.Current ?? "";
        HasCurrent = variable.Current is not null;
        Enabled = variable.Enabled;
        IsSecret = variable.IsSecret;
        _edited = edited;
    }

    [ObservableProperty]
    public partial bool Enabled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial string Key { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial string Initial { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial string Current { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Mask))]
    public partial bool IsSecret { get; set; }

    /// <summary>It has a value of this machine's (an empty one is one too, once typed).</summary>
    public bool HasCurrent { get; private set; }

    /// <summary>A secret's values are not shown as they are typed.</summary>
    public char Mask => IsSecret ? '•' : '\0';

    public bool IsEmpty => Key.Length == 0 && Initial.Length == 0 && Current.Length == 0;

    public PostmanVariable Variable => new(Key.Trim(), Initial) { Current = HasCurrent ? Current : null, Enabled = Enabled, IsSecret = IsSecret };

    partial void OnCurrentChanged(string value)
    {
        if (_edited is not null)
            HasCurrent = value.Length > 0;
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is not (nameof(Mask) or nameof(IsEmpty)))
            _edited?.Invoke(this);
    }
}

/// <summary>
/// An environment's document: its variables, with the value its file has (shared with whoever has the file) and the current
/// one (this machine's: what is typed here or set by a script; in Karvon's vault, never in the file).
/// </summary>
public sealed partial class PostmanEnvironmentDocument : PostmanDocument
{
    PostmanEnvironment _environment;
    bool _loading;
    string _saved = "";

    public PostmanEnvironmentDocument(IPostmanHost host, PostmanEnvironment environment) : base(host)
    {
        _environment = environment;
        host.Library.Changed += OnLibraryChanged;
        Load();
    }

    public string File => _environment.File;

    public override string Key => "postman-environment:" + File;

    public override string Title => Name + (IsDirty ? "*" : "");

    public override string ToolTip => File;

    protected override string SavedName => Name;

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    public ObservableCollection<PostmanVariableRow> Rows { get; } = [];

    /// <summary>It is what requests are sent with.</summary>
    public bool IsActive => Host.Library.ActiveId == _environment.Id;

    public bool CanBeActive => !_environment.IsGlobals && !IsActive;

    void OnLibraryChanged()
    {
        if (Host.Library.Environments.Append(Host.Library.Globals).FirstOrDefault(e => e?.File == File) is { } environment)
        {
            _environment = environment;
            // What a script set, or the file's: read again when nothing is being changed here.
            if (!IsDirty)
                Load();
        }
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(CanBeActive));
    }

    void Load()
    {
        _loading = true;
        Name = _environment.Name;
        Rows.Clear();
        foreach (var variable in _environment.Variables)
            Rows.Add(new PostmanVariableRow(variable, OnEdited));
        Rows.Add(Empty());
        _loading = false;
        _saved = Text();
        Touched();
    }

    PostmanVariableRow Empty() => new(new PostmanVariable("", ""), OnEdited);

    void OnEdited(PostmanVariableRow row)
    {
        if (_loading)
            return;
        if (Rows.Count == 0 || !Rows[^1].IsEmpty)
            Rows.Add(Empty());
        Touched();
    }

    partial void OnNameChanged(string value) => Touched();

    void Touched()
    {
        if (_loading)
            return;
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(Title));
    }

    IReadOnlyList<PostmanVariable> Variables => [.. Rows.Where(r => r.Key.Trim().Length > 0).Select(r => r.Variable).DistinctBy(v => v.Key)];

    string Text() => Name + "\n" + string.Join('\n', Variables.Select(v => v.ToString()));

    public override bool IsDirty => Text() != _saved;

    [RelayCommand]
    void Remove(PostmanVariableRow? row)
    {
        if (row is null || !Rows.Remove(row))
            return;
        if (Rows.Count == 0 || !Rows[^1].IsEmpty)
            Rows.Add(Empty());
        Touched();
    }

    [RelayCommand]
    void SaveEnvironment() => Save();

    public override bool Save()
    {
        if (!IsDirty)
            return false;
        try
        {
            _environment.Put(Name.Trim().Length > 0 ? Name.Trim() : _environment.Name, Variables);
            _saved = Text();
            Host.Library.RaiseChanged();
            Touched();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Host.Status($"{Name} was not saved: {ex.Message}");
            return false;
        }
    }

    [RelayCommand]
    void Use() => Host.Library.ActiveId = _environment.Id;

    public override void Dispose() => Host.Library.Changed -= OnLibraryChanged;
}
