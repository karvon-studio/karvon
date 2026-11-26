using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Karvon.Claude.Remote;

/// <summary>
/// An agent's Markdown as Telegram's HTML shows it: bold, italic, struck, inline code, links; fenced code as a block (its
/// language kept); headings bold; bullets as •; quotes as a quote; a table as it is, in a fixed-width block. What is not
/// Markdown is escaped, so nothing the agent wrote becomes a tag of its own.
/// </summary>
public static partial class TelegramMarkdown
{
    public static string ToHtml(string markdown)
    {
        var html = new StringBuilder();
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (Fence().Match(line) is { Success: true } fence)
            {
                // Fenced code, to its closing fence (or the end, where the text was cut).
                var code = new List<string>();
                while (++i < lines.Length && !Fence().IsMatch(lines[i]))
                    code.Add(lines[i]);
                var language = fence.Groups[1].Value;
                html.Append(language.Length > 0 ? $"<pre><code class=\"language-{Escape(language)}\">" : "<pre>")
                    .Append(Escape(string.Join('\n', code)))
                    .Append(language.Length > 0 ? "</code></pre>" : "</pre>")
                    .Append('\n');
                continue;
            }
            if (line.TrimStart().StartsWith('|'))
            {
                // A table: its rows as they are, lined up in a fixed-width font.
                var rows = new List<string>();
                for (; i < lines.Length && lines[i].TrimStart().StartsWith('|'); i++)
                    rows.Add(lines[i].Trim());
                i--;
                html.Append("<pre>").Append(Escape(string.Join('\n', rows))).Append("</pre>\n");
                continue;
            }
            if (line.TrimStart().StartsWith('>'))
            {
                var quoted = new List<string>();
                for (; i < lines.Length && lines[i].TrimStart().StartsWith('>'); i++)
                    quoted.Add(Inline(lines[i].TrimStart()[1..].TrimStart()));
                i--;
                html.Append("<blockquote>").Append(string.Join('\n', quoted)).Append("</blockquote>\n");
                continue;
            }
            if (Heading().Match(line) is { Success: true } heading)
                html.Append("<b>").Append(Inline(heading.Groups[1].Value)).Append("</b>");
            else if (Bullet().Match(line) is { Success: true } bullet)
                html.Append(new string(' ', bullet.Groups[1].Value.Length)).Append("• ").Append(Inline(bullet.Groups[2].Value));
            else if (Rule().IsMatch(line))
                html.Append("──────────");
            else
                html.Append(Inline(line));
            html.Append('\n');
        }
        return html.ToString().TrimEnd('\n');
    }

    /// <summary>A line's inline Markdown: code spans as they are, the rest escaped and marked up.</summary>
    public static string Inline(string text)
    {
        var html = new StringBuilder();
        var parts = CodeSpan().Split(text);
        // Split keeps the captured code at the odd places.
        for (var i = 0; i < parts.Length; i++)
        {
            if (i % 2 == 1)
            {
                html.Append("<code>").Append(Escape(parts[i])).Append("</code>");
                continue;
            }
            var part = Escape(parts[i]);
            part = Link().Replace(part, m => $"<a href=\"{m.Groups[2].Value.Replace("\"", "&quot;", StringComparison.Ordinal)}\">{m.Groups[1].Value}</a>");
            part = Bold().Replace(part, "<b>$1</b>");
            part = Italic().Replace(part, "<i>$1</i>");
            part = Struck().Replace(part, "<s>$1</s>");
            html.Append(part);
        }
        return html.ToString();
    }

    static string Escape(string text) => WebUtility.HtmlEncode(text).Replace("&#39;", "'", StringComparison.Ordinal).Replace("&quot;", "\"", StringComparison.Ordinal);

    [GeneratedRegex(@"^\s*```\s*([\w#+.-]*)\s*$")]
    private static partial Regex Fence();

    [GeneratedRegex(@"^\s{0,3}#{1,6}\s+(.+?)\s*#*\s*$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^(\s*)[-*+]\s+(.*)$")]
    private static partial Regex Bullet();

    [GeneratedRegex(@"^\s{0,3}([-*_])(\s*\1){2,}\s*$")]
    private static partial Regex Rule();

    [GeneratedRegex(@"`([^`\n]+)`")]
    private static partial Regex CodeSpan();

    [GeneratedRegex(@"\[([^\]\n]+)\]\((https?://[^)\s]+)\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"\*\*(?=\S)(.+?)(?<=\S)\*\*")]
    private static partial Regex Bold();

    [GeneratedRegex(@"(?<![\*\w])\*(?=\S)([^*\n]+?)(?<=\S)\*(?![\*\w])")]
    private static partial Regex Italic();

    [GeneratedRegex(@"~~(?=\S)(.+?)(?<=\S)~~")]
    private static partial Regex Struck();
}
