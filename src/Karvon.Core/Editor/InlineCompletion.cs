namespace Karvon.Editor;

/// <summary>What an inline completion is asked with: the file, and its text before the caret and after it (each cut to the part nearest the caret).</summary>
public sealed record InlineCompletionRequest(string FilePath, string Prefix, string Suffix);

/// <summary>
/// A suggestion fitted to where it goes, as the editor shows it: its lines joined by \n, without what is already there
/// (the line's start written again, the brackets after the caret it closes), its trailing blank space off; null when
/// nothing is left of it.
/// </summary>
public static class InlineSuggestions
{
    public static string? Fit(string suggestion, string lineBefore, string lineAfter)
    {
        var text = suggestion.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd();
        // The line's start written again: its indentation on a blank line, what was typed on it.
        if (lineBefore.Length > 0 && text.StartsWith(lineBefore, StringComparison.Ordinal))
            text = text[lineBefore.Length..];
        else if (lineBefore.TrimStart() is { Length: >= 2 } typed && text.Length > typed.Length && text.StartsWith(typed, StringComparison.Ordinal))
            text = text[typed.Length..];
        // A line less indented than the caret's ends the block the caret is in, and the code after the caret has that end
        // already (the method's closing brace, the next member): a model often writes it, and the rest of the file, again.
        var lines = text.Split('\n');
        var indentation = Indentation(lineBefore);
        for (var i = 1; i < lines.Length; i++)
            if (lines[i].Trim().Length > 0 && Indentation(lines[i]) < indentation)
            {
                text = string.Join("\n", lines[..i]).TrimEnd();
                break;
            }
        // What closes the line is there after the caret already: it stays, and the suggestion goes before it; not when
        // the suggestion's own end closes what it opened itself (string.Join(a, b) before a ")").
        if (lineAfter.Trim() is { Length: > 0 } after && text.EndsWith(after, StringComparison.Ordinal) && ClosesAgain(text, after))
            text = text[..^after.Length].TrimEnd();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>A line's indentation in columns (a tab is four).</summary>
    static int Indentation(string line)
    {
        var columns = 0;
        foreach (var c in line)
        {
            if (c == ' ')
                columns++;
            else if (c == '\t')
                columns += 4;
            else
                break;
        }
        return columns;
    }

    /// <summary>
    /// The suggestion's end closes again what is closed after the caret: more of a closing bracket in it than of its
    /// opening one, a quote left open, or a separator (; ,) written twice.
    /// </summary>
    static bool ClosesAgain(string text, string after)
    {
        int Count(char c) => text.Count(x => x == c);
        foreach (var (open, close) in new[] { ('(', ')'), ('[', ']'), ('{', '}') })
            if (after.Contains(close) && Count(close) > Count(open))
                return true;
        foreach (var quote in "'\"`")
            if (after.Contains(quote) && Count(quote) % 2 == 1)
                return true;
        return after.All(c => c is ';' or ',' || char.IsWhiteSpace(c));
    }

    /// <summary>
    /// Whether a suggestion is asked for with the rest of the line as it is: nothing after the caret, or only what closes
    /// what the line opened (brackets, quotes, a semicolon), as Visual Studio's and Copilot's are.
    /// </summary>
    public static bool FitsBefore(string lineAfter) => lineAfter.All(c => char.IsWhiteSpace(c) || ")]}>;,'\"`".Contains(c));
}
