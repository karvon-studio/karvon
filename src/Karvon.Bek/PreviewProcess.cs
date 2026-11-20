using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Karvon.Workspace;

namespace Karvon.Bek;

/// <summary>
/// bek-preview (bek-sdk's), run for one app: found on the PATH or in cargo's bin folder, started on a free port of 127.0.0.1,
/// what it says passed on line by line (Karvon's Output), and ended with the app, the solution or Karvon.
/// </summary>
public sealed class PreviewProcess : IAsyncDisposable
{
    public const string Install = "cargo install --git https://github.com/bek-family/bek-sdk bek-preview";

    /// <summary>What bek-preview says once its page and API answer: <c>bek-preview: http://127.0.0.1:7300</c>.</summary>
    const string ReadyLine = "bek-preview: ";

    readonly Process _process;

    PreviewProcess(Process process, PreviewClient client, string component)
    {
        _process = process;
        Client = client;
        Component = component;
    }

    public PreviewClient Client { get; }

    /// <summary>The app's component, as it was given.</summary>
    public string Component { get; }

    public bool HasExited => _process.HasExited;

    static string Program => OperatingSystem.IsWindows() ? "bek-preview.exe" : "bek-preview";

    /// <summary>bek-preview: on <paramref name="path"/> (the PATH), else where cargo install put it; null when it is in neither.</summary>
    public static string? Find(string? path = null)
    {
        path ??= Environment.GetEnvironmentVariable("PATH") ?? "";
        if (TaskVariables.Find(Program, path, "/") is { } onPath)
            return onPath;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var cargoHome = Environment.GetEnvironmentVariable("CARGO_HOME") is { Length: > 0 } own ? own : Path.Combine(home, ".cargo");
        var installed = Path.Combine(cargoHome, "bin", Program);
        return File.Exists(installed) ? installed : null;
    }

    /// <summary>bek-preview started for <paramref name="component"/>, once its API answers.</summary>
    /// <exception cref="InvalidOperationException">It ended first (its last words went to <paramref name="said"/>), or did not answer in a minute.</exception>
    public static async Task<PreviewProcess> StartAsync(string program, string component, string? manifest, Action<string> said, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(component);
        start.ArgumentList.Add("--port");
        start.ArgumentList.Add(FreePort().ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (manifest is not null)
        {
            start.ArgumentList.Add("--manifest");
            start.ArgumentList.Add(manifest);
        }
        var process = Process.Start(start) ?? throw new InvalidOperationException($"{program} did not start.");
        var ready = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not { } line)
                return;
            said(line);
            if (line.StartsWith(ReadyLine, StringComparison.Ordinal) && Uri.TryCreate(line[ReadyLine.Length..], UriKind.Absolute, out var address))
                ready.TrySetResult(address);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is { } line)
                said(line);
        };
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => ready.TrySetException(new InvalidOperationException($"bek-preview ended ({process.ExitCode}): see its output."));
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(TimeSpan.FromMinutes(1));
        try
        {
            var address = await ready.Task.WaitAsync(limit.Token);
            return new PreviewProcess(process, new PreviewClient(address), component);
        }
        catch
        {
            End(process);
            process.Dispose();
            throw;
        }
    }

    /// <summary>A port of 127.0.0.1 no one listens on now.</summary>
    static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    static void End(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // It ended on its own meanwhile.
        }
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        End(_process);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await _process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            // Killed; the system gathers it.
        }
        _process.Dispose();
    }
}
