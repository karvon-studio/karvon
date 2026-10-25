using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Karvon.Debugging;

namespace Karvon.ViewModels;

/// <summary>An exception kind a debugger can break on, checked or not (Exception Settings).</summary>
public sealed partial class ExceptionFilterItem(string debugger, ExceptionFilter filter, bool isChecked, Action<ExceptionFilterItem> changed) : ObservableObject
{
    public string Debugger => debugger;

    public ExceptionFilter Filter => filter;

    public string Label => filter.Label.Length > 0 ? filter.Label : filter.Filter;

    public string? Description => filter.Description;

    [ObservableProperty]
    public partial bool IsChecked { get; set; } = isChecked;

    partial void OnIsCheckedChanged(bool value) => changed(this);
}

/// <summary>A line of the Immediate window: what was asked, and what the debugger said.</summary>
public sealed record ImmediateEntry(string Expression, string Result, bool IsError);

/// <summary>A row of the Breakpoints window.</summary>
public sealed partial class BreakpointItem(BreakpointStore store, string file, int line, BreakpointSettings settings, bool? verified) : ObservableObject
{
    public string File => file;

    public int Line => line;

    public BreakpointSettings Settings => settings;

    public string Name => $"{Path.GetFileName(file)}, line {line}";

    public string Conditions => settings.Text;

    public bool HasConditions => Conditions.Length > 0;

    /// <summary>A logpoint's diamond, a conditional one's plus, a hollow one off or not bound.</summary>
    public string Glyph => !settings.Enabled ? "○" : settings.IsLogpoint ? "◆" : settings.HasConditions ? "⊕" : verified == false ? "○" : "●";

    [ObservableProperty]
    public partial bool IsEnabled { get; set; } = settings.Enabled;

    partial void OnIsEnabledChanged(bool value) => store.SetEnabled(file, line, value);
}

/// <summary>
/// The debugger's other windows: Breakpoints (each one on or off, its condition, hit count and log message edited),
/// Exception Settings (the exceptions the debugger breaks on, as it offers them), and Immediate (an expression
/// evaluated where the program stopped, as Visual Studio's Immediate window does).
/// </summary>
public partial class MainViewModel
{
    // ---- Exception Settings ---------------------------------------------------------------------------------------

    /// <summary>The exceptions the last debugger offered (or the one it last offered, before it runs again).</summary>
    public ObservableCollection<ExceptionFilterItem> ExceptionFilters { get; } = [];

    [ObservableProperty]
    public partial string? ExceptionDebugger { get; private set; }

    public bool HasExceptionFilters => ExceptionFilters.Count > 0;

    /// <summary>
    /// The filters a debugger starting now breaks on: those chosen for its type before (that it still offers), else its
    /// own defaults; Exception Settings shows them. Called as the debugger starts (off the UI thread).
    /// </summary>
    internal IReadOnlyList<string> ExceptionFiltersFor(string debugger, DebugCapabilities capabilities)
    {
        var offered = capabilities.ExceptionFilters;
        IReadOnlyList<string> chosen;
        lock (Settings)
        {
            chosen = Settings.ExceptionBreakpoints.TryGetValue(debugger, out var kept)
                ? [.. kept.Where(f => offered.Any(o => o.Filter == f))]
                : [.. offered.Where(f => f.Default).Select(f => f.Filter)];
            if (offered.Count > 0)
                Settings.ExceptionFilters[debugger] = [.. offered];
        }
        Dispatcher.UIThread.Post(() => ShowExceptionFilters(debugger, offered, chosen));
        return chosen;
    }

    void ShowExceptionFilters(string debugger, IReadOnlyList<ExceptionFilter> offered, IReadOnlyList<string> chosen)
    {
        ExceptionFilters.Clear();
        foreach (var filter in offered)
            ExceptionFilters.Add(new ExceptionFilterItem(debugger, filter, chosen.Contains(filter.Filter), OnExceptionFilterChanged));
        ExceptionDebugger = offered.Count > 0 ? debugger : null;
        OnPropertyChanged(nameof(HasExceptionFilters));
    }

    /// <summary>Exception Settings before a debugger runs: what it offered last time.</summary>
    void ShowKnownExceptionFilters()
    {
        if (ExceptionFilters.Count > 0 || Settings.ExceptionFilters.Count == 0)
            return;
        var (debugger, offered) = Settings.ExceptionFilters.First();
        var chosen = Settings.ExceptionBreakpoints.TryGetValue(debugger, out var kept) ? kept : [.. offered.Where(f => f.Default).Select(f => f.Filter)];
        ShowExceptionFilters(debugger, offered, chosen);
    }

    /// <summary>A filter checked or not: kept for the debugger's type, and told to its sessions running now.</summary>
    void OnExceptionFilterChanged(ExceptionFilterItem item)
    {
        var chosen = ExceptionFilters.Where(f => f.Debugger == item.Debugger && f.IsChecked).Select(f => f.Filter.Filter).ToList();
        lock (Settings)
            Settings.ExceptionBreakpoints[item.Debugger] = chosen;
        Settings.Save();
        foreach (var session in _sessions.Where(s => !s.HasEnded && s.Capabilities.ExceptionFilters.Any(f => f.Filter == item.Filter.Filter)).ToList())
            _ = SetExceptionFiltersAsync(session, chosen);
    }

    async Task SetExceptionFiltersAsync(DebugSession session, IReadOnlyList<string> filters)
    {
        try
        {
            await session.SetExceptionBreakpointsAsync(filters);
        }
        catch (Exception ex) when (ex is DapException or IOException)
        {
            DebugOutput.Append("Failed to update the exception settings: " + ex.Message);
        }
    }

