using System.Net.Http.Headers;
using System.Text.Json;
using Karvon.Modules;
using Karvon.Packages;

namespace Karvon.TypeScript;

/// <summary>
/// A package.json folder's packages: npmjs.com's registry, its dependencies and devDependencies (what node_modules has
/// of them), added and removed by the folder's package manager (npm, pnpm, yarn or bun).
/// </summary>
public sealed class NpmPackages(string directory) : PackageSource
{
    internal static readonly HttpClient Http = CreateHttp();

    static HttpClient CreateHttp()
    {
        var http = new HttpClient { BaseAddress = new Uri("https://registry.npmjs.org/"), Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Karvon", "0.1"));
        return http;
    }

    static readonly JsonDocumentOptions Options = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    readonly string _manager = PackageScripts.ManagerOf(directory);

    public override string Name => _manager;

    public override string Registry => "npmjs.com";

    public override string Directory => directory;

    public override string Project => Manifest() is { } package && package.TryGetProperty("name", out var name) && name.GetString() is { Length: > 0 } text
        ? text
        : Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar));

    public override IReadOnlyList<string> Groups => ["dependencies", "devDependencies"];

    JsonElement? Manifest()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "package.json")), Options);
            return doc.RootElement.Clone();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public override async Task<IReadOnlyList<RegistryPackage>> SearchAsync(string text, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await Http.GetStringAsync($"-/v1/search?text={Uri.EscapeDataString(text)}&size=40", ct));
        return [.. doc.RootElement.GetProperty("objects").EnumerateArray().Select(o =>
        {
            var p = o.GetProperty("package");
            return new RegistryPackage(p.GetProperty("name").GetString()!, Str(p, "version"), Str(p, "description"))
            {
                Owners = p.TryGetProperty("publisher", out var publisher) ? Str(publisher, "username") : null,
                Downloads = o.TryGetProperty("downloads", out var downloads) && downloads.TryGetProperty("monthly", out var monthly) && monthly.TryGetInt64(out var n) ? n : 0,
                ProjectUrl = p.TryGetProperty("links", out var links) ? Str(links, "homepage") ?? Str(links, "npm") : null,
                License = Str(p, "license"),
            };
        })];
    }

    static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>A package's path on the registry: a scoped one's slash stays ("@types/node").</summary>
    static string PathOf(string id) => string.Join('/', id.Split('/').Select(Uri.EscapeDataString));

    public override async Task<RegistryPackage?> FindAsync(string id, CancellationToken ct)
    {
        using var response = await Http.GetAsync($"{PathOf(id)}/latest", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var p = doc.RootElement;
        return new RegistryPackage(Str(p, "name") ?? id, Str(p, "version"), Str(p, "description"))
        {
            Owners = p.TryGetProperty("author", out var author) ? author.ValueKind == JsonValueKind.String ? author.GetString() : Str(author, "name") : null,
            ProjectUrl = Str(p, "homepage"),
            License = Str(p, "license"),
        };
    }

    public override async Task<IReadOnlyList<string>> VersionsAsync(string id, bool prerelease, CancellationToken ct)
    {
        // The abbreviated document: the versions without their readmes.
        using var request = new HttpRequestMessage(HttpMethod.Get, PathOf(id));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.npm.install-v1+json"));
        using var response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return [.. doc.RootElement.GetProperty("versions").EnumerateObject().Select(v => v.Name)
            .Where(v => prerelease || !PackageVersions.IsPrerelease(v))
            .OrderByDescending(v => v, Comparer<string>.Create(PackageVersions.Compare))];
    }

    public override IReadOnlyList<DependencyPackage> Dependencies()
    {
        if (Manifest() is not { } package)
            return [];
        List<DependencyPackage> found = [];
        foreach (var group in Groups)
            if (package.TryGetProperty(group, out var dependencies) && dependencies.ValueKind == JsonValueKind.Object)
                foreach (var dependency in dependencies.EnumerateObject())
                    found.Add(new(dependency.Name, dependency.Value.GetString(), InstalledVersion(dependency.Name), group));
        return found;
    }

    /// <summary>The version node_modules has of a package: the folder's own, else one above it (a workspace's).</summary>
    string? InstalledVersion(string id)
    {
        for (var dir = directory; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            var file = Path.Combine(dir, "node_modules", id, "package.json");
            if (!File.Exists(file))
                continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file), Options);
                return Str(doc.RootElement, "version");
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return null;
            }
        }
        return null;
    }

    public override PackageChange Add(string id, string version, string? group)
    {
        var dev = group == "devDependencies";
        List<string> args = _manager switch
        {
            "npm" => ["install", $"{id}@{version}", .. dev ? ["--save-dev"] : Array.Empty<string>()],
            "bun" => ["add", $"{id}@{version}", .. dev ? ["--dev"] : Array.Empty<string>()],
            _ => ["add", $"{id}@{version}", .. dev ? ["--dev"] : Array.Empty<string>()],
        };
        return new WorkspaceTask($"{_manager}: add {id}", WorkspaceTaskKind.Other, _manager, args, directory) { Source = PackageScripts.Source };
    }

    public override PackageChange Remove(DependencyPackage package) =>
new WorkspaceTask($"{_manager}: remove {package.Id}", WorkspaceTaskKind.Other, _manager, [_manager == "npm" ? "uninstall" : "remove", package.Id], directory) { Source = PackageScripts.Source };
}
