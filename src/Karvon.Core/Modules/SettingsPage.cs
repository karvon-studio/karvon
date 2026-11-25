using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Karvon.Modules;

/// <summary>
/// A page of Settings (Ctrl+,): a category at its left, its groups of settings at its right. Karvon's own and the modules'
/// (<see cref="KarvonModule.SettingsPages"/>); pages of the same id are one page, groups of the same title one group.
/// </summary>
/// <param name="Id">"editor", "claude"...</param>
/// <param name="Icon">A <c>VsIcon</c> kind.</param>
public sealed record SettingsPage(string Id, string Title, string Icon, IReadOnlyList<SettingsGroup> Groups)
{
    /// <summary>The pages with those of the same id put together, in the order each first comes.</summary>
    public static IReadOnlyList<SettingsPage> Merge(IEnumerable<SettingsPage> pages) =>
    [
        .. pages.GroupBy(p => p.Id).Select(same => same.First() with
        {
            Groups = [.. same.SelectMany(p => p.Groups).GroupBy(g => g.Title).Select(g => new SettingsGroup(g.Key, [.. g.SelectMany(x => x.Settings)]))],
        }),
    ];
}

/// <summary>Settings under a heading.</summary>
public sealed record SettingsGroup(string Title, IReadOnlyList<Setting> Settings);

/// <summary>
/// A setting of a page: its title and what it does. It keeps no value of its own: it reads and writes where the setting is
/// kept, so what another part of Karvon changes (the View menu, a module's page) shows as it is.
/// </summary>
public abstract class Setting(string title, string? description) : ObservableObject
{
    public string Title => title;

    public string? Description => description;

    public bool HasDescription => Description is { Length: > 0 };

    /// <summary>Words the search finds it by besides its title and description (its name in settings.json).</summary>
    public string Keywords { get; init; } = "";

    /// <summary>Whether it applies now (Ollama's address while Ollama is chosen); always when null.</summary>
    public Func<bool>? ShownWhen { get; init; }

    public bool IsShown => ShownWhen?.Invoke() ?? true;

    /// <summary>What is wrong with it now, in red under it; null for nothing.</summary>
    public Func<string?>? Problem { get; init; }

    public string? ProblemText => Problem?.Invoke();

    /// <summary>It was changed here: the others read theirs again (some apply only with it).</summary>
    public event Action? Changed;

    protected void RaiseChanged() => Changed?.Invoke();

    /// <summary>What it shows read again.</summary>
    public virtual void Refresh()
    {
        OnPropertyChanged(nameof(IsShown));
        OnPropertyChanged(nameof(ProblemText));
    }

