namespace Karvon.Debugging;

/// <summary>
/// A breakpoint's settings, as Visual Studio's Breakpoint Settings has them: break only when a condition is true or at
/// a hit count (the debugger's own expression: "5", "&gt;= 5", "% 2"), log a message instead of breaking ({x} puts in
/// a value), or keep it but off.
/// </summary>
public sealed record BreakpointSettings(string? Condition = null, string? HitCondition = null, string? LogMessage = null, bool Enabled = true)
{
    public static readonly BreakpointSettings Default = new();

    public bool IsLogpoint => LogMessage is { Length: > 0 };

    public bool HasConditions => Condition is { Length: > 0 } || HitCondition is { Length: > 0 };

    /// <summary>"when x > 5, hit count >= 3, logs 'total={total}'", as the Breakpoints window says it.</summary>
    public string Text => string.Join(", ", new[]
    {
        Condition is { Length: > 0 } c ? $"when {c}" : "",
        HitCondition is { Length: > 0 } h ? $"hit count {h}" : "",
        LogMessage is { Length: > 0 } m ? $"logs '{m}'" : "",
    }.Where(s => s.Length > 0));
}

/// <summary>A breakpoint as a debugger is given it (DAP's SourceBreakpoint): an off one is not.</summary>
public sealed record SourceBreakpoint(int Line, string? Condition = null, string? HitCondition = null, string? LogMessage = null);

/// <summary>A breakpoint's settings as a solution keeps them.</summary>
public sealed class SavedBreakpoint
{
    public int Line { get; set; }

    public string? Condition { get; set; }

    public string? HitCondition { get; set; }

    public string? LogMessage { get; set; }

    public bool Enabled { get; set; } = true;
}

/// <summary>Line breakpoints per file (1-based lines), their settings, plus what the debugger reported about each one.</summary>
public sealed class BreakpointStore
{
    readonly Dictionary<string, SortedSet<int>> _lines = new(StringComparer.Ordinal);
    readonly Dictionary<(string File, int Line), bool> _verified = [];
    readonly Dictionary<(string File, int Line), BreakpointSettings> _settings = [];

    /// <summary>Raised with the file whose breakpoint lines changed; the flag is true when the editor itself caused it.</summary>
    public event Action<string, bool>? Changed;

    /// <summary>Raised when the debugger reported (or forgot) whether a file's breakpoints bind. Redraw only.</summary>
    public event Action<string>? VerificationChanged;

    public IReadOnlyCollection<int> For(string file) => _lines.TryGetValue(file, out var set) ? set : [];

    public bool Has(string file, int line) => _lines.TryGetValue(file, out var set) && set.Contains(line);

    /// <summary>null = no session has reported on this breakpoint yet.</summary>
    public bool? IsVerified(string file, int line) => _verified.TryGetValue((file, line), out var v) ? v : null;

    public IReadOnlyDictionary<string, IReadOnlyList<int>> All =>
        _lines.Where(kv => kv.Value.Count > 0).ToDictionary(kv => kv.Key, kv => (IReadOnlyList<int>)kv.Value.ToList());

    public int Count => _lines.Values.Sum(s => s.Count);

    public BreakpointSettings SettingsOf(string file, int line) => _settings.GetValueOrDefault((file, line)) ?? BreakpointSettings.Default;

    /// <summary>Every breakpoint, by file and line, with its settings (the Breakpoints window's).</summary>
    public IEnumerable<(string File, int Line, BreakpointSettings Settings)> Everything =>
        _lines.OrderBy(kv => kv.Key, StringComparer.Ordinal).SelectMany(kv => kv.Value.Select(line => (kv.Key, line, SettingsOf(kv.Key, line))));

    /// <summary>A file's breakpoints as its debugger is given them: the ones on, their conditions and messages.</summary>
    public IReadOnlyList<SourceBreakpoint> SourceBreakpoints(string file) =>
        [.. For(file).Select(line => (Line: line, Settings: SettingsOf(file, line))).Where(b => b.Settings.Enabled)
            .Select(b => new SourceBreakpoint(b.Line, Blank(b.Settings.Condition), Blank(b.Settings.HitCondition), Blank(b.Settings.LogMessage)))];

    /// <summary>Every file's breakpoints as the debugger is given them (a file whose breakpoints are all off has none).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<SourceBreakpoint>> AllSource =>
        _lines.Keys.Select(file => (File: file, Breakpoints: SourceBreakpoints(file))).Where(f => f.Breakpoints.Count > 0).ToDictionary(f => f.File, f => f.Breakpoints);

