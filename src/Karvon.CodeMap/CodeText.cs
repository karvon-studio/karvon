using System.Text;
using System.Text.RegularExpressions;

namespace Karvon.CodeMap;

/// <summary>What the map reads from code's text: a name's words, a declaration's doc comment, a file's language.</summary>
public static partial class CodeText
{
    /// <summary>
    /// A name's words in lower case, as a search finds them: OrderTotals is "order totals", HTTPClient "http client",
    /// get_user_id "get user id"; the whole name too, so its start finds it ("orderto"). Letters of any script.
    /// </summary>
    public static string Words(string name)
    {
        var words = new List<string>();
        foreach (Match part in Parts().Matches(name))
            words.Add(part.Value.ToLowerInvariant());
        var whole = new string([.. name.Where(char.IsLetterOrDigit)]).ToLowerInvariant();
        if (words.Count > 1 && whole.Length > 0)
            words.Add(whole);
        return string.Join(' ', words);
    }

    /// <summary>The parts of a name: an acronym, a capitalised word, a lower-case word, a word of a script without cases, digits.</summary>
    [GeneratedRegex(@"(?:[\p{Lu}\p{Lt}]\p{M}*)+(?!\p{Ll})|(?:[\p{Lu}\p{Lt}]\p{M}*)?(?:\p{Ll}\p{M}*)+|(?:[\p{Lo}\p{Lm}]\p{M}*)+|\d+")]
    private static partial Regex Parts();

    /// <summary>
    /// The first sentence of a declaration's doc comment: the comment lines right above it (///, //, #, /** */, --, past
    /// its attributes), or those it starts with, or a Python docstring after it; XML tags out. Null when it has none.
    /// </summary>
    public static string? Doc(IReadOnlyList<string> lines, int start, int end)
    {
        if (start < 0 || start >= lines.Count)
            return null;
        var comment = new List<string>();
        // A range that starts with its comment (some servers' do).
        for (var i = start; i <= end && i < lines.Count && IsComment(lines[i]); i++)
            comment.Add(lines[i]);
        if (comment.Count == 0)
        {
            var i = start - 1;
            while (i >= 0 && IsAttribute(lines[i]))
                i--;
            for (; i >= 0 && IsComment(lines[i]); i--)
                comment.Insert(0, lines[i]);
        }
        if (comment.Count == 0 && Docstring(lines, start, end) is { } docstring)
            return docstring;
        return comment.Count == 0 ? null : FirstSentence(string.Join(' ', comment.Select(Uncomment)));
    }

    static bool IsComment(string line)
    {
        var text = line.TrimStart();
        return text.StartsWith("//", StringComparison.Ordinal) || text.StartsWith("/*", StringComparison.Ordinal)
            || text.StartsWith('*') || text.StartsWith("--", StringComparison.Ordinal)
            || (text.StartsWith('#') && !text.StartsWith("#[", StringComparison.Ordinal) && !text.StartsWith("#!", StringComparison.Ordinal)
                && !text.StartsWith("#include", StringComparison.Ordinal) && !text.StartsWith("#region", StringComparison.Ordinal)
                && !text.StartsWith("#endregion", StringComparison.Ordinal) && !text.StartsWith("#if", StringComparison.Ordinal)
                && !text.StartsWith("#define", StringComparison.Ordinal) && !text.StartsWith("#pragma", StringComparison.Ordinal));
    }

    static bool IsAttribute(string line)
    {
        var text = line.TrimStart();
        return text.StartsWith('[') || text.StartsWith('@') || text.StartsWith("#[", StringComparison.Ordinal);
    }

