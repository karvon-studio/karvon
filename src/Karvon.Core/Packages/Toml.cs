using System.Text;

namespace Karvon.Packages;

/// <summary>
/// The few TOML forms a manifest's dependencies are written in (Cargo.toml, Cargo.lock, pyproject.toml), read without
/// a TOML library: its tables in order ([section], each [[array]] one a table of its own), their keys and values as
/// written (an array over several lines in one).
/// </summary>
public static class Toml
{
    public sealed record Table(string Name, IReadOnlyList<(string Key, string Value)> Entries)
    {
        public string? this[string key] => Entries.FirstOrDefault(e => e.Key == key).Value;
    }

    public static IReadOnlyList<Table> Read(string text)
    {
        List<Table> tables = [];
        var name = "";
        var entries = new List<(string, string)>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = WithoutComment(lines[i]).Trim();
            if (line.Length == 0)
                continue;
            if (line.StartsWith('['))
            {
                tables.Add(new(name, entries));
                name = line.Trim('[', ']', ' ');
                entries = [];
                continue;
            }
            var equals = line.IndexOf('=');
            if (equals <= 0)
                continue;
            var key = Unquote(line[..equals].Trim());
            var value = line[(equals + 1)..].Trim();
            // An array (or a table) over several lines: to its last bracket.
            while (Depth(value) > 0 && i + 1 < lines.Length)
                value += " " + WithoutComment(lines[++i]).Trim();
            entries.Add((key, value));
        }
        tables.Add(new(name, entries));
        return tables;
    }

    /// <summary>A string's text ("1.0", '1.0'), or the value as it is.</summary>
    public static string Unquote(string value) =>
        value.Length >= 2 && (value[0] == '"' && value[^1] == '"' || value[0] == '\'' && value[^1] == '\'') ? value[1..^1] : value;

    /// <summary>An array's strings: ["requests>=2", "rich"].</summary>
    public static IReadOnlyList<string> Strings(string value)
    {
        List<string> found = [];
        var current = new StringBuilder();
        char? quote = null;
        foreach (var c in value)
        {
            if (quote is null)
            {
                if (c is '"' or '\'')
                    (quote, current) = (c, new StringBuilder());
                continue;
            }
            if (c == quote)
            {
                found.Add(current.ToString());
                quote = null;
            }
            else
                current.Append(c);
        }
        return found;
    }

    /// <summary>A key of an inline table: version in { version = "1.0", features = ["derive"] }.</summary>
    public static string? InlineValue(string table, string key)
    {
        if (!table.StartsWith('{'))
            return null;
        foreach (var part in Split(table.Trim('{', '}')))
        {
            var equals = part.IndexOf('=');
            if (equals > 0 && Unquote(part[..equals].Trim()) == key)
                return Unquote(part[(equals + 1)..].Trim());
        }
        return null;
    }

    /// <summary>An inline table's entries, split at the commas outside its arrays and strings.</summary>
    static IEnumerable<string> Split(string text)
    {
        var depth = 0;
        char? quote = null;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote is not null)
            {
                if (c == quote)
                    quote = null;
                continue;
            }
            switch (c)
            {
                case '"' or '\'':
                    quote = c;
                    break;
                case '[' or '{':
                    depth++;
                    break;
                case ']' or '}':
                    depth--;
                    break;
                case ',' when depth == 0:
                    yield return text[start..i];
                    start = i + 1;
                    break;
            }
        }
        yield return text[start..];
    }

    static int Depth(string value)
    {
        var depth = 0;
        char? quote = null;
        foreach (var c in value)
        {
            if (quote is not null)
            {
                if (c == quote)
                    quote = null;
                continue;
            }
            if (c is '"' or '\'')
                quote = c;
            else if (c is '[' or '{')
                depth++;
            else if (c is ']' or '}')
                depth--;
        }
        return depth;
    }

    static string WithoutComment(string line)
    {
        char? quote = null;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quote is not null)
            {
                if (c == quote)
                    quote = null;
            }
            else if (c is '"' or '\'')
                quote = c;
            else if (c == '#')
                return line[..i];
        }
        return line;
    }
}
