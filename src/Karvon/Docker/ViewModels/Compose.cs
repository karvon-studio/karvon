using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Karvon.Docker.ViewModels;

/// <summary>A problem in a compose file, for one of its services.</summary>
public sealed record ComposeProblem(string File, string Service, string Text);

/// <summary>
/// The open solution's compose file, which containers compose started there, and the file's inspections: inside a
/// container, <c>localhost</c> is the container itself, so a service that reaches another through it fails as soon as
/// it runs in compose.
/// </summary>
public static partial class Compose
{
    public static readonly IReadOnlyList<string> FileNames = ["compose.yaml", "compose.yml", "docker-compose.yml", "docker-compose.yaml"];

    static readonly Dictionary<string, (DateTime Written, IReadOnlyList<ComposeProblem> Problems)> Inspected = [];

    public static string? FileIn(string folder) => FileNames.Select(name => Path.Combine(folder, name)).FirstOrDefault(File.Exists);

    /// <summary>Compose ran in the folder (or a folder inside it).</summary>
    public static bool IsInFolder(string? workingDir, string folder)
    {
        if (string.IsNullOrEmpty(workingDir))
            return false;
        var dir = Path.GetFullPath(workingDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return dir.StartsWith(root, StringComparison.Ordinal);
    }

    /// <summary>The problems of a compose file (read again when it changes); none when it cannot be read.</summary>
    public static IReadOnlyList<ComposeProblem> Inspect(string file)
    {
        DateTime written;
        try
        {
            written = File.GetLastWriteTimeUtc(file);
        }
        catch (IOException)
        {
            return [];
        }
        lock (Inspected)
            if (Inspected.TryGetValue(file, out var known) && known.Written == written)
                return known.Problems;
        var problems = Read(file);
        lock (Inspected)
            Inspected[file] = (written, problems);
        return problems;
    }

    static IReadOnlyList<ComposeProblem> Read(string file)
    {
        YamlMappingNode? services;
        try
        {
            var yaml = new YamlStream();
            using (var reader = new StreamReader(file))
                yaml.Load(reader);
            services = yaml.Documents.FirstOrDefault()?.RootNode is YamlMappingNode root
                && root.Children.TryGetValue(new YamlScalarNode("services"), out var node) ? node as YamlMappingNode : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or YamlException)
        {
            return [];
        }
        if (services is null)
            return [];
        var names = services.Children.Keys.OfType<YamlScalarNode>().Select(k => k.Value ?? "").ToList();
        var problems = new List<ComposeProblem>();
        foreach (var (key, value) in services.Children)
        {
            var service = (key as YamlScalarNode)?.Value ?? "";
            var others = names.Where(n => n != service).ToList();
            if (value is not YamlMappingNode body || others.Count == 0)
                continue;
            foreach (var (variable, text) in Environment(body))
            {
                if (!Localhost().IsMatch(text))
                    continue;
                var guess = Guess(text, others, services);
                problems.Add(new ComposeProblem(file, service,
                    $"{service}: {variable} uses localhost, which inside a container is {service} itself. "
                    + (guess is null ? $"Use another service's name ({string.Join(", ", others)})." : $"Use the service name: {guess}.")));
            }
        }
        return problems;
    }

    /// <summary>The service's environment as written: a map, or a list of NAME=value.</summary>
    static IEnumerable<(string Name, string Value)> Environment(YamlMappingNode service)
    {
        if (!service.Children.TryGetValue(new YamlScalarNode("environment"), out var node))
            yield break;
        if (node is YamlMappingNode map)
        {
            foreach (var (k, v) in map.Children)
                yield return ((k as YamlScalarNode)?.Value ?? "", (v as YamlScalarNode)?.Value ?? "");
        }
        else if (node is YamlSequenceNode list)
        {
            foreach (var item in list.Children.OfType<YamlScalarNode>())
                if (item.Value?.IndexOf('=') is > 0 and var eq)
                    yield return (item.Value[..eq], item.Value[(eq + 1)..]);
        }
    }

    /// <summary>The service the value most likely means: one whose image or ports match the port it names.</summary>
    static string? Guess(string value, IReadOnlyList<string> others, YamlMappingNode services)
    {
        var port = Port().Match(value) is { Success: true } m ? m.Groups[1].Value : null;
        foreach (var name in others)
        {
            if (services.Children[new YamlScalarNode(name)] is not YamlMappingNode body)
                continue;
            var image = body.Children.TryGetValue(new YamlScalarNode("image"), out var i) ? (i as YamlScalarNode)?.Value ?? "" : "";
            var known = image switch
            {
                _ when image.Contains("postgres") => "5432",
                _ when image.Contains("mysql") || image.Contains("mariadb") => "3306",
                _ when image.Contains("mssql") => "1433",
                _ when image.Contains("mongo") => "27017",
                _ when image.Contains("redis") => "6379",
                _ when image.Contains("rabbitmq") => "5672",
                _ => null,
            };
            if (port is not null && (known == port || body.ToString().Contains(port, StringComparison.Ordinal)))
                return name;
        }
        return others.Count == 1 ? others[0] : null;
    }

    [GeneratedRegex(@"(?i)(\blocalhost\b|127\.0\.0\.1)")]
    private static partial Regex Localhost();

    [GeneratedRegex(@"(?i)(?:port\s*=\s*|:)(\d{2,5})\b")]
    private static partial Regex Port();
}
