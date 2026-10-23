using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Karvon.Build;
using Karvon.Lsp;
using Karvon.Modules;
using Karvon.Workspace;

namespace Karvon.ViewModels;

/// <summary>
/// The open folder's tasks: the modules' (its package.json scripts...) and its .vscode/tasks.json's; and its launch.json's
/// configurations. In a folder opened as it is, Build runs its build task, Run All Tests its test task, and F5 and Ctrl+F5
/// what is chosen on the toolbar (a launch configuration or a run task); Build > Run Task runs any task, in a solution too
/// (whose own build, run and tests are .NET's).
/// </summary>
public partial class MainViewModel
{
    /// <summary>The tasks of no kind and the background ones running now (a watch, a dev server), by name.</summary>
    readonly Dictionary<string, ProcessRunner> _otherTasks = new(StringComparer.Ordinal);

    OutputChannel? _tasksOutput;
    OutputChannel? _testsOutput;

    /// <summary>The last test task run: its name and exit code (for Claude's run_tests).</summary>
    (string Task, int ExitCode)? _lastTestTask;

    public ObservableCollection<WorkspaceTask> WorkspaceTasks { get; } = [];

    /// <summary>A folder's launch configurations, then its run tasks: the toolbar's choice in place of a startup project.</summary>
    public ObservableCollection<StartupItem> StartupItems { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartWithoutDebuggingCommand), nameof(StartDebuggingCommand))]
    public partial StartupItem? StartupItem { get; set; }

    /// <summary>A folder opened as it is: its tasks build, run and test it (a solution or a project has .NET's).</summary>
    public bool IsFolderWorkspace => Solution is { IsFolder: true };

    /// <summary>The task Build runs in a folder: its default build task, else its first.</summary>
    public WorkspaceTask? BuildTask => DefaultOf(WorkspaceTaskKind.Build);

    /// <summary>The task Run All Tests runs in a folder.</summary>
    public WorkspaceTask? TestTask => DefaultOf(WorkspaceTaskKind.Test);

    WorkspaceTask? DefaultOf(WorkspaceTaskKind kind) =>
        WorkspaceTasks.Where(t => t.Kind == kind && !t.IsBackground).OrderByDescending(t => t.IsDefault).FirstOrDefault();

    /// <summary>Output (Tasks): the tasks of no kind and the background ones.</summary>
    public OutputChannel TasksOutput => _tasksOutput ??= ((IWorkbench)this).Output("Tasks");

    /// <summary>Output (Tests): a folder's test task.</summary>
    public OutputChannel TestsOutput => _testsOutput ??= ((IWorkbench)this).Output("Tests");

    public bool IsTaskRunning(WorkspaceTask task) => _otherTasks.ContainsKey(task.Name);

    partial void OnSolutionChanged(SolutionInfo? value) => OnPropertyChanged(nameof(IsFolderWorkspace));

    partial void OnStartupItemChanged(StartupItem? value)
    {
        if (Solution is not null && value is not null && !_restoringState)
        {
            _state.StartupTask = value.Name;
            SaveSession();
        }
    }

    /// <summary>The tasks and launch configurations read again (the folder opened, a file they come from saved); the startup item kept by its name.</summary>
    void RefreshWorkspaceTasks(string? startup = null)
    {
        startup ??= StartupItem?.Name;
        var tasks = new List<WorkspaceTask>();
        var launches = new List<DebugLaunch>();
        if (Solution is { } solution)
        {
            if (IsFolderWorkspace)
            {
                try
                {
                    launches.AddRange(VsCodeLaunch.Read(solution.Directory));
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    TasksOutput.Append($"{VsCodeLaunch.FileOf(solution.Directory)} could not be read: {ex.Message}");
                }
            }
            // tasks.json's first: one the user wrote wins over a module's of the same name.
            try
            {
                tasks.AddRange(VsCodeTasks.Read(solution.Directory));
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                TasksOutput.Append($"{VsCodeTasks.FileOf(solution.Directory)} could not be read: {ex.Message}");
            }
            foreach (var module in Modules)
            {
                try
                {
                    tasks.AddRange(module.WorkspaceTasks(solution.Directory).Where(t => tasks.All(known => known.Name != t.Name)));
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    TasksOutput.Append($"{module.Name} could not read the tasks: {ex.Message}");
                }
            }
        }
        WorkspaceTasks.Clear();
        StartupItems.Clear();
        foreach (var launch in launches)
            StartupItems.Add(StartupItem.Of(launch));
        foreach (var task in tasks)
        {
            WorkspaceTasks.Add(task);
            if (IsFolderWorkspace && task.Kind == WorkspaceTaskKind.Run && StartupItems.All(i => i.Name != task.Name))
                StartupItems.Add(StartupItem.Of(task));
        }
        StartupItem = StartupItems.FirstOrDefault(i => i.Name == startup) ?? StartupItems.FirstOrDefault(i => i.Debug?.Source == VsCodeLaunch.Source)
            ?? StartupItems.FirstOrDefault(i => i.Task?.IsDefault == true) ?? StartupItems.FirstOrDefault();
        OnPropertyChanged(nameof(BuildTask));
        OnPropertyChanged(nameof(TestTask));
        BuildSolutionCommand.NotifyCanExecuteChanged();
        RebuildSolutionCommand.NotifyCanExecuteChanged();
        RunAllTestsCommand.NotifyCanExecuteChanged();
        DebugAllTestsCommand.NotifyCanExecuteChanged();
        StartWithoutDebuggingCommand.NotifyCanExecuteChanged();
        StartDebuggingCommand.NotifyCanExecuteChanged();
    }

    /// <summary>A file was saved: one the tasks come from (tasks.json, launch.json, a module's package.json) reads them again.</summary>
    void OnWorkspaceFileSaved(string path)
    {
        var name = Path.GetFileName(path);
        if (Solution is not null && (path == VsCodeTasks.FileOf(Solution.Directory) || path == VsCodeLaunch.FileOf(Solution.Directory)
            || Modules.Any(m => m.WorkspaceTaskFiles.Contains(name))))
            RefreshWorkspaceTasks();
    }

    /// <summary>The workspace closes: what its tasks started stops.</summary>
    void StopWorkspaceTasks()
    {
        foreach (var runner in _otherTasks.Values)
            runner.Kill();
        _otherTasks.Clear();
        WorkspaceTasks.Clear();
        StartupItems.Clear();
        StartupItem = null;
    }

    /// <summary>
    /// Build > Run Task: a build task builds, a run task runs as the program, a test task tests; one of no kind or a
    /// background one runs in Output (Tasks) beside the rest, and run again while it runs, it stops.
    /// </summary>
    [RelayCommand]
    async Task RunTask(WorkspaceTask? task)
    {
        switch (task)
        {
            case null:
                return;
            case { Kind: WorkspaceTaskKind.Build, IsBackground: false }:
                await RunBuildTaskAsync(task);
                return;
            case { Kind: WorkspaceTaskKind.Run, IsBackground: false } when !IsRunning:
                if (StartupItems.FirstOrDefault(i => i.Task == task) is { } item)
                    StartupItem = item;
                await RunProgramTaskAsync(task);
                return;
            case { Kind: WorkspaceTaskKind.Test, IsBackground: false }:
                await RunTestTaskAsync(task);
                return;
            default:
                await RunOtherTaskAsync(task);
                return;
        }
    }

    /// <summary>The PATH of the tasks: the modules' tool folders (Node.js's bin...) before Karvon's own.</summary>
    string TaskPath() =>
        string.Join(Path.PathSeparator, Modules.SelectMany(m => m.ToolDirectories).Distinct().Append(Environment.GetEnvironmentVariable("PATH") ?? ""));

    /// <summary>Starts a task (the variables of the editor's file put in, its program found in the tasks' PATH).</summary>
    ProcessRunner StartTask(WorkspaceTask task, Action<string> output)
    {
        task = TaskVariables.ForFile(task, Solution?.Directory ?? task.WorkingDirectory, ActiveEditor?.FilePath);
        var path = task.Environment?.GetValueOrDefault("PATH") ?? TaskPath();
        var program = TaskVariables.Find(task.Program, path, task.WorkingDirectory)
            ?? throw new InvalidOperationException($"'{task.Program}' was not found on the PATH.");
        var environment = new Dictionary<string, string>(task.Environment ?? new Dictionary<string, string>(), StringComparer.Ordinal) { ["PATH"] = path };
        return ProcessRunner.Start(program, task.Arguments, task.WorkingDirectory, output, environment);
    }

    static bool IsStartFailure(Exception ex) => ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException;

    /// <summary>A folder's Build: its build task, the Output window and the Error List fed as by a .NET build.</summary>
    async Task<bool> RunBuildTaskAsync(WorkspaceTask task)
    {
        if (IsBuilding)
            return false;
        SaveAll();
        IsBuilding = true;
        SelectedOutput = BuildOutput;
        ShowToolWindow(ToolWindows.Output);
        BuildOutput.Clear();
        _buildErrors.Clear();
        RefreshErrorList();
        SetStatus("Build started...", StatusKind.Busy);
        var sw = Stopwatch.StartNew();
        BuildOutput.Append($"Build started at {DateTime.Now:HH:mm}: {task.Name}");
        BuildOutput.Append("> " + task.CommandLine);
        var cts = _buildCts = new CancellationTokenSource();
        int exitCode;
        try
        {
            // tsc writes file(line,col): error CODE: message, as MSBuild does, relative to where it ran. npm first echoes the
            // script's command ("> tsc -p ."), which is no error even when it reads like one.
            using var runner = StartTask(task, line =>
            {
                BuildOutput.Append(line);
                if (!line.StartsWith("> ", StringComparison.Ordinal) && BuildOutputParser.Parse(line) is { } d)
                    Dispatcher.UIThread.Post(() => AddBuildError(d with { File = d.File is null ? null : Path.GetFullPath(d.File, task.WorkingDirectory) }));
            });
            exitCode = await runner.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            BuildOutput.Append("Build was canceled.");
            exitCode = -1;
        }
        catch (Exception ex) when (IsStartFailure(ex))
        {
            BuildOutput.Append($"{task.Name} could not start: {ex.Message}");
            exitCode = -1;
        }
        finally
        {
            IsBuilding = false;
        }
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        return ReportBuild("Build", task.Name, exitCode == 0, sw.Elapsed, null);
    }

    /// <summary>
    /// A folder's Start Without Debugging: the run task as the program, its output in Output (Debug) until it ends or Stop
    /// (a note first, when there is one to say).
    /// </summary>
    async Task RunProgramTaskAsync(WorkspaceTask task, string? note = null)
    {
        if (IsRunning || IsBuilding || Mode != DebugMode.Design)
            return;
        SaveAll();
        DebugOutput.Clear();
        SelectedOutput = DebugOutput;
        ShowToolWindow(ToolWindows.Output);
        if (note is not null)
            DebugOutput.Append(note);
        DebugOutput.Append("> " + task.CommandLine);
        _lastExitCode = null;
        ProcessRunner runner;
        try
        {
            runner = StartTask(task, DebugOutput.Append);
        }
        catch (Exception ex) when (IsStartFailure(ex))
        {
            DebugOutput.Append($"{task.Name} could not start: {ex.Message}");
            SetStatus($"{task.Name} could not start: {ex.Message}");
            return;
        }
        IsRunning = true;
        SetStatus($"Running {task.Name}", StatusKind.Running);
        _runProcess = runner;
        var exit = await runner.WaitAsync();
        DebugOutput.Append($"The program '{task.Name}' has exited with code {exit} (0x{exit:x}).");
        _lastExitCode = exit;
        if (_runProcess == runner)
            _runProcess = null;
        runner.Dispose();
        IsRunning = false;
        SetStatus("Ready", Solution is null ? StatusKind.NoSolution : StatusKind.Ready);
    }

    /// <summary>A folder's Run All Tests: its test task, in Output (Tests); passed when it exits with 0.</summary>
    async Task RunTestTaskAsync(WorkspaceTask task)
    {
        if (IsTesting || IsBuilding)
            return;
        SaveAll();
        IsTesting = true;
        TestsOutput.Clear();
        SelectedOutput = TestsOutput;
        ShowToolWindow(ToolWindows.Output);
        TestsOutput.Append("> " + task.CommandLine);
        SetStatus($"Running {task.Name}...", StatusKind.Busy);
        int exit;
        var cts = _testCts = new CancellationTokenSource();
        try
        {
            using var runner = StartTask(task, TestsOutput.Append);
            exit = await runner.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            TestsOutput.Append("The test run was canceled.");
            exit = -1;
        }
        catch (Exception ex) when (IsStartFailure(ex))
        {
            TestsOutput.Append($"{task.Name} could not start: {ex.Message}");
            exit = -1;
        }
        finally
        {
            IsTesting = false;
        }
        _lastTestTask = (task.Name, exit);
        TestsOutput.Append(exit == 0 ? $"========== {task.Name}: passed ==========" : $"========== {task.Name}: failed (exit code {exit}) ==========");
        SetStatus(exit == 0 ? "Tests passed" : "Tests failed", Solution is null ? StatusKind.NoSolution : StatusKind.Ready);
    }

    /// <summary>
    /// A task of no kind or a background one, in Output (Tasks) beside the rest; run again while it runs, it stops. Its exit
    /// code once it ends; null when it did not start, or was stopped.
    /// </summary>
    async Task<int?> RunOtherTaskAsync(WorkspaceTask task)
    {
        if (_otherTasks.Remove(task.Name, out var running))
        {
            running.Kill();
            return null;
        }
        SaveAll();
        SelectedOutput = TasksOutput;
        ShowToolWindow(ToolWindows.Output);
        TasksOutput.Append($"> {task.Name}: {task.CommandLine}");
        ProcessRunner runner;
        try
        {
            runner = StartTask(task, TasksOutput.Append);
        }
        catch (Exception ex) when (IsStartFailure(ex))
        {
            TasksOutput.Append($"{task.Name} could not start: {ex.Message}");
            SetStatus($"{task.Name} could not start: {ex.Message}");
            return null;
        }
        _otherTasks[task.Name] = runner;
        SetStatus($"{task.Name} started");
        var exit = await runner.WaitAsync();
        TasksOutput.Append($"{task.Name} exited with code {exit}.");
        if (_otherTasks.GetValueOrDefault(task.Name) == runner)
            _otherTasks.Remove(task.Name);
        runner.Dispose();
        return exit;
    }

    /// <summary>
    /// A launch configuration's preLaunchTask, by its name: a build task builds (the Error List fed), a background one
    /// starts (a watch: the session does not wait for it), another runs to its end in Output (Tasks). Whether to go on.
    /// </summary>
    async Task<bool> RunPreLaunchTaskAsync(string name)
    {
        if (WorkspaceTasks.FirstOrDefault(t => t.Name == name) is not { } task)
            return PreLaunchFailed($"The preLaunchTask '{name}' is not a task of the folder (.vscode/tasks.json, package.json scripts).");
        if (task.IsBackground)
        {
            if (!IsTaskRunning(task))
                _ = RunOtherTaskAsync(task);
            return true;
        }
        if (IsTaskRunning(task))
            return PreLaunchFailed($"The preLaunchTask '{name}' is running already: start the session once it ends.");
        if (task.Kind == WorkspaceTaskKind.Build)
            return await RunBuildTaskAsync(task);
        if (await RunOtherTaskAsync(task) is 0)
            return true;
        return PreLaunchFailed($"The preLaunchTask '{name}' did not succeed, so the session does not start.");
    }

    bool PreLaunchFailed(string message)
    {
        DebugOutput.Append(message);
        SelectedOutput = DebugOutput;
        ShowToolWindow(ToolWindows.Output);
        SetStatus(message);
        return false;
    }
}
