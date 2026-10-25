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

    [ObservableProperty]
    public partial string TerminalTitle { get; set; } = "Terminal";

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
        terminal.StateChanged += () => TerminalTitle = terminal.Title;
        return terminal;
    }

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
        if (old is not null)
            await old.DisposeAsync();
    }

    [RelayCommand]
    async Task ShowTerminal()
    {
        await FollowRemoteTerminalAsync();
        ShowToolWindow(ToolWindows.Terminal);
        Dispatcher.UIThread.Post(() => Terminal.Control.Focus(), DispatcherPriority.Loaded);
    }

    [RelayCommand]
    async Task NewTerminal()
    {
        await FollowRemoteTerminalAsync();
        Terminal.WorkingDirectory = Solution?.Directory ?? Terminal.WorkingDirectory;
        await ShowTerminal();
        await Terminal.RestartAsync();
    }

    [RelayCommand]
    Task KillTerminal() => _terminal?.KillAsync() ?? Task.CompletedTask;

    public async Task ShutdownTerminalAsync()
    {
        if (_terminal is { } terminal)
            await terminal.DisposeAsync();
    }
}
