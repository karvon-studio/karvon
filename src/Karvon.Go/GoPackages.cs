using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Karvon.Modules;
using Karvon.Packages;

namespace Karvon.Go;

/// <summary>
/// A Go module's dependencies: pkg.go.dev's packages (its search page, as it has no API), their versions from the Go
/// module proxy, go.mod's requirements (those its code imports, and the indirect ones: one just added, before code
/// imports it), added by go get module@version and removed by go get module@none.
/// </summary>
public sealed partial class GoPackages(string directory) : PackageSource
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public override string Name => "go";

    public override string Registry => "pkg.go.dev";

    public override string Directory => directory;

    string? GoMod()
    {
        var file = Path.Combine(directory, "go.mod");
        try
        {
            return File.Exists(file) ? File.ReadAllText(file) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"^\s*module\s+(\S+)", RegexOptions.Multiline)]
    private static partial Regex ModuleLine();

    public override string Project => GoMod() is { } mod && ModuleLine().Match(mod) is { Success: true } m
        ? m.Groups[1].Value
        : Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar));

    [GeneratedRegex(@"data-clicked-package=""(?<v>[^""]+)""")]
    private static partial Regex SnippetPath();

    [GeneratedRegex(@"<p class=""SearchSnippet-synopsis""[^>]*>\s*(?<v>[\s\S]*?)\s*</p>")]
    private static partial Regex SnippetSynopsis();

    [GeneratedRegex(@"Imported by </span><strong>(?<v>[\d,]+)</strong>")]
    private static partial Regex SnippetImportedBy();

    [GeneratedRegex(@"<strong>(?<v>v[^<]+)</strong> published")]
    private static partial Regex SnippetVersion();

    [GeneratedRegex(@"data-test-id=""snippet-license"">\s*<a [^>]*>\s*(?<v>[^<]*?)\s*</a>")]
    private static partial Regex SnippetLicense();

    public override async Task<IReadOnlyList<RegistryPackage>> SearchAsync(string text, CancellationToken ct)
    {
        var page = await Http.GetStringAsync($"https://pkg.go.dev/search?q={Uri.EscapeDataString(text)}&m=package&limit=25", ct);
        static string? Value(Regex regex, string snippet) => regex.Match(snippet) is { Success: true } m && m.Groups["v"].Value.Trim() is { Length: > 0 } v ? WebUtility.HtmlDecode(v) : null;
        // Each result's own part of the page.
        return [.. page.Split("<div class=\"SearchSnippet\"").Skip(1).Select(snippet => Value(SnippetPath(), snippet) is { } path
            ? new RegistryPackage(path, Value(SnippetVersion(), snippet), Value(SnippetSynopsis(), snippet))
            {
                Downloads = long.TryParse(Value(SnippetImportedBy(), snippet)?.Replace(",", ""), out var n) ? n : 0,
                License = Value(SnippetLicense(), snippet),
                ProjectUrl = "https://pkg.go.dev/" + path,
            }
            : null).OfType<RegistryPackage>().DistinctBy(p => p.Id)];
    }

    /// <summary>A module's path as the proxy has it: each capital as ! and its small letter.</summary>
    static string Escape(string path) => string.Concat(path.Select(c => char.IsUpper(c) ? "!" + char.ToLowerInvariant(c) : c.ToString()));

    /// <summary>The module a package is in, by its path and those above it, with its versions; null when the proxy has none.</summary>
    static async Task<(string Module, IReadOnlyList<string> Versions)?> ModuleOfAsync(string path, CancellationToken ct)
    {
        for (var module = path; module.Contains('/'); module = module[..module.LastIndexOf('/')])
        {
            using var response = await Http.GetAsync($"https://proxy.golang.org/{Escape(module)}/@v/list", ct);
            if (!response.IsSuccessStatusCode)
                continue;
            var versions = (await response.Content.ReadAsStringAsync(ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (versions.Length > 0)
                return (module, versions);
            // A module with no tagged version: its latest pseudo-version.
            using var latest = await Http.GetAsync($"https://proxy.golang.org/{Escape(module)}/@latest", ct);
            if (latest.IsSuccessStatusCode && JsonDocument.Parse(await latest.Content.ReadAsStringAsync(ct)).RootElement.TryGetProperty("Version", out var v))
                return (module, [v.GetString()!]);
        }
        return null;
    }

    public override async Task<RegistryPackage?> FindAsync(string id, CancellationToken ct)
    {
        IReadOnlyList<RegistryPackage> found;
        try
        {
            found = await SearchAsync(id, ct);
        }
        catch (HttpRequestException)
        {
            found = [];
        }
        if (found.FirstOrDefault(p => p.Id == id) is { } package)
            return package;
        // Not in the search: the proxy's latest version only.
        return await ModuleOfAsync(id, ct) is { } module
            ? new RegistryPackage(id, module.Versions.Where(v => !PackageVersions.IsPrerelease(v)).DefaultIfEmpty(module.Versions[^1]).Max(Comparer<string>.Create(PackageVersions.Compare)), null)
            : null;
    }

    public override async Task<IReadOnlyList<string>> VersionsAsync(string id, bool prerelease, CancellationToken ct) =>
        await ModuleOfAsync(id, ct) is { } module
            ? [.. module.Versions.Where(v => prerelease || !PackageVersions.IsPrerelease(v) || module.Versions.Count == 1)
                .OrderByDescending(v => v, Comparer<string>.Create(PackageVersions.Compare))]
            : [];

    [GeneratedRegex(@"^\s*(?:require\s+)?(?<path>[^\s()]+)\s+(?<version>v\d\S*)(?<rest>.*)$")]
    private static partial Regex RequireLine();

    public override IReadOnlyList<DependencyPackage> Dependencies()
    {
        if (GoMod() is not { } mod)
            return [];
        List<DependencyPackage> found = [];
        var inRequire = false;
        foreach (var raw in mod.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("require (", StringComparison.Ordinal) || line == "require(")
            {
                inRequire = true;
                continue;
            }
            if (inRequire && line == ")")
            {
                inRequire = false;
                continue;
            }
            if (!inRequire && !line.StartsWith("require ", StringComparison.Ordinal))
                continue;
            if (RequireLine().Match(line) is { Success: true } m)
                found.Add(new(m.Groups["path"].Value, m.Groups["version"].Value, m.Groups["version"].Value,
                    m.Groups["rest"].Value.Contains("// indirect", StringComparison.Ordinal) ? "indirect" : "require"));
        }
        return found;
    }

    public override PackageChange Add(string id, string version, string? group) =>
new WorkspaceTask($"go: get {id}", WorkspaceTaskKind.Other, "go", ["get", $"{id}@{version}"], directory) { Source = GoTasks.Source };

    public override PackageChange Remove(DependencyPackage package) =>
new WorkspaceTask($"go: get {package.Id}@none", WorkspaceTaskKind.Other, "go", ["get", $"{package.Id}@none"], directory) { Source = GoTasks.Source };
}
