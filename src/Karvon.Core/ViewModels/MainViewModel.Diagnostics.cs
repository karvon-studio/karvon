using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Karvon.Diagnostics;

namespace Karvon.ViewModels;

/// <summary>
/// Diagnostic Tools (Debug > Windows > Show Diagnostic Tools), as Visual Studio's: while the program runs (with the
/// debugger or without), its processes' CPU (a share of all the processors) and memory each second, as graphs; and what
/// the modules find out about one of them when asked (.NET: a memory snapshot's types, a CPU profile's functions).
/// </summary>
public partial class MainViewModel
{
    const int UsageHistoryLength = 60;
    DispatcherTimer? _usageTimer;
    UsageSample? _lastUsage;
    int? _usageRoot;
    bool _sampling;
    CancellationTokenSource? _diagnosticCts;

    /// <summary>The CPU graph's points (%, the last minute's).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<double> CpuHistory { get; private set; } = [];

    /// <summary>The memory graph's points (MB).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<double> MemoryHistory { get; private set; } = [];

    [ObservableProperty]
    public partial string CpuText { get; private set; } = "";

    [ObservableProperty]
    public partial string MemoryText { get; private set; } = "";

    public ObservableCollection<DiagnosedProcess> DiagnosticProcesses { get; } = [];

    [ObservableProperty]
    public partial DiagnosedProcess? SelectedDiagnosticProcess { get; set; }

    partial void OnSelectedDiagnosticProcessChanged(DiagnosedProcess? value)
    {
        AvailableDiagnostics.Clear();
        if (value is not null)
            foreach (var diagnostic in ProcessDiagnosticsOf())
                if (diagnostic.Applies(value))
                    AvailableDiagnostics.Add(diagnostic);
        OnPropertyChanged(nameof(HasDiagnostics));
        OnPropertyChanged(nameof(HasTimedDiagnostics));
    }

    /// <summary>The selected process's diagnostics (the buttons).</summary>
    public ObservableCollection<ProcessDiagnostic> AvailableDiagnostics { get; } = [];

    public bool HasDiagnostics => AvailableDiagnostics.Count > 0;

    /// <summary>One of them records for a while (the duration is chosen).</summary>
    public bool HasTimedDiagnostics => AvailableDiagnostics.Any(d => d.IsTimed);

    /// <summary>How long a CPU profile records (seconds).</summary>
    public IReadOnlyList<int> TraceDurations { get; } = [5, 10, 30];

