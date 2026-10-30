using Avalonia.Threading;

namespace Karvon.Services;

/// <summary>
/// What no code of Karvon's caught. On the UI thread (a click's handler, the end of an awaited task) it is written to
/// errors.log in Karvon's settings folder, shown (the status bar, Output (Karvon)) and Karvon Studio goes on, as Visual
/// Studio does; a task's that nothing awaited is the same. On another thread .NET ends the process: the log says why. The
/// text is written with its secrets hidden.
/// </summary>
public static class ErrorReports
{
    const long MaxLog = 1024 * 1024;

    static int _installed;

    static readonly DispatcherUnhandledExceptionEventHandler OnDispatcher = (_, e) =>
    {
        Report(e.Exception);
        e.Handled = true;
    };

    static readonly EventHandler<UnobservedTaskExceptionEventArgs> OnTask = (_, e) =>
    {
        Report(e.Exception);
        e.SetObserved();
    };

    static readonly UnhandledExceptionEventHandler OnDomain = (_, e) =>
    {
        if (e.ExceptionObject is Exception ex)
            Write(ex, "Karvon Studio stopped");
    };

    public static string LogFile => Path.Combine(Settings.ConfigDirectory, "errors.log");

    /// <summary>An error was caught here (on the thread it was caught on).</summary>
    public static event Action<Exception>? Reported;

    /// <summary>Catches them from now on (once).</summary>
    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) == 1)
            return;
        Dispatcher.UIThread.UnhandledException += OnDispatcher;
        TaskScheduler.UnobservedTaskException += OnTask;
        AppDomain.CurrentDomain.UnhandledException += OnDomain;
    }

    /// <summary>Lets them through again (a test's errors fail it).</summary>
    public static void Uninstall()
    {
        if (Interlocked.Exchange(ref _installed, 0) == 0)
            return;
        Dispatcher.UIThread.UnhandledException -= OnDispatcher;
        TaskScheduler.UnobservedTaskException -= OnTask;
        AppDomain.CurrentDomain.UnhandledException -= OnDomain;
    }

    /// <summary>An error caught: written to the log, and shown by whoever listens.</summary>
    public static void Report(Exception ex)
    {
        Write(ex, "Caught, Karvon Studio went on");
        try
        {
            Reported?.Invoke(ex);
        }
        catch (Exception listener) when (listener is not OutOfMemoryException)
        {
            Write(listener, "Showing an error failed");
        }
    }

    static void Write(Exception ex, string what)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
            var file = new FileInfo(LogFile);
            // The last megabyte or so: the one before goes when it is full.
            if (file.Exists && file.Length > MaxLog)
                File.Move(LogFile, LogFile + ".1", overwrite: true);
            File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {what}: {Karvon.Claude.Tools.Redaction.Text(ex.ToString())}\n\n");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(LogFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception write) when (write is IOException or UnauthorizedAccessException)
        {
        }
    }
}
