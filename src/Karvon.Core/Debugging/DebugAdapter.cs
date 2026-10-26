using System.Diagnostics;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Karvon.Debugging;

/// <summary>
/// A debugger a debug session talks to (the Debug Adapter Protocol): a program on its stdin and stdout (netcoredbg), or a
/// server the session connects to (js-debug's), where each child session (one for each process it debugs) is a
/// connection of its own. It goes when the session does.
/// </summary>
public sealed partial class DebugAdapter : IAsyncDisposable
{
    /// <summary>Its program; null for a server another program runs (jdtls's java-debug).</summary>
    readonly Process? _process;
    readonly (string Host, int Port)? _server;
    readonly List<TcpClient> _clients = [];
    bool _stdioTaken;
    bool _logging;

    DebugAdapter(string type, Process? process, (string Host, int Port)? server)
    {
        Type = type;
        _process = process;
        _server = server;
    }

    /// <summary>The adapter's id in its initialize request ("coreclr", "pwa-node").</summary>
    public string Type { get; }

    /// <summary>
    /// A DAP server that is there already, which another program runs (a language server's: jdtls starts java-debug's
    /// and says its port): each session connects to it, and nothing is started or stopped with it.
    /// </summary>
    public static DebugAdapter AtServer(string type, string host, int port) => new(type, null, (host, port));

    /// <summary>A program speaking DAP on its stdin and stdout; what it writes to stderr is its log.</summary>
    public static DebugAdapter Start(string type, ProcessStartInfo program)
    {
        Redirect(program);
        var process = Process.Start(program) ?? throw new InvalidOperationException($"{Path.GetFileName(program.FileName)} did not start.");
        return new DebugAdapter(type, process, null);
    }

    [GeneratedRegex(@"(\S+):(\d+)\s*$")]
    private static partial Regex HostAndPort();

    /// <summary>
    /// A DAP server: the program says where it listens on its first line ending in host:port ("Debug server listening at
    /// 127.0.0.1:41235"); each session connects there.
    /// </summary>
    public static async Task<DebugAdapter> StartServerAsync(string type, ProcessStartInfo program, CancellationToken ct)
    {
        Redirect(program);
        var process = Process.Start(program) ?? throw new InvalidOperationException($"{Path.GetFileName(program.FileName)} did not start.");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var said = new List<string>();
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                if (HostAndPort().Match(line) is { Success: true } match)
                    return new DebugAdapter(type, process, (match.Groups[1].Value, int.Parse(match.Groups[2].Value)));
                said.Add(line);
            }
            said.Add(await process.StandardError.ReadToEndAsync(timeout.Token));
            throw new InvalidOperationException($"{Path.GetFileName(program.FileName)} ended before it listened: {string.Join('\n', said).Trim()}");
        }
        catch
        {
            Kill(process);
            process.Dispose();
            throw;
        }
    }

    static void Redirect(ProcessStartInfo program)
    {
        program.RedirectStandardInput = true;
        program.RedirectStandardOutput = true;
        program.RedirectStandardError = true;
        program.UseShellExecute = false;
        // No console window of its own on Windows: the adapter's and the program's output come to Karvon.
        program.CreateNoWindow = true;
    }

    /// <summary>
    /// A connection for a session: the program's stdin and stdout (once), or a new one to the server (the first for the
    /// session itself, the others for its children).
    /// </summary>
    public async Task<DapConnection> ConnectAsync(CancellationToken ct)
    {
        if (_server is not { } server)
        {
            if (_stdioTaken)
                throw new NotSupportedException("this debugger has one session only");
            _stdioTaken = true;
            return new DapConnection(_process!.StandardOutput.BaseStream, _process.StandardInput.BaseStream);
        }
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(server.Host, server.Port, ct);
        }
        catch
        {
            client.Dispose();
            throw;
        }
        lock (_clients)
            _clients.Add(client);
        var stream = client.GetStream();
        return new DapConnection(stream, stream);
    }

    /// <summary>What the adapter writes besides DAP (its stderr; a server's stdout after it listens), line by line, from now on.</summary>
    public void StartLogging(Action<string> log)
    {
        if (_logging || _process is null)
            return;
        _logging = true;
        _ = Task.Run(() => ReadLinesAsync(_process.StandardError, log));
        if (_server is not null)
            _ = Task.Run(() => ReadLinesAsync(_process.StandardOutput, log));
    }

    static async Task ReadLinesAsync(StreamReader reader, Action<string> log)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
                log(line);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    public bool HasExited => _process?.HasExited == true;

    static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_clients)
        {
            foreach (var client in _clients)
                client.Dispose();
            _clients.Clear();
        }
        if (_process is not null)
        {
            Kill(_process);
            _process.Dispose();
        }
        return ValueTask.CompletedTask;
    }
}
