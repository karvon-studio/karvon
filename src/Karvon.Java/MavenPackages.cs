using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Karvon.Packages;

namespace Karvon.Java;

/// <summary>
/// A Java project's packages: Maven Central's artifacts (groupId:artifactId), its pom.xml's dependencies by their scopes,
/// or its build.gradle's (.kts) by their configurations. Neither Maven nor Gradle has a command that adds one, so the
/// file is edited, as it is written (its indentation kept).
/// </summary>
public sealed partial class MavenPackages(string directory) : PackageSource
{
    static readonly HttpClient Http = CreateHttp();

    static HttpClient CreateHttp()
    {
        // search.maven.org refuses a request that does not say who makes it.
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Karvon-Studio (https://github.com/karvon-studio/karvon)");
        return http;
    }

    string Pom => Path.Combine(directory, "pom.xml");

    /// <summary>The project's Gradle build file, when it has no pom.xml.</summary>
    string? Gradle => File.Exists(Pom) ? null : new[] { "build.gradle.kts", "build.gradle" }.Select(f => Path.Combine(directory, f)).FirstOrDefault(File.Exists);

    public override string Name => Gradle is null ? "maven" : "gradle";

    public override string Registry => "Maven Central";

    public override string Directory => directory;

    public override string Project =>
        (Gradle is null && LoadPom() is { } pom ? pom.Root?.Element(pom.Root.Name.Namespace + "artifactId")?.Value : null)
        ?? Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar));

    public override IReadOnlyList<string> Groups => Gradle is null
        ? ["compile", "test", "provided", "runtime"]
        : ["implementation", "testImplementation", "api", "compileOnly", "runtimeOnly"];

    XDocument? LoadPom()
    {
        try
        {
            return File.Exists(Pom) ? XDocument.Load(Pom, LoadOptions.PreserveWhitespace) : null;
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ---- Maven Central ----------------------------------------------------------------------------------------

    static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public override async Task<IReadOnlyList<RegistryPackage>> SearchAsync(string text, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://search.maven.org/solrsearch/select?q={Uri.EscapeDataString(text)}&rows=30&wt=json", ct));
        return [.. doc.RootElement.GetProperty("response").GetProperty("docs").EnumerateArray()
            .Select(d => new RegistryPackage(Str(d, "id")!, Str(d, "latestVersion"), null) { Owners = Str(d, "g") })];
    }

    static (string Group, string Artifact) Split(string id) => id.IndexOf(':') is var colon and > 0 ? (id[..colon], id[(colon + 1)..]) : (id, id);

    static string BaseUrl(string id)
    {
        var (group, artifact) = Split(id);
        return $"https://repo1.maven.org/maven2/{group.Replace('.', '/')}/{artifact}/";
    }

    /// <summary>The repository's maven-metadata.xml of an artifact: its versions and its release; null when there is none.</summary>
    static async Task<XDocument?> MetadataAsync(string id, CancellationToken ct)
    {
        using var response = await Http.GetAsync(BaseUrl(id) + "maven-metadata.xml", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        return XDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    public override async Task<RegistryPackage?> FindAsync(string id, CancellationToken ct)
    {
        if (!id.Contains(':') || await MetadataAsync(id, ct) is not { } metadata)
            return null;
        var release = metadata.Descendants("release").FirstOrDefault()?.Value
            ?? metadata.Descendants("version").Select(v => v.Value).Where(v => !IsPrerelease(v)).DefaultIfEmpty().Max(Comparer<string?>.Create((a, b) => CompareVersions(a ?? "", b ?? "")));
        if (release is null)
            return new RegistryPackage(id, null, null);
        // The release's pom: what it says it is, its license and URL from its parents when it has them from there.
        string? description = null, url = null, license = null;
        var (pomId, pomVersion) = (id, release);
        try
        {
            for (var depth = 0; depth < 4 && (description is null || url is null || license is null); depth++)
            {
                var artifact = Split(pomId).Artifact;
                var pom = XDocument.Parse(await Http.GetStringAsync($"{BaseUrl(pomId)}{pomVersion}/{artifact}-{pomVersion}.pom", ct));
                var ns = pom.Root!.Name.Namespace;
                if (depth == 0)
                    description = pom.Root.Element(ns + "description")?.Value.Trim() is { Length: > 0 } d ? d : pom.Root.Element(ns + "name")?.Value.Trim();
                url ??= pom.Root.Element(ns + "url")?.Value.Trim();
                license ??= pom.Root.Element(ns + "licenses")?.Elements(ns + "license").FirstOrDefault()?.Element(ns + "name")?.Value.Trim();
                if (pom.Root.Element(ns + "parent") is not { } parent || parent.Element(ns + "version")?.Value is not { } version)
                    break;
                (pomId, pomVersion) = ($"{parent.Element(ns + "groupId")?.Value}:{parent.Element(ns + "artifactId")?.Value}", version);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Xml.XmlException)
        {
        }
        return new RegistryPackage(id, release, description) { Owners = Split(id).Group, ProjectUrl = url, License = license };
    }

    public override async Task<IReadOnlyList<string>> VersionsAsync(string id, bool prerelease, CancellationToken ct) =>
        await MetadataAsync(id, ct) is { } metadata
            ? [.. metadata.Descendants("version").Select(v => v.Value).Where(v => prerelease || !IsPrerelease(v))
                .OrderByDescending(v => v, Comparer<string>.Create(CompareVersions))]
            : [];

    public override int Compare(string a, string b) => CompareVersions(a, b);

    [GeneratedRegex(@"(?:^|[.-])(?:alpha|a\d|beta|b\d|milestone|m\d|rc|cr|snapshot|preview|ea)", RegexOptions.IgnoreCase)]
    private static partial Regex PrereleaseQualifier();

    /// <summary>Maven's: alpha, beta, milestone, rc and snapshot versions come before their release (-jre is no such one).</summary>
    public static bool IsPrerelease(string version) => PrereleaseQualifier().IsMatch(version);

    [GeneratedRegex(@"\d+|[a-zA-Z]+")]
    private static partial Regex Token();

    /// <summary>Maven's order of versions, as its ComparableVersion has it, near enough: numbers as numbers, then the qualifiers.</summary>
    public static int CompareVersions(string a, string b)
    {
        static int Rank(string qualifier) => qualifier.ToLowerInvariant() switch
        {
            "alpha" or "a" => -5,
            "beta" or "b" => -4,
            "milestone" or "m" => -3,
            "rc" or "cr" => -2,
            "snapshot" => -1,
            "" or "ga" or "final" or "release" => 0,
            "sp" => 1,
            _ => 2,
        };
        var ta = Token().Matches(a).Select(m => m.Value).ToList();
        var tb = Token().Matches(b).Select(m => m.Value).ToList();
        for (var i = 0; i < Math.Max(ta.Count, tb.Count); i++)
        {
            var x = i < ta.Count ? ta[i] : "";
            var y = i < tb.Count ? tb[i] : "";
            var c = (long.TryParse(x, out var nx), long.TryParse(y, out var ny)) switch
            {
                (true, true) => nx.CompareTo(ny),
                // A number after the release's numbers is newer than a qualifier (1.0.1 > 1.0-rc), nothing is a zero.
                (true, false) => y.Length == 0 ? (nx == 0 ? 0 : 1) : 1,
                (false, true) => x.Length == 0 ? (ny == 0 ? 0 : -1) : -1,
                _ => Rank(x) != Rank(y) || Rank(x) != 2 ? Rank(x).CompareTo(Rank(y)) : string.Compare(x, y, StringComparison.OrdinalIgnoreCase),
            };
            if (c != 0)
                return c;
        }
        return 0;
    }

    // ---- The project's dependencies ---------------------------------------------------------------------------

    [GeneratedRegex(@"^(?<indent>\s*)(?<conf>implementation|api|compileOnly|runtimeOnly|testImplementation|testRuntimeOnly|testCompileOnly|annotationProcessor)\s*\(?\s*[""'](?<group>[^:""']+):(?<artifact>[^:""']+)(?::(?<version>[^""']+))?[""']\s*\)?\s*$")]
    private static partial Regex GradleDependency();

    public override IReadOnlyList<DependencyPackage> Dependencies()
    {
        if (Gradle is { } gradle)
            return [.. File.ReadAllLines(gradle).Select(l => GradleDependency().Match(l)).Where(m => m.Success)
                .Select(m => new DependencyPackage($"{m.Groups["group"].Value}:{m.Groups["artifact"].Value}", NullIfEmpty(m.Groups["version"].Value),
                    NullIfEmpty(m.Groups["version"].Value), m.Groups["conf"].Value))];
        if (LoadPom() is not { Root: { } project })
            return [];
        var ns = project.Name.Namespace;
        var properties = project.Element(ns + "properties")?.Elements().ToDictionary(e => e.Name.LocalName, e => e.Value.Trim()) ?? [];
        string? Resolve(string? value) =>
            value is not null && value.StartsWith("${", StringComparison.Ordinal) && value.EndsWith('}') && properties.TryGetValue(value[2..^1], out var v) ? v : value;
        return [.. (project.Element(ns + "dependencies")?.Elements(ns + "dependency") ?? []).Select(d =>
        {
            var version = Resolve(d.Element(ns + "version")?.Value.Trim());
            return new DependencyPackage($"{d.Element(ns + "groupId")?.Value.Trim()}:{d.Element(ns + "artifactId")?.Value.Trim()}", version, version,
                d.Element(ns + "scope")?.Value.Trim() ?? "compile");
        })];
    }

    static string? NullIfEmpty(string value) => value.Length > 0 ? value : null;

    public override PackageChange Add(string id, string version, string? group)
    {
        var file = Gradle ?? Pom;
        return new($"{Path.GetFileName(file)}: {id} {version}")
        {
            Edit = ct => Task.Run(() => Gradle is { } gradle ? EditGradle(gradle, id, version, group ?? "implementation") : EditPom(id, version, group ?? "compile"), ct),
        };
    }

    public override PackageChange Remove(DependencyPackage package)
    {
        var file = Gradle ?? Pom;
        return new($"{Path.GetFileName(file)}: {package.Id} removed")
        {
            Edit = ct => Task.Run(() => Gradle is { } gradle ? EditGradle(gradle, package.Id, null, package.Group) : EditPom(package.Id, null, package.Group), ct),
        };
    }

    /// <summary>The whitespace before an element on its line.</summary>
    static string IndentOf(XElement e) => e.PreviousNode is XText t && t.Value.LastIndexOf('\n') is var nl and >= 0 ? t.Value[(nl + 1)..] : "";

    /// <summary>A child at the end of an element, on a line of its own one indent in, as the file indents.</summary>
    static void Append(XElement parent, XElement child, string unit)
    {
        var indent = IndentOf(parent) + unit;
        if (parent.LastNode is XText closing && string.IsNullOrWhiteSpace(closing.Value))
            closing.AddBeforeSelf(new XText("\n" + indent), child);
        else
            parent.Add(new XText("\n" + indent), child, new XText("\n" + IndentOf(parent)));
    }

    /// <summary>pom.xml with a dependency added, moved to a version, or (no version) removed: what was done.</summary>
    string EditPom(string id, string? version, string scope)
    {
        var doc = LoadPom() ?? throw new InvalidOperationException("pom.xml could not be read.");
        var project = doc.Root!;
        var ns = project.Name.Namespace;
        var (groupId, artifactId) = Split(id);
        var unit = project.Elements().FirstOrDefault() is { } first && IndentOf(first) is { Length: > 0 } u ? u : "    ";
        var dependencies = project.Element(ns + "dependencies");
        var existing = dependencies?.Elements(ns + "dependency").FirstOrDefault(d =>
            d.Element(ns + "groupId")?.Value.Trim() == groupId && d.Element(ns + "artifactId")?.Value.Trim() == artifactId);
        string done;
        if (version is null)
        {
            if (existing is null)
                return $"{id} was not in pom.xml.";
            if (existing.PreviousNode is XText space && string.IsNullOrWhiteSpace(space.Value))
                space.Remove();
            existing.Remove();
            done = $"{id} removed from pom.xml";
        }
        else if (existing is not null)
        {
            if (existing.Element(ns + "version") is { } v)
                v.Value = version;
            else
                Append(existing, new XElement(ns + "version", version), unit);
            done = $"{id} is {version} in pom.xml";
        }
        else
        {
            if (dependencies is null)
            {
                dependencies = new XElement(ns + "dependencies");
                Append(project, dependencies, unit);
                dependencies.Add(new XText("\n" + IndentOf(dependencies)));
            }
            var dependency = new XElement(ns + "dependency");
            Append(dependencies, dependency, unit);
            dependency.Add(new XText("\n" + IndentOf(dependency)));
            Append(dependency, new XElement(ns + "groupId", groupId), unit);
            Append(dependency, new XElement(ns + "artifactId", artifactId), unit);
            Append(dependency, new XElement(ns + "version", version), unit);
            if (scope != "compile")
                Append(dependency, new XElement(ns + "scope", scope), unit);
            done = $"{id} {version} added to pom.xml" + (scope != "compile" ? $" ({scope})" : "");
        }
        // As it was written: its declaration, and what is around its root (a comment, the lines between) as it was, its
        // lines' ends too (XML reads them as \n).
        var text = (doc.Declaration?.ToString() ?? "") + string.Concat(doc.Nodes().Select(n => n.ToString(SaveOptions.DisableFormatting)));
        text = text.EndsWith('\n') ? text : text + "\n";
        // (XNode.ToString writes the platform's line ends: \r\n on Windows.)
        text = text.Replace("\r\n", "\n");
        File.WriteAllText(Pom, File.ReadAllText(Pom).Contains("\r\n") ? text.Replace("\n", "\r\n") : text);
        return done;
    }

    /// <summary>build.gradle with a dependency added, moved to a version, or (no version) removed: what was done.</summary>
    static string EditGradle(string file, string id, string? version, string configuration)
    {
        var lines = File.ReadAllLines(file).ToList();
        var (groupId, artifactId) = Split(id);
        var index = lines.FindIndex(l => GradleDependency().Match(l) is { Success: true } m && m.Groups["group"].Value == groupId && m.Groups["artifact"].Value == artifactId);
        var kotlin = file.EndsWith(".kts", StringComparison.Ordinal);
        string Line(string indent) => kotlin ? $"{indent}{configuration}(\"{id}:{version}\")" : $"{indent}{configuration} '{id}:{version}'";
        string done;
        if (version is null)
        {
            if (index < 0)
                return $"{id} was not in {Path.GetFileName(file)}.";
            lines.RemoveAt(index);
            done = $"{id} removed from {Path.GetFileName(file)}";
        }
        else if (index >= 0)
        {
            var m = GradleDependency().Match(lines[index]);
            configuration = m.Groups["conf"].Value;
            lines[index] = Line(m.Groups["indent"].Value);
            done = $"{id} is {version} in {Path.GetFileName(file)}";
        }
        else
        {
            // At the end of the dependencies { } block (one made at the file's end when there is none).
            var start = lines.FindIndex(l => l.TrimStart().StartsWith("dependencies", StringComparison.Ordinal) && l.Contains('{'));
            if (start < 0)
            {
                lines.AddRange(["", "dependencies {", "}"]);
                start = lines.Count - 2;
            }
            var depth = 0;
            var end = start;
            for (var i = start; i < lines.Count; i++)
            {
                depth += lines[i].Count(c => c == '{') - lines[i].Count(c => c == '}');
                if (depth <= 0)
                {
                    end = i;
                    break;
                }
            }
            var indent = Enumerable.Range(start + 1, Math.Max(0, end - start - 1)).Select(i => lines[i]).FirstOrDefault(l => l.Trim().Length > 0) is { } child
                ? child[..(child.Length - child.TrimStart().Length)]
                : lines[start][..(lines[start].Length - lines[start].TrimStart().Length)] + "    ";
            lines.Insert(end, Line(indent));
            done = $"{id} {version} added to {Path.GetFileName(file)} ({configuration})";
        }
        File.WriteAllText(file, string.Join("\n", lines) + "\n");
        return done;
    }
}
