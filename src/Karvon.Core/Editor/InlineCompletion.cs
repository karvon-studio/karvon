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
        // What closes the line is there after the caret already: it stays, and the suggestion goes before it.
        if (lineAfter.Trim() is { Length: > 0 } after && text.EndsWith(after, StringComparison.Ordinal))
            text = text[..^after.Length].TrimEnd();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>
    /// Whether a suggestion is asked for with the rest of the line as it is: nothing after the caret, or only what closes
    /// what the line opened (brackets, quotes, a semicolon), as Visual Studio's and Copilot's are.
    /// </summary>
    public static bool FitsBefore(string lineAfter) => lineAfter.All(c => char.IsWhiteSpace(c) || ")]}>;,'\"`".Contains(c));
}