    /// <summary>A Python function's or class's docstring: the line after its def that opens with triple quotes.</summary>
    static string? Docstring(IReadOnlyList<string> lines, int start, int end)
    {
        for (var i = start + 1; i <= Math.Min(end, start + 3) && i < lines.Count; i++)
        {
            var text = lines[i].Trim();
            if (text.Length == 0)
                continue;
            if (!text.StartsWith("\"\"\"", StringComparison.Ordinal) && !text.StartsWith("'''", StringComparison.Ordinal))
                return null;
            var body = new StringBuilder(text[3..]);
            for (var j = i + 1; j <= end && j < lines.Count && !body.ToString().Contains(text[..3], StringComparison.Ordinal) && j < i + 6; j++)
                body.Append(' ').Append(lines[j].Trim());
            var docstring = body.ToString();
            var close = docstring.IndexOf(text[..3], StringComparison.Ordinal);
            return FirstSentence(close >= 0 ? docstring[..close] : docstring);
        }
        return null;
    }

    static string Uncomment(string line) => CommentMarks().Replace(line.Trim(), "");

    [GeneratedRegex(@"^(///?|/\*\*?|\*/|\*|#+|--)\s?|\*/$")]
    private static partial Regex CommentMarks();

    [GeneratedRegex(@"<(see|seealso|paramref|typeparamref)\s+(c?ref|name|langword)=""([^""]*)""\s*/>")]
    private static partial Regex XmlReference();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex XmlTag();

    /// <summary>A comment's first sentence, XML doc tags out (a see cref's name kept), at most 200 characters; null when empty.</summary>
    public static string? FirstSentence(string text)
    {
        text = XmlReference().Replace(text, m => m.Groups[3].Value);
        text = XmlTag().Replace(text, " ");
        text = Regex.Replace(text, @"\s+", " ").Trim();
        if (text.Length == 0)
            return null;
        var end = Regex.Match(text, @"[.!?](\s|$)");
        if (end.Success && end.Index > 0)
            text = text[..(end.Index + 1)];
        return text.Length > 200 ? text[..199].TrimEnd() + "…" : text;
    }

    /// <summary>A file's language, by its extension (or name), for the map; null for one it does not name.</summary>
    public static string? Language(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        if (name is "dockerfile" or "makefile" or "cmakelists.txt")
            return name == "dockerfile" ? "Dockerfile" : name == "makefile" ? "Make" : "CMake";
        return Path.GetExtension(name) switch
        {
            ".cs" => "C#",
            ".razor" or ".cshtml" => "Razor",
            ".fs" => "F#",
            ".vb" => "Visual Basic",
            ".ts" or ".tsx" or ".mts" or ".cts" => "TypeScript",
            ".js" or ".jsx" or ".mjs" or ".cjs" => "JavaScript",
            ".vue" => "Vue",
            ".svelte" => "Svelte",
            ".py" or ".pyi" => "Python",
            ".go" => "Go",
            ".rs" => "Rust",
            ".c" or ".h" => "C",
            ".cpp" or ".cc" or ".cxx" or ".hpp" or ".hh" or ".hxx" => "C++",
            ".java" => "Java",
            ".kt" or ".kts" => "Kotlin",
            ".swift" => "Swift",
            ".rb" => "Ruby",
            ".php" => "PHP",
            ".sql" => "SQL",
            ".sh" or ".bash" => "Shell",
            ".ps1" => "PowerShell",
            ".html" or ".htm" => "HTML",
            ".css" or ".scss" or ".less" => "CSS",
            ".axaml" or ".xaml" => "XAML",
            ".json" => "JSON",
            ".yaml" or ".yml" => "YAML",
            ".toml" => "TOML",
            ".xml" or ".csproj" or ".fsproj" or ".vbproj" or ".props" or ".targets" or ".slnx" => "XML",
            ".md" => "Markdown",
            ".proto" => "Protobuf",
            ".graphql" or ".gql" => "GraphQL",
            _ => null,
        };
    }

    /// <summary>Whether a file is data, configuration or prose: no language server outlines it for the map.</summary>
    public static bool IsData(string? language) => language is "JSON" or "YAML" or "TOML" or "XML" or "Markdown";
}
