using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Karvon.Workspace;

namespace Karvon.Kubernetes;

/// <summary>What kubectl said, as it said it (the cluster not reached, a resource it may not read).</summary>
public sealed class KubeException(string message) : Exception(message);

public sealed record KubeResult(int ExitCode, string Output, string Error)
{
    public bool Succeeded => ExitCode == 0;

    public string Message => (Error.Length > 0 ? Error : Output).Trim();
}

/// <summary>
/// The user's own kubectl, with their kubeconfig (its auth plugins and all): every command names its context, so the
/// one kubectl itself is set to never changes. Long commands (logs -f, port-forward) stream.
/// </summary>
public sealed class Kubectl(string program)
{
    static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    public string Program => program;

    /// <summary>
    /// KARVON_KUBECTL_PATH's (off: none, as the tests have it), else kubectl on PATH or where its installers put it (a
    /// desktop launcher's PATH may not have ~/.local/bin).
    /// </summary>
    public static Kubectl? Find()
    {
        if (Environment.GetEnvironmentVariable("KARVON_KUBECTL_PATH") is { Length: > 0 } given)
            return given == "off" ? null : new Kubectl(given);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] more = [Path.Combine(home, ".local", "bin"), "/usr/local/bin", "/opt/homebrew/bin", "/snap/bin", "/usr/bin"];
        var path = string.Join(Path.PathSeparator, [Environment.GetEnvironmentVariable("PATH") ?? "", .. more]);
        return TaskVariables.Find("kubectl", path, "/") is { } found ? new Kubectl(found) : null;
    }

    /// <summary>A context's arguments before a command's: its context, and its namespace (none for every namespace's).</summary>
    public static IEnumerable<string> In(string context, string? ns) =>
        ns is null ? ["--context", context] : ["--context", context, "--namespace", ns];

    public async Task<KubeResult> RunAsync(IEnumerable<string> args, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        using var process = new Process { StartInfo = StartInfo(args) };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new KubeException($"kubectl could not be started ({ex.Message}).");
        }
        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout ?? DefaultTimeout);
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            if (cancellationToken.IsCancellationRequested)
                throw;
            throw new KubeException($"kubectl {string.Join(' ', process.StartInfo.ArgumentList.Where(a => !a.StartsWith('{')))} did not finish in {(timeout ?? DefaultTimeout).TotalSeconds:0} s.");
        }
        return new KubeResult(process.ExitCode, (await output).TrimEnd(), (await error).TrimEnd());
    }

    /// <summary>Its output, or kubectl's own words.</summary>
    public async Task<string> OutputAsync(IEnumerable<string> args, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(args, cancellationToken: cancellationToken);
        return result.Succeeded ? result.Output : throw new KubeException(Explain(result.Message));
    }

    /// <summary>
    /// JSON kubectl printed (a List of what it could read, even when it could not read some: then <paramref name="warning"/>
    /// says what), or kubectl's words when it printed none.
    /// </summary>
    public async Task<(JsonDocument Json, string? Warning)> JsonAsync(IEnumerable<string> args, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync([.. args, "-o", "json"], cancellationToken: cancellationToken);
        if (result.Output.StartsWith('{'))
        {
            try
            {
                return (JsonDocument.Parse(result.Output), result.Succeeded || result.Error.Length == 0 ? null : Explain(result.Error));
            }
            catch (JsonException)
            {
            }
        }
        throw new KubeException(Explain(result.Message));
    }

    /// <summary>kubectl's error, and what it means when it is a known one.</summary>
    internal static string Explain(string message)
    {
        var first = message.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "kubectl failed.";
        if (first.StartsWith("error: ", StringComparison.Ordinal))
            first = first[7..];
        if (first.Contains("Unable to connect to the server", StringComparison.Ordinal) || first.Contains("connection refused", StringComparison.Ordinal))
            return $"The cluster cannot be reached: {first}";
        if (first.Contains("You must be logged in", StringComparison.Ordinal) || first.Contains("Unauthorized", StringComparison.Ordinal))
            return $"The cluster did not accept the credentials of this context: {first}";
        return first;
    }

    /// <summary>
    /// A command that runs until stopped (logs -f, port-forward): each line of its output and error goes to
    /// <paramref name="onLine"/> on a background thread; <paramref name="onExit"/> gets its exit code.
    /// </summary>
    public KubeStream Stream(IEnumerable<string> args, Action<string> onLine, Action<int>? onExit = null)
    {
        try
        {
            return new KubeStream(StartInfo(args), onLine, onExit);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new KubeException($"kubectl could not be started ({ex.Message}).");
        }
    }

    ProcessStartInfo StartInfo(IEnumerable<string> args)
    {
        var info = new ProcessStartInfo(program)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        return info;
    }

    internal static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }
}

/// <summary>A long-running kubectl (see <see cref="Kubectl.Stream"/>); disposing it ends the command.</summary>
public sealed class KubeStream : IDisposable
{
    readonly Process _process = new();
    int _disposed;

    internal KubeStream(ProcessStartInfo info, Action<string> onLine, Action<int>? onExit)
    {
        _process.StartInfo = info;
        _process.Start();
        async Task Read(StreamReader reader)
        {
            try
            {
                while (await reader.ReadLineAsync() is { } line)
                    if (Volatile.Read(ref _disposed) == 0)
                        onLine(line);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
            }
        }
        _ = Task.Run(async () =>
        {
            await Task.WhenAll(Read(_process.StandardOutput), Read(_process.StandardError));
            int code;
            try
            {
                await _process.WaitForExitAsync();
                code = _process.ExitCode;
            }
            catch (InvalidOperationException)
            {
                code = -1;
            }
            if (Volatile.Read(ref _disposed) == 0)
                onExit?.Invoke(code);
        });
    }

    public bool HasExited
    {
        get
        {
            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        Kubectl.Kill(_process);
        _process.Dispose();
    }
}
