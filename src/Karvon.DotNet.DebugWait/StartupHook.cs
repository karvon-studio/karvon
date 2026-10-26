using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

/// <summary>
/// .NET's startup hook (DOTNET_STARTUP_HOOKS), for a console app Karvon debugs in its own console: Karvon starts the
/// program in the console, the hook says so in the file KARVON_DEBUGGER_WAIT names and waits, before Main, until the
/// debugger has attached (a minute at most), so that a breakpoint on Main's first line is hit. Neither variable goes on
/// to the programs it starts. (netcoredbg starts a program with its output its own, never in a terminal.)
/// </summary>
internal static class StartupHook
{
    public static void Initialize()
    {
        var marker = Environment.GetEnvironmentVariable("KARVON_DEBUGGER_WAIT");
        Environment.SetEnvironmentVariable("KARVON_DEBUGGER_WAIT", null);
        Environment.SetEnvironmentVariable("DOTNET_STARTUP_HOOKS", null);
        if (string.IsNullOrEmpty(marker))
            return;
        try
        {
            File.WriteAllText(marker, Process.GetCurrentProcess().Id.ToString());
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
        var until = DateTime.UtcNow.AddMinutes(1);
        while (!Debugger.IsAttached && DateTime.UtcNow < until)
            Thread.Sleep(10);
    }
}
