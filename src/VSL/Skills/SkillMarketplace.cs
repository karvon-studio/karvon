using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using ModelContextProtocol.Client;

namespace VSL.Skills;

/// <summary>A skill a source offers.</summary>
public sealed record RemoteSkill(string Name, string Description, string Source, string Version)
{
    public string? Category { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    public double? Rating { get; init; }

    public int? Installs { get; init; }

    /// <summary>Where its folder is: a local path, a URL, or a path in a GitHub repository.</summary>
    public required string Location { get; init; }

    /// <summary>Its files relative to the folder, when the source lists them.</summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    /// <summary>"free", or what it costs ("$5.00").</summary>
    public string Price { get; init; } = "free";

    public bool IsFree => Price == "free";

    /// <summary>Its page on the web, when it has one.</summary>
    public string? Url { get; init; }

    /// <summary>What the agent needs for it (a terminal, Git, web access...), as the source says.</summary>
    public IReadOnlyList<string> Requires { get; init; } = [];

    /// <summary>Licensed to run through its source (Agensi's): loaded by the agent on demand, never saved in a skills folder.</summary>
    public bool IsLicensed { get; init; }

    /// <summary>The folder it is installed as: its own folder's name.</summary>
    public string FolderName => Path.GetFileName(Location.TrimEnd('/', '\\')) is { Length: > 0 } name && !name.Contains(':') ? name : Name;
}

/// <summary>What a skill asks for, read before it is installed: the tools it allows, the scripts it ships, the network.</summary>
public sealed record SkillReview(IReadOnlyList<string> AllowedTools, IReadOnlyList<(string Path, string Text)> Scripts, IReadOnlyList<string> Network,
    IReadOnlyList<(string Path, byte[] Content)> Files)
{
    /// <summary>Its content's hash: a source without versions has an update when it changes.</summary>
    public string Hash => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", Files.OrderBy(f => f.Path, StringComparer.Ordinal)
        .Select(f => f.Path + ":" + Convert.ToHexString(SHA256.HashData(f.Content))))))).ToLowerInvariant()[..16];
}

/// <summary>A place skills come from: a GitHub repository, or a team's registry (a file or a URL).</summary>
public abstract class SkillSource(string spec)
{
    protected static readonly HttpClient Http = CreateClient();

    public string Spec { get; } = spec;

    public abstract string Name { get; }

    public abstract Task<IReadOnlyList<RemoteSkill>> ListAsync(CancellationToken cancellationToken);

    public abstract Task<IReadOnlyList<(string Path, byte[] Content)>> FilesAsync(RemoteSkill skill, CancellationToken cancellationToken);

    /// <summary>The source searches its own catalog (too big to list): <see cref="SearchAsync"/> instead of filtering what it listed.</summary>
    public virtual bool CanSearch => false;

    public virtual Task<IReadOnlyList<RemoteSkill>> SearchAsync(string query, CancellationToken cancellationToken) =>
        throw new NotSupportedException($"{Name} lists its skills, it does not search them.");

    /// <summary>A licensed skill's page (Markdown): its description, use cases, permissions, price.</summary>
    public virtual Task<string> DetailsAsync(RemoteSkill skill, CancellationToken cancellationToken) =>
        throw new NotSupportedException($"{Name}'s skills are read from their files.");

    static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("VSL", "0.1"));
        // GitHub allows 60 requests an hour without a token.
        if (Environment.GetEnvironmentVariable("GITHUB_TOKEN") is { Length: > 0 } token)
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    /// <summary>
    /// <c>github:owner/repo[/folder][@ref]</c>, <c>agensi</c> (agensi.io), a registry's URL, or a registry on disk (its
    /// folder or its <c>registry.json</c>).
    /// </summary>
    public static SkillSource Parse(string spec) =>
        spec.StartsWith("github:", StringComparison.OrdinalIgnoreCase) ? new GitHubSkillSource(spec)
        : spec.Equals(AgensiSkillSource.Id, StringComparison.OrdinalIgnoreCase) ? new AgensiSkillSource()
        : new RegistrySkillSource(spec);
}

/// <summary>
/// Agensi (agensi.io), a marketplace of free and paid skills. Its catalog is read anonymously from its MCP server
/// (popular skills, a search, a skill's page); its skills are licensed to run through it, so they are not installed as
/// files: Claude Code gets Agensi's server (signed in once) and loads a skill on demand, buying a paid one only when the
/// user agrees.
/// </summary>
public sealed class AgensiSkillSource() : SkillSource(Id)
{
    public const string Id = "agensi";

