using System.Runtime.CompilerServices;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Karvon.Services;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>The window's watchdog: a thread kept waiting is written down with where it waits, and said once it answers.</summary>
public class UiWatchdogTests
{
    [AvaloniaFact]
    public async Task The_window_s_thread_kept_waiting_is_written_down_with_where_it_waits_and_said_once_it_answers()
    {
        var stalls = new List<WindowStall>();
        var answers = new List<WindowStall>();
        using var watchdog = new UiWatchdog(Dispatcher.UIThread, TimeSpan.FromSeconds(1),
            stall => { lock (stalls) stalls.Add(stall); },
            answer => { lock (answers) answers.Add(answer); });

        // Answering: nothing to say.
        await WaitUntil(() => false, TimeSpan.FromSeconds(2));
        Assert.Empty(stalls);

        // A module waits on the window's thread for something that comes late; twice, each read by Karvon started for it
        // (the reader of dumps, loaded into the process it reads, would take that process down the second time).
        for (var time = 1; time <= 2; time++)
        {
            WaitsOnTheWindowsThread(TimeSpan.FromSeconds(6));
            await WaitUntil(() => answers.Count == time, TimeSpan.FromSeconds(5));
            var stall = stalls[time - 1];
            Assert.StartsWith("Karvon.Tests.UiWatchdogTests.WaitsOnTheWindowsThread(", stall.Where);
            Assert.Contains(stall.Stack, f => f.StartsWith("System.Threading.ManualResetEventSlim.Wait(", StringComparison.Ordinal));
            Assert.Contains("It waited in Karvon.Tests.UiWatchdogTests.WaitsOnTheWindowsThread(", stall.Describe());
            var answered = answers[time - 1];
            Assert.InRange(answered.Seconds, 5, 9);
            Assert.StartsWith("the window did not answer for ", new WindowStallException(answered).Message);
            Assert.Contains("waiting in Karvon.Tests.UiWatchdogTests.WaitsOnTheWindowsThread (errors.log has where).", new WindowStallException(answered).Message);
        }
        Assert.Equal(2, stalls.Count);

        // No dump is left behind; once disposed, it watches no more.
        Assert.Empty(Directory.GetDirectories(Path.GetTempPath(), "karvon-watchdog-*"));
        watchdog.Dispose();
        await WaitUntil(() => false, TimeSpan.FromSeconds(1));
        WaitsOnTheWindowsThread(TimeSpan.FromSeconds(2));
        await WaitUntil(() => false, TimeSpan.FromSeconds(1));
        Assert.Equal(2, stalls.Count);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void WaitsOnTheWindowsThread(TimeSpan time)
    {
        using var never = new ManualResetEventSlim();
        never.Wait(time);
    }
}
