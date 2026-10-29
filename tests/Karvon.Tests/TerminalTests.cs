using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Karvon.Terminal;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>The terminal on its pseudo-terminal.</summary>
public class TerminalTests
{
    [AvaloniaFact]
    public async Task A_program_that_writes_faster_than_the_terminal_shows_waits_for_it()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "A POSIX shell's flood.");
        // 4.4 MB of lines, then the last one.
        await using var terminal = new TerminalSession("/bin/sh", ["-c", "yes 0123456789 | head -n 400000; echo the-end"]);
        await terminal.EnsureStartedAsync();
        using var program = Process.GetProcessById(terminal.ProcessId!.Value);

        // The UI thread busy: what the terminal has not shown stays a backlog, and the program waits on its writes.
        Thread.Sleep(TimeSpan.FromSeconds(3));
        Assert.InRange(terminal.Control.QueuedBytes, 0, TerminalPty.Backlog + 16384);
        Assert.False(program.HasExited, "the program wrote all of it while the terminal showed none");

        await WaitUntil(() => terminal.ReadText().Contains("the-end", StringComparison.Ordinal), TimeSpan.FromSeconds(60));
        Assert.Contains("the-end", terminal.ReadText());
    }

    [AvaloniaFact]
    public async Task That_the_program_exited_shows_after_all_it_wrote()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "A POSIX shell's output.");
        // A backlog's worth and a little more: the rest fits in the pty, and the program exits while the terminal is behind.
        await using var terminal = new TerminalSession("/bin/sh", ["-c", "yes 0123456789 | head -n 23000; echo the-end"]);
        var exited = new TaskCompletionSource<int>();
        terminal.Exited += code => exited.TrySetResult(code);
        await terminal.EnsureStartedAsync();
        using var program = Process.GetProcessById(terminal.ProcessId!.Value);
        Thread.Sleep(TimeSpan.FromSeconds(3));
        Assert.True(program.HasExited);

        await WaitUntil(() => exited.Task.IsCompleted && terminal.ReadText().Contains("[Process exited", StringComparison.Ordinal), TimeSpan.FromSeconds(30));
        Assert.Equal(0, await exited.Task.WaitAsync(TimeSpan.FromSeconds(1)));
        var text = terminal.ReadText();
        Assert.Contains("the-end", text);
        Assert.True(text.IndexOf("the-end", StringComparison.Ordinal) < text.IndexOf("[Process exited", StringComparison.Ordinal), text[^1000..]);
    }
}