    [RelayCommand]
    void ShowBreakpointsWindow() => ShowToolWindow(ToolWindows.Breakpoints);

    [RelayCommand]
    void ShowImmediateWindow() => ShowToolWindow(ToolWindows.Immediate);

    [RelayCommand]
    void ShowExceptionSettings()
    {
        ShowKnownExceptionFilters();
        ShowToolWindow(ToolWindows.ExceptionSettings);
    }

    // ---- Immediate --------------------------------------------------------------------------------------------------

    public ObservableCollection<ImmediateEntry> Immediate { get; } = [];

    readonly List<string> _immediateHistory = [];

    /// <summary>The expressions asked before, oldest first (Up and Down go through them).</summary>
    public IReadOnlyList<string> ImmediateHistory => _immediateHistory;

    /// <summary>An expression evaluated in the frame shown (an assignment changes the program's state: Locals and Watch follow).</summary>
    [RelayCommand]
    public async Task EvaluateImmediate(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return;
        expression = expression.Trim();
        _immediateHistory.Remove(expression);
        _immediateHistory.Add(expression);
        if (_session is not { } session || Mode != DebugMode.Break)
        {
            Immediate.Add(new ImmediateEntry(expression, "The expression can be evaluated only while the program is stopped (break mode).", true));
            return;
        }
        try
        {
            var result = await session.EvaluateAsync(expression, SelectedFrame?.Id, "repl");
            Immediate.Add(new ImmediateEntry(expression, result.Type is { Length: > 0 } type ? $"{result.Value}  ({type})" : result.Value, false));
        }
        catch (Exception ex) when (ex is DapException or IOException)
        {
            Immediate.Add(new ImmediateEntry(expression, ex is DapException dap ? dap.Message[(dap.Command.Length + 2)..] : ex.Message, true));
        }
        if (SelectedFrame is { } frame)
        {
            _ = LoadLocalsAsync(frame.Id);
            _ = RefreshWatchesAsync();
        }
    }

    [RelayCommand]
    void ClearImmediate() => Immediate.Clear();

    // ---- Breakpoints ------------------------------------------------------------------------------------------------

    public ObservableCollection<BreakpointItem> BreakpointItems { get; } = [];

    /// <summary>The breakpoint whose settings are edited below the list.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedBreakpoint))]
    public partial BreakpointItem? SelectedBreakpoint { get; set; }

    public bool HasSelectedBreakpoint => SelectedBreakpoint is not null;

    [ObservableProperty]
    public partial string BreakpointCondition { get; set; } = "";

    [ObservableProperty]
    public partial string BreakpointHitCondition { get; set; } = "";

    [ObservableProperty]
    public partial string BreakpointLogMessage { get; set; } = "";

    partial void OnSelectedBreakpointChanged(BreakpointItem? value)
    {
        BreakpointCondition = value?.Settings.Condition ?? "";
        BreakpointHitCondition = value?.Settings.HitCondition ?? "";
        BreakpointLogMessage = value?.Settings.LogMessage ?? "";
    }

    void ShowBreakpoints()
    {
        var keep = SelectedBreakpoint is { } s ? (s.File, s.Line) : default;
        BreakpointItems.Clear();
        foreach (var (file, line, settings) in Breakpoints.Everything)
            BreakpointItems.Add(new BreakpointItem(Breakpoints, file, line, settings, Breakpoints.IsVerified(file, line)));
        SelectedBreakpoint = BreakpointItems.FirstOrDefault(b => (b.File, b.Line) == keep);
    }

    /// <summary>The settings edited below the list made the breakpoint's: its condition, hit count and log message.</summary>
    [RelayCommand]
    void ApplyBreakpointSettings()
    {
        if (SelectedBreakpoint is not { } item)
            return;
        Breakpoints.Configure(item.File, item.Line, new BreakpointSettings(Blank(BreakpointCondition), Blank(BreakpointHitCondition), Blank(BreakpointLogMessage), item.Settings.Enabled));
        SetStatus($"Breakpoint at {item.Name}: {(Breakpoints.SettingsOf(item.File, item.Line).Text is { Length: > 0 } text ? text : "breaks every time")}.");
    }

    static string? Blank(string text) => text.Trim().Length > 0 ? text.Trim() : null;

    [RelayCommand]
    void GoToBreakpoint(BreakpointItem? item)
    {
        if (item is not null && File.Exists(item.File))
            NavigateTo(item.File, new Lsp.LspRange(new Lsp.LspPosition(item.Line - 1, 0), new Lsp.LspPosition(item.Line - 1, 0)));
    }

    [RelayCommand]
    void DeleteBreakpoint(BreakpointItem? item)
    {
        if (item is not null)
            Breakpoints.Remove(item.File, item.Line);
    }

    [RelayCommand]
    void EnableAllBreakpoints(string? enabled)
    {
        var on = enabled != "false";
        foreach (var (file, line, settings) in Breakpoints.Everything.ToList())
            if (settings.Enabled != on)
                Breakpoints.SetEnabled(file, line, on);
    }

    /// <summary>
    /// A breakpoint's settings to edit (the editor's Conditions... and Actions...): the breakpoint made when there is
    /// none, chosen in the Breakpoints window, a logpoint's message started when asked for one.
    /// </summary>
    public void EditBreakpoint(string file, int line, bool logpoint = false)
    {
        if (!Breakpoints.Has(file, line))
            Breakpoints.Toggle(file, line);
        ShowToolWindow(ToolWindows.Breakpoints);
        SelectedBreakpoint = BreakpointItems.FirstOrDefault(b => b.File == file && b.Line == line);
        if (logpoint && BreakpointLogMessage.Length == 0)
            BreakpointLogMessage = "Reached line " + line;
    }
}
