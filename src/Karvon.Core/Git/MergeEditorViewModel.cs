using System.Collections.ObjectModel;
using System.Text;
using AvaloniaEdit.Document;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Karvon.ViewModels;

namespace Karvon.Git;

/// <summary>A conflict's place in the result: it moves as the result is edited.</summary>
public sealed class ConflictSegment(int index) : TextSegment
{
    public int Index => index;
}

/// <summary>A conflict in the merge editor's list: where it is, and how it was resolved.</summary>
public sealed partial class MergeConflictRow(int index, MergeConflict conflict) : ObservableObject
{
    public int Index => index;

    public MergeConflict Conflict => conflict;

    public string Title => $"Conflict {index + 1}";

    [ObservableProperty]
    public partial int Line { get; set; }

    /// <summary>"Unresolved", "Incoming", "Current", "Both", "Edited".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsResolved))]
    public partial string State { get; set; } = "Unresolved";

    public bool IsResolved => State != "Unresolved";
}

/// <summary>
/// Visual Studio's merge editor for a file git could not merge: the incoming side and the current side above (each the
/// file with its side of every conflict, the conflicts marked), the result below, editable; each conflict taken from
/// one side, the other, or both (current first), all at once if wanted; Accept Merge writes the result and stages it.
/// Claude can be asked to resolve it: its edit of the file is read back here.
/// </summary>
public sealed partial class MergeEditorViewModel : DocumentTab
{
    readonly MainViewModel _vm;
    readonly bool _bom;
    IReadOnlyList<MergeSegment> _segments = [];
    TextSegmentCollection<ConflictSegment> _places = null!;
    string _loaded = "";
    bool _replacing;

    public MergeEditorViewModel(MainViewModel vm, GitRepository repository, string path)
    {
        _vm = vm;
        Repository = repository;
        FilePath = path;
        var bytes = File.ReadAllBytes(path);
        _bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        // After the change's handlers: the conflicts' places have moved by then.
        Result.TextChanged += (_, _) =>
        {
            if (!_replacing)
                Follow();
        };
        Load(Encoding.UTF8.GetString(bytes, _bom ? 3 : 0, bytes.Length - (_bom ? 3 : 0)));
    }

    public GitRepository Repository { get; }

    public string FilePath { get; }

    public override string Key => "merge:" + FilePath;

    public override string Title => $"Merging - {Path.GetFileName(FilePath)}";

    public override string ToolTip => FilePath;

    public string FileName => Path.GetFileName(FilePath);

    /// <summary>The file with the incoming side (theirs) of every conflict; each conflict's lines (1-based start, count).</summary>
    [ObservableProperty]
    public partial string Incoming { get; private set; } = "";

    [ObservableProperty]
    public partial string Current { get; private set; } = "";

    public IReadOnlyList<(int Line, int Count)> IncomingRanges { get; private set; } = [];

    public IReadOnlyList<(int Line, int Count)> CurrentRanges { get; private set; } = [];

    [ObservableProperty]
    public partial string IncomingLabel { get; private set; } = "Incoming";

    [ObservableProperty]
    public partial string CurrentLabel { get; private set; } = "Current";

    /// <summary>The result: what Accept Merge writes.</summary>
    public TextDocument Result { get; } = new();

    public IEnumerable<ConflictSegment> Places => _places;

