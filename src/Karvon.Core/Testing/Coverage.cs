using System.Globalization;
using System.Xml.Linq;

namespace Karvon.Testing;

/// <summary>A line's coverage: how many times it ran, and whether only some of its branches did.</summary>
public readonly record struct LineCoverage(int Hits, bool Partial)
{
    public bool IsCovered => Hits > 0;
}

/// <summary>A file's coverage: its lines' (1-based) that the report counts, how many ran.</summary>
public sealed record FileCoverage(string Path, IReadOnlyDictionary<int, LineCoverage> Lines)
{
    public int Total => Lines.Count;

    public int Covered => Lines.Values.Count(l => l.IsCovered);

    public double Percent => Total == 0 ? 100 : 100.0 * Covered / Total;
}

/// <summary>
/// Code coverage of the workspace's files, as a test run measured it: Cobertura's XML (coverlet's, pytest-cov's) or LCOV
/// (Vitest's and Jest's, cargo llvm-cov's), merged when there are several (a line covered by either is covered).
/// </summary>
public sealed class CoverageReport
{
    readonly Dictionary<string, Dictionary<int, LineCoverage>> _files = new(StringComparer.Ordinal);

    public IReadOnlyList<FileCoverage> Files =>
        [.. _files.Select(f => new FileCoverage(f.Key, f.Value)).OrderBy(f => f.Path, StringComparer.Ordinal)];

    public FileCoverage? For(string path) => _files.TryGetValue(System.IO.Path.GetFullPath(path), out var lines) ? new FileCoverage(path, lines) : null;

    public int Total => _files.Values.Sum(f => f.Count);

    public int Covered => _files.Values.Sum(f => f.Values.Count(l => l.IsCovered));

    public double Percent => Total == 0 ? 100 : 100.0 * Covered / Total;

    void Add(string path, int line, int hits, bool partial)
    {
        if (!_files.TryGetValue(path, out var lines))
            _files[path] = lines = [];
        // Two runs of a line: its hits summed, partial when either says so and it ran.
        lines[line] = lines.TryGetValue(line, out var was)
            ? new LineCoverage(was.Hits + hits, (was.Partial || partial) && was.Hits + hits > 0)
            : new LineCoverage(hits, partial);
    }

    /// <summary>Another report's lines added in (their hits summed).</summary>
    public void Merge(CoverageReport other)
    {
        foreach (var (path, lines) in other._files)
            foreach (var (line, coverage) in lines)
                Add(path, line, coverage.Hits, coverage.Partial);
    }

    /// <summary>A report file read, by its kind (Cobertura's XML, else LCOV); its paths made full against its folder (and the sources it names).</summary>
    public static CoverageReport Load(string file, string? root = null)
    {
        var text = File.ReadAllText(file);
        var folder = root ?? System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(file)) ?? "";
        return text.TrimStart().StartsWith('<') ? Cobertura(text, folder) : Lcov(text, folder);
    }

    public static CoverageReport Cobertura(string xml, string folder)
    {
        var report = new CoverageReport();
        var document = XDocument.Parse(xml);
        var sources = document.Descendants("source").Select(s => s.Value.Trim()).Where(s => s.Length > 0).ToList();
        foreach (var cls in document.Descendants("class"))
        {
            var name = (string?)cls.Attribute("filename") ?? "";
            if (name.Length == 0)
                continue;
            var path = Resolve(name, sources, folder);
            foreach (var line in cls.Element("lines")?.Elements("line") ?? [])
            {
                if (!int.TryParse((string?)line.Attribute("number"), out var number))
                    continue;
                var hits = long.TryParse((string?)line.Attribute("hits"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var h) ? (int)Math.Min(h, int.MaxValue) : 0;
                // condition-coverage="50% (1/2)": some of its branches only.
                var partial = (string?)line.Attribute("branch") == "true" && (string?)line.Attribute("condition-coverage") is { } condition && !condition.StartsWith("100%", StringComparison.Ordinal);
                report.Add(path, number, hits, partial && hits > 0);
            }
        }
        return report;
    }

    public static CoverageReport Lcov(string text, string folder)
    {
        var report = new CoverageReport();
        string? file = null;
        var branches = new Dictionary<int, (int Taken, int Total)>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("SF:", StringComparison.Ordinal))
            {
                file = Resolve(line[3..], [], folder);
                branches.Clear();
            }
            else if (line.StartsWith("BRDA:", StringComparison.Ordinal) && line[5..].Split(',') is [var l, _, _, var taken] && int.TryParse(l, out var bl))
            {
                var (t, n) = branches.GetValueOrDefault(bl);
                branches[bl] = (t + (taken is "-" or "0" ? 0 : 1), n + 1);
            }
            else if (line.StartsWith("DA:", StringComparison.Ordinal) && file is not null && line[3..].Split(',') is [var number, var hits, ..]
                     && int.TryParse(number, out var n2) && long.TryParse(hits, out var h))
            {
                var partial = branches.TryGetValue(n2, out var b) && b.Taken > 0 && b.Taken < b.Total;
                report.Add(file, n2, (int)Math.Min(h, int.MaxValue), partial);
            }
            else if (line == "end_of_record" && file is not null)
            {
                // Branches listed after the lines: a covered line some of whose branches did not run is partial.
                foreach (var (n3, (ran, total)) in branches)
                    if (ran > 0 && ran < total && report._files.TryGetValue(file, out var lines) && lines.TryGetValue(n3, out var was) && was.IsCovered)
                        lines[n3] = was with { Partial = true };
                file = null;
            }
        }
        return report;
    }

    static string Resolve(string name, IReadOnlyList<string> sources, string folder)
    {
        if (System.IO.Path.IsPathRooted(name))
            return System.IO.Path.GetFullPath(name);
        foreach (var source in sources)
        {
            var candidate = System.IO.Path.Combine(source, name);
            if (File.Exists(candidate))
                return System.IO.Path.GetFullPath(candidate);
        }
        return System.IO.Path.GetFullPath(System.IO.Path.Combine(sources.Count > 0 ? sources[0] : folder, name));
    }
}
