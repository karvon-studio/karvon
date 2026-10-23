using System.Text.RegularExpressions;

namespace Karvon.Servers;

/// <summary>A machine of the Servers explorer: from ~/.ssh/config, or added in Karvon.</summary>
public sealed record ServerHost(string Name, string Address, int Port, string User)
{
    /// <summary>Key files to try, in order (IdentityFile, else the usual ~/.ssh/id_* keys).</summary>
    public IReadOnlyList<string> IdentityFiles { get; init; } = [];

    /// <summary>A host to go through first (ProxyJump: an alias or [user@]host[:port]).</summary>
    public string? ProxyJump { get; init; }

    /// <summary>"ssh config" or "Karvon".</summary>
    public string Source { get; init; } = "ssh config";

    public string? Group { get; init; }

    /// <summary>Every change on it asks, from the explorer and from Claude.</summary>
    public bool Production { get; init; }

    public bool FromSshConfig => Source == "ssh config";

    /// <summary>"user@host:port" (the port only when it is not 22).</summary>
    public string Target => $"{User}@{Address}{(Port == 22 ? "" : ":" + Port)}";
}

/// <summary>
/// The hosts of ~/.ssh/config as OpenSSH reads them: <c>Host</c> blocks (patterns with * ? and !), <c>Include</c>, the
/// first value of each keyword winning, so a <c>Host *</c> at the end gives the others what they did not set; %h, %d,
/// %u and ~ in IdentityFile. <c>Match</c> blocks are skipped. Each concrete alias is a machine.
/// </summary>
public static partial class SshConfig
{
    sealed record Block(IReadOnlyList<string> Patterns, List<(string Key, string Value)> Options);

    /// <summary>The home whose .ssh is read: KARVON_SSH_HOME when set (the tests), else the user's.</summary>
    public static string Home => Environment.GetEnvironmentVariable("KARVON_SSH_HOME") is { Length: > 0 } home
        ? home
        : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string ConfigFile => Path.Combine(Home, ".ssh", "config");

    /// <summary>The machines of the config, in the order they first appear.</summary>
    public static IReadOnlyList<ServerHost> Read(string? file = null)
    {
        var blocks = new List<Block> { new(["*"], []) };
        Load(file ?? ConfigFile, blocks, 0);
        var aliases = blocks.SelectMany(b => b.Patterns).Where(p => !p.StartsWith('!') && p.IndexOfAny(['*', '?']) < 0).Distinct(StringComparer.Ordinal).ToList();
        return aliases.Select(Resolve).ToList();

        ServerHost Resolve(string alias)
        {
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            var identities = new List<string>();
            foreach (var block in blocks.Where(b => Matches(b.Patterns, alias)))
                foreach (var (key, value) in block.Options)
                {
                    if (key == "identityfile")
                        identities.Add(value);
                    else
                        options.TryAdd(key, value);
                }
            var user = options.GetValueOrDefault("user") ?? Environment.UserName;
            var address = (options.GetValueOrDefault("hostname") ?? alias).Replace("%h", alias, StringComparison.Ordinal);
            return new ServerHost(alias, address, int.TryParse(options.GetValueOrDefault("port"), out var port) ? port : 22, user)
            {
                IdentityFiles = identities.Select(i => Expand(i, alias, user)).Distinct(StringComparer.Ordinal).ToList(),
                ProxyJump = options.GetValueOrDefault("proxyjump") is { Length: > 0 } jump && !jump.Equals("none", StringComparison.OrdinalIgnoreCase) ? jump : null,
            };
        }
    }

    static void Load(string file, List<Block> blocks, int depth)
    {
        if (depth > 8 || !File.Exists(file))
            return;
        string[] lines;
        try
        {
            lines = File.ReadAllLines(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }
        // Keywords before any Host apply to every host, as the block blocks.Add started with.
        var current = depth == 0 ? blocks[0] : blocks[^1];
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var match = KeyValue().Match(line);
            if (!match.Success)
                continue;
            var key = match.Groups[1].Value.ToLowerInvariant();
            var value = match.Groups[2].Value.Trim().Trim('"');
            switch (key)
            {
                case "host":
                    current = new Block(value.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries), []);
                    blocks.Add(current);
                    break;
                case "match":
                    // Conditions Karvon does not evaluate: the block's keywords apply to no machine.
                    current = new Block([], []);
                    blocks.Add(current);
                    break;
                case "include":
                    foreach (var pattern in value.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
                        foreach (var included in Glob(Path.IsPathRooted(Expand(pattern, "", "")) ? Expand(pattern, "", "") : Path.Combine(Home, ".ssh", pattern)))
                            Load(included, blocks, depth + 1);
                    // Keywords after an Include belong to the block it was in.
                    blocks.Add(current = new Block(current.Patterns, []));
                    break;
                default:
                    current.Options.Add((key, value));
                    break;
            }
        }
    }

    /// <summary>Whether a Host line's patterns take the alias: one matches, and no negated one does.</summary>
    public static bool Matches(IReadOnlyList<string> patterns, string alias)
    {
        var any = false;
        foreach (var pattern in patterns)
        {
            if (pattern.StartsWith('!'))
            {
                if (Wildcard(pattern[1..], alias))
                    return false;
            }
            else if (Wildcard(pattern, alias))
                any = true;
        }
        return any;
    }

    static bool Wildcard(string pattern, string text) =>
        Regex.IsMatch(text, "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase);

    static IEnumerable<string> Glob(string path)
    {
        var dir = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileName(path);
        if (name.IndexOfAny(['*', '?']) < 0)
            return [path];
        return Directory.Exists(dir) ? Directory.EnumerateFiles(dir).Where(f => Wildcard(name, Path.GetFileName(f))).Order(StringComparer.Ordinal) : [];
    }

    static string Expand(string path, string alias, string user)
    {
        path = path.Replace("%d", Home, StringComparison.Ordinal).Replace("%h", alias, StringComparison.Ordinal)
            .Replace("%u", user, StringComparison.Ordinal).Replace("%%", "%", StringComparison.Ordinal);
        return path.StartsWith("~/", StringComparison.Ordinal) ? Path.GetFullPath(Path.Combine(Home, path[2..])) : path;
    }

    /// <summary>The keys ssh tries when a host names none.</summary>
    public static IReadOnlyList<string> DefaultKeys => new[] { "id_ed25519", "id_ecdsa", "id_rsa" }.Select(k => Path.Combine(Home, ".ssh", k)).ToList();

    [GeneratedRegex(@"^(\w+)\s*[=\s]\s*(.+)$")]
    private static partial Regex KeyValue();
}