    public ObservableCollection<MergeConflictRow> Conflicts { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary), nameof(IsResolved))]
    public partial int Remaining { get; private set; }

    public bool IsResolved => Remaining == 0;

    public string Summary => Conflicts.Count == 0 ? "No conflicts left in the file."
        : Remaining == 0 ? $"{(Conflicts.Count == 1 ? "The conflict" : $"All {Conflicts.Count} conflicts")} resolved: Accept Merge writes the result and stages it."
        : $"{Remaining} of {Conflicts.Count} conflict{(Conflicts.Count == 1 ? "" : "s")} left.";

    /// <summary>A line of the result to show (a conflict taken, Next, Previous).</summary>
    public event Action<int>? RevealRequested;

    /// <summary>The panes' text changed (a load): they redraw.</summary>
    public event Action? Loaded;

    void Load(string text)
    {
        _loaded = text;
        _segments = MergeConflicts.Parse(text);
        var conflicts = _segments.Where(s => s.Conflict is not null).Select(s => s.Conflict!).ToList();
        if (conflicts.Count > 0)
        {
            CurrentLabel = conflicts[0].CurrentLabel is { Length: > 0 } ours ? $"Current ({ours})" : "Current";
            IncomingLabel = conflicts[0].IncomingLabel is { Length: > 0 } theirs ? $"Incoming ({theirs})" : "Incoming";
        }
        (Incoming, IncomingRanges) = Side(c => c.Incoming);
        (Current, CurrentRanges) = Side(c => c.Current);
        _replacing = true;
        Result.Text = text;
        Result.UndoStack.ClearAll();
        _places = new TextSegmentCollection<ConflictSegment>(Result);
        var offset = 0;
        var index = 0;
        foreach (var segment in _segments)
        {
            if (segment.Conflict is not null)
                _places.Add(new ConflictSegment(index++) { StartOffset = offset, Length = segment.Text.Length });
            offset += segment.Text.Length;
        }
        _replacing = false;
        Conflicts.Clear();
        for (var i = 0; i < conflicts.Count; i++)
            Conflicts.Add(new MergeConflictRow(i, conflicts[i]));
        Follow();
        Loaded?.Invoke();
    }

    (string Text, IReadOnlyList<(int, int)> Ranges) Side(Func<MergeConflict, string> side)
    {
        var text = new StringBuilder();
        var ranges = new List<(int, int)>();
        var line = 1;
        foreach (var segment in _segments)
        {
            var part = segment.Conflict is { } c ? side(c) : segment.Text;
            var count = part.Count(ch => ch == '\n');
            if (segment.Conflict is not null)
                ranges.Add((line, Math.Max(1, count)));
            text.Append(part);
            line += count;
        }
        return (text.ToString(), ranges);
    }

    /// <summary>Each conflict's line and state, as the result now has it.</summary>
    void Follow()
    {
        foreach (var place in _places)
        {
            var row = Conflicts[place.Index];
            row.Line = Result.GetLineByOffset(Math.Min(place.StartOffset, Result.TextLength)).LineNumber;
            var text = Result.GetText(place.StartOffset, place.Length);
            if (MergeConflicts.HasMarkers(text))
                row.State = "Unresolved";
            else if (row.State == "Unresolved")
                row.State = "Edited";
        }
        Remaining = Conflicts.Count(c => !c.IsResolved);
        OnPropertyChanged(nameof(Summary));
    }

    void Take(MergeConflictRow row, string text, string state)
    {
        var place = _places.First(p => p.Index == row.Index);
        var start = place.StartOffset;
        _replacing = true;
        Result.Replace(start, place.Length, text);
        // The replaced run is the conflict's place again (an edit at a segment's edge does not grow it).
        place.StartOffset = start;
        place.Length = text.Length;
        _replacing = false;
        row.State = state;
        Follow();
        RevealRequested?.Invoke(row.Line);
    }

    [RelayCommand]
    void TakeIncoming(MergeConflictRow? row)
    {
        if (row is not null)
            Take(row, row.Conflict.Incoming, "Incoming");
    }

    [RelayCommand]
    void TakeCurrent(MergeConflictRow? row)
    {
        if (row is not null)
            Take(row, row.Conflict.Current, "Current");
    }

    /// <summary>Both sides: the current one's lines, then the incoming one's.</summary>
    [RelayCommand]
    void TakeBoth(MergeConflictRow? row)
    {
        if (row is not null)
            Take(row, row.Conflict.Current + row.Conflict.Incoming, "Both");
    }

    [RelayCommand]
    void TakeAllIncoming()
    {
        foreach (var row in Conflicts.Where(c => !c.IsResolved).ToList())
            TakeIncoming(row);
    }

    [RelayCommand]
    void TakeAllCurrent()
    {
        foreach (var row in Conflicts.Where(c => !c.IsResolved).ToList())
            TakeCurrent(row);
    }

    [RelayCommand]
    void Reveal(MergeConflictRow? row)
    {
        if (row is not null)
            RevealRequested?.Invoke(row.Line);
    }

    /// <summary>The file changed on disk (Claude resolved it, an editor saved it): read again, unless the result was edited here.</summary>
    public void ReloadIfChanged()
    {
        if (!File.Exists(FilePath))
            return;
        var text = File.ReadAllText(FilePath);
        if (text == _loaded)
            return;
        if (!Result.UndoStack.IsOriginalFile && Result.Text != _loaded)
            return;
        Load(text);
    }

    /// <summary>The result written to the file; staged (resolved) when no conflict is left in it.</summary>
    [RelayCommand]
    public async Task AcceptMergeAsync()
    {
        if (Remaining > 0 && _vm.Dialogs is { } dialogs
            && await dialogs.ConfirmAsync("Accept Merge", $"{Remaining} conflicts are left with their markers. Write the file as it is, without staging it?") != true)
            return;
        var text = Result.Text;
        await File.WriteAllBytesAsync(FilePath, [.. (_bom ? Encoding.UTF8.Preamble : []), .. new UTF8Encoding(false).GetBytes(text)]);
        _loaded = text;
        if (Remaining == 0)
            await _vm.HistoryOperationAsync($"Resolve {Repository.Relative(FilePath)}", () => Repository.StageAsync([FilePath]));
        _vm.CloseMergeEditor(this);
    }

    /// <summary>Claude Code resolves it in the file; this editor reads its edit back.</summary>
    [RelayCommand]
    void AskClaude() => _vm.AskClaudeToResolve(this);
}
