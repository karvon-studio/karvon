using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Karvon.ViewModels;

/// <summary>
/// Hot Reload, as Visual Studio's: a run whose language applies code changes while it runs (Ctrl+F5 of a .NET project,
/// under dotnet watch) gets the changes as they are saved, and Debug > Hot Reload (Alt+F10) saves them to apply them
/// now. Any other run without the debugger (Ctrl+F5 of a Go, Python, Rust, Java program, a .NET console app...) restarts
/// with the changes instead, once a file of the workspace is saved: Restart on Save, as it is not Hot Reload. A Hot
/// Reload agent in the program (Node.js's) applies changes to the files it says it has, and asks for a restart for one
/// it cannot apply; the rest of the workspace's files restart the run. What each did is said in the status bar.
/// </summary>
public partial class MainViewModel
{
    /// <summary>Debug > Enable Hot Reload, Restart on Save where there is none (kept in the settings).</summary>
    public bool HotReload
    {
        get => Settings.HotReload;
        set
        {
            if (value == Settings.HotReload)
                return;
            Settings.HotReload = value;
            Settings.Save();
            OnPropertyChanged();
            ApplyHotReloadCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>The program running applies code changes as they are saved.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyHotReloadCommand))]
    public partial bool IsHotReloading { get; set; }

    /// <summary>How the run without the debugger in progress started (Ctrl+F5, Run Task), to start it again; null for a debug session's.</summary>
    Func<Task>? _rerun;

    /// <summary>A restart's build failed, or the program it started stopped with an error: the next save starts the run again.</summary>
    bool _restartFailed;

    bool _restartPending;

    /// <summary>The run was restarted by a save (a crash of its program then waits for the next save).</summary>
    bool _restartedOnSave;

    /// <summary>The files the run's Hot Reload agent applies changes to (their saves restart nothing).</summary>
    readonly HashSet<string> _liveFiles = new(OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);

    /// <summary>A run without the debugger goes on (its program, its console's, its sessions), which a save restarts.</summary>
    bool RestartsOnSave => Settings.HotReload && _rerun is not null && !IsHotReloading
        && (IsRunning || Mode == DebugMode.Running || _restartFailed);

    bool CanApplyChanges => IsHotReloading || RestartsOnSave;

    partial void OnIsRunningChanged(bool value) => ApplyHotReloadCommand.NotifyCanExecuteChanged();

    /// <summary>A run without the debugger starts (or, null, a debug session): what a save restarts.</summary>
    void StartedRun(Func<Task>? rerun)
    {
        _rerun = rerun;
        _restartFailed = false;
        _restartedOnSave = false;
        _liveFiles.Clear();
        ApplyHotReloadCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Debug > Hot Reload: the documents with changes saved, which the running program applies; a run that restarts on
    /// save restarts with them (with none to save, it restarts all the same).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanApplyChanges))]
    void ApplyHotReload()
    {
        var changed = Editors.Any(d => d.IsDirty);
        if (IsHotReloading && !changed)
        {
            SetStatus("Hot Reload: no changes to apply (every document is saved)", StatusKind.Running);
            return;
        }
        SaveAll();
        if (IsHotReloading)
            SetStatus("Hot Reload: the changes are saved, for the app to apply them...", StatusKind.Running);
        else if (!changed)
            _ = RestartRunAsync(null);
    }

    /// <summary>Restart on Save: a file of the workspace saved while a run without the debugger goes on.</summary>
    void RestartOnSave(string path)
    {
        if (!RestartsOnSave || Solution is not { } solution || !IsCodeOf(solution.Directory, path) || _liveFiles.Contains(Path.GetFullPath(path)))
            return;
        _ = RestartRunAsync(path);
    }

    /// <summary>
    /// A run ended: when a save restarted it and its program stopped with an error (a script with a mistake in it), the
    /// next save starts it again, as nodemon waits for a change after a crash.
    /// </summary>
    void RunEnded(string name, int? exitCode)
    {
        if (!_restartedOnSave || _restartPending || exitCode is 0 or null || _rerun is null || !Settings.HotReload)
            return;
        _restartFailed = true;
        SetStatus($"Restart on Save: {name} exited with code {exitCode}; it starts again when a file is saved", StatusKind.Ready);
        ApplyHotReloadCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    /// <summary>A file the program may be made of or read: in the workspace, not its notes (Markdown) nor an IDE's folders.</summary>
    static bool IsCodeOf(string workspace, string path)
    {
        var relative = Path.GetRelativePath(workspace, path);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            return false;
        if (Path.GetExtension(path).ToLowerInvariant() is ".md" or ".markdown")
            return false;
        return !relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => part is ".git" or ".vs" or ".vscode" or ".idea");
    }

    /// <summary>The run stopped and started again (the files saved together, as Save All saves them, restart it once).</summary>
    async Task RestartRunAsync(string? saved)
    {
        if (_restartPending || _rerun is not { } rerun)
            return;
        _restartPending = true;
        try
        {
            await Task.Delay(300);
            var name = _debuggedProject ?? StartupItem?.Name ?? StartupProject?.Name ?? "the program";
            SetStatus(saved is null ? $"Restart on Save: restarting {name}..." : $"Restart on Save: {Path.GetFileName(saved)} saved, restarting {name}...", StatusKind.Busy);
            await StopRunAsync();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnStarted(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
            {
                if (e.PropertyName is nameof(IsRunning) or nameof(Mode) && (IsRunning || Mode != DebugMode.Design))
                    started.TrySetResult();
            }
            PropertyChanged += OnStarted;
            Task run;
            try
            {
                _restartFailed = false;
                _liveFiles.Clear();
                run = rerun();
                await Task.WhenAny(started.Task, run);
            }
            finally
            {
                PropertyChanged -= OnStarted;
            }
            _restartedOnSave = true;
            // Started, and stopped already with an error (a script with a mistake in it): the next save starts it.
            if (started.Task.IsCompleted && !IsRunning && Mode == DebugMode.Design && _rerun is not null && _lastExitCode is not 0)
            {
                _restartFailed = true;
                SetStatus($"Restart on Save: {name} exited with code {_lastExitCode}; it starts again when a file is saved", StatusKind.Ready);
            }
            else if (started.Task.IsCompleted)
                SetStatus(saved is null ? $"Restart on Save: {name} restarted" : $"Restart on Save: {name} restarted with {Path.GetFileName(saved)}", StatusKind.Running);
            // Its build failed: the run is still on (Stop ends it), the next save (of the fix) starts it.
            else if (_rerun is not null)
            {
                _restartFailed = true;
                SetStatus($"Restart on Save: {name} did not start (see the Error List and Output); it starts again when a file is saved", StatusKind.Ready);
            }
            ApplyHotReloadCommand.NotifyCanExecuteChanged();
            StopCommand.NotifyCanExecuteChanged();
        }
        finally
        {
            _restartPending = false;
        }
    }

    /// <summary>The run stopped as Stop stops it, once it has ended (its program, console and sessions).</summary>
    async Task StopRunAsync()
    {
        await StopCore();
        var until = DateTime.UtcNow.AddSeconds(15);
        while ((IsRunning || Mode != DebugMode.Design) && DateTime.UtcNow < until)
            await Task.Delay(50);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }
}
