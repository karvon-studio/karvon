using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Karvon.Terminal;

namespace Karvon.ViewModels;

/// <summary>The integrated terminal tool window (View > Terminal, Ctrl+`).</summary>
public partial class MainViewModel
{
    TerminalSession? _terminal;

    /// <summary>The machine the terminal's shell is on (null: here).</summary>
    string? _terminalRemote;

    /// <summary>Created on first use; the shell itself starts when the terminal is first laid out.</summary>
    public TerminalSession Terminal => _terminal ??= CreateTerminal();

    /// <summary>
    /// A program's own console, as Visual Studio's console window: a console app run (Ctrl+F5) or debugged (F5) in a
    /// terminal of its own, its output there and the keyboard its input. The Terminal window shows it instead of the
    /// shell while it is there (Switch goes to the shell and back); after the program exits, Enter closes it.
    /// </summary>
    public TerminalSession? ProgramConsole { get; private set; }

    public bool HasProgramConsole => ProgramConsole is not null;

    /// <summary>The shell shown while there is a program's console.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShownTerminal), nameof(TerminalTitle))]
    public partial bool IsShellShown { get; set; }

    /// <summary>What the Terminal window shows: the program's console, else the shell.</summary>
    public TerminalSession ShownTerminal => ProgramConsole is { } console && !IsShellShown ? console : Terminal;

    public string TerminalTitle => ShownTerminal.Title;

    TaskCompletionSource<int>? _consoleExited;

    TerminalSession CreateTerminal(string? folder = null)
    {
        // A copy of a machine's folder: the shell is on the machine, in the folder.
        var remote = Remote;
        _terminalRemote = remote?.Name;
        var terminal = remote is not null && remote.Terminal(folder ?? Solution!.Directory) is var (program, arguments)
            ? new TerminalSession(program, arguments)
            : new TerminalSession();
        if (Solution is not null)
            terminal.WorkingDirectory = Solution.Directory;
        terminal.StateChanged += () => OnPropertyChanged(nameof(TerminalTitle));
        return terminal;
    }

    /// <summary>A program in a console of its own (the one there before closed): started, its exit to come.</summary>
    async Task<(TerminalSession Console, Task<int> Exited)> OpenProgramConsoleAsync(string name, string program, IReadOnlyList<string> arguments,
        string workingDirectory, IReadOnlyDictionary<string, string> environment)
    {
        await CloseProgramConsoleAsync();
        var exited = _consoleExited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var console = new TerminalSession(program, arguments)
        {
            Name = name,
            WorkingDirectory = workingDirectory,
            Variables = environment.ToDictionary(v => v.Key, v => (string?)v.Value),
            RestartHint = "Press Enter to close this console.",
            EnterAfterExit = () => _ = CloseProgramConsoleAsync(),
        };
        console.Exited += code => exited.TrySetResult(code);
        console.StateChanged += () => OnPropertyChanged(nameof(TerminalTitle));
        ProgramConsole = console;
        IsShellShown = false;
        OnPropertyChanged(nameof(ProgramConsole));
        OnPropertyChanged(nameof(HasProgramConsole));
        OnPropertyChanged(nameof(ShownTerminal));
        OnPropertyChanged(nameof(TerminalTitle));
        ShowToolWindow(ToolWindows.Terminal);
        await console.EnsureStartedAsync();
        if (console.ProcessId is null)
        {
            exited.TrySetResult(-1);
            throw new InvalidOperationException($"{Path.GetFileName(program)} could not start in its console.");
        }
        return (console, exited.Task);
    }

    /// <summary>The program's console closed (its program, if it runs, ended): the shell shown again.</summary>
    async Task CloseProgramConsoleAsync()
    {
        if (ProgramConsole is not { } console)
            return;
        ProgramConsole = null;
        IsShellShown = false;
        OnPropertyChanged(nameof(ProgramConsole));
        OnPropertyChanged(nameof(HasProgramConsole));
        OnPropertyChanged(nameof(ShownTerminal));
        OnPropertyChanged(nameof(TerminalTitle));
        _consoleExited?.TrySetResult(-1);
        await console.DisposeAsync();
    }

    /// <summary>The program's console ended (Stop): its process tree killed, its console kept with what it wrote.</summary>
    void KillProgramConsole()
    {
        if (ProgramConsole?.ProcessId is not { } pid)
            return;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Gone already.
        }
    }

    /// <summary>The Terminal window from the program's console to the shell, and back.</summary>
    [RelayCommand]
    void SwitchTerminal() => IsShellShown = !IsShellShown;

    /// <summary>The workspace's machine is another than the shell's (a remote folder opened or closed): a shell where it is now.</summary>
    async Task FollowRemoteTerminalAsync()
    {
        if (_terminal is { } && _terminalRemote != Remote?.Name)
            await ReplaceTerminalAsync(null);
    }

    async Task ReplaceTerminalAsync(string? folder)
    {
        var old = _terminal;
        _terminal = folder is null ? null : CreateTerminal(folder);
        OnPropertyChanged(nameof(Terminal));
        OnPropertyChanged(nameof(ShownTerminal));
        OnPropertyChanged(nameof(TerminalTitle));
        if (old is not null)
            await old.DisposeAsync();
    }

    [RelayCommand]
    async Task ShowTerminal()
    {
        await FollowRemoteTerminalAsync();
        ShowToolWindow(ToolWindows.Terminal);
        Dispatcher.UIThread.Post(() => ShownTerminal.Control.Focus(), DispatcherPriority.Loaded);
    }

    [RelayCommand]
    async Task NewTerminal()
    {
        await FollowRemoteTerminalAsync();
        Terminal.WorkingDirectory = Solution?.Directory ?? Terminal.WorkingDirectory;
        IsShellShown = true;
        await ShowTerminal();
        await Terminal.RestartAsync();
    }

    [RelayCommand]
    Task KillTerminal() => ProgramConsole is { } console && !IsShellShown ? console.KillAsync() : _terminal?.KillAsync() ?? Task.CompletedTask;

    public async Task ShutdownTerminalAsync()
    {
        await CloseProgramConsoleAsync();
        if (_terminal is { } terminal)
            await terminal.DisposeAsync();
    }
}