    /// <summary>The server Claude Code is given: it signs in there (OAuth) to load and buy skills.</summary>
    public const string ConnectorUrl = "https://mcp.agensi.io/mcp/auth";

    /// <summary>Its catalog, read without signing in.</summary>
    const string CatalogUrl = "https://mcp.agensi.io/mcp";

    const string Reason = "Showing Agensi's skills in VSL's skills marketplace";

    static readonly SemaphoreSlim Gate = new(1, 1);
    static McpClient? _client;

    /// <summary>Calls one of the catalog's tools and returns its text (tests answer here instead of agensi.io).</summary>
    internal static Func<string, IReadOnlyDictionary<string, object?>, CancellationToken, Task<string>> CallTool { get; set; } = CallOverMcpAsync;

    public override string Name => "Agensi";

    public override bool CanSearch => true;

    public override async Task<IReadOnlyList<RemoteSkill>> ListAsync(CancellationToken cancellationToken) =>
        Skills(await CallTool("get_popular", new Dictionary<string, object?> { ["timeframe"] = "all_time", ["limit"] = 50, ["reason"] = Reason }, cancellationToken));

    public override async Task<IReadOnlyList<RemoteSkill>> SearchAsync(string query, CancellationToken cancellationToken) =>
        Skills(await CallTool("search_skills", new Dictionary<string, object?> { ["query"] = query, ["reason"] = Reason }, cancellationToken));

    /// <summary>Its page, without the licensed instructions a free skill's comes with (those are the agent's to load).</summary>
    public override async Task<string> DetailsAsync(RemoteSkill skill, CancellationToken cancellationToken) =>
        PageOf(await CallTool("get_skill", new Dictionary<string, object?> { ["slug"] = skill.Location, ["reason"] = Reason }, cancellationToken));

    public override Task<IReadOnlyList<(string Path, byte[] Content)>> FilesAsync(RemoteSkill skill, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Agensi's skills are not installed as files: Claude Code loads them through Agensi's MCP server.");

    /// <summary>The catalog's skills: [{ slug, name, best_for, price_usd, pricing_type, requires, install_count, rating, url }].</summary>
    internal static IReadOnlyList<RemoteSkill> Skills(string json)
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            parsed = null;
        }
        if (parsed is not JsonArray items)
            throw new InvalidOperationException("Agensi answered: " + (json.Length > 200 ? json[..200] : json));
        var skills = new List<RemoteSkill>();
        foreach (var item in items.OfType<JsonObject>())
        {
            if ((string?)item["slug"] is not { Length: > 0 } slug)
                continue;
            var price = item["price_usd"] is JsonValue p && p.TryGetValue<double>(out var usd) ? usd : 0;
            skills.Add(new RemoteSkill((string?)item["name"] ?? slug, (string?)item["best_for"] ?? "", "Agensi", "")
            {
                Location = slug,
                Url = (string?)item["url"],
                Price = (string?)item["pricing_type"] == "free" || price == 0 ? "free"
                    : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"${price:0.00}") + ((string?)item["pricing_type"] == "subscription" ? " a month" : ""),
                Requires = item["requires"] is JsonArray requires ? [.. requires.Select(r => (string?)r).OfType<string>()] : [],
                Rating = item["rating"] is JsonValue r && r.TryGetValue<double>(out var rating) ? rating : null,
                Installs = item["install_count"] is JsonValue n && n.TryGetValue<int>(out var installs) ? installs : null,
                IsLicensed = true,
            });
        }
        return skills;
    }

    /// <summary>get_skill's text up to the purchase or the licensed instructions.</summary>
    internal static string PageOf(string text)
    {
        foreach (var end in new[] { "\n## Skill execution directive", "\n## Buy " })
            if (text.IndexOf(end, StringComparison.Ordinal) is var at and >= 0)
                text = text[..at];
        return text.TrimEnd().TrimEnd('-').TrimEnd();
    }

    /// <summary>The tools and hosts its page's Permissions say it uses.</summary>
    internal static (IReadOnlyList<string> Tools, IReadOnlyList<string> Hosts) PermissionsOf(string page)
    {
        var at = page.IndexOf("## Permissions", StringComparison.Ordinal);
        var open = at < 0 ? -1 : page.IndexOf('{', at);
        var close = open < 0 ? -1 : page.IndexOf("\n}", open, StringComparison.Ordinal);
        if (close < 0)
            return ([], []);
        try
        {
            var permissions = JsonNode.Parse(page[open..(close + 2)]);
            static IReadOnlyList<string> List(JsonNode? node) => node is JsonArray array ? [.. array.Select(x => (string?)x).OfType<string>()] : [];
            return (List(permissions?["tools"]), List(permissions?["hosts"]));
        }
        catch (JsonException)
        {
            return ([], []);
        }
    }

    /// <summary>The page without its Permissions (shown as what the skill asks for).</summary>
    internal static string WithoutPermissions(string page)
    {
        var at = page.IndexOf("## Permissions", StringComparison.Ordinal);
        var close = at < 0 ? -1 : page.IndexOf("\n}", at, StringComparison.Ordinal);
        return close < 0 ? page : (page[..at] + page[(close + 2)..].TrimStart('\n')).TrimEnd();
    }

    internal static async Task<string> CallOverMcpAsync(string tool, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            _client ??= await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions { Name = "agensi", Endpoint = new Uri(CatalogUrl) }),
                new McpClientOptions { ClientInfo = new ModelContextProtocol.Protocol.Implementation { Name = "vsl", Version = "0.1" } }, cancellationToken: cancellationToken);
            var result = await _client.CallToolAsync(tool, arguments, cancellationToken: cancellationToken);
            var text = VSL.Mcp.McpConnection.TextOf(result.Content);
            return result.IsError == true ? throw new InvalidOperationException(text) : text;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not InvalidOperationException)
        {
            // The connection is not trusted any more: the next call makes another.
            if (_client is { } broken)
                await broken.DisposeAsync();
            _client = null;
            throw new InvalidOperationException(ex.Message, ex);
        }
        finally
        {
            Gate.Release();
        }
    }
}

