using Karvon.Modules;

namespace Karvon.Packages;

/// <summary>A package on a registry (npmjs.com, crates.io, PyPI...).</summary>
public sealed record RegistryPackage(string Id, string? LatestVersion, string? Description)
{
    public string? Owners { get; init; }

    public long Downloads { get; init; }

    public string? ProjectUrl { get; init; }

    public string? License { get; init; }
}

/// <summary>A package a project depends on: what its manifest asks for, what is installed (null: not yet), and its group.</summary>
public sealed record DependencyPackage(string Id, string? Requested, string? Version, string Group);

/// <summary>
/// A language's packages for one of its projects (a package.json folder, a Cargo package...): its registry, what the
/// project depends on, and its package manager's commands that add and remove one. The Package Manager document shows it,
/// as Visual Studio's NuGet Package Manager shows a project's.
/// </summary>
public abstract class PackageSource
{
    /// <summary>The package manager ("npm", "cargo"), in the document's title.</summary>
    public abstract string Name { get; }

    /// <summary>The registry it searches ("npmjs.com").</summary>
    public abstract string Registry { get; }

    /// <summary>The project's name.</summary>
    public abstract string Project { get; }

    /// <summary>The project's folder, where its package manager runs.</summary>
    public abstract string Directory { get; }

    /// <summary>The groups a package is added to ("dependencies", "devDependencies"), the first by default; none, one kind.</summary>
    public virtual IReadOnlyList<string> Groups => [];

    /// <summary>The registry's packages for a text.</summary>
    public abstract Task<IReadOnlyList<RegistryPackage>> SearchAsync(string text, CancellationToken ct);

    /// <summary>One package by its exact id (an installed one's description and latest version), or null when there is none.</summary>
    public abstract Task<RegistryPackage?> FindAsync(string id, CancellationToken ct);

    /// <summary>Its versions, newest first; prereleases only when asked.</summary>
    public abstract Task<IReadOnlyList<string>> VersionsAsync(string id, bool prerelease, CancellationToken ct);

    /// <summary>What the project depends on, read from its manifest and what is installed. Quick: it reads files.</summary>
    public abstract IReadOnlyList<DependencyPackage> Dependencies();

    /// <summary>The package manager's command that adds a package (or moves it to another version) in a group.</summary>
    public abstract WorkspaceTask Add(string id, string version, string? group);

    /// <summary>The package manager's command that removes a package.</summary>
    public abstract WorkspaceTask Remove(DependencyPackage package);

    /// <summary>Which of two of its versions is newer, as semantic versions are ordered (a prerelease before its release).</summary>
    public virtual int Compare(string a, string b) => PackageVersions.Compare(a, b);
}

public static class PackageVersions
{
    /// <summary>Semantic versions' order: their numbers, then a prerelease before its release ("1.2.0-rc.1" &lt; "1.2.0").</summary>
    public static int Compare(string a, string b)
    {
        static (long[] Core, string Pre) Parse(string v)
        {
            v = v.TrimStart('v', '=', '^', '~');
            var build = v.IndexOf('+');
            if (build >= 0)
                v = v[..build];
            var dash = v.IndexOf('-');
            var core = dash < 0 ? v : v[..dash];
            return ([.. core.Split('.').Select(p => long.TryParse(p, out var n) ? n : 0)], dash < 0 ? "" : v[(dash + 1)..]);
        }
        var (ca, pa) = Parse(a);
        var (cb, pb) = Parse(b);
        for (var i = 0; i < Math.Max(ca.Length, cb.Length); i++)
        {
            var c = (i < ca.Length ? ca[i] : 0).CompareTo(i < cb.Length ? cb[i] : 0);
            if (c != 0)
                return c;
        }
        return (pa.Length == 0, pb.Length == 0) switch
        {
            (true, false) => 1,
            (false, true) => -1,
            _ => string.CompareOrdinal(pa, pb),
        };
    }

    public static bool IsPrerelease(string version) => version.Contains('-');

    /// <summary>A count as the registries show it: 1.2K, 3.4M.</summary>
    public static string FormatCount(long n) => n switch
    {
        >= 1_000_000_000 => $"{n / 1e9:0.#}B",
        >= 1_000_000 => $"{n / 1e6:0.#}M",
        >= 1_000 => $"{n / 1e3:0.#}K",
        _ => n.ToString(),
    };
}
