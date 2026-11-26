using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Karvon.Postman.ViewModels;

/// <summary>
/// A variable of an environment, as its row: its name, and its value (Karvon's, or else the file's where it came with one:
/// shown, and Karvon's once it is changed), hidden until it is shown.
/// </summary>
public sealed partial class PostmanVariableRow : ObservableObject
{
    readonly Action<PostmanVariableRow> _edited;

    public PostmanVariableRow(PostmanVariable variable, Action<PostmanVariableRow> edited)
    {
        Key = variable.Key;
        Initial = variable.Initial;
        Current = variable.Current ?? variable.Initial;
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
    public partial bool IsSecret { get; set; }

    /// <summary>It has a value Karvon keeps (an empty one is one too, once typed).</summary>
    public bool HasCurrent { get; private set; }

    /// <summary>Its value is hidden until its eye is clicked.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Mask), nameof(Eye))]
    public partial bool IsShown { get; set; }

    public char Mask => IsShown ? '\0' : '•';

    public string Eye => IsShown ? "EyeOff" : "Eye";

    /// <summary>What stands in place of a value taken away: the file's is the one used then.</summary>
    public string Hint => Initial.Length > 0 ? "In the file" : "Value";

    [RelayCommand]
    void Show() => IsShown = !IsShown;

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
        if (e.PropertyName is nameof(Enabled) or nameof(Key) or nameof(Initial) or nameof(Current) or nameof(IsSecret))
            _edited?.Invoke(this);
    }
}

/// <summary>
/// An environment's document: its variables' names (the file's) and values (Karvon's, in the vault, never in the file), and
/// whether agents may send requests with it, to which hosts. They get its values in no way.
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
        Kept();
    }

    // ---- Where it is kept, and who may send with it -----------------------------------------------------------

    /// <summary>Its file came with values, where any program reads them.</summary>
    public bool HasFileValues => _environment.HasFileValues;

    public bool IsLocked => _environment.IsLocked;

    /// <summary>What is typed here goes when Karvon closes: the vault has no passphrase.</summary>
    public bool IsForSession => !_environment.IsKept && !IsLocked;

    public bool AgentsMaySend
    {
        get => _environment.AgentsMaySend;
        set
        {
            if (value == _environment.AgentsMaySend || (value && HasFileValues))
                return;
            _environment.AgentsMaySend = value;
            Kept();
        }
    }

    public bool CanLetAgents => !HasFileValues;

    public string HostsText => _environment.Hosts.Except(_environment.AddedHosts, StringComparer.OrdinalIgnoreCase).ToList() is { Count: > 0 } hosts
        ? "Its own hosts: " + string.Join(", ", hosts) : "It has no host of its own yet (a variable with an address, as baseUrl).";

    /// <summary>The hosts added to its own, as they are typed.</summary>
    public string AddedHosts
    {
        get => string.Join(", ", _environment.AddedHosts);
        set
        {
            var hosts = value.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(h => Uri.TryCreate(h, UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? uri.Authority : h).ToList();
            if (hosts.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(_environment.AddedHosts))
                return;
            _environment.AddedHosts = hosts;
            Kept();
        }
    }

    /// <summary>One of its hosts is this machine, where an agent's own program may answer.</summary>
    public bool ReachesThisMachine => _environment.Hosts.Any(h => Karvon.Http.HttpRunner.IsLocal(h.Split(':')[0]) || h.StartsWith("127.", StringComparison.Ordinal) || h.StartsWith("0.0.0.0", StringComparison.Ordinal) || h.StartsWith('['));

    public bool WarnsOfThisMachine => AgentsMaySend && ReachesThisMachine;

    void Kept()
    {
        foreach (var name in new[] { nameof(HasFileValues), nameof(IsLocked), nameof(IsForSession), nameof(AgentsMaySend), nameof(CanLetAgents), nameof(HostsText), nameof(AddedHosts), nameof(WarnsOfThisMachine) })
            OnPropertyChanged(name);
    }

    /// <summary>The vault opened, or given a passphrase, so that values are kept.</summary>
    [RelayCommand]
    async Task OpenVaultAsync()
    {
        await Host.OpenSealedAsync();
        Kept();
    }

    /// <summary>The file's values moved to the vault; the file keeps the names.</summary>
    [RelayCommand]
    public async Task MoveToVaultAsync()
    {
        if (!_environment.IsKept && !await Host.OpenSealedAsync())
            return;
        try
        {
            await _environment.MoveToVaultAsync();
            Host.Status($"{Name}'s values are in Karvon's vault; its file keeps the names.");
        }
        catch (Exception ex) when (ex is PostmanException or IOException or UnauthorizedAccessException)
        {
            Host.Status($"{Name}'s values were not moved: {ex.Message}");
        }
        Host.Library.RaiseChanged();
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
            Kept();
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
