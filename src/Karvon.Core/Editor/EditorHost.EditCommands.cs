using Avalonia.Threading;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;

namespace Karvon.Editor;

/// <summary>
/// Visual Studio's text editing keys: Ctrl+D duplicates, Alt+Up and Alt+Down move the selected lines, Ctrl+L cuts them
/// and Ctrl+Shift+L deletes them, Ctrl+Enter and Ctrl+Shift+Enter open a line above and below, Ctrl+U and Ctrl+Shift+U
/// make the selection lower and upper case, Ctrl+] goes to the matching brace (Ctrl+Shift+] selects to it), Ctrl+W
/// selects the word, Ctrl+F3 finds the selection's next match (Ctrl+Shift+F3 the one before), Ctrl+G goes to a line, and
/// Ctrl+K, Ctrl+C / Ctrl+U / Ctrl+/ comment the lines out, back in, or either, in the file's language.
/// </summary>
public sealed partial class EditorHost
{
    const string OpeningBraces = "([{";
    const string ClosingBraces = ")]}";

    /// <summary>The lines the selection is on, the caret's without one (a line selected to the next one's start is one line).</summary>
    (DocumentLine First, DocumentLine Last) SelectedLines()
    {
        var selection = Editor.TextArea.Selection;
        if (selection.IsEmpty)
        {
            var line = Document.GetLineByOffset(Editor.CaretOffset);
            return (line, line);
        }
        var segment = selection.SurroundingSegment;
        var first = Document.GetLineByOffset(segment.Offset);
        var last = Document.GetLineByOffset(segment.EndOffset);
        if (last != first && segment.EndOffset == last.Offset)
            last = last.PreviousLine;
        return (first, last);
    }

    /// <summary>The line break after a line, else the one before it (the last line has none), else "\n".</summary>
    string LineBreakAt(DocumentLine line) =>
        line.DelimiterLength > 0 ? Document.GetText(line.EndOffset, line.DelimiterLength)
        : line.PreviousLine is { DelimiterLength: > 0 } previous ? Document.GetText(previous.EndOffset, previous.DelimiterLength)
        : "\n";

    /// <summary>Ctrl+D: the selection again after it (the copy selected), else the caret's line again below it.</summary>
    public void Duplicate()
    {
        if (Editor.SelectionLength > 0)
        {
            var text = Editor.SelectedText;
            var end = Editor.SelectionStart + text.Length;
            Document.Insert(end, text);
            Editor.Select(end, text.Length);
            return;
        }
        var line = Document.GetLineByOffset(Editor.CaretOffset);
        var (lineEnd, column, lineBreak) = (line.EndOffset, Editor.CaretOffset - line.Offset, LineBreakAt(line));
        Document.Insert(lineEnd, lineBreak + Document.GetText(line));
        Editor.CaretOffset = lineEnd + lineBreak.Length + column;
    }

    /// <summary>Alt+Up, Alt+Down: the selected lines over the line above them, or under the one below, the selection with them.</summary>
    public void MoveSelectedLines(bool down)
    {
        var (first, last) = SelectedLines();
        if ((down ? last.NextLine : first.PreviousLine) is not { } other)
            return;
        var (caret, start, length) = (Editor.CaretOffset, Editor.SelectionStart, Editor.SelectionLength);
        var lines = Document.GetText(first.Offset, last.EndOffset - first.Offset);
        var otherText = Document.GetText(other);
        var lineBreak = LineBreakAt(down ? last : other);
        var shift = (otherText.Length + lineBreak.Length) * (down ? 1 : -1);
        if (down)
            Document.Replace(first.Offset, other.EndOffset - first.Offset, otherText + lineBreak + lines);
        else
            Document.Replace(other.Offset, last.EndOffset - other.Offset, lines + lineBreak + otherText);
        if (length > 0)
            Editor.Select(start + shift, length);
        else
            Editor.CaretOffset = caret + shift;
    }

