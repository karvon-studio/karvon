using Avalonia.Threading;
using Exclr8.Terminal;
using Porta.Pty;

namespace Karvon.Terminal;

/// <summary>
/// A terminal control on a pseudo-terminal (Porta.Pty): the program's output read into it, what is typed and the
/// terminal's answers written to the program, its size kept. As Exclr8.Terminal.Pty's PtyTerminalAdapter (MIT), but the
/// output is read only as fast as the terminal takes it in: that adapter posted each chunk read to the UI thread, above
/// the keyboard's and the mouse's priority, so a program that wrote faster than the terminal showed (millions of lines)
/// left the window unanswering, the chunks piling up in memory.
/// </summary>
sealed class TerminalPty(TerminalControl terminal) : IAsyncDisposable
{
    /// <summary>Output read that the terminal has not taken in yet, past which the reading waits for it.</summary>
    internal const int Backlog = 256 * 1024;

    // ConPTY corrupts its input under concurrent writes: one write at a time.
    readonly Lock _writerLock = new();

    IPtyConnection? _pty;
    CancellationTokenSource? _readCts;
    Task? _readTask;
    volatile bool _disposed;

    /// <summary>The chunks read so far, and whether the reading waits for the terminal: the exit comes after them.</summary>
    int _chunks;
    volatile bool _holding;

    public int? Pid => _pty?.Pid;

    /// <summary>From its start until its output ends (the program exited) or it is disposed.</summary>
    public bool IsAlive => _pty is not null && _readTask is { IsCompleted: false };

    /// <summary>Raised on a thread of the pool, after the output the program wrote before it exited.</summary>
    public event EventHandler<PtyExitedEventArgs>? ProcessExited;

    public async Task StartAsync(PtyOptions options, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pty is not null)
            await DisposeConnectionAsync();
        terminal.PrepareForNewSession();
        var pty = await PtyProvider.SpawnAsync(options, ct);
        _pty = pty;
        terminal.RootProcessId = pty.Pid;
        terminal.Input += OnInput;
        terminal.Resized += OnResized;
        pty.ProcessExited += OnProcessExited;
        _readCts = new CancellationTokenSource();
        var token = _readCts.Token;
        _readTask = Task.Run(() => ReadAsync(pty, token));
    }

    /// <summary>
    /// The program's output into the terminal (which takes it from any thread). Past a backlog it has not shown, the
    /// reading waits until the UI thread has nothing above the background left, the keyboard's and the mouse's input
    /// included: meanwhile the pty's buffer fills and the program waits on its writes, as in any terminal.
    /// </summary>
    async Task ReadAsync(IPtyConnection pty, CancellationToken ct)
    {
        var buffer = new byte[16384];
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var n = await pty.ReaderStream.ReadAsync(buffer, ct);
                if (n == 0 || _disposed)
                    break;
                terminal.Write(buffer.AsSpan(0, n));
                Interlocked.Increment(ref _chunks);
                if (terminal.QueuedBytes > Backlog)
                {
                    _holding = true;
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background).GetTask().WaitAsync(ct);
                    _holding = false;
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
                break;
            }
        }
    }

    void OnInput(object? sender, ReadOnlyMemory<byte> bytes) => Send(bytes.Span);

    void OnResized(object? sender, (int Cols, int Rows) size)
    {
        if (_pty is not { } pty || size.Cols <= 0 || size.Rows <= 0)
            return;
        try
        {
            pty.Resize(size.Cols, size.Rows);
        }
        catch (Exception)
        {
            // Exiting: its size does not matter any more.
        }
    }

    void OnProcessExited(object? sender, PtyExitedEventArgs e) => _ = ExitedAsync(_readTask, e);

    /// <summary>
    /// The program's exit, once what it wrote before is in the terminal: the reading may be holding back, the rest of it
    /// still in the pty. That is when the reading ends (the pty closed), or reads nothing more for a moment and is not
    /// holding back (a process the program started keeps the pty open; so does Windows's console until it is closed).
    /// </summary>
    async Task ExitedAsync(Task? reading, PtyExitedEventArgs e)
    {
        var chunks = -1;
        while (reading is { IsCompleted: false } && (_holding || chunks != Volatile.Read(ref _chunks)))
        {
            chunks = Volatile.Read(ref _chunks);
            await Task.WhenAny(reading, Task.Delay(100));
        }
        ProcessExited?.Invoke(this, e);
    }

    /// <summary>Bytes written to the program, as if typed; false when there is none to take them (it is exiting).</summary>
    public bool Send(ReadOnlySpan<byte> bytes)
    {
        if (_pty is not { } pty || bytes.IsEmpty)
            return false;
        try
        {
            lock (_writerLock)
            {
                pty.WriterStream.Write(bytes);
                pty.WriterStream.Flush();
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        await DisposeConnectionAsync();
    }

    async Task DisposeConnectionAsync()
    {
        var (pty, cts, task) = (_pty, _readCts, _readTask);
        (_pty, _readCts, _readTask) = (null, null, null);
        terminal.Input -= OnInput;
        terminal.Resized -= OnResized;
        if (pty is not null)
            pty.ProcessExited -= OnProcessExited;
        cts?.Cancel();
        if (task is not null)
            await task;
        cts?.Dispose();
        pty?.Dispose();
    }
}
