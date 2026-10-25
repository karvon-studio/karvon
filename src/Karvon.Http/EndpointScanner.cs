using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Karvon.Http;

/// <summary>An endpoint of a project's: its method and route, where it is written, and which project it is of.</summary>
public sealed record HttpEndpoint(string Method, string Route, string File, int Line, string Project)
{
    /// <summary>The route with its parameters' constraints left out ({id:int} as {id}).</summary>
    public string Path => Regex.Replace(Route, @"\{([^}:?=]+)[^}]*\}", "{$1}");
}

/// <summary>
/// The ASP.NET Core endpoints of a folder's projects, read from their sources as Visual Studio's Endpoints Explorer lists
/// them: controllers' actions ([Route] on the class, [HttpGet("{id}")] on the action, [controller] and [action] put in),
/// minimal APIs' MapGet, MapPost... (MapGroup's prefixes before them), Razor pages' and Blazor components' @page.
/// </summary>
public static partial class EndpointScanner
{
    static readonly string[] Skipped = ["bin", "obj", "node_modules", ".git", ".vs", "wwwroot"];

    public static IReadOnlyList<HttpEndpoint> Scan(string root)
    {
        var endpoints = new List<HttpEndpoint>();
        if (!Directory.Exists(root))
            return endpoints;
        foreach (var file in Files(root))
        {
            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch (IOException)
            {
                continue;
            }
            var project = ProjectOf(file, root);
            if (file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                endpoints.AddRange(InCode(StripComments(text), file, project));
            else if (PageDirective().Match(text) is { Success: true } page)
                endpoints.Add(new HttpEndpoint("GET", page.Groups[1].Value, file, LineOf(text, page.Index), project));
        }
        return [.. endpoints.OrderBy(e => e.Project).ThenBy(e => e.Path, StringComparer.Ordinal).ThenBy(e => e.Method)];
    }

    static IEnumerable<string> Files(string root)
    {
        var pending = new Stack<string>([root]);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            IEnumerable<string> files, dirs;
            try
            {
                files = Directory.EnumerateFiles(dir);
                dirs = Directory.EnumerateDirectories(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var f in files.Where(f => Path.GetExtension(f).ToLowerInvariant() is ".cs" or ".razor" or ".cshtml"))
                yield return f;
            foreach (var d in dirs.Where(d => !Skipped.Contains(Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)))
                pending.Push(d);
        }
    }

    /// <summary>The folder of the nearest .csproj above the file (its project).</summary>
    public static string ProjectOf(string file, string root)
    {
        for (var dir = Path.GetDirectoryName(file); dir is not null && dir.Length >= root.TrimEnd('/', '\\').Length; dir = Path.GetDirectoryName(dir))
            if (Directory.EnumerateFiles(dir, "*.csproj").FirstOrDefault() is { } csproj)
                return csproj;
        return root;
    }

    internal static IEnumerable<HttpEndpoint> InCode(string code, string file, string project)
    {
        // Controllers: each class's routes, and its actions' between it and the next class.
        var classes = Class().Matches(code).Cast<Match>().ToList();
        for (var c = 0; c < classes.Count; c++)
        {
            var cls = classes[c];
            var name = cls.Groups["name"].Value;
            var attributes = cls.Groups["attrs"].Value;
            var bases = cls.Groups["bases"].Value;
            var isController = name.EndsWith("Controller", StringComparison.Ordinal) && !cls.Groups["abstract"].Success
                || attributes.Contains("ApiController", StringComparison.Ordinal) || bases.Contains("Controller", StringComparison.Ordinal);
            if (!isController)
                continue;
            var prefix = RouteAttribute().Match(attributes) is { Success: true } r ? r.Groups[1].Value : "";
            var controller = name.EndsWith("Controller", StringComparison.Ordinal) ? name[..^"Controller".Length] : name;
            var end = c + 1 < classes.Count ? classes[c + 1].Index : code.Length;
            foreach (Match action in Action().Matches(code[cls.Index..end]))
            {
                var attrs = action.Groups["attrs"].Value;
                var verbs = HttpAttribute().Matches(attrs).Cast<Match>().ToList();
                if (verbs.Count == 0)
                    continue;
                var actionRoute = RouteAttribute().Match(attrs) is { Success: true } ar ? ar.Groups[1].Value : null;
                foreach (var verb in verbs)
                {
                    var template = verb.Groups["template"].Success ? verb.Groups["template"].Value : actionRoute ?? "";
                    var route = template.StartsWith('/') || template.StartsWith("~/", StringComparison.Ordinal) ? template.TrimStart('~')
                        : "/" + string.Join('/', new[] { prefix.Trim('/'), template.Trim('/') }.Where(s => s.Length > 0));
                    route = route.Replace("[controller]", controller, StringComparison.OrdinalIgnoreCase)
                        .Replace("[action]", action.Groups["method"].Value, StringComparison.OrdinalIgnoreCase);
                    yield return new HttpEndpoint(verb.Groups["verb"].Value.ToUpperInvariant(), route, file, LineOf(code, cls.Index + action.Groups["method"].Index), project);
                }
            }
        }

        // Minimal APIs: a group's prefix (var api = app.MapGroup("/api")), then its maps (api.MapGet("/x", ...)).
        var groups = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match g in Group().Matches(code))
            groups[g.Groups["var"].Value] = Join(groups.GetValueOrDefault(g.Groups["parent"].Value) ?? "", g.Groups["route"].Value);
        foreach (Match map in Map().Matches(code))
        {
            var prefix = string.Concat(GroupChain().Matches(map.Groups["chain"].Value).Select(m => m.Groups[1].Value));
            var owner = groups.GetValueOrDefault(map.Groups["target"].Value) ?? "";
            yield return new HttpEndpoint(map.Groups["verb"].Value.ToUpperInvariant(), Join(Join(owner, prefix), map.Groups["route"].Value), file, LineOf(code, map.Groups["verb"].Index), project);
        }
    }