/// <summary>
/// A team's registry: a <c>registry.json</c> with its name and its skills (name, description, version, category, tags,
/// rating, installs, and the skill's folder relative to the file, with its files when served over HTTP).
/// </summary>
public sealed class RegistrySkillSource(string spec) : SkillSource(spec)
{
    string _name = spec;

    public override string Name => _name;

    bool IsUrl => Spec.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || Spec.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    string Index => IsUrl || Spec.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? Spec : Path.Combine(Spec, "registry.json");

    string Base => IsUrl ? Index[..(Index.LastIndexOf('/') + 1)] : Path.GetDirectoryName(Path.GetFullPath(Index))!;

    public override async Task<IReadOnlyList<RemoteSkill>> ListAsync(CancellationToken cancellationToken)
    {
        var text = IsUrl ? await Http.GetStringAsync(Index, cancellationToken) : await File.ReadAllTextAsync(Index, cancellationToken);
        var root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })!;
        _name = (string?)root["name"] ?? Spec;
        var skills = new List<RemoteSkill>();
        foreach (var s in root["skills"] as JsonArray ?? [])
        {
            if (s is null || (string?)s["name"] is not { Length: > 0 } name)
                continue;
            var path = (string?)s["path"] ?? name;
            skills.Add(new RemoteSkill(name, (string?)s["description"] ?? "", _name, (string?)s["version"] ?? "")
            {
                Category = (string?)s["category"],
                Tags = s["tags"] is JsonArray tags ? [.. tags.Select(t => (string?)t ?? "")] : [],
                Rating = s["rating"]?.GetValue<double>(),
                Installs = s["installs"]?.GetValue<int>(),
                Location = IsUrl ? Base + path.TrimEnd('/') + "/" : Path.Combine(Base, path),
                Files = s["files"] is JsonArray files ? [.. files.Select(f => (string?)f ?? "")] : [],
            });
        }
        return skills;
    }

    public override async Task<IReadOnlyList<(string Path, byte[] Content)>> FilesAsync(RemoteSkill skill, CancellationToken cancellationToken)
    {
        if (!IsUrl)
            return [.. Directory.EnumerateFiles(skill.Location, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
                .Select(f => (Path.GetRelativePath(skill.Location, f).Replace('\\', '/'), File.ReadAllBytes(f)))];
        // Over HTTP there is no folder listing: the registry names the files (SKILL.md when it does not).
        var files = new List<(string, byte[])>();
        foreach (var file in skill.Files.Count > 0 ? skill.Files : ["SKILL.md"])
            files.Add((file, await Http.GetByteArrayAsync(skill.Location + file, cancellationToken)));
        return files;
    }
}

/// <summary>The skills of a GitHub repository: every folder with a SKILL.md (as Anthropic's skills repository).</summary>
public sealed partial class GitHubSkillSource : SkillSource
{
    readonly string _repo;
    readonly string _folder;
    readonly string _ref;

