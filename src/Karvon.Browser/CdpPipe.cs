using System.Diagnostics;
using System.Text;

namespace Karvon.Browser;

/// <summary>
/// The pipe Chrome is driven through when it starts with <c>--remote-debugging-pipe</c>: it reads the protocol's messages
/// from its descriptor 3 and writes its own to 4, each ended by a NUL. Unlike a debugging port, which any program on the
/// machine (and any other user of it) can reach, nothing but Karvon holds its ends: no one else can drive Karvon's Chrome
/// or read its cookies, and Chrome closes when Karvon lets go of it or exits. The ends are handed to Chrome through two
/// FIFOs in a folder of the user's alone, taken away as soon as both sides have opened them. Not on Windows, where a
/// program Karvon starts gets no descriptors 3 and 4.
/// </summary>
public sealed class CdpPipe : ICdpTransport
{
    readonly FileStream _toChrome;
    readonly FileStream _fromChrome;
    readonly byte[] _buffer = new byte[1 << 16];
    readonly MemoryStream _message = new();
    int _start;
    int _end;
    volatile bool _open = true;

    CdpPipe(FileStream toChrome, FileStream fromChrome)
    {
        _toChrome = toChrome;
        _fromChrome = fromChrome;
    }

    public static bool IsSupported => !OperatingSystem.IsWindows();

    /// <summary>
    /// <paramref name="program"/> started with <paramref name="arguments"/> (and <c>--remote-debugging-pipe</c> among
    /// them), and its pipe; an error when it does not take the pipe in <paramref name="timeout"/>.
    /// </summary>
    public static Task<(Process Process, CdpPipe Pipe)> StartAsync(string program, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct) =>
        StartAsync(program, arguments, timeout, "/bin/sh", ct);

    /// <summary>The same, through <paramref name="shell"/> (the tests' one that never opens Chrome's ends).</summary>
    internal static async Task<(Process Process, CdpPipe Pipe)> StartAsync(string program, IReadOnlyList<string> arguments, TimeSpan timeout, string shell, CancellationToken ct)
    {
        // Made with mode 700: the FIFOs in it are the user's alone.
        var folder = Directory.CreateTempSubdirectory("karvon-cdp-");
        try
        {
            var input = Path.Combine(folder.FullName, "in");
            var output = Path.Combine(folder.FullName, "out");
            MakeFifos(input, output);
            var start = new ProcessStartInfo(shell) { UseShellExecute = false, CreateNoWindow = true };
            // The shell opens Chrome's ends as its descriptors 3 and 4 (each open waits for Karvon's end), then becomes Chrome.
            foreach (var argument in (string[])["-c", "exec 3<\"$1\" 4>\"$2\" && shift 2 && exec \"$0\" \"$@\"", program, input, output, .. arguments])
                start.ArgumentList.Add(argument);
            var process = Process.Start(start) ?? throw new InvalidOperationException($"{Path.GetFileName(program)} did not start.");
            // Karvon's ends, in the shell's order.
            var opening = Task.Run(() =>
            {
                var toChrome = new FileStream(input, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, 0);
                try
                {
                    return new CdpPipe(toChrome, new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 0));
                }
                catch
                {
                    toChrome.Dispose();
                    throw;
                }
            }, CancellationToken.None);
            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var done = await Task.WhenAny(opening, process.WaitForExitAsync(waiting.Token), Task.Delay(timeout, waiting.Token));
            waiting.Cancel();
            if (done == opening && opening.IsCompletedSuccessfully)
                return (process, opening.Result);
            var exited = process.HasExited;
            await AbandonAsync(opening, input, output);
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            process.Dispose();
            ct.ThrowIfCancellationRequested();
            if (done == opening)
                await opening;
            throw new InvalidOperationException(exited
                ? $"{Path.GetFileName(program)} closed as it started."
                : $"{Path.GetFileName(program)} did not start in {timeout.TotalSeconds:0} seconds.");
        }
        finally
        {
            // Both ends are held (or let go): the FIFOs go, so no one opens them after.
            try
            {
                folder.Delete(recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    static void MakeFifos(params string[] paths)
    {
        var start = new ProcessStartInfo("mkfifo") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var argument in (string[])["-m", "600", .. paths])
            start.ArgumentList.Add(argument);
        using var mkfifo = Process.Start(start) ?? throw new InvalidOperationException("mkfifo did not start.");
        var error = mkfifo.StandardError.ReadToEnd();
        mkfifo.WaitForExit();
        if (mkfifo.ExitCode != 0)
            throw new InvalidOperationException($"mkfifo failed: {error.Trim()}");
    }

    /// <summary>Karvon's own opens that wait for an end Chrome never took are let go (a FIFO opened both ways waits for no one).</summary>
    static async Task AbandonAsync(Task<CdpPipe> opening, string input, string output)
    {
        var both = new List<FileStream>();
        try
        {
            foreach (var fifo in new[] { input, output })
            {
                if (opening.IsCompleted)
                    break;
                both.Add(new FileStream(fifo, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 0));
                await Task.WhenAny(opening, Task.Delay(200));
            }
            if (await Task.WhenAny(opening, Task.Delay(TimeSpan.FromSeconds(2))) == opening && opening.IsCompletedSuccessfully)
                await opening.Result.DisposeAsync();
        }
        catch (IOException)
        {
        }
        finally
        {
            foreach (var stream in both)
                await stream.DisposeAsync();
        }
    }

    public bool IsOpen => _open;

    public async Task SendAsync(byte[] message, CancellationToken ct)
    {
        var ended = new byte[message.Length + 1];
        message.CopyTo(ended, 0);
        await _toChrome.WriteAsync(ended, ct);
    }

    public async Task<string?> ReceiveAsync(CancellationToken ct)
    {
        while (true)
        {
            var nul = Array.IndexOf(_buffer, (byte)0, _start, _end - _start);
            if (nul >= 0)
            {
                _message.Write(_buffer, _start, nul - _start);
                _start = nul + 1;
                var text = Encoding.UTF8.GetString(_message.GetBuffer(), 0, (int)_message.Length);
                _message.SetLength(0);
                return text;
            }
            _message.Write(_buffer, _start, _end - _start);
            _start = _end = 0;
            var read = await _fromChrome.ReadAsync(_buffer, ct);
            if (read == 0)
                return null;
            _end = read;
        }
    }

    public void Lost() => _open = false;

    /// <summary>Karvon's end closed: Chrome reads its end's close as the word to quit.</summary>
    public async ValueTask DisposeAsync()
    {
        _open = false;
        try
        {
            await _toChrome.DisposeAsync();
        }
        catch (IOException)
        {
            // Chrome had gone: what was left unsent is nobody's.
        }
        await _fromChrome.DisposeAsync();
    }
}