    [ObservableProperty]
    public partial int SelectedTraceDuration { get; set; } = 5;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunDiagnosticCommand))]
    public partial bool IsRunningDiagnostic { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDiagnosticReport))]
    public partial DiagnosticReport? DiagnosticReport { get; private set; }

    public bool HasDiagnosticReport => DiagnosticReport is not null;

    [ObservableProperty]
    public partial string DiagnosticsMessage { get; private set; } = "Start the program (F5 or Ctrl+F5): its CPU and memory show here as it runs.";

    IEnumerable<ProcessDiagnostic> ProcessDiagnosticsOf() => Modules.SelectMany(m => m.ProcessDiagnostics);

    /// <summary>The process Karvon runs the program in: the one it started, else the debugger's.</summary>
    int? ProgramProcessId => _runProcess?.Id ?? _session?.ProcessId;

    [RelayCommand]
    void ShowDiagnosticTools() => ShowToolWindow(ToolWindows.DiagnosticTools);

    void WatchUsage()
    {
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not (nameof(IsRunning) or nameof(Mode)))
                return;
            var running = IsRunning || Mode != DebugMode.Design;
            if (running && _usageTimer is not { IsEnabled: true })
            {
                CpuHistory = [];
                MemoryHistory = [];
                _lastUsage = null;
                _usageRoot = null;
                DiagnosticsMessage = "";
                _usageTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, async (_, _) => await SampleUsageAsync());
                _usageTimer.Start();
            }
            else if (!running)
            {
                _usageTimer?.Stop();
                DiagnosticProcesses.Clear();
                SelectedDiagnosticProcess = null;
                if (CpuHistory.Count > 0)
                    DiagnosticsMessage = "The program has ended: its last minute is shown.";
            }
        };
    }

    /// <summary>A sample of the program's processes: the graphs moved on, the processes listed again when they changed.</summary>
    internal async Task SampleUsageAsync()
    {
        if (_sampling || ProgramProcessId is not { } root)
            return;
        _sampling = true;
        try
        {
            var (processes, sample) = await Task.Run(() =>
            {
                var all = ProcessUsage.Processes(root);
                return (all, ProcessUsage.Sample(all.Select(p => p.Pid)));
            });
            if (root != _usageRoot)
            {
                _usageRoot = root;
                _lastUsage = null;
            }
            if (_lastUsage is { } before)
            {
                var cpu = ProcessUsage.CpuPercent(before, sample);
                CpuHistory = [.. CpuHistory.TakeLast(UsageHistoryLength - 1), cpu];
                CpuText = $"{cpu:0}%";
            }
            _lastUsage = sample;
            var mb = sample.Memory / 1024.0 / 1024.0;
            MemoryHistory = [.. MemoryHistory.TakeLast(UsageHistoryLength - 1), mb];
            MemoryText = $"{mb:0.#} MB";
            if (!processes.Select(p => p.Pid).SequenceEqual(DiagnosticProcesses.Select(p => p.Pid)))
                ListDiagnosticProcesses(processes);
        }
        finally
        {
            _sampling = false;
        }
    }

    void ListDiagnosticProcesses(IReadOnlyList<DiagnosedProcess> processes)
    {
        var was = SelectedDiagnosticProcess?.Pid;
        DiagnosticProcesses.Clear();
        foreach (var process in processes)
            DiagnosticProcesses.Add(process);
        // The one chosen before, else the last a module can look at (dotnet run's app, not the SDK's dotnet), else the first.
        var diagnostics = ProcessDiagnosticsOf().ToList();
        SelectedDiagnosticProcess = processes.FirstOrDefault(p => p.Pid == was)
            ?? processes.LastOrDefault(p => diagnostics.Any(d => d.Applies(p)))
            ?? processes.FirstOrDefault();
    }

    bool CanRunDiagnostic => !IsRunningDiagnostic;

    [RelayCommand(CanExecute = nameof(CanRunDiagnostic))]
    async Task RunDiagnostic(ProcessDiagnostic? diagnostic)
    {
        if (diagnostic is null || SelectedDiagnosticProcess is not { } process)
            return;
        IsRunningDiagnostic = true;
        var cts = _diagnosticCts = new CancellationTokenSource();
        DiagnosticsMessage = diagnostic.IsTimed ? $"{diagnostic.Name}: {SelectedTraceDuration} seconds of {process.Label}..." : $"{diagnostic.Name} of {process.Label}...";
        try
        {
            async Task<bool> Confirm(string title, string message) =>
                Dialogs is not null && await Dispatcher.UIThread.InvokeAsync(() => Dialogs.ConfirmAsync(title, message)) == true;
            // What it says as it goes, until it ends (a line posted late never hides what it found).
            void Log(string line) => Dispatcher.UIThread.Post(() =>
            {
                if (_diagnosticCts == cts)
                    DiagnosticsMessage = line;
            });
            var report = await diagnostic.RunAsync(new DiagnosticRun(process, TimeSpan.FromSeconds(SelectedTraceDuration), Log, Confirm), cts.Token);
            _diagnosticCts = null;
            DiagnosticReport = report;
            DiagnosticsMessage = report.Summary;
        }
        catch (OperationCanceledException)
        {
            _diagnosticCts = null;
            DiagnosticsMessage = diagnostic.Name + " was canceled.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException)
        {
            _diagnosticCts = null;
            DiagnosticsMessage = $"{diagnostic.Name} failed: {ex.Message}";
        }
        finally
        {
            IsRunningDiagnostic = false;
            if (_diagnosticCts == cts)
                _diagnosticCts = null;
            cts.Dispose();
        }
    }

    [RelayCommand]
    void CancelDiagnostic() => _diagnosticCts?.Cancel();
}