    public GitHubSkillSource(string spec) : base(spec)
    {
        var match = SpecPattern().Match(spec);
        if (!match.Success)
            throw new ArgumentException($"A GitHub source is github:owner/repo[/folder][@ref], not {spec}.");
        _repo = match.Groups["repo"].Value;
        _folder = match.Groups["folder"].Value.Trim('/');
        _ref = match.Groups["ref"].Success ? match.Groups["ref"].Value : "HEAD";
    }

    public override string Name => _repo + (_folder.Length > 0 ? "/" + _folder : "");

    public override async Task<IReadOnlyList<RemoteSkill>> ListAsync(CancellationToken cancellationToken)
    {
        var tree = JsonNode.Parse(await Http.GetStringAsync($"https://api.github.com/repos/{_repo}/git/trees/{_ref}?recursive=1", cancellationToken))!;
        var paths = (tree["tree"] as JsonArray ?? []).Where(e => (string?)e?["type"] == "blob").Select(e => (string)e!["path"]!).ToList();
        var skills = new List<RemoteSkill>();
        foreach (var skillFile in paths.Where(p => p.EndsWith("/SKILL.md", StringComparison.Ordinal) && (_folder.Length == 0 || p.StartsWith(_folder + "/", StringComparison.Ordinal))))
        {
            var folder = skillFile[..^"/SKILL.md".Length];
            var front = Frontmatter.Read(await Http.GetStringAsync(Raw(skillFile), cancellationToken));
            skills.Add(new RemoteSkill(front.GetValueOrDefault("name") ?? Path.GetFileName(folder), front.GetValueOrDefault("description") ?? "", Name,
                front.GetValueOrDefault("version") ?? "")
            {
                Category = folder.Contains('/') ? folder[..folder.LastIndexOf('/')] : null,
                Location = folder,
                Files = [.. paths.Where(p => p.StartsWith(folder + "/", StringComparison.Ordinal)).Select(p => p[(folder.Length + 1)..])],
            });
        }
        return skills;
    }

    string Raw(string path) => $"https://raw.githubusercontent.com/{_repo}/{_ref}/{path}";

    public override async Task<IReadOnlyList<(string Path, byte[] Content)>> FilesAsync(RemoteSkill skill, CancellationToken cancellationToken)
    {
        var files = new List<(string, byte[])>();
        foreach (var file in skill.Files)
            files.Add((file, await Http.GetByteArrayAsync(Raw(skill.Location + "/" + file), cancellationToken)));
        return files;
    }

    [GeneratedRegex(@"^github:(?<repo>[\w.-]+/[\w.-]+)(?<folder>/[^@]*)?(@(?<ref>.+))?$", RegexOptions.IgnoreCase)]
    private static partial Regex SpecPattern();
}

/// <summary>
/// The marketplace: the skills of the sources (GitHub repositories, teams' registries), what each asks for, and
/// installing one in the folder's skills or the user's, updating and removing it.
/// </summary>
public static partial class SkillMarketplace
{
    /// <summary>Beside an installed skill's files: where it came from, its version and its content's hash.</summary>
    public const string Marker = ".vsl-source.json";

    public static IReadOnlyList<SkillSource> Sources(IEnumerable<string> specs, List<string> problems)
    {
        var sources = new List<SkillSource>();
        foreach (var spec in specs.Select(s => s.Trim()).Where(s => s.Length > 0))
            try
            {
                sources.Add(SkillSource.Parse(spec));
            }
            catch (ArgumentException ex)
            {
                problems.Add(ex.Message);
            }
        return sources;
    }

    /// <summary>Every source's skills; a source that cannot be read says why, the others still list.</summary>
    public static async Task<(IReadOnlyList<(SkillSource Source, RemoteSkill Skill)> Skills, IReadOnlyList<string> Problems)> ListAsync(IEnumerable<string> specs, CancellationToken cancellationToken)
    {
        var problems = new List<string>();
        var all = new List<(SkillSource, RemoteSkill)>();
        foreach (var source in Sources(specs, problems))
            try
            {
                all.AddRange((await source.ListAsync(cancellationToken)).Select(s => (source, s)));
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or TaskCanceledException or UnauthorizedAccessException or InvalidOperationException)
            {
                problems.Add($"{source.Spec}: {ex.Message}");
            }
        return (all, problems);
    }