    /// <summary>Ctrl+L: the selected lines (the caret's) cut to the clipboard, their line break with them.</summary>
    public void CutLines()
    {
        var (first, last) = SelectedLines();
        Editor.Select(first.Offset, last.EndOffset + last.DelimiterLength - first.Offset);
        Editor.Cut();
    }

    /// <summary>Ctrl+Shift+L: the selected lines (the caret's) deleted.</summary>
    public void DeleteLines()
    {
        var (first, last) = SelectedLines();
        var start = first.Offset;
        var end = last.EndOffset + last.DelimiterLength;
        // The document's last line takes the line break before it.
        if (last.DelimiterLength == 0 && first.PreviousLine is { } previous)
            start = previous.EndOffset;
        Editor.TextArea.ClearSelection();
        Document.Remove(start, end - start);
    }

    /// <summary>Ctrl+Enter: an empty line above the caret's (Ctrl+Shift+Enter: below), indented as it is, the caret on it.</summary>
    public void OpenLine(bool below)
    {
        var line = Document.GetLineByOffset(Editor.CaretOffset);
        var text = Document.GetText(line);
        var indent = text[..(text.Length - text.TrimStart().Length)];
        var lineBreak = LineBreakAt(line);
        Editor.TextArea.ClearSelection();
        if (below)
        {
            var end = line.EndOffset;
            Document.Insert(end, lineBreak + indent);
            Editor.CaretOffset = end + lineBreak.Length + indent.Length;
        }
        else
        {
            var start = line.Offset;
            Document.Insert(start, indent + lineBreak);
            Editor.CaretOffset = start + indent.Length;
        }
    }

    /// <summary>Ctrl+Shift+U: the selection in upper case (Ctrl+U: lower), still selected; without one, the character after the caret.</summary>
    public void ChangeCase(bool upper)
    {
        string Changed(string text) => upper ? text.ToUpperInvariant() : text.ToLowerInvariant();
        if (Editor.SelectionLength == 0)
        {
            var at = Editor.CaretOffset;
            if (at >= Document.TextLength)
                return;
            Document.Replace(at, 1, Changed(Document.GetText(at, 1)));
            Editor.CaretOffset = at + 1;
            return;
        }
        var (start, selected) = (Editor.SelectionStart, Editor.SelectedText);
        var changed = Changed(selected);
        Document.Replace(start, selected.Length, changed);
        Editor.Select(start, changed.Length);
    }

    /// <summary>
    /// Ctrl+]: from an opening brace to after its closing one, from after a closing brace to its opening one; Ctrl+Shift+]
    /// selects what is between, the braces too.
    /// </summary>
    public void GoToMatchingBrace(bool select)
    {
        var text = Document.Text;
        var caret = Editor.CaretOffset;
        int? To(int at, bool opening) => (opening ? OpeningBraces : ClosingBraces).IndexOf(text[at]) is var kind and >= 0
            ? MatchingBrace(text, at, kind, forward: opening) is { } match ? opening ? match + 1 : match : null
            : null;
        var target = (caret < text.Length ? To(caret, opening: true) : null)
            ?? (caret > 0 ? To(caret - 1, opening: false) : null)
            ?? (caret < text.Length ? To(caret, opening: false) : null)
            ?? (caret > 0 ? To(caret - 1, opening: true) : null);
        if (target is not { } to)
            return;
        if (select)
            Editor.TextArea.Selection = Selection.Create(Editor.TextArea, caret, to);
        else
            Editor.TextArea.ClearSelection();
        Editor.CaretOffset = to;
    }

    /// <summary>The offset of the brace matching the one at <paramref name="at"/>, of the same kind; null without one.</summary>
    static int? MatchingBrace(string text, int at, int kind, bool forward)
    {
        var depth = 0;
        for (var i = at; i >= 0 && i < text.Length; i += forward ? 1 : -1)
        {
            if (text[i] == OpeningBraces[kind])
                depth += forward ? 1 : -1;
            else if (text[i] == ClosingBraces[kind])
                depth += forward ? -1 : 1;
            if (depth == 0)
                return i;
        }
        return null;
    }

