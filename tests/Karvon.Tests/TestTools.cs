namespace Karvon.Tests;

/// <summary>What the tests run their fakes with, as each platform has it.</summary>
static class TestTools
{
    /// <summary>Python's command: python3 on Linux and macOS, python on Windows (where python3 is a store's stub).</summary>
    public static string Python => OperatingSystem.IsWindows() ? "python" : "python3";

    /// <summary>
    /// A Python script as a command of its own: itself, executable, on Linux and macOS (its #! line runs it); on
    /// Windows a .cmd beside it that runs it with Python, its variables set first.
    /// </summary>
    public static string Command(string script, string? folder = null, IReadOnlyDictionary<string, string>? environment = null)
    {
        folder ??= Path.GetDirectoryName(script)!;
        var name = Path.GetFileNameWithoutExtension(script);
        if (OperatingSystem.IsWindows())
        {
            var cmd = Path.Combine(folder, name + ".cmd");
            var set = string.Concat((environment ?? new Dictionary<string, string>()).Select(e => $"set \"{e.Key}={e.Value}\"\r\n"));
            File.WriteAllText(cmd, $"@echo off\r\n{set}{Python} -I \"{script}\" %*\r\n");
            return cmd;
        }
        if (environment is null || environment.Count == 0)
        {
            File.SetUnixFileMode(script, File.GetUnixFileMode(script) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            return script;
        }
        var wrapper = Path.Combine(folder, name);
        var exports = string.Concat(environment.Select(e => $"{e.Key}='{e.Value}' "));
        File.WriteAllText(wrapper, $"#!/bin/sh\n{exports}exec python3 -I '{script}' \"$@\"\n");
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return wrapper;
    }

    /// <summary>A test that runs Unix's shell (sh -c, its commands): not on Windows.</summary>
    public static void SkipOnWindows(string why = "It runs Unix's shell.") => Assert.SkipWhen(OperatingSystem.IsWindows(), why);
}