    /// <summary>What it asks for: the tools its frontmatter allows, the scripts it ships (with their text), why it may reach the network.</summary>
    public static SkillReview Review(IReadOnlyList<(string Path, byte[] Content)> files)
    {
        var skillMd = files.FirstOrDefault(f => f.Path == "SKILL.md");
        var front = skillMd.Content is null ? [] : Frontmatter.Read(Encoding.UTF8.GetString(skillMd.Content));
        var tools = (front.GetValueOrDefault("allowed-tools") ?? "").Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.Trim('[', ']', '"', '\'')).Where(t => t.Length > 0).ToList();
        var scripts = files.Where(f => IsScript(f.Path, f.Content)).Select(f => (f.Path, Encoding.UTF8.GetString(f.Content))).ToList();
        var network = new List<string>();
        foreach (var tool in tools.Where(t => t.StartsWith("WebFetch", StringComparison.Ordinal) || t.StartsWith("WebSearch", StringComparison.Ordinal)))
            network.Add($"it allows {tool}");
        foreach (var (path, text) in scripts)
            if (NetworkUse().Match(text) is { Success: true } use)
                network.Add($"{path} uses {use.Value.Trim()}");
        return new SkillReview(tools, scripts, network, files);
    }

    static bool IsScript(string path, byte[] content) =>
        Path.GetExtension(path) is ".sh" or ".bash" or ".py" or ".js" or ".mjs" or ".ts" or ".ps1" or ".rb" or ".pl"
        || content.Length > 2 && content[0] == '#' && content[1] == '!';

    /// <summary>The installed copy of a skill (the folder's first), and the version and hash it was installed at.</summary>
    public static (Skill? Skill, string? Version, string? Hash) Installed(SkillLibrary library, RemoteSkill remote)
    {
        var skill = library.Skills().FirstOrDefault(s => s.IsYours && (s.Name == remote.Name || Path.GetFileName(s.Folder) == remote.FolderName));
        if (skill is null)
            return (null, null, null);
        var marker = Path.Combine(skill.Folder, Marker);
        if (!File.Exists(marker))
            return (skill, null, null);
        try
        {
            var json = JsonNode.Parse(File.ReadAllText(marker));
            return (skill, (string?)json?["version"], (string?)json?["hash"]);
        }
        catch (JsonException)
        {
            return (skill, null, null);
        }
    }

    /// <summary>A newer one than the installed copy: by version, or by content when the source has no versions.</summary>
    public static bool HasUpdate(RemoteSkill remote, string? installedVersion, string? installedHash, string? remoteHash) =>
        remote.Version.Length > 0 ? installedVersion is not null && installedVersion != remote.Version
            : installedHash is not null && remoteHash is not null && installedHash != remoteHash;

    /// <summary>Writes the skill's files into the folder's skills or the user's (its old copy replaced); returns its folder.</summary>
    public static string Install(SkillLibrary library, RemoteSkill remote, SkillReview review, bool forFolder)
    {
        var root = forFolder ? library.ProjectSkills ?? throw new InvalidOperationException("Open a solution to install a skill in its folder.") : library.UserSkills;
        var folder = Path.Combine(root, remote.FolderName);
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
        var inside = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
        foreach (var (path, content) in review.Files)
        {
            // A file that would land outside its folder (../) is not written.
            var target = Path.GetFullPath(Path.Combine(folder, path));
            if (!target.StartsWith(inside, StringComparison.Ordinal))
                continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, content);
            if (!OperatingSystem.IsWindows() && content.Length > 2 && content[0] == '#' && content[1] == '!')
                File.SetUnixFileMode(target, File.GetUnixFileMode(target) | UnixFileMode.UserExecute);
        }
        File.WriteAllText(Path.Combine(folder, Marker), new JsonObject
        {
            ["source"] = remote.Source,
            ["version"] = remote.Version,
            ["hash"] = review.Hash,
            ["installed"] = DateTime.UtcNow.ToString("O"),
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return folder;
    }

    /// <summary>
    /// Words the query shares with the skill's name, description, category and tags; its name counts double. Zero is no
    /// match.
    /// </summary>
    public static int Score(RemoteSkill skill, string query)
    {
        var probe = new Skill(skill.Name, $"{skill.Description} {skill.Category} {string.Join(' ', skill.Tags)}", SkillScope.User, skill.Name, null);
        return SkillLibrary.Try(probe, query).Shared.Count + (skill.Name.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase) ? 2 : 0);
    }

    [GeneratedRegex(@"\b(curl|wget|Invoke-WebRequest|Invoke-RestMethod|requests\.(get|post)|urllib|fetch\(|https?://[^\s'""]+)", RegexOptions.IgnoreCase)]
    private static partial Regex NetworkUse();
}
