using System.Collections;
using System.Text;
using Avalonia.Media;
using Avalonia.Threading;
using Exclr8.Terminal;
using Exclr8.Terminal.Pty;
using Exclr8.Terminal.Render;
using Porta.Pty;

namespace Karvon.Terminal;

/// <summary>
/// The integrated terminal (View > Terminal): the user's shell in a real pseudo-terminal, started in the solution
/// folder on first show. Rendering and VT parsing come from Exclr8.Terminal, the PTY from Porta.Pty (both MIT).
/// </summary>
public sealed class TerminalSession : IAsyncDisposable
{
    readonly string? _program;
    readonly IReadOnlyList<string> _arguments;
    PtyTerminalAdapter _adapter;
    (int Cols, int Rows)? _size;
    bool _starting;
    bool _exited;
    TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The user's shell, or <paramref name="program"/> (a shell in a container: docker exec -it ...).</summary>
    public TerminalSession(string? program = null, IReadOnlyList<string>? arguments = null)
    {
        // Windows's console starts a program by its file: one on the PATH by its full path.
        if (OperatingSystem.IsWindows() && program is not null && !Path.IsPathRooted(program)
            && Workspace.TaskVariables.Find(program, System.Environment.GetEnvironmentVariable("PATH") ?? "", System.Environment.CurrentDirectory) is { } found)
            program = found;
        _program = program;
        _arguments = arguments ?? [];
        Control = new TerminalControl
        {
            FontFamily = "avares://Karvon.Core/Assets/Fonts#Cascadia Mono, DejaVu Sans Mono, monospace",
            FontSize = Editor.EditorHost.BaseFontSize,
            ScrollbackLimit = 10_000,
            ColorScheme = VsDarkTheme(),
        };
        _adapter = CreateAdapter();
        Control.Resized += (_, size) =>
        {
            _size = size;
            if (!_adapter.IsAlive && !_starting && !_exited)
                _ = StartAsync(size.Item1, size.Item2);
        };
        Control.TitleChanged += (_, title) => Dispatcher.UIThread.Post(() =>
        {
            Title = string.IsNullOrWhiteSpace(title) ? DefaultTitle : title;
            StateChanged?.Invoke();
        });
        // After the shell exits, Enter starts a new one (like VS / Windows Terminal), or does what EnterAfterExit says.
        Control.Input += (_, data) =>
        {
            if (_exited && data.Span.IndexOf((byte)'\r') >= 0)
                Dispatcher.UIThread.Post(() =>
                {
                    if (EnterAfterExit is { } enter)
                        enter();
                    else
                        _ = RestartAsync();
                });
        };
    }

    public TerminalControl Control { get; }

    /// <summary>Folder a new shell starts in; the view model points it at the open solution.</summary>
    public string WorkingDirectory { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>The user's shell: $SHELL (else zsh on macOS, bash elsewhere); on Windows PowerShell 7, else Windows PowerShell, else cmd.</summary>
    public static string Shell => OperatingSystem.IsWindows() ? WindowsShell()
        : Environment.GetEnvironmentVariable("SHELL") is { Length: > 0 } sh && File.Exists(sh) ? sh
        : OperatingSystem.IsMacOS() ? "/bin/zsh" : "/bin/bash";

    static string WindowsShell()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        if (Workspace.TaskVariables.Find("pwsh.exe", path, Environment.CurrentDirectory) is { } pwsh)
            return pwsh;
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(powershell) ? powershell : Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
    }

    string DefaultTitle => Name ?? Path.GetFileName(_program ?? Shell);

    /// <summary>What its title says while the program sets none (a program's console: the project's name).</summary>
    public string? Name { get; init; }

    string? _title;

    public string Title
    {
        get => _title ?? DefaultTitle;
        private set => _title = value;
    }

    /// <summary>What the line shown after the program exits says Enter does.</summary>
    public string RestartHint { get; init; } = "Press Enter to start a new terminal.";

    /// <summary>What Enter does after the program exited, instead of starting it again (a program's console closes).</summary>
    public Action? EnterAfterExit { get; init; }

    /// <summary>Variables the program gets beside Karvon's own (a launch profile's), or without (a null value).</summary>
    public IReadOnlyDictionary<string, string?> Variables { get; init; } = new Dictionary<string, string?>();

    /// <summary>The program exited (on the UI thread), with its exit code.</summary>
    public event Action<int>? Exited;

    public bool IsAlive => _adapter.IsAlive;

    public int? ProcessId => _adapter.Pid;

    /// <summary>Raised on the UI thread when the title or running state changes.</summary>
    public event Action? StateChanged;

    PtyTerminalAdapter CreateAdapter()
    {
        var adapter = new PtyTerminalAdapter(Control);
        adapter.ProcessExited += (_, e) => Dispatcher.UIThread.Post(() =>
        {
            if (adapter != _adapter)
                return;
            _exited = true;
            Control.Write(Encoding.UTF8.GetBytes($"\r\n\x1b[90m[Process exited with code {e.ExitCode}. {RestartHint}]\x1b[0m\r\n"));
            Title = DefaultTitle;
            StateChanged?.Invoke();
            Exited?.Invoke(e.ExitCode);
        });
        return adapter;
    }

