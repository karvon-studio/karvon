using System.Text;
using System.Text.RegularExpressions;

namespace Karvon.Http;

/// <summary>A request of a .http file, as written: its lines (1-based), its method and URL, its headers and body, its variables unresolved.</summary>
public sealed record HttpRequestBlock(int Line, int EndLine, string Method, string Url, IReadOnlyList<(string Name, string Value)> Headers, string? Body)
{
    /// <summary>Its name (# @name login), by which a later request takes its response's values.</summary>
    public string? Name { get; init; }

    /// <summary>The comment above it (### Get the orders), as its title.</summary>
    public string? Comment { get; init; }

    public string Title => Name ?? Comment ?? $"{Method} {Url}";
}

/// <summary>A variable of the file's (@name = value), at its line.</summary>
public sealed record HttpVariable(string Name, string Value, int Line);

/// <summary>
/// A .http file as Visual Studio's HTTP editor reads it: requests between <c>###</c> lines (a method and URL, query lines
/// starting with ? or &amp;, headers to the first empty line, then the body), comments (# or //), <c>@name = value</c>
/// variables, <c># @name</c> names.
/// </summary>
public static partial class HttpFile
{
    static readonly HashSet<string> Methods = new(StringComparer.OrdinalIgnoreCase) { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS", "TRACE", "CONNECT" };

    public static (IReadOnlyList<HttpRequestBlock> Requests, IReadOnlyList<HttpVariable> Variables) Parse(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var requests = new List<HttpRequestBlock>();
        var variables = new List<HttpVariable>();
        string? name = null, comment = null;
        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i].Trim();
            if (line.StartsWith("###", StringComparison.Ordinal))
            {
                comment = line[3..].Trim() is { Length: > 0 } c ? c : null;
                name = null;
                i++;
                continue;
            }
            if (line.Length == 0)
            {
                i++;
                continue;
            }
            if (line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal))
            {
                if (NameComment().Match(line) is { Success: true } named)
                    name = named.Groups[1].Value;
                i++;
                continue;
            }
            if (Variable().Match(line) is { Success: true } variable)
            {
                variables.Add(new HttpVariable(variable.Groups[1].Value, variable.Groups[2].Value.Trim(), i + 1));
                i++;
                continue;
            }
            // A request: its line, its query lines, its headers, its body to the next ###.
            var start = i;
            var (method, url) = RequestLine(line);
            i++;
            var query = new StringBuilder(url);
            while (i < lines.Length && lines[i].TrimStart() is var q && (q.StartsWith('?') || q.StartsWith('&')))
            {
                query.Append(q.Trim());
                i++;
            }
            var headers = new List<(string, string)>();
            while (i < lines.Length && lines[i].Trim().Length > 0 && !lines[i].TrimStart().StartsWith("###", StringComparison.Ordinal))
            {
                var header = lines[i].Trim();
                if (!header.StartsWith('#') && !header.StartsWith("//", StringComparison.Ordinal) && header.IndexOf(':') is > 0 and var colon)
                    headers.Add((header[..colon].Trim(), header[(colon + 1)..].Trim()));
                i++;
            }
            var body = new List<string>();
            if (i < lines.Length && lines[i].Trim().Length == 0)
            {
                i++;
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("###", StringComparison.Ordinal))
                    body.Add(lines[i++]);
            }
            while (body.Count > 0 && body[^1].Trim().Length == 0)
                body.RemoveAt(body.Count - 1);
            requests.Add(new HttpRequestBlock(start + 1, Math.Max(start + 1, i), method, query.ToString(), headers, body.Count > 0 ? string.Join('\n', body) : null)
            {
                Name = name,
                Comment = comment,
            });
            name = comment = null;
        }
        return (requests, variables);
    }

    static (string Method, string Url) RequestLine(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var method = parts.Length > 1 && Methods.Contains(parts[0]) ? parts[0].ToUpperInvariant() : "GET";
        var url = method == "GET" && !Methods.Contains(parts[0]) ? parts[0] : parts.ElementAtOrDefault(1) ?? "";
        return (method, url);
    }

    /// <summary>The request a line (1-based) is in, or the last one above it.</summary>
    public static HttpRequestBlock? At(IReadOnlyList<HttpRequestBlock> requests, int line) =>
        requests.LastOrDefault(r => r.Line <= line);

    [GeneratedRegex(@"^@([A-Za-z_][\w.-]*)\s*=\s*(.*)$")]
    private static partial Regex Variable();

    [GeneratedRegex(@"^(?:#|//)\s*@name\s+(\S+)")]
    private static partial Regex NameComment();
}