    static string? Blank(string? text) => text is { Length: > 0 } t && t.Trim().Length > 0 ? t.Trim() : null;

    public void Toggle(string file, int line)
    {
        if (!_lines.TryGetValue(file, out var set))
            _lines[file] = set = [];
        if (!set.Remove(line))
            set.Add(line);
        else
            _settings.Remove((file, line));
        _verified.Remove((file, line));
        Changed?.Invoke(file, false);
    }

    /// <summary>A breakpoint's settings (the breakpoint made when there is none).</summary>
    public void Configure(string file, int line, BreakpointSettings settings)
    {
        if (!_lines.TryGetValue(file, out var set))
            _lines[file] = set = [];
        set.Add(line);
        if (settings == BreakpointSettings.Default)
            _settings.Remove((file, line));
        else
            _settings[(file, line)] = settings;
        _verified.Remove((file, line));
        Changed?.Invoke(file, false);
    }

    public void SetEnabled(string file, int line, bool enabled)
    {
        if (Has(file, line))
            Configure(file, line, SettingsOf(file, line) with { Enabled = enabled });
    }

    public void Remove(string file, int line)
    {
        if (Has(file, line))
            Toggle(file, line);
    }

    /// <summary>
    /// The editor moved breakpoints because lines were inserted or deleted above them: the lines in the order they had
    /// (each breakpoint's settings go with it).
    /// </summary>
    public void Replace(string file, IEnumerable<int> lines)
    {
        var moved = lines.ToList();
        var updated = new SortedSet<int>(moved);
        _lines.TryGetValue(file, out var current);
        if (current is not null && current.SetEquals(updated))
            return;
        var before = current?.ToList() ?? [];
        var settings = new Dictionary<int, BreakpointSettings>();
        for (var i = 0; i < before.Count && i < moved.Count; i++)
            if (_settings.TryGetValue((file, before[i]), out var s))
                settings.TryAdd(moved[i], s);
        // A breakpoint gone takes its settings with it.
        foreach (var key in _settings.Keys.Where(k => k.File == file).ToList())
            _settings.Remove(key);
        foreach (var (line, s) in settings)
            _settings[(file, line)] = s;
        _lines[file] = updated;
        foreach (var key in _verified.Keys.Where(k => k.File == file).ToList())
            _verified.Remove(key);
        Changed?.Invoke(file, true);
    }

    public void SetVerification(string file, IReadOnlyList<int> lines, IReadOnlyList<BreakpointResult> results)
    {
        for (var i = 0; i < lines.Count && i < results.Count; i++)
            _verified[(file, lines[i])] = results[i].Verified;
        VerificationChanged?.Invoke(file);
    }

    public void ClearVerification()
    {
        var files = _verified.Keys.Select(k => k.File).Distinct().ToList();
        _verified.Clear();
        foreach (var f in files)
            VerificationChanged?.Invoke(f);
    }

    public void Load(IReadOnlyDictionary<string, List<int>> saved, IReadOnlyDictionary<string, List<SavedBreakpoint>>? settings = null)
    {
        _lines.Clear();
        _verified.Clear();
        _settings.Clear();
        foreach (var (file, lines) in saved)
            _lines[file] = new SortedSet<int>(lines);
        foreach (var (file, list) in settings ?? new Dictionary<string, List<SavedBreakpoint>>())
            foreach (var s in list)
                if (Has(file, s.Line))
                    _settings[(file, s.Line)] = new BreakpointSettings(s.Condition, s.HitCondition, s.LogMessage, s.Enabled);
    }

    public Dictionary<string, List<int>> Snapshot() =>
        _lines.Where(kv => kv.Value.Count > 0).ToDictionary(kv => kv.Key, kv => kv.Value.ToList());

    /// <summary>The breakpoints that have settings of their own, as a solution keeps them.</summary>
    public Dictionary<string, List<SavedBreakpoint>> SettingsSnapshot() =>
        _settings.Where(kv => Has(kv.Key.File, kv.Key.Line)).GroupBy(kv => kv.Key.File).ToDictionary(g => g.Key, g => g.Select(kv => new SavedBreakpoint
        {
            Line = kv.Key.Line, Condition = kv.Value.Condition, HitCondition = kv.Value.HitCondition, LogMessage = kv.Value.LogMessage, Enabled = kv.Value.Enabled,
        }).OrderBy(b => b.Line).ToList());
}