    static string Join(string prefix, string route) =>
        "/" + string.Join('/', new[] { prefix.Trim('/'), route.Trim('/') }.Where(s => s.Length > 0));

    static int LineOf(string text, int index) => text.AsSpan(0, Math.Min(index, text.Length)).Count('\n') + 1;

    /// <summary>The code without its comments, each replaced by as many line breaks, so lines keep their numbers.</summary>
    internal static string StripComments(string code) => Comment().Replace(code, m =>
        m.Value.StartsWith('"') || m.Value.StartsWith('@') || m.Value.StartsWith('\'') ? m.Value : new string('\n', m.Value.Count(c => c == '\n')));

    /// <summary>The host address a project's launchSettings.json gives (its first http URL, else its https one).</summary>
    public static string? HostAddress(string project)
    {
        var settings = Path.Combine(Path.GetDirectoryName(project) ?? "", "Properties", "launchSettings.json");
        try
        {
            if (JsonNode.Parse(File.ReadAllText(settings), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })?["profiles"] is not JsonObject profiles)
                return null;
            var urls = profiles.Select(p => p.Value?["applicationUrl"]?.GetValue<string>()).OfType<string>()
                .SelectMany(u => u.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList();
            return urls.FirstOrDefault(u => u.StartsWith("http://", StringComparison.Ordinal)) ?? urls.FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"(?<attrs>(?:\[(?:""[^""]*""|[^\]""])*\]\s*)*)(?:(?:public|internal|sealed|partial|static|(?<abstract>abstract))\s+)*class\s+(?<name>\w+)(?:<[^>]*>)?(?:\s*\([^)]*\))?\s*(?::\s*(?<bases>[^{]+))?\{")]
    private static partial Regex Class();

    [GeneratedRegex(@"(?<attrs>(?:\[(?:""[^""]*""|[^\]""])*\]\s*)+)(?:(?:public|internal|protected|virtual|override|async|static|new)\s+)+[\w<>\[\]?,.\s]+?\s(?<method>\w+)\s*(?:<[^>]*>)?\s*\(")]
    private static partial Regex Action();

    [GeneratedRegex(@"Http(?<verb>Get|Post|Put|Patch|Delete|Head|Options)(?:Attribute)?\s*(?:\(\s*(?:template\s*:\s*)?""(?<template>[^""]*)"")?")]
    private static partial Regex HttpAttribute();

    [GeneratedRegex(@"\bRoute(?:Attribute)?\s*\(\s*(?:template\s*:\s*)?""([^""]*)""")]
    private static partial Regex RouteAttribute();

    [GeneratedRegex(@"(?<var>\w+)\s*=\s*(?<parent>\w+)\s*\.\s*MapGroup\s*\(\s*""(?<route>[^""]*)""\s*\)")]
    private static partial Regex Group();

    [GeneratedRegex(@"(?<target>\w+)(?<chain>(?:\s*\.\s*MapGroup\s*\(\s*""[^""]*""\s*\))*)\s*\.\s*Map(?<verb>Get|Post|Put|Patch|Delete)\s*\(\s*""(?<route>[^""]*)""")]
    private static partial Regex Map();

    [GeneratedRegex(@"MapGroup\s*\(\s*""([^""]*)""")]
    private static partial Regex GroupChain();

    [GeneratedRegex(@"^\s*@page\s+""([^""]+)""", RegexOptions.Multiline)]
    private static partial Regex PageDirective();

    [GeneratedRegex(@"@""(?:[^""]|"""")*""|""(?:\\.|[^""\\\n])*""|'(?:\\.|[^'\\\n])*'|//[^\n]*|/\*[\s\S]*?\*/")]
    private static partial Regex Comment();
}