    /// <summary>Whether every word of a search is in it (or in <paramref name="context"/>, its page's and group's titles).</summary>
    public bool Matches(IReadOnlyList<string> words, string context)
    {
        var text = $"{context} {Title} {Description} {Keywords}";
        return words.All(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>A setting that is on or off.</summary>
public sealed class SwitchSetting(string title, string? description, Func<bool> get, Action<bool> set) : Setting(title, description)
{
    public bool IsOn
    {
        get => get();
        set
        {
            if (get() == value)
                return;
            set(value);
            OnPropertyChanged();
            RaiseChanged();
        }
    }

    public override void Refresh()
    {
        base.Refresh();
        OnPropertyChanged(nameof(IsOn));
    }
}

/// <summary>One of a setting's choices: its value, and its name as shown.</summary>
public sealed record SettingChoice(string Value, string Name)
{
    public override string ToString() => Name;
}

/// <summary>A setting that is one of a few choices; a value that is none of them is a choice as it is.</summary>
public sealed class ChoiceSetting(string title, string? description, IReadOnlyList<SettingChoice> choices, Func<string> get, Action<string> set)
    : Setting(title, description)
{
    public IReadOnlyList<SettingChoice> Choices => choices.Any(c => c.Value == get()) ? choices : [.. choices, new(get(), get())];

    public SettingChoice? Choice
    {
        get => Choices.FirstOrDefault(c => c.Value == get());
        set
        {
            if (value is null || value.Value == get())
                return;
            set(value.Value);
            OnPropertyChanged();
            RaiseChanged();
        }
    }

    public override void Refresh()
    {
        base.Refresh();
        OnPropertyChanged(nameof(Choices));
        OnPropertyChanged(nameof(Choice));
    }
}

/// <summary>A setting that is a text, or a list of them (one a line); kept when the focus leaves it.</summary>
public sealed class TextSetting(string title, string? description, Func<string> get, Action<string> set) : Setting(title, description)
{
    string? _shown;

    public string? Placeholder { get; init; }

    /// <summary>Several lines.</summary>
    public bool IsList { get; init; }

    /// <summary>Why a text is not kept (it is not an address); null when it is.</summary>
    public Func<string, string?>? Check { get; init; }

    /// <summary>Why the last text typed was not kept; null when it was.</summary>
    public string? Refused { get; private set; }

    public string Text
    {
        get => _shown = get();
        set
        {
            var text = value.Trim();
            Refused = Check?.Invoke(text);
            OnPropertyChanged(nameof(Refused));
            if (Refused is not null || text == get())
                return;
            set(text);
            OnPropertyChanged();
            RaiseChanged();
        }
    }

    /// <summary>Read again only when it changed elsewhere: what is being typed stays.</summary>
    public override void Refresh()
    {
        base.Refresh();
        if (_shown != get())
            OnPropertyChanged(nameof(Text));
    }

    /// <summary>A list's lines, those that are something, each once.</summary>
    public static List<string> Lines(string text) =>
        [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal)];
}

/// <summary>An entry of a <see cref="EntriesSetting"/>: its key, as shown.</summary>
public sealed record SettingEntry(string Key, string Text, IRelayCommand Remove);

/// <summary>A setting that is entries made elsewhere (the folders trusted), each of which can be removed here.</summary>
public sealed class EntriesSetting : Setting
{
    readonly Func<IEnumerable<(string Key, string Text)>> _get;
    readonly Action<string> _remove;

    public EntriesSetting(string title, string? description, Func<IEnumerable<(string Key, string Text)>> get, Action<string> remove) : base(title, description)
    {
        _get = get;
        _remove = remove;
        Read();
    }

    public ObservableCollection<SettingEntry> Entries { get; } = [];

    /// <summary>What shows when there is none.</summary>
    public string Empty { get; init; } = "None.";

    public bool IsEmpty => Entries.Count == 0;

    void Read()
    {
        var entries = _get().ToList();
        if (entries.SequenceEqual(Entries.Select(e => (e.Key, e.Text))))
            return;
        Entries.Clear();
        foreach (var (key, text) in entries)
            Entries.Add(new SettingEntry(key, text, new RelayCommand(() =>
            {
                _remove(key);
                Read();
                RaiseChanged();
            })));
        OnPropertyChanged(nameof(IsEmpty));
    }

    public override void Refresh()
    {
        base.Refresh();
        Read();
    }
}

/// <summary>A button of an <see cref="ActionSetting"/>.</summary>
public sealed class SettingAction : ObservableObject
{
    public SettingAction(string text, Func<Task> run)
    {
        Text = text;
        Command = new AsyncRelayCommand(async () =>
        {
            await run();
            Done?.Invoke();
        });
    }

    public string Text { get; }

    public IAsyncRelayCommand Command { get; }

    public Func<bool>? ShownWhen { get; init; }

    public bool IsShown => ShownWhen?.Invoke() ?? true;

    internal Action? Done { get; set; }

    internal void Refresh() => OnPropertyChanged(nameof(IsShown));
}

/// <summary>A setting changed in a place of its own (a dialog, a window): how it is now, and the buttons that go there.</summary>
public sealed class ActionSetting : Setting
{
    readonly Func<string?>? _state;

    public ActionSetting(string title, string? description, Func<string?>? state, params IReadOnlyList<SettingAction> actions) : base(title, description)
    {
        _state = state;
        Actions = actions;
        foreach (var action in actions)
            action.Done = RaiseChanged;
    }

    public IReadOnlyList<SettingAction> Actions { get; }

    /// <summary>How it is now ("Off", "On: GitHub"); null for nothing to say.</summary>
    public string? State => _state?.Invoke();

    public bool HasState => State is { Length: > 0 };

    public override void Refresh()
    {
        base.Refresh();
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(HasState));
        foreach (var action in Actions)
            action.Refresh();
    }
}
