using System.Text.Json;
using System.Text.RegularExpressions;
using Karvon.Modules;
using Karvon.Packages;

namespace Karvon.Python;

/// <summary>
/// A Python project's packages: PyPI's (found by their names, as pypi.org's search is for browsers only); a uv project's
/// (pyproject.toml's [project]) dependencies and dev group, added and removed by uv add and uv remove; else the
/// requirements.txt of a folder, installed into its virtual environment by pip. The versions are those the virtual
/// environment has.
/// </summary>
public sealed partial class PypiPackages(string directory) : PackageSource
{
    static readonly HttpClient Http = new() { BaseAddress = new Uri("https://pypi.org/pypi/"), Timeout = TimeSpan.FromSeconds(30) };

    public override string Name => IsUv ? "uv" : "pip";

    public override string Registry => "pypi.org";

    public override string Directory => directory;

    IReadOnlyList<Toml.Table> Pyproject()
    {
        var file = System.IO.Path.Combine(directory, "pyproject.toml");
        try
        {
            return File.Exists(file) ? Toml.Read(File.ReadAllText(file)) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>A project of its own (pyproject.toml's [project]): uv adds to it.</summary>
    bool IsUv => Pyproject().Any(t => t.Name == "project");

    public override string Project => Pyproject().FirstOrDefault(t => t.Name == "project")?["name"] is { } name
        ? Toml.Unquote(name)
        : System.IO.Path.GetFileName(directory.TrimEnd(System.IO.Path.DirectorySeparatorChar));

    public override IReadOnlyList<string> Groups => IsUv ? ["dependencies", "dev"] : [];

    /// <summary>PEP 503's name: requests, Typing_Extensions and typing.extensions are typing-extensions.</summary>
    public static string Normalize(string name) => Separators().Replace(name, "-").ToLowerInvariant();

    [GeneratedRegex("[-_.]+")]
    private static partial Regex Separators();

    public override async Task<IReadOnlyList<RegistryPackage>> SearchAsync(string text, CancellationToken ct) =>
        await FindAsync(text, ct) is { } found ? [found] : [];

    async Task<JsonDocument?> ProjectAsync(string id, CancellationToken ct)
    {
        using var response = await Http.GetAsync($"{Uri.EscapeDataString(Normalize(id.Trim()))}/json", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    public override async Task<RegistryPackage?> FindAsync(string id, CancellationToken ct)
    {
        if (id.Trim().Length == 0)
            return null;
        using var doc = await ProjectAsync(id, ct);
        if (doc is null)
            return null;
        var info = doc.RootElement.GetProperty("info");
        var urls = info.TryGetProperty("project_urls", out var u) && u.ValueKind == JsonValueKind.Object ? u : default;
        return new RegistryPackage(Str(info, "name") ?? id, Str(info, "version"), Str(info, "summary"))
        {
            Owners = Str(info, "author") ?? Str(info, "maintainer"),
            License = Str(info, "license_expression") ?? (Str(info, "license") is { Length: < 60 } license ? license : null),
            ProjectUrl = Str(info, "home_page") ?? (urls.ValueKind == JsonValueKind.Object
                ? urls.EnumerateObject().Select(p => p.Value.GetString()).FirstOrDefault(v => v is { Length: > 0 }) : null),
        };
    }

    public override async Task<IReadOnlyList<string>> VersionsAsync(string id, bool prerelease, CancellationToken ct)
    {
        using var doc = await ProjectAsync(id, ct);
        if (doc is null)
            return [];
        // A release with files, not all of them yanked.
        return [.. doc.RootElement.GetProperty("releases").EnumerateObject()
            .Where(r => r.Value.ValueKind == JsonValueKind.Array && r.Value.EnumerateArray().Any(f => !(f.TryGetProperty("yanked", out var y) && y.ValueKind == JsonValueKind.True)))
            .Select(r => r.Name)
            .Where(v => prerelease || !IsPrerelease(v))
            .OrderByDescending(v => v, Comparer<string>.Create(CompareVersions))];
    }

    public override int Compare(string a, string b) => CompareVersions(a, b);

    [GeneratedRegex(@"^v?(?<release>\d+(?:\.\d+)*)(?:[-_.]?(?<pre>a|b|c|rc|alpha|beta|pre|preview)[-_.]?(?<pren>\d*))?(?:[-_.]?(?:post|rev|r)[-_.]?(?<post>\d*))?(?:[-_.]?dev[-_.]?(?<dev>\d*))?", RegexOptions.IgnoreCase)]
    private static partial Regex Pep440();

    public static bool IsPrerelease(string version) => Pep440().Match(version) is { Success: true } m && (m.Groups["pre"].Success || m.Groups["dev"].Success);

    /// <summary>PEP 440's order: the release's numbers; then a dev release before a prerelease before the release, and a post release after it.</summary>
    public static int CompareVersions(string a, string b)
    {
        static (long[] Release, int Phase, long Number, long Post, long Dev) Parse(string v)
        {
            var m = Pep440().Match(v.Trim());
            if (!m.Success)
                return ([], 0, 0, 0, 0);
            long N(string group) => long.TryParse(m.Groups[group].Value, out var n) ? n : 0;
            var phase = m.Groups["pre"].Value.ToLowerInvariant() switch
            {
                "a" or "alpha" => -3,
                "b" or "beta" => -2,
                "c" or "rc" or "pre" or "preview" => -1,
                _ => m.Groups["dev"].Success && !m.Groups["post"].Success ? -4 : 0,
            };
            return ([.. m.Groups["release"].Value.Split('.').Select(long.Parse)], phase, N("pren"), m.Groups["post"].Success ? N("post") + 1 : 0,
                m.Groups["dev"].Success ? N("dev") : long.MaxValue);
        }
        var (ra, pa, na, posta, deva) = Parse(a);
        var (rb, pb, nb, postb, devb) = Parse(b);
        for (var i = 0; i < Math.Max(ra.Length, rb.Length); i++)
        {
            var c = (i < ra.Length ? ra[i] : 0).CompareTo(i < rb.Length ? rb[i] : 0);
            if (c != 0)
                return c;
        }
        var order = pa.CompareTo(pb);
        if (order == 0)
            order = na.CompareTo(nb);
        if (order == 0)
            order = posta.CompareTo(postb);
        return order != 0 ? order : deva.CompareTo(devb);
    }

    [GeneratedRegex(@"^\s*(?<name>[A-Za-z0-9][A-Za-z0-9._-]*)\s*(?:\[[^\]]*\])?\s*(?<spec>[^;#]*)")]
    private static partial Regex Requirement();

    static (string Name, string? Spec)? RequirementOf(string text) =>
        Requirement().Match(text) is { Success: true } m ? (m.Groups["name"].Value, m.Groups["spec"].Value.Trim() is { Length: > 0 } spec ? spec : null) : null;

    public override IReadOnlyList<DependencyPackage> Dependencies()
    {
        var installed = InstalledVersions();
        List<DependencyPackage> found = [];
        void Add(string requirement, string group)
        {
            if (RequirementOf(requirement) is { } r)
                found.Add(new(r.Name, r.Spec, installed.GetValueOrDefault(Normalize(r.Name)), group));
        }
        if (IsUv)
        {
            var tables = Pyproject();
            foreach (var requirement in Toml.Strings(tables.First(t => t.Name == "project")["dependencies"] ?? "[]"))
                Add(requirement, "dependencies");
            // PEP 735's dependency groups (uv's dev group among them), and uv's older dev-dependencies.
            if (tables.FirstOrDefault(t => t.Name == "dependency-groups") is { } groups)
                foreach (var (group, value) in groups.Entries)
                    foreach (var requirement in Toml.Strings(value))
                        Add(requirement, group);
            foreach (var requirement in Toml.Strings(tables.FirstOrDefault(t => t.Name == "tool.uv")?["dev-dependencies"] ?? "[]"))
                Add(requirement, "dev");
            return found;
        }
        var requirements = System.IO.Path.Combine(directory, "requirements.txt");
        if (File.Exists(requirements))
            foreach (var line in File.ReadLines(requirements).Select(l => l.Trim()).Where(l => l.Length > 0 && l[0] is not ('#' or '-')))
                Add(line, "requirements.txt");
        return found;
    }

    /// <summary>What the virtual environment has: its site-packages' dist-info folders, by their normalized names.</summary>
    Dictionary<string, string> InstalledVersions()
    {
        var versions = new Dictionary<string, string>(StringComparer.Ordinal);
        if (PythonEnvironment.VirtualEnvironmentBin(directory) is not { } bin || System.IO.Path.GetDirectoryName(bin) is not { } venv)
            return versions;
        var lib = System.IO.Path.Combine(venv, OperatingSystem.IsWindows() ? "Lib" : "lib");
        try
        {
            var sites = OperatingSystem.IsWindows()
                ? [System.IO.Path.Combine(lib, "site-packages")]
                : System.IO.Directory.GetDirectories(lib, "python*").Select(d => System.IO.Path.Combine(d, "site-packages"));
            foreach (var site in sites.Where(System.IO.Directory.Exists))
                foreach (var info in System.IO.Directory.GetDirectories(site, "*.dist-info"))
                {
                    var name = System.IO.Path.GetFileName(info)[..^".dist-info".Length];
                    var dash = name.LastIndexOf('-');
                    if (dash > 0)
                        versions.TryAdd(Normalize(name[..dash]), name[(dash + 1)..]);
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return versions;
    }

    /// <summary>The virtual environment's python (pip installs into it), else python3.</summary>
    string Python => PythonEnvironment.VirtualEnvironmentBin(directory) is { } bin
        ? System.IO.Path.Combine(bin, OperatingSystem.IsWindows() ? "python.exe" : "python")
        : OperatingSystem.IsWindows() ? "python" : "python3";

    public override WorkspaceTask Add(string id, string version, string? group) => IsUv
        ? new($"uv: add {id}", WorkspaceTaskKind.Other, "uv", ["add", $"{id}=={version}", .. group is null or "dependencies" ? Array.Empty<string>() : ["--group", group]], directory)
        : new($"pip: install {id}", WorkspaceTaskKind.Other, Python, ["-m", "pip", "install", $"{id}=={version}"], directory);

    public override WorkspaceTask Remove(DependencyPackage package) => IsUv
        ? new($"uv: remove {package.Id}", WorkspaceTaskKind.Other, "uv", ["remove", package.Id, .. package.Group is "dependencies" ? Array.Empty<string>() : ["--group", package.Group]], directory)
        : new($"pip: uninstall {package.Id}", WorkspaceTaskKind.Other, Python, ["-m", "pip", "uninstall", "-y", package.Id], directory);
}