    /// <summary>Ctrl+W: the word at the caret selected.</summary>
    public void SelectCurrentWord()
    {
        Editor.TextArea.ClearSelection();
        MatchText();
    }

    /// <summary>Ctrl+F3: the selection's next match selected (the word at the caret's), from the top after the end; Ctrl+Shift+F3: the one before.</summary>
    public void FindNextSelected(bool backward)
    {
        if (MatchText() is not { Length: > 0 } match)
            return;
        var text = Document.Text;
        var start = Editor.SelectionStart;
        int at;
        if (backward)
        {
            at = -1;
            for (var i = text.IndexOf(match, StringComparison.OrdinalIgnoreCase); i >= 0 && i < start; i = text.IndexOf(match, i + 1, StringComparison.OrdinalIgnoreCase))
                at = i;
            if (at < 0)
                at = text.LastIndexOf(match, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            at = text.IndexOf(match, start + 1, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
                at = text.IndexOf(match, StringComparison.OrdinalIgnoreCase);
        }
        if (at < 0)
            return;
        RevealOffset(at);
        Editor.Select(at, match.Length);
        var location = Document.GetLocation(at);
        Editor.ScrollTo(location.Line, location.Column);
    }

    /// <summary>Ctrl+G: the caret at a line's start, shown.</summary>
    public void GoToLine(int line)
    {
        var start = Document.GetLineByNumber(line).Offset;
        RevealOffset(start);
        Editor.TextArea.ClearSelection();
        Editor.CaretOffset = start;
        Editor.ScrollTo(line, 1);
        Dispatcher.UIThread.Post(() => Editor.TextArea.Focus(), DispatcherPriority.Background);
    }

    /// <summary>A line comment as the file's language writes one (the end too for the languages with block comments only); null for others.</summary>
    static (string Start, string End)? CommentSyntax(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        return Path.GetExtension(name) switch
        {
            ".razor" or ".cshtml" => ("@*", "*@"),
            ".cs" or ".csx" or ".fs" or ".fsx" or ".fsi" or ".js" or ".jsx" or ".mjs" or ".cjs" or ".ts" or ".tsx" or ".mts" or ".cts"
                or ".java" or ".kt" or ".kts" or ".scala" or ".groovy" or ".gradle" or ".go" or ".rs" or ".c" or ".h" or ".cpp"
                or ".cc" or ".cxx" or ".hpp" or ".swift" or ".dart" or ".php" or ".proto" or ".jsonc" or ".scss" or ".less" or ".zig" or ".jwc" => ("//", ""),
            ".py" or ".pyi" or ".sh" or ".bash" or ".zsh" or ".rb" or ".yaml" or ".yml" or ".toml" or ".ps1" or ".psm1" or ".r"
                or ".pl" or ".cmake" or ".tf" or ".nix" or ".jl" or ".ex" or ".exs" or ".mk" or ".properties" or ".editorconfig"
                or ".gitignore" or ".gitattributes" or ".dockerignore" or ".env" or ".conf" => ("#", ""),
            ".sql" or ".lua" or ".hs" => ("--", ""),
            ".xml" or ".xaml" or ".axaml" or ".html" or ".htm" or ".csproj" or ".fsproj" or ".vbproj" or ".props" or ".targets"
                or ".config" or ".resx" or ".svg" or ".md" => ("<!--", "-->"),
            ".css" => ("/*", "*/"),
            ".ini" => (";", ""),
            ".vb" => ("'", ""),
            ".bat" or ".cmd" => ("REM ", ""),
            _ when name is "dockerfile" or "containerfile" or "makefile" or "gnumakefile" || name.StartsWith("dockerfile.", StringComparison.Ordinal) => ("#", ""),
            _ => null,
        };
    }
}
