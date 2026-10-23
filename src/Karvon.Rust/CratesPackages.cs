using System.Net.Http.Headers;
using System.Text.Json;
using Karvon.Modules;
using Karvon.Packages;

namespace Karvon.Rust;

/// <summary>
/// A Cargo package's crates: crates.io's, its [dependencies], [dev-dependencies] and [build-dependencies] (the
/// versions Cargo.lock has of them), added and removed by cargo add and cargo remove.
/// </summary>
public sealed class CratesPackages(string directory) : PackageSource
{
    static readonly HttpClient Http = CreateHttp();

    static HttpClient CreateHttp()
    {
        // crates.io asks for a User-Agent that says who calls.
        var http = new HttpClient { BaseAddress = new Uri("https://crates.io/api/v1/"), Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Karvon-Studio (https://github.com/karvon-studio/karvon)");
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return http;
    }

    public override string Name => "cargo";

    public override string Registry => "crates.io";

    public override string Directory => directory;

    public override string Project => Manifest().FirstOrDefault(t => t.Name == "package")?["name"] is { } name
        ? Toml.Unquote(name)
        : Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar));

    public override IReadOnlyList<string> Groups => ["dependencies", "dev-dependencies", "build-dependencies"];

    IReadOnlyList<Toml.Table> Manifest() => Read(Path.Combine(directory, "Cargo.toml"));

    static IReadOnlyList<Toml.Table> Read(string file)
    {
        try
        {
            return File.Exists(file) ? Toml.Read(File.ReadAllText(file)) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static RegistryPackage PackageOf(JsonElement crate, string? license) =>
        new(crate.GetProperty("name").GetString()!, Str(crate, "max_stable_version") ?? Str(crate, "newest_version"), Str(crate, "description"))
        {
            Downloads = crate.TryGetProperty("downloads", out var d) && d.TryGetInt64(out var n) ? n : 0,
            ProjectUrl = Str(crate, "homepage") ?? Str(crate, "repository"),
            License = license,
        };

    public override async Task<IReadOnlyList<RegistryPackage>> SearchAsync(string text, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await Http.GetStringAsync($"crates?q={Uri.EscapeDataString(text)}&per_page=40", ct));
        return [.. doc.RootElement.GetProperty("crates").EnumerateArray().Select(c => PackageOf(c, null))];
    }

    async Task<JsonDocument?> CrateAsync(string id, CancellationToken ct)
    {
        using var response = await Http.GetAsync($"crates/{Uri.EscapeDataString(id)}", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    public override async Task<RegistryPackage?> FindAsync(string id, CancellationToken ct)
    {
        using var doc = await CrateAsync(id, ct);
        if (doc is null)
            return null;
        var license = doc.RootElement.TryGetProperty("versions", out var versions) && versions.GetArrayLength() > 0 ? Str(versions[0], "license") : null;
        return PackageOf(doc.RootElement.GetProperty("crate"), license);
    }

    public override async Task<IReadOnlyList<string>> VersionsAsync(string id, bool prerelease, CancellationToken ct)
    {
        using var doc = await CrateAsync(id, ct);
        if (doc is null)
            return [];
        return [.. doc.RootElement.GetProperty("versions").EnumerateArray()
            .Where(v => !(v.TryGetProperty("yanked", out var yanked) && yanked.ValueKind == JsonValueKind.True))
            .Select(v => v.GetProperty("num").GetString()!)
            .Where(v => prerelease || !PackageVersions.IsPrerelease(v))
            .OrderByDescending(v => v, Comparer<string>.Create(PackageVersions.Compare))];
    }

    public override IReadOnlyList<DependencyPackage> Dependencies()
    {
        var locked = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var package in Read(Path.Combine(directory, "Cargo.lock")).Where(t => t.Name == "package"))
            if (package["name"] is { } name && package["version"] is { } version)
                locked.TryAdd(Toml.Unquote(name), Toml.Unquote(version));
        List<DependencyPackage> found = [];
        foreach (var table in Manifest())
        {
            // [dependencies]'s entries, or a dependency's own table: [dependencies.serde].
            if (Groups.Contains(table.Name))
            {
                foreach (var (id, value) in table.Entries)
                    found.Add(new(id, Requested(value), locked.GetValueOrDefault(id), table.Name));
            }
            else if (table.Name.IndexOf('.') is var dot && dot > 0 && Groups.Contains(table.Name[..dot]))
            {
                var id = Toml.Unquote(table.Name[(dot + 1)..]);
                found.Add(new(id, table["version"] is { } v ? Toml.Unquote(v) : table["path"] is { } p ? "path " + Toml.Unquote(p) : null,
                    locked.GetValueOrDefault(id), table.Name[..dot]));
            }
        }
        return found;
    }

    /// <summary>"1.0", or { version = "1.0", ... }'s; a path's or a git one's as such.</summary>
    static string? Requested(string value) =>
        value.StartsWith('{')
            ? Toml.InlineValue(value, "version") ?? (Toml.InlineValue(value, "path") is { } path ? "path " + path : Toml.InlineValue(value, "git") is { } git ? "git " + git : null)
            : Toml.Unquote(value);

    static IEnumerable<string> GroupFlag(string? group) => group switch
    {
        "dev-dependencies" => ["--dev"],
        "build-dependencies" => ["--build"],
        _ => [],
    };

    public override PackageChange Add(string id, string version, string? group) =>
new WorkspaceTask($"cargo: add {id}", WorkspaceTaskKind.Other, "cargo", ["add", $"{id}@{version}", .. GroupFlag(group)], directory) { Source = CargoTasks.Source };

    public override PackageChange Remove(DependencyPackage package) =>
new WorkspaceTask($"cargo: remove {package.Id}", WorkspaceTaskKind.Other, "cargo", ["remove", package.Id, .. GroupFlag(package.Group)], directory) { Source = CargoTasks.Source };
}
