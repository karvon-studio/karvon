using System.Diagnostics;
using Avalonia.Threading;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Runtime;

namespace Karvon.Services;

/// <summary>
/// Watches the window's thread, which every window draws on, so that a module waiting on it (for a machine that does not
/// answer, say) shows. It is asked to answer every half second; when it has not for <see cref="Threshold"/>, the log
/// (<see cref="ErrorReports.LogFile"/>) is told where it is waiting: its stack, read from a dump of Karvon's own threads.
/// The dump is a triage one, without the heap and so without what Karvon holds (a password, a token), in a folder of the
/// user's alone, and is deleted once read. It is read by Karvon started again for it alone (<c>--read-stack</c>): the
/// runtime's reader of dumps, loaded into the process it reads, takes that process down. When it answers again, the window says for how long it did not, and where.
/// Not while a debugger is attached (a breakpoint stops it too), nor with KARVON_WATCHDOG=off.
/// </summary>
public sealed class UiWatchdog : IDisposable
{
    /// <summary>How long the window's thread may not answer before its stack is written down.</summary>
    public static readonly TimeSpan Threshold = TimeSpan.FromSeconds(5);

    static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(500);

    readonly Dispatcher _dispatcher;
    readonly int _threadId;
    readonly TimeSpan _threshold;
    readonly Action<WindowStall> _stalled;
    readonly Action<WindowStall> _answered;
    readonly Thread _thread;
    volatile bool _stop;
    long _beat = Stopwatch.GetTimestamp();
    int _asked;

    /// <summary>
    /// Watches the thread of <paramref name="dispatcher"/>, which this is called on. <paramref name="stalled"/> is called
    /// (on the watchdog's thread) when it has not answered for <paramref name="threshold"/>, with where it waits;
    /// <paramref name="answered"/> once it answers again, with for how long it did not.
    /// </summary>
    public UiWatchdog(Dispatcher dispatcher, TimeSpan threshold, Action<WindowStall> stalled, Action<WindowStall> answered)
    {
        dispatcher.VerifyAccess();
        _dispatcher = dispatcher;
        _threadId = Environment.CurrentManagedThreadId;
        _threshold = threshold;
        _stalled = stalled;
        _answered = answered;
        _thread = new Thread(Watch) { IsBackground = true, Name = "Karvon window watchdog" };
        _thread.Start();
    }

    /// <summary>Karvon Studio's own: what it finds goes to the log, and to the window once it answers again.</summary>
    public static UiWatchdog? Start()
    {
        if (Debugger.IsAttached || string.Equals(Environment.GetEnvironmentVariable("KARVON_WATCHDOG"), "off", StringComparison.OrdinalIgnoreCase))
            return null;
        return new UiWatchdog(Dispatcher.UIThread, Threshold,
            stall => ErrorReports.Log($"The window did not answer for {stall.Seconds:0} s", stall.Describe()),
            stall => ErrorReports.Show(new WindowStallException(stall)));
    }

    public void Dispose() => _stop = true;

    void Watch()
    {
        WindowStall? stall = null;
        var from = 0L;
        while (!_stop)
        {
            // One question at a time: a thread that does not answer is not asked again and again.
            if (Interlocked.Exchange(ref _asked, 1) == 0)
                _dispatcher.Post(() =>
                {
                    Volatile.Write(ref _beat, Stopwatch.GetTimestamp());
                    Volatile.Write(ref _asked, 0);
                }, DispatcherPriority.Send);
            Thread.Sleep(Interval);
            if (_stop)
                break;
            var beat = Volatile.Read(ref _beat);
            if (stall is null && Stopwatch.GetElapsedTime(beat) > _threshold)
            {
                from = beat;
                stall = new WindowStall(Stopwatch.GetElapsedTime(beat), StackOf(_threadId));
                _stalled(stall);
            }
            else if (stall is not null && beat != from)
            {
                // From its last answer before to its first after.
                _answered(stall with { For = Stopwatch.GetElapsedTime(from, beat) });
                stall = null;
            }
        }
    }

    /// <summary>
    /// <c>Karvon --read-stack dump thread</c>, in a process of its own: a thread's managed stack in a dump, a method a line
    /// (or why it could not be read).
    /// </summary>
    public static IReadOnlyList<string> ReadStack(string dump, int managedThreadId)
    {
        try
        {
            using var target = DataTarget.LoadDump(dump);
            using var runtime = target.ClrVersions[0].CreateRuntime();
            if (runtime.Threads.FirstOrDefault(t => t.ManagedThreadId == managedThreadId) is not { } thread)
                return ["(the window's thread was not in the dump)"];
            return [.. thread.EnumerateStackTrace().Select(f => f.Method?.Signature).OfType<string>().Take(80)];
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return [$"(its stack could not be read: {ex.Message})"];
        }
    }

    /// <summary>Karvon's own program, which reads a dump's stack when started with <c>--read-stack</c>.</summary>
    static string Reader => Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Karvon.exe" : "Karvon");

    /// <summary>
    /// The managed stack of a thread of this process, innermost first: read from a triage dump of it (written by the
    /// runtime's createdump) by Karvon started for it, or why it could not be.
    /// </summary>
    public static IReadOnlyList<string> StackOf(int managedThreadId)
    {
        DirectoryInfo? folder = null;
        try
        {
            // A folder of the user's alone (0700) for it.
            folder = Directory.CreateTempSubdirectory("karvon-watchdog-");
            var dump = Path.Combine(folder.FullName, "karvon.dmp");
            new DiagnosticsClient(Environment.ProcessId).WriteDump(DumpType.Triage, dump, logDumpGeneration: false);
            var start = new ProcessStartInfo(Reader) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "--read-stack", dump, managedThreadId.ToString(System.Globalization.CultureInfo.InvariantCulture) })
                start.ArgumentList.Add(argument);
            using var reader = Process.Start(start) ?? throw new InvalidOperationException($"{Reader} could not be started.");
            var output = reader.StandardOutput.ReadToEndAsync();
            // Read so that it does not fill; its fault, when the process goes first, is seen here (not left unobserved).
            _ = reader.StandardError.ReadToEndAsync().ContinueWith(t => t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            if (!reader.WaitForExit(60_000))
            {
                reader.Kill(entireProcessTree: true);
                return ["(its stack was not read in a minute)"];
            }
            return output.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return [$"(its stack could not be read: {ex.Message})"];
        }
        finally
        {
            try
            {
                folder?.Delete(recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}

/// <summary>The window's thread not answering: for how long, and where it waited.</summary>
public sealed record WindowStall(TimeSpan For, IReadOnlyList<string> Stack)
{
    public double Seconds => For.TotalSeconds;

    /// <summary>The innermost of Karvon's own methods it waited in (the module's), else its innermost method.</summary>
    public string? Where =>
        Stack.FirstOrDefault(f => f.StartsWith("Karvon.", StringComparison.Ordinal) && !f.StartsWith("Karvon.Services.UiWatchdog", StringComparison.Ordinal))
        ?? Stack.FirstOrDefault();

    public string Describe() => $"It waited in {Where ?? "(unknown)"}:\n" + string.Join("\n", Stack.Select(f => "    " + f));
}

/// <summary>Shown once the window answers again: for how long it did not, and in which of Karvon's methods.</summary>
public sealed class WindowStallException(WindowStall stall)
    : Exception($"the window did not answer for {stall.Seconds:0} s, waiting in {Short(stall.Where)} (errors.log has where).")
{
    public WindowStall Stall => stall;

    static string Short(string? method) => method is null ? "an unknown method" : method.Split('(')[0];

    public override string ToString() => Message + "\n" + stall.Describe();
}
