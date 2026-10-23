using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Karvon.Debugging;
using Karvon.Workspace;

namespace Karvon.ViewModels;

/// <summary>Debugger integration: F5 / F10 / F11, breakpoints, Locals, Watch and Call Stack.</summary>
public partial class MainViewModel
{
    /// <summary>
    /// The session the debugger's windows show (Call Stack, Locals, the editor's line) and F10, F11 and Continue act on:
    /// the one that stopped last, else the last started. One of <see cref="_sessions"/>.
    /// </summary>
    DebugSession? _session;

    /// <summary>The sessions running: one, or several started together (a compound's), each its program's.</summary>
    readonly List<DebugSession> _sessions = [];

    /// <summary>Each session's program (its configuration's name) and its postDebugTask.</summary>
    readonly Dictionary<DebugSession, (string Name, string? PostDebugTask)> _sessionLaunches = [];

    /// <summary>A compound's sessions that stop together (its stopAll): one that ends ends the rest.</summary>
    readonly HashSet<DebugSession> _stopTogether = [];

    /// <summary>The sessions that end with another (the projects that start with the startup project, under their debuggers).</summary>
    readonly Dictionary<DebugSession, List<DebugSession>> _endWith = [];

    /// <summary>The sessions stopped now, where (their thread, why): the one shown, and those waiting to be.</summary>
    readonly Dictionary<DebugSession, (int Thread, string Reason, string? Text)> _stoppedSessions = [];

    int _stoppedThread;
    string _stopReason = "";
    string? _stopText;
    string? _debuggedProject;
    string? _postDebugTask;

    /// <summary>The debuggers F5 starts for a launch configuration's type: the modules' (.NET's netcoredbg for coreclr and dotnet).</summary>
    IEnumerable<Modules.DebuggerInfo> Debuggers => Modules.SelectMany(m => m.Debuggers);

    public BreakpointStore Breakpoints { get; } = new();
    public ObservableCollection<StackFrameItem> CallStack { get; } = [];
    public ObservableCollection<VariableNode> Locals { get; } = [];
    public ObservableCollection<WatchItem> Watches { get; } = [];

