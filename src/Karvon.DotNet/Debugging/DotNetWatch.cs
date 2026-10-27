using System.Globalization;
using System.Text.RegularExpressions;
using Karvon.Modules;

namespace Karvon.DotNet.Debugging;

/// <summary>
/// Hot Reload of a .NET project run without the debugger (Ctrl+F5): the run is dotnet watch's, which applies a code
/// change as it is saved (Roslyn's deltas, applied in the program by the SDK's agent), restarts the program for a change
/// it cannot apply, and refreshes a web app's pages. Its lines say in the status bar what it did; once the program exits
/// by itself (dotnet watch then waits for a change to start it again), the run ends, with the program's exit code. Not a
/// console app's run: dotnet watch reads the keyboard itself (for its Ctrl+R), so the app would get none of it.
/// </summary>
static partial class DotNetWatch
{
    /// <summary>dotnet watch of a project, the keyboard the program's; its build's output short (Karvon built it already).</summary>
    public static List<string> Arguments(string project, string configuration) =>
        ["watch", "--project", project, "--non-interactive", "-c", configuration, "-v", "q"];

    public static IReadOnlyDictionary<string, string> Environment { get; } = WatchEnvironment();

    static Dictionary<string, string> WatchEnvironment()
    {
        var environment = new Dictionary<string, string>(DotNetDebugger.OutputEnvironment)
        {
            ["DOTNET_WATCH_RESTART_ON_RUDE_EDIT"] = "true",
            // Karvon opens the browser where the app listens, as it does without Hot Reload.
            ["DOTNET_WATCH_SUPPRESS_LAUNCH_BROWSER"] = "1",
            ["DOTNET_WATCH_SUPPRESS_EMOJIS"] = "1",
        };
        // On Linux each file system watcher is an inotify instance, of which a user has 128 by default (an editor, its
        // language servers and the desktop take most), and dotnet watch takes one for each folder of the project: it
        // polls instead (the app too: ASP.NET Core's reload of appsettings.json).
        if (OperatingSystem.IsLinux())
            environment["DOTNET_USE_POLLING_FILE_WATCHER"] = "1";
        return environment;
    }

    /// <summary>A line's colors (its output keeps them, redirected).</summary>
    [GeneratedRegex(@"\x1b\[[0-?]*[ -/]*[@-~]")]
    private static partial Regex Escapes();

    /// <summary>A line of dotnet watch's own: "dotnet watch", its emoji (or ':'), what it says.</summary>
    [GeneratedRegex(@"^dotnet watch \S+ (?<text>.+)$")]
    private static partial Regex WatchLine();

    [GeneratedRegex(@"\] Exited(?: with error code (?<code>-?\d+))?$")]
    private static partial Regex Exited();

    [GeneratedRegex(@"^(?<path>.+?)\((?<position>\d+,\d+)\): error (?<message>.+)$")]
    private static partial Regex CompileError();

    /// <summary>A reader of a run's lines: Hot Reload's results, and the end of the program.</summary>
    public static Func<string, RunNews?> Reader()
    {
        var exitCode = 0;
        var restarting = false;
        return line =>
        {
            if (WatchLine().Match(Escapes().Replace(line, "").Trim()) is not { Success: true } match)
                return null;
            var text = match.Groups["text"].Value.Trim();
            if (Exited().Match(text) is { Success: true } exited)
            {
                exitCode = exited.Groups["code"].Success ? int.Parse(exited.Groups["code"].Value, CultureInfo.InvariantCulture) : 0;
                return null;
            }
            if (text.StartsWith("Waiting for a file to change before restarting", StringComparison.Ordinal))
                return new RunNews(null, exitCode);
            if (CompileError().Match(text) is { Success: true } error)
                return new RunNews($"Hot Reload: {Path.GetFileName(error.Groups["path"].Value)}({error.Groups["position"].Value}): error {error.Groups["message"].Value}");
            if (text.StartsWith("Restart is needed", StringComparison.Ordinal))
            {
                restarting = true;
                return new RunNews("Hot Reload: these changes need a restart, which is under way...");
            }
            if (restarting && text.StartsWith("Waiting for changes", StringComparison.Ordinal))
            {
                restarting = false;
                return new RunNews("Hot Reload: restarted with the changes");
            }
            return text switch
            {
                _ when text.Contains(" changes applied", StringComparison.Ordinal) => new RunNews("Hot Reload: " + text.TrimEnd('.')),
                _ when text.StartsWith("No managed code changes", StringComparison.Ordinal) => new RunNews("Hot Reload: no code changes to apply"),
                _ when text.StartsWith("Unable to apply changes due to compilation errors", StringComparison.Ordinal) =>
                    new RunNews("Hot Reload: the code does not compile, so nothing was applied"),
                _ => null,
            };
        };
    }
}
