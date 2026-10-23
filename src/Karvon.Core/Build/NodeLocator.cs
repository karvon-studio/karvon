namespace Karvon.Build;

/// <summary>
/// Where Node.js is when it is not on Karvon's PATH (Karvon started from the desktop, whose PATH has no nvm): the newest
/// of nvm's, fnm's or Volta's, or Homebrew's and /usr/local's. Its folder goes first on the tasks' PATH, so npm and the
/// scripts' node are found as in the user's terminal.
/// </summary>
public static class NodeLocator
{
    static string NodeName => OperatingSystem.IsWindows() ? "node.exe" : "node";

    /// <summary>The folder of node when the PATH has none; null when node is on the PATH or nowhere Karvon knows.</summary>
    public static string? DirectoryOffPath(string? path = null)
    {
        path ??= Environment.GetEnvironmentVariable("PATH") ?? "";
        if (path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Any(dir => File.Exists(Path.Combine(dir, NodeName))))
            return null;
        return Candidates().FirstOrDefault(dir => File.Exists(Path.Combine(dir, NodeName)));
    }

    static IEnumerable<string> Candidates()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var nvm = Environment.GetEnvironmentVariable("NVM_DIR") is { Length: > 0 } nvmDir ? nvmDir : Path.Combine(home, ".nvm");
        foreach (var version in Newest(Path.Combine(nvm, "versions", "node")))
            yield return Path.Combine(version, "bin");
        foreach (var version in Newest(Path.Combine(home, ".local", "share", "fnm", "node-versions")))
            yield return Path.Combine(version, "installation", "bin");
        yield return Path.Combine(home, ".volta", "bin");
        yield return "/opt/homebrew/bin";
        yield return "/usr/local/bin";
    }

    /// <summary>The version folders (v24.21.0...), the newest first.</summary>
    static IEnumerable<string> Newest(string folder)
    {
        if (!Directory.Exists(folder))
            return [];
        return Directory.EnumerateDirectories(folder)
            .Select(dir => (Dir: dir, Version: Version.TryParse(Path.GetFileName(dir).TrimStart('v'), out var v) ? v : null))
            .Where(x => x.Version is not null)
            .OrderByDescending(x => x.Version)
            .Select(x => x.Dir);
    }
}
