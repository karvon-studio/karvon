namespace Karvon.Claude;

/// <summary>
/// Finds an agent's program as a terminal would, and where Karvon started from the desktop does not look: PATH, then
/// ~/.local/bin, npm's, Bun's and nvm's folders (the newest Node first). An npm agent is a Node script: its folder holds
/// the node it runs with, so the folder goes first on the agent's PATH (<see cref="WithFolderOnPath"/>).
/// </summary>
public static class AgentExecutables
{
    static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>The first of <paramref name="names"/> found (without an extension; .exe and .cmd too on Windows); null for none.</summary>
    public static string? Find(params string[] names)
    {
        var files = OperatingSystem.IsWindows() ? names.SelectMany(n => new[] { n + ".exe", n + ".cmd" }).ToArray() : names;
        return Folders()
            .SelectMany(dir => files.Select(name => Path.Combine(dir, name)))
            .FirstOrDefault(File.Exists);
    }

    static IEnumerable<string> Folders()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            yield return dir.Trim().Trim('"');
        yield return Path.Combine(Home, ".local", "bin");
        yield return Path.Combine(Home, ".npm-global", "bin");
        yield return Path.Combine(Home, ".bun", "bin");
        if (OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("APPDATA") is { Length: > 0 } appData)
            yield return Path.Combine(appData, "npm");
        var nvm = Path.Combine(Home, ".nvm", "versions", "node");
        if (Directory.Exists(nvm))
            foreach (var version in Directory.EnumerateDirectories(nvm).OrderByDescending(d => Version.TryParse(Path.GetFileName(d).TrimStart('v'), out var v) ? v : new Version()))
                yield return Path.Combine(version, "bin");
    }

    /// <summary>The program's folder first on the environment's PATH (a Node script finds the node beside it).</summary>
    public static void WithFolderOnPath(IDictionary<string, string?> environment, string executable)
    {
        if (Path.GetDirectoryName(executable) is not { Length: > 0 } folder)
            return;
        var key = environment.Keys.FirstOrDefault(k => string.Equals(k, "PATH", StringComparison.OrdinalIgnoreCase)) ?? "PATH";
        var path = environment.TryGetValue(key, out var value) ? value ?? "" : "";
        if (!path.Split(Path.PathSeparator).Contains(folder))
            environment[key] = folder + Path.PathSeparator + path;
    }
}