    /// <summary>Raised when the debugger breaks, so the window can come to the front like VS does.</summary>
    public event Action? BreakModeEntered;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDebugging), nameof(IsInBreakMode), nameof(StartButtonText))]
    [NotifyCanExecuteChangedFor(nameof(StartDebuggingCommand), nameof(StartWithoutDebuggingCommand), nameof(StopCommand),
        nameof(RestartCommand), nameof(StepOverCommand), nameof(StepIntoCommand), nameof(StepOutCommand), nameof(BreakAllCommand),
        nameof(RunAllTestsCommand), nameof(RunFailedTestsCommand), nameof(RunSelectedTestsCommand), nameof(RepeatLastTestRunCommand), nameof(RefreshTestsCommand), nameof(DebugAllTestsCommand), nameof(DebugSelectedTestsCommand))]
    public partial DebugMode Mode { get; set; }

    [ObservableProperty]
    public partial StackFrameItem? SelectedFrame { get; set; }

    public bool IsDebugging => Mode != DebugMode.Design;

    public bool IsInBreakMode => Mode == DebugMode.Break;

    public string StartButtonText => Mode == DebugMode.Design ? LaunchProfile ?? "Start" : "Continue";

    void InitializeDebugging()
    {
        Breakpoints.Changed += (file, fromEditor) => _ = OnBreakpointsChangedAsync(file);
    }

    async Task OnBreakpointsChangedAsync(string file)
    {
        if (Solution is not null && !_restoringState)
        {
            _state.Breakpoints = Breakpoints.Snapshot();
            SaveSession();
        }
        // Breakpoints added or removed while debugging take effect immediately, in every session.
        var lines = Breakpoints.For(file).ToList();
        foreach (var session in _sessions.Where(s => !s.HasEnded).ToList())
        {
            try
            {
                var results = await session.SetBreakpointsAsync(file, lines);
                Breakpoints.SetVerification(file, lines, results);
            }
            catch (Exception ex) when (ex is DapException or IOException)
            {
                DebugOutput.Append("Failed to update breakpoints: " + ex.Message);
            }
        }
    }

    void LoadDebugState(Services.SolutionState state)
    {
        Breakpoints.Load(state.Breakpoints);
        Watches.Clear();
        foreach (var w in state.Watches)
            Watches.Add(new WatchItem(w));
    }

    void SaveDebugState(Services.SolutionState state)
    {
        state.Breakpoints = Breakpoints.Snapshot();
        state.Watches = Watches.Select(w => w.Expression).ToList();
    }

    // ---- Start / continue / stop -----------------------------------------------------------------------------

    bool CanStartDebugging => Mode == DebugMode.Break
        || (Mode == DebugMode.Design && Solution is not null && (IsFolderWorkspace ? StartupItem is not null : StartupProject is not null) && !IsBuilding && !IsRunning);

    [RelayCommand(CanExecute = nameof(CanStartDebugging))]
    async Task StartDebugging()
    {
        if (Mode == DebugMode.Break && _session is { } running)
        {
            await StepAsync(s => s.ContinueAsync(_stoppedThread));
            // Another session stopped meanwhile (a compound's other program): it is the one shown now.
            if (_stoppedSessions.Keys.FirstOrDefault(s => s != running) is { } waiting)
            {
                var (thread, reason, text) = _stoppedSessions[waiting];
                await OnStoppedAsync(waiting, thread, reason, text);
            }
            return;
        }
        await LaunchDebuggerAsync();
    }

    async Task LaunchDebuggerAsync()
    {
        // A folder debugs what is chosen on the toolbar: a launch configuration, or a task its module debugs.
        if (IsFolderWorkspace)
        {
            switch (StartupItem)
            {
                case { Compound: { } compound }:
                    await LaunchCompoundAsync(compound, noDebug: false);
                    break;
                case { Debug: { } launch }:
                    await LaunchConfigurationAsync(launch, noDebug: false);
                    break;
                case { Task: { } task }:
                    await RunProgramTaskAsync(task, $"{task.Name} has no debugger, so it runs without one: a launch configuration (.vscode/launch.json) debugs it.");
                    break;
            }
            return;
        }
        if (Solution is null || StartupProject is not { } project)
            return;
        if (!await RunBuildAsync("build", "Build", project.FilePath))
            return;
        if (StartupLaunch(project) is { } startup)
            await LaunchConfigurationAsync(startup, noDebug: false, withSolution: true);
    }

    /// <summary>
    /// F5 and Ctrl+F5 on the startup project: the launch configuration its module starts it with (.NET's: netcoredbg's
    /// "dotnet", the project with the launch profile and the build configuration chosen on the toolbar); null, said, when
    /// no module starts it.
    /// </summary>
    Modules.DebugLaunch? StartupLaunch(ProjectInfo project)
    {
        if (Modules.Select(m => m.StartupLaunch(project, LaunchProfile, Configuration)).FirstOrDefault(l => l is not null) is { } launch)
            return launch;
        SetStatus($"No module starts {project.Name}: its module is off (Tools > Modules).");
        return null;
    }

    /// <summary>
    /// A launch.json compound: its preLaunchTask, then its configurations, each beside those started before it, as VS
    /// Code starts them; with stopAll, one that ends ends the rest.
    /// </summary>
    async Task LaunchCompoundAsync(VsCodeLaunch.Compound compound, bool noDebug)
    {
        if (Solution is null || Mode != DebugMode.Design || IsRunning)
            return;
        DebugOutput.Clear();
        if (compound.PreLaunchTask is { } pre && !await RunPreLaunchTaskAsync(pre))
            return;
        var started = new List<DebugSession>();
        foreach (var launch in compound.Launches)
        {
            var before = _session;
            await LaunchConfigurationAsync(launch, noDebug, beside: started.Count > 0);
            if (_session is { } session && session != before && !started.Contains(session))
                started.Add(session);
        }
        if (compound.StopAll && started.Count > 1)
            foreach (var session in started)
                _stopTogether.Add(session);
    }

    /// <summary>
    /// A launch configuration started: its preLaunchTask first, then the debugger of its type with it (its variables put
    /// in), the breakpoints set; without them when it runs without debugging (Ctrl+F5, noDebug as VS Code has it, or the
    /// program its debugger runs for that). With the solution, the projects that start with the startup project start
    /// beside it. Beside the sessions running (a compound's next one), it starts one more.
    /// </summary>
    async Task LaunchConfigurationAsync(Modules.DebugLaunch launch, bool noDebug, bool withSolution = false, bool beside = false)
    {
        if (Solution is not { } solution || (Mode != DebugMode.Design && !beside) || IsRunning)
            return;
        SaveAll();
        if (!beside)
            DebugOutput.Clear();
        if (launch.PreLaunchTask is { } pre && !await RunPreLaunchTaskAsync(pre))
            return;
        SelectedOutput = DebugOutput;
        ShowToolWindow(ToolWindows.Output);
        if (Debuggers.FirstOrDefault(d => d.Types.Contains(launch.Type)) is not { } debugger)
        {
            DebugOutput.Append($"No debugger debugs '{launch.Type}' ({launch.Name}): its module brings one (Tools > Modules).");
            SetStatus($"No debugger for '{launch.Type}'");
            return;
        }
        if (debugger.PrepareAsync is { } prepare && !await prepare(launch))
            return;
        var configuration = TaskVariables.ForFile(launch.Configuration, solution.Directory, ActiveEditor?.FilePath);
        if (await AskInputsAsync(TaskInputs.TextsOf(configuration), launch.Inputs, DebugOutput.Append) is not { } answers)
            return;
        configuration = (System.Text.Json.Nodes.JsonObject)TaskInputs.Put(configuration, answers)!;
        if (noDebug && debugger.RunWithoutDebuggingAsync is { } run)
        {
            Modules.WorkspaceTask? program;
            try
            {
                program = await run(configuration);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                DebugOutput.Append($"{launch.Name} could not run: {ex.Message}");
                SetStatus($"{launch.Name} could not run: {ex.Message}");
                return;
            }
            if (program is not null)
            {
                await RunProgramTaskAsync(program, withSolution: withSolution);
                return;
            }
        }
        if (noDebug)
            configuration["noDebug"] = true;
        _debuggedProject = launch.Name;
        _postDebugTask = launch.PostDebugTask;
        if (Mode == DebugMode.Design)
        {
            Mode = DebugMode.Running;
            SetStatus("Running", StatusKind.Running);
        }
        DebugAdapter adapter;
        try
        {
            adapter = await debugger.StartAsync(new Modules.DebuggerStart(solution.Directory, configuration, TaskPath(), DebugOutput.Append, CancellationToken.None));
        }
        catch (Exception ex)
        {
            DebugOutput.Append($"{debugger.Name} could not start: {ex.Message}");
            if (_sessions.Count == 0)
            {
                Mode = DebugMode.Design;
                SetStatus("Ready", StatusKind.Ready);
            }
            return;
        }
        DebugOutput.Append(noDebug ? $"Running {launch.Name} (without debugging) with {debugger.Name}" : $"Debugging {launch.Name} with {debugger.Name}");
        // After the debugger completed the configuration (a "dotnet" one gets its launch profile's browser).
        _serverReady = ServerReadyAction.Of(configuration, DebugOutput.Append);
        if (withSolution)
            StartWithSolution(debugging: !noDebug);
        var breakpoints = noDebug ? new Dictionary<string, IReadOnlyList<int>>() : Breakpoints.All;
        try
        {
            _session = await DebugSession.StartAsync(adapter, launch.Request, configuration, breakpoints,
                (file, results) => Dispatcher.UIThread.Post(() => Breakpoints.SetVerification(file, breakpoints[file], results)),
                WireSession, CancellationToken.None);
        }
        catch (Exception ex)
        {
            DebugOutput.Append("Failed to start debugging: " + StartFailure(ex));
            _session = _sessions.LastOrDefault();
            if (_sessions.Count == 0)
            {
                StopStartedWithSolution();
                Mode = DebugMode.Design;
                SetStatus("Ready", StatusKind.Ready);
            }
            return;
        }
        if (withSolution && !noDebug && _session is { } main)
            await DebugWithSolutionAsync(main);
    }

    /// <summary>
    /// F5 on the startup project: the projects that start with it (a frontend's dev server) that their modules debug start
    /// under their own debuggers beside it, as Visual Studio debugs several startup projects, and end when it does.
    /// </summary>
    async Task DebugWithSolutionAsync(DebugSession main)
    {
        foreach (var project in WorkspaceProjects.Where(StartsWithSolution).ToList())
        {
            if (RunTaskOf(project) is not { Debug: { } launch })
                continue;
            DebugOutput.Append($"{project.Name} starts with the solution, under the debugger: {launch.Name}.");
            var before = _session;
            await LaunchConfigurationAsync(launch, noDebug: false, beside: true);
            if (_session is { } companion && companion != before && companion != main)
            {
                if (!_endWith.TryGetValue(main, out var companions))
                    _endWith[main] = companions = [];
                companions.Add(companion);
            }
        }
    }

    /// <summary>
    /// Why a session did not start: what the debugger or its connection said, or, for what neither should throw, the
    /// whole exception (where it was thrown is then worth seeing).
    /// </summary>
    static string StartFailure(Exception ex) =>
        ex is DapException or IOException or TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException
            ? ex.Message
            : ex.ToString();

    /// <summary>A debug session ended (the program exited, or Stop).</summary>
    event Action? DebugEnded;

    /// <summary>A launch configuration under its debugger (a test framework's tests: Vitest's), until its session ends: its Output (Debug).</summary>
    async Task<string> DebugToEndAsync(Modules.DebugLaunch launch)
    {
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnEnded() => ended.TrySetResult();
        DebugEnded += OnEnded;
        try
        {
            await LaunchConfigurationAsync(launch, noDebug: false);
            if (_session is not null || Mode != DebugMode.Design)
                await ended.Task;
            // The program's last lines come through the UI thread's queue.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            return DebugOutput.Document.Text;
        }
        finally
        {
            DebugEnded -= OnEnded;
        }
    }

    /// <summary>
    /// Routes a session's events into break mode, the Output window and breakpoint state. It is the session from now on,
    /// before the program runs: a program that stops at once (debugpy's, on its first lines) stops before the session is
    /// started, and its stop would find none.
    /// </summary>
    void WireSession(DebugSession session)
    {
        _session = session;
        _sessions.Add(session);
        _sessionLaunches[session] = (_debuggedProject ?? "", _postDebugTask);
        var ready = _serverReady;
        session.Stopped += (thread, reason, text) => Dispatcher.UIThread.Post(() => _ = OnStoppedAsync(session, thread, reason, text));
        // Another session's going on is not this one's: the one shown stays where it stopped.
        session.Continued += () => Dispatcher.UIThread.Post(() =>
        {
            _stoppedSessions.Remove(session);
            if (_session == session)
                BeginRunning();
        });
        session.Ended += code => Dispatcher.UIThread.Post(() => OnDebugEnded(session, code));
        session.BreakpointChanged += (file, line, verified) => Dispatcher.UIThread.Post(() =>
            Breakpoints.SetVerification(file, [line], [new BreakpointResult(verified, line, null)]));
        session.Output += (category, text) =>
        {
            if (category == "console")
            {
                // Adapter chatter: one message per line, minus the Just-My-Code notices about framework assemblies.
                if (text.StartsWith("You are debugging a Release build of", StringComparison.Ordinal))
                    return;
                if (!text.EndsWith('\n'))
                    text += "\n";
            }
            DebugOutput.AppendRaw(text);
            // The program listens: its launch configuration's serverReadyAction opens the browser.
            if (ready?.Match(text) is { } url)
                Dispatcher.UIThread.Post(() => OpenBrowser(url));
        };
    }

    /// <summary>The running launch configuration's serverReadyAction, until the program says it listens.</summary>
    ServerReadyAction? _serverReady;

    /// <summary>
    /// A launch configuration under its debugger, as F5 starts it (a test framework's attach to its test host): done once
    /// its session runs; it throws when there is none (why is in Output (Debug)).
    /// </summary>
    async Task StartDebuggingAsync(Modules.DebugLaunch launch)
    {
        var before = _session;
        await LaunchConfigurationAsync(launch, noDebug: false);
        if (_session is null || _session == before)
            throw new InvalidOperationException($"{launch.Name} did not start under the debugger: see Output (Debug).");
    }

    bool CanStepping => Mode == DebugMode.Break && _session is not null;

    [RelayCommand(CanExecute = nameof(CanStepping))]
    Task StepOver() => StepAsync(s => s.StepOverAsync(_stoppedThread));

    [RelayCommand(CanExecute = nameof(CanStepping))]
    Task StepInto() => StepAsync(s => s.StepInAsync(_stoppedThread));

    [RelayCommand(CanExecute = nameof(CanStepping))]
    Task StepOut() => StepAsync(s => s.StepOutAsync(_stoppedThread));

    /// <summary>
    /// Continue or a step. A program that runs to its end at once may take its connection with it before the debugger
    /// answers (js-debug's node): that is no error, the session ends.
    /// </summary>
    async Task StepAsync(Func<DebugSession, Task> step)
    {
        if (_session is not { } session)
            return;
        _stoppedSessions.Remove(session);
        BeginRunning();
        try
        {
            await step(session);
        }
        catch (IOException) when (session.HasEnded || _session != session)
        {
        }
        catch (Exception ex) when (ex is DapException or IOException)
        {
            DebugOutput.Append(ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunningUnderDebugger))]
    async Task BreakAll()
    {
        foreach (var session in _sessions.ToList())
            await session.PauseAsync();
    }

    bool IsRunningUnderDebugger => Mode == DebugMode.Running;

    bool CanStop => IsRunning || Mode != DebugMode.Design;

    [RelayCommand(CanExecute = nameof(CanStop))]
    async Task Stop()
    {
        _runProcess?.Kill();
        foreach (var session in _sessions.ToList())
            await session.TerminateAsync();
    }

    [RelayCommand(CanExecute = nameof(IsDebugging))]
    async Task Restart()
    {
        if (_sessions.Count > 0)
        {
            var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnEnded() => ended.TrySetResult();
            DebugEnded += OnEnded;
            try
            {
                foreach (var session in _sessions.ToList())
                    await session.TerminateAsync();
                await ended.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                DebugEnded -= OnEnded;
            }
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
        await LaunchDebuggerAsync();
    }

    [RelayCommand]
    void DeleteAllBreakpoints()
    {
        foreach (var file in Breakpoints.All.Keys.ToList())
            Breakpoints.Replace(file, []);
        foreach (var doc in Editors)
            doc.Host.AttachBreakpoints(Breakpoints);
    }

    // ---- Break mode ------------------------------------------------------------------------------------------

    void BeginRunning()
    {
        if (Mode == DebugMode.Design)
            return;
        Mode = DebugMode.Running;
        SetStatus("Running", StatusKind.Running);
        SelectedFrame = null;
        CallStack.Clear();
        Locals.Clear();
        foreach (var doc in Editors)
        {
            doc.Host.SetExecutionLine(null);
            doc.Host.DataTipProvider = null;
        }
    }

    /// <summary>How many stops were shown or are being shown: a stop shows only when no later one has come meanwhile.</summary>
    int _stops;

    async Task OnStoppedAsync(DebugSession session, int threadId, string reason, string? text)
    {
        if (!_sessions.Contains(session))
            return;
        _stoppedSessions[session] = (threadId, reason, text);
        var stop = ++_stops;
        if (reason == "exception")
            DebugOutput.Append($"Exception thrown: {text}");

        IReadOnlyList<StackFrameInfo> frames;
        try
        {
            frames = await session.StackTraceAsync(threadId);
        }
        catch (Exception ex) when (ex is DapException or IOException)
        {
            DebugOutput.Append("Could not read the call stack: " + ex.Message);
            frames = [];
        }
        // A later stop (another session's) is the one shown, this one waits its turn; one that went on is not shown.
        if (stop != _stops || !_stoppedSessions.ContainsKey(session))
            return;
        // The session that stopped is the one shown, and the one F10, F11 and Continue act on: all of it at once.
        _session = session;
        _stoppedThread = threadId;
        (_stopReason, _stopText) = (reason, text);
        Mode = DebugMode.Break;
        SetStatus(reason == "exception" ? $"Exception thrown: {text}" : "Break mode", StatusKind.Running);
        CallStack.Clear();
        foreach (var f in frames)
            CallStack.Add(new StackFrameItem(f.Id, f.Name, f.SourcePath, f.Line, f.Column));
        foreach (var doc in Editors)
            doc.Host.DataTipProvider = EvaluateDataTipAsync;
        SelectedFrame = CallStack.FirstOrDefault(f => f.HasSource) ?? CallStack.FirstOrDefault();
        ShowToolWindow(ToolWindows.Locals, ToolWindows.Watch, ToolWindows.CallStack);
        BreakModeEntered?.Invoke();
    }

    partial void OnSelectedFrameChanged(StackFrameItem? value)
    {
        if (value is null || Mode != DebugMode.Break)
            return;
        foreach (var doc in Editors)
            doc.Host.SetExecutionLine(null);
        if (value.HasSource && OpenDocument(value.SourcePath!) is { } doc2)
            Dispatcher.UIThread.Post(() => doc2.Host.SetExecutionLine(value.Line), DispatcherPriority.Loaded);
        _ = LoadLocalsAsync(value.Id);
        _ = RefreshWatchesAsync();
    }

    async Task LoadLocalsAsync(int frameId)
    {
        Locals.Clear();
        if (_session is not { } session)
            return;
        try
        {
            var scopes = await session.ScopesAsync(frameId);
            if (scopes.Count == 1)
            {
                foreach (var v in await session.VariablesAsync(scopes[0].VariablesReference))
                    Locals.Add(Node(session, v));
            }
            else
            {
                foreach (var scope in scopes)
                    Locals.Add(new VariableNode(scope.Name, "", null, () => Children(session, scope.VariablesReference)));
            }
        }
        catch (Exception ex) when (ex is DapException or IOException)
        {
            Locals.Add(new VariableNode("", ex.Message, null, null));
        }
    }

    static VariableNode Node(DebugSession session, VariableInfo v) =>
        new(v.Name, v.Value, v.Type, v.VariablesReference > 0 && v.Value != "null" ? () => Children(session, v.VariablesReference) : null);

    static async Task<IReadOnlyList<VariableNode>> Children(DebugSession session, int reference) =>
        (await session.VariablesAsync(reference)).Select(v => Node(session, v)).ToList();

    async Task<string?> EvaluateDataTipAsync(string expression)
    {
        if (_session is not { } session || Mode != DebugMode.Break)
            return null;
        try
        {
            return (await session.EvaluateAsync(expression, SelectedFrame?.Id, "hover")).Value;
        }
        catch (Exception ex) when (ex is DapException or IOException)
        {
            return null;
        }
    }

    [RelayCommand]
    async Task AddWatch(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return;
        var item = new WatchItem(expression.Trim());
        Watches.Add(item);
        SaveSession();
        await EvaluateWatchAsync(item);
    }

    [RelayCommand]
    void RemoveWatch(WatchItem? item)
    {
        if (item is null)
            return;
        Watches.Remove(item);
        SaveSession();
    }

    async Task RefreshWatchesAsync()
    {
        foreach (var w in Watches.ToList())
            await EvaluateWatchAsync(w);
    }

    async Task EvaluateWatchAsync(WatchItem item)
    {
        if (_session is not { } session || Mode != DebugMode.Break)
        {
            item.Value = "";
            item.Type = "";
            return;
        }
        try
        {
            var result = await session.EvaluateAsync(item.Expression, SelectedFrame?.Id, "watch");
            item.Value = result.Value;
            item.Type = result.Type ?? "";
        }
        catch (Exception ex) when (ex is DapException or IOException)
        {
            item.Value = ex is DapException dap ? dap.Message[(dap.Command.Length + 2)..] : ex.Message;
            item.Type = "";
        }
    }

    void OnDebugEnded(DebugSession session, int? exitCode)
    {
        if (!_sessions.Remove(session))
            return;
        _stoppedSessions.Remove(session);
        var (name, post) = _sessionLaunches.Remove(session, out var launched) ? launched : (_debuggedProject ?? "", _postDebugTask);
        // The projects that started with it under their debuggers end with it.
        if (_endWith.Remove(session, out var companions))
            foreach (var companion in companions.Where(_sessions.Contains))
                _ = companion.TerminateAsync();
        // A compound's that stop together: the rest go with it.
        if (_stopTogether.Remove(session))
        {
            foreach (var other in _stopTogether.ToList())
                _ = other.TerminateAsync();
            _stopTogether.Clear();
        }
        if (_sessions.Count > 0)
        {
            // Others run on: this program's end, and the windows show another.
            DebugOutput.Append(exitCode is { } exited ? $"The program '{name}' has exited with code {exited} (0x{exited:x})." : $"The program '{name}' has exited.");
            _ = session.DisposeAsync().AsTask();
            if (post is not null)
                _ = RunTask(WorkspaceTasks.FirstOrDefault(t => t.Name == post));
            if (_session == session)
            {
                _session = _sessions[^1];
                BeginRunning();
            }
            return;
        }
        _session = null;
        _debuggedProject = name;
        _postDebugTask = post;
        foreach (var doc in Editors)
        {
            doc.Host.SetExecutionLine(null);
            doc.Host.DataTipProvider = null;
        }
        CallStack.Clear();
        Locals.Clear();
        foreach (var w in Watches)
        {
            w.Value = "";
            w.Type = "";
        }
        Breakpoints.ClearVerification();
        StopStartedWithSolution();
        DebugEnded?.Invoke();
        // js-debug does not say how a program exited.
        DebugOutput.Append(exitCode is { } code
            ? $"The program '{_debuggedProject}' has exited with code {code} (0x{code:x})."
            : $"The program '{_debuggedProject}' has exited.");
        Mode = DebugMode.Design;
        SetStatus("Ready", Solution is null ? StatusKind.NoSolution : StatusKind.Ready);
        _ = session.DisposeAsync().AsTask();
        if (_postDebugTask is { } after)
        {
            _postDebugTask = null;
            _ = RunTask(WorkspaceTasks.FirstOrDefault(t => t.Name == after));
        }
    }
}