    async Task StartAsync(int cols, int rows)
    {
        _starting = true;
        try
        {
            var env = new Dictionary<string, string>();
            foreach (DictionaryEntry e in System.Environment.GetEnvironmentVariables())
                env[(string)e.Key] = e.Value?.ToString() ?? "";
            env["TERM"] = "xterm-256color";
            env["COLORTERM"] = "truecolor";
            env["TERM_PROGRAM"] = "Karvon";
            foreach (var (name, value) in Variables)
                if (value is null)
                    env.Remove(name);
                else
                    env[name] = value;
            await _adapter.StartAsync(new PtyOptions
            {
                Name = "xterm-256color",
                // Porta.Pty creates the pty with zeroed termios (no ONLCR, ICRNL, ECHO...), which makes output
                // "staircase" because LF is not turned into CR LF. Reset the line discipline, then become the shell.
                // Windows's console (ConPTY) has none of that: the program starts as it is.
                App = OperatingSystem.IsWindows() ? _program ?? Shell : "/bin/sh",
                Cwd = Directory.Exists(WorkingDirectory) ? WorkingDirectory : System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
                Cols = cols,
                Rows = rows,
                CommandLine = OperatingSystem.IsWindows()
                    ? _program is null ? [] : [.. _arguments]
                    : _program is null
                        ? ["-c", "stty sane 2>/dev/null; exec \"$0\"", Shell]
                        : ["-c", "stty sane 2>/dev/null; exec \"$0\" \"$@\"", _program, .. _arguments],
                Environment = env,
            }, CancellationToken.None);
            _exited = false;
        }
        catch (Exception ex)
        {
            Control.Write(Encoding.UTF8.GetBytes($"\x1b[31mCould not start {_program ?? Shell}: {ex.Message}\x1b[0m\r\n"));
            _exited = true;
        }
        finally
        {
            _starting = false;
            _started.TrySetResult();
            StateChanged?.Invoke();
        }
    }

    /// <summary>
    /// Started, when it is not yet (it starts when it is first laid out): a moment for that, then at a terminal's usual
    /// size. A program's console needs it at once, its process id given to a debugger.
    /// </summary>
    public async Task EnsureStartedAsync()
    {
        if (_adapter.IsAlive || _exited)
            return;
        await Task.WhenAny(_started.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        if (!_adapter.IsAlive && !_starting && !_exited)
            await StartAsync(_size?.Cols ?? 120, _size?.Rows ?? 30);
    }

    /// <summary>
    /// Ends a session: the adapter's dispose waits for its read loop, which only returns once the pty closes,
    /// so the shell's process tree is killed first (as VS does when a terminal is closed).
    /// </summary>
    static async Task StopAsync(PtyTerminalAdapter adapter)
    {
        if (adapter.Pid is { } pid)
        {
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(pid);
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone.
            }
        }
        try
        {
            await adapter.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (TimeoutException)
        {
            // Never let a stuck pty freeze the IDE.
        }
    }

    /// <summary>Kills the current shell (if any) and starts a fresh one in <see cref="WorkingDirectory"/>.</summary>
    public async Task RestartAsync()
    {
        var old = _adapter;
        _adapter = CreateAdapter();
        await StopAsync(old);
        _exited = false;
        Control.PrepareForNewSession();
        if (_size is { } size)
            await StartAsync(size.Cols, size.Rows);
    }

    public async Task KillAsync()
    {
        if (!_adapter.IsAlive)
            return;
        var old = _adapter;
        _adapter = CreateAdapter();
        await StopAsync(old);
        _exited = true;
        Control.Write(Encoding.UTF8.GetBytes("\r\n\x1b[90m[Terminal killed. Press Enter to start a new terminal.]\x1b[0m\r\n"));
        StateChanged?.Invoke();
    }

    /// <summary>The visible screen and scrollback as plain text (used by tests).</summary>
    internal string ReadText()
    {
        Control.Buffer.SelectAll();
        var text = Control.Buffer.GetSelectedText();
        Control.Buffer.ClearSelection();
        return text;
    }

    /// <summary>Visual Studio's dark terminal palette.</summary>
    static TerminalTheme VsDarkTheme() => new()
    {
        Background = Color.Parse("#1E1E1E"),
        Foreground = Color.Parse("#CCCCCC"),
        Cursor = Color.Parse("#AEAFAD"),
        AnsiColors =
        [
            Color.Parse("#000000"), Color.Parse("#CD3131"), Color.Parse("#0DBC79"), Color.Parse("#E5E510"),
            Color.Parse("#2472C8"), Color.Parse("#BC3FBC"), Color.Parse("#11A8CD"), Color.Parse("#E5E5E5"),
            Color.Parse("#666666"), Color.Parse("#F14C4C"), Color.Parse("#23D18B"), Color.Parse("#F5F543"),
            Color.Parse("#3B8EEA"), Color.Parse("#D670D6"), Color.Parse("#29B8DB"), Color.Parse("#FFFFFF"),
        ],
    };

    public async ValueTask DisposeAsync() => await StopAsync(_adapter);
}
