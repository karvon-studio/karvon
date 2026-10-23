using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Karvon.Jira;

/// <summary>
/// Jira's rich text both ways: what it renders (its HTML: a description or a comment as the site shows it) read as
/// Markdown, which Karvon's views and Claude read; Atlassian Document Format (Cloud's descriptions and comments) read as
/// Markdown, and made from plain text (paragraphs, their lines).
/// </summary>
public static partial class JiraText
{
    [GeneratedRegex(@"<(/?)([a-zA-Z0-9]+)([^>]*)>", RegexOptions.Singleline)]
    private static partial Regex Tag();

    [GeneratedRegex(@"href\s*=\s*""([^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex Href();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLines();

    /// <summary>Jira's rendered HTML as Markdown: paragraphs, headings, lists, code, links, bold and italic, a table's rows.</summary>
    public static string FromHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return "";
        var md = new StringBuilder();
        var lists = new Stack<(bool Ordered, int Next)>();
        string? link = null;
        var pre = 0;
        var last = 0;
        foreach (Match m in Tag().Matches(html))
        {
            Text(html[last..m.Index]);
            last = m.Index + m.Length;
            var closing = m.Groups[1].Value == "/";
            var name = m.Groups[2].Value.ToLowerInvariant();
            switch (name)
            {
                case "p" or "div":
                    md.Append(closing ? "\n\n" : "");
                    break;
                case "br":
                    md.Append(pre > 0 ? "\n" : "  \n");
                    break;
                case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                    md.Append(closing ? "\n\n" : "\n" + new string('#', name[1] - '0') + " ");
                    break;
                case "strong" or "b":
                    md.Append("**");
                    break;
                case "em" or "i":
                    md.Append('*');
                    break;
                case "del" or "s":
                    md.Append("~~");
                    break;
                case "code" or "tt" when pre == 0:
                    md.Append('`');
                    break;
                case "pre":
                    pre += closing ? -1 : 1;
                    md.Append(closing ? "\n```\n\n" : "\n```\n");
                    break;
                case "ul" or "ol":
                    if (closing)
                    {
                        if (lists.Count > 0)
                            lists.Pop();
                        // After a whole list a blank line, as after a paragraph; after a nested one a line.
                        md.Append(lists.Count == 0 ? "\n\n" : "\n");
                    }
                    else
                        lists.Push((name == "ol", 1));
                    break;
                case "li" when !closing:
                {
                    var indent = new string(' ', Math.Max(0, lists.Count - 1) * 2);
                    if (lists.Count > 0 && lists.Peek() is { Ordered: true } ordered)
                    {
                        lists.Pop();
                        lists.Push((true, ordered.Next + 1));
                        md.Append($"\n{indent}{ordered.Next}. ");
                    }
                    else
                        md.Append($"\n{indent}- ");
                    break;
                }
                case "a":
                    if (closing)
                    {
                        if (link is not null)
                            md.Append($"]({link})");
                        link = null;
                    }
                    else if (Href().Match(m.Groups[3].Value) is { Success: true } href && !href.Groups[1].Value.StartsWith('#'))
                    {
                        link = WebUtility.HtmlDecode(href.Groups[1].Value);
                        md.Append('[');
                    }
                    break;
                case "blockquote":
                    md.Append(closing ? "\n\n" : "\n> ");
                    break;
                case "tr":
                    md.Append(closing ? "|\n" : "");
                    break;
                case "td" or "th" when !closing:
                    md.Append("| ");
                    break;
                case "td" or "th":
                    md.Append(' ');
                    break;
                case "hr":
                    md.Append("\n---\n");
                    break;
            }
        }
        Text(html[last..]);
        return BlankLines().Replace(md.ToString().Replace("\r", ""), "\n\n").Trim();

        void Text(string text)
        {
            if (text.Length == 0)
                return;
            var decoded = WebUtility.HtmlDecode(text);
            md.Append(pre > 0 ? decoded : decoded.Replace('\n', ' '));
        }
    }

    /// <summary>Plain text as Atlassian Document Format: a paragraph for each block of lines, a hard break between its lines.</summary>
    public static object ToAdf(string text)
    {
        var paragraphs = new List<object>();
        foreach (var block in text.Replace("\r", "").Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var content = new List<object>();
            foreach (var (line, i) in block.Split('\n').Select((line, i) => (line, i)))
            {
                if (i > 0)
                    content.Add(new { type = "hardBreak" });
                if (line.Length > 0)
                    content.Add(new { type = "text", text = line });
            }
            paragraphs.Add(new { type = "paragraph", content });
        }
        return new { type = "doc", version = 1, content = paragraphs };
    }

    /// <summary>Atlassian Document Format as Markdown (where a site gave no rendered HTML).</summary>
    public static string FromAdf(JsonElement doc)
    {
        if (doc.ValueKind != JsonValueKind.Object)
            return doc.ValueKind == JsonValueKind.String ? doc.GetString() ?? "" : "";
        var md = new StringBuilder();
        Block(doc, 0);
        return BlankLines().Replace(md.ToString(), "\n\n").Trim();

        void Block(JsonElement node, int depth)
        {
            var type = node.TryGetProperty("type", out var t) ? t.GetString() : null;
            switch (type)
            {
                case "paragraph":
                    Inline(node);
                    md.Append("\n\n");
                    break;
                case "heading":
                    md.Append(new string('#', node.TryGetProperty("attrs", out var a) && a.TryGetProperty("level", out var l) ? l.GetInt32() : 1)).Append(' ');
                    Inline(node);
                    md.Append("\n\n");
                    break;
                case "bulletList" or "orderedList":
                    var n = 1;
                    foreach (var item in Children(node))
                    {
                        md.Append(new string(' ', depth * 2)).Append(type == "orderedList" ? $"{n++}. " : "- ");
                        foreach (var child in Children(item))
                            if (child.TryGetProperty("type", out var ct) && ct.GetString() is "bulletList" or "orderedList")
                            {
                                md.Append('\n');
                                Block(child, depth + 1);
                            }
                            else
                                Inline(child);
                        md.Append('\n');
                    }
                    md.Append('\n');
                    break;
                case "codeBlock":
                    md.Append("```\n");
                    Inline(node);
                    md.Append("\n```\n\n");
                    break;
                case "blockquote":
                    md.Append("> ");
                    foreach (var child in Children(node))
                        Inline(child);
                    md.Append("\n\n");
                    break;
                case "rule":
                    md.Append("---\n\n");
                    break;
                default:
                    foreach (var child in Children(node))
                        Block(child, depth);
                    break;
            }
        }

        void Inline(JsonElement node)
        {
            foreach (var child in Children(node))
            {
                var type = child.TryGetProperty("type", out var t) ? t.GetString() : null;
                switch (type)
                {
                    case "text":
                    {
                        var text = child.GetProperty("text").GetString() ?? "";
                        string? href = null;
                        if (child.TryGetProperty("marks", out var marks))
                            foreach (var mark in marks.EnumerateArray())
                                switch (mark.GetProperty("type").GetString())
                                {
                                    case "strong": text = $"**{text}**"; break;
                                    case "em": text = $"*{text}*"; break;
                                    case "code": text = $"`{text}`"; break;
                                    case "strike": text = $"~~{text}~~"; break;
                                    case "link": href = mark.GetProperty("attrs").GetProperty("href").GetString(); break;
                                }
                        md.Append(href is null ? text : $"[{text}]({href})");
                        break;
                    }
                    case "hardBreak":
                        md.Append("  \n");
                        break;
                    case "mention" or "emoji" or "status" or "date" or "inlineCard":
                        var attrs = child.TryGetProperty("attrs", out var at) ? at : default;
                        md.Append(attrs.ValueKind == JsonValueKind.Object && (attrs.TryGetProperty("text", out var said) || attrs.TryGetProperty("url", out said)) ? said.GetString() : "");
                        break;
                    default:
                        Inline(child);
                        break;
                }
            }
        }

        static IEnumerable<JsonElement> Children(JsonElement node) =>
            node.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array ? content.EnumerateArray() : [];
    }
}
