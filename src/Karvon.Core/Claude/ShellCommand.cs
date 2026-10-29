using System.Text;

namespace Karvon.Claude;

/// <summary>
/// A Bash command read as far as Karvon's shell rules need it: its simple commands in order (split at ; &amp;&amp; || | &amp;
/// ( ) and new lines, quotes kept together, redirections left out), each with its words.
/// </summary>
static class ShellCommand
{
    /// <summary>Options whose value is where output goes: a folder outside the workspace there is not the work's.</summary>
    static readonly HashSet<string> OutputOptions = ["-o", "--output", "--results-directory", "--diag", "--artifacts-path"];

    static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Whether the simple commands of <paramref name="command"/> that <paramref name="matches"/> run in another folder than
    /// <paramref name="workspace"/>: the folder cd (or pushd) went to before them, or a path they are given to work on (a
    /// project, a solution, a folder; not where their output goes).
    /// </summary>
    public static bool RunsElsewhere(string command, string workspace, Func<string, bool> matches)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
        // Null once a cd went somewhere this cannot tell ($VAR, cd -).
        string? folder = root;
        foreach (var (text, all) in Commands(command))
        {
            var words = all.SkipWhile(w => w == "sudo" || IsAssignment(w)).ToList();
            if (words.Count == 0)
                continue;
            if (words[0] is "cd" or "pushd")
            {
                folder = words.Count > 1 ? Resolve(folder, words[1]) : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                continue;
            }
            if (!matches(text))
                continue;
            if (folder is not null && !IsUnder(folder, root))
                return true;
            for (var i = 1; i < words.Count; i++)
            {
                var word = words[i];
                if (OutputOptions.Contains(word))
                {
                    i++;
                    continue;
                }
                if (word.StartsWith('-'))
                    word = word.IndexOf('=') is var eq and > 0 && !OutputOptions.Contains(word[..eq]) ? word[(eq + 1)..] : "";
                // A plain name is in the folder the command runs in; only a rooted path, ~ or .. can leave it.
                if ((Path.IsPathRooted(word) || word.StartsWith('~') || word.Contains("..", StringComparison.Ordinal))
                    && Resolve(folder, word) is { } path && !IsUnder(path, root))
                    return true;
            }
        }
        return false;
    }

    static bool IsAssignment(string word) =>
        word.IndexOf('=') is var eq and > 0 && (char.IsLetter(word[0]) || word[0] == '_') && word[..eq].All(c => char.IsLetterOrDigit(c) || c == '_');

    /// <summary>A path a command was given, from <paramref name="folder"/>; null when the shell would expand it ($VAR).</summary>
    static string? Resolve(string? folder, string word)
    {
        if (word.Length == 0 || word.Contains('$') || word.Contains('`') || word == "-")
            return null;
        if (word == "~" || word.StartsWith("~/", StringComparison.Ordinal))
            word = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + word[1..];
        try
        {
            if (Path.IsPathRooted(word))
                return Path.TrimEndingDirectorySeparator(Path.GetFullPath(word));
            return folder is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(folder, word)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    static bool IsUnder(string path, string root) =>
        path.Equals(root, PathComparison) || path.StartsWith(root + Path.DirectorySeparatorChar, PathComparison)
        || Path.DirectorySeparatorChar != '/' && path.StartsWith(root + '/', PathComparison);

    /// <summary>The simple commands: each one's text as written (for the rules' patterns) and its words, unquoted.</summary>
    static IEnumerable<(string Text, List<string> Words)> Commands(string command)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        var inWord = false;
        var start = 0;
        var skipNext = false;
        void EndWord()
        {
            if (inWord && !skipNext)
                words.Add(word.ToString());
            else if (inWord)
                skipNext = false;
            word.Clear();
            inWord = false;
        }
        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];
            if (c is '\'' or '"')
            {
                var close = command.IndexOf(c, i + 1);
                if (close < 0)
                    close = command.Length;
                word.Append(command, i + 1, close - i - 1);
                inWord = true;
                i = close;
                continue;
            }
            if (c == '\\' && i + 1 < command.Length)
            {
                word.Append(command[++i]);
                inWord = true;
                continue;
            }
            if (c is '>' or '<' || c == '&' && i + 1 < command.Length && command[i + 1] == '>')
            {
                // A redirection (2>&1, > file, &> file): its target is not a word of the command.
                if (inWord && word.ToString().All(char.IsDigit))
                {
                    word.Clear();
                    inWord = false;
                }
                EndWord();
                while (i + 1 < command.Length && command[i + 1] is '>' or '<' or '&')
                    i++;
                if (i + 1 < command.Length && char.IsDigit(command[i + 1]) && command[i] == '&')
                    i++;
                else
                    skipNext = true;
                continue;
            }
            if (c is ';' or '\n' or '|' or '&' or '(' or ')')
            {
                EndWord();
                skipNext = false;
                if (words.Count > 0)
                    yield return (command[start..i], words);
                words = [];
                start = i + 1;
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                EndWord();
                continue;
            }
            word.Append(c);
            inWord = true;
        }
        EndWord();
        if (words.Count > 0)
            yield return (command[start..], words);
    }
}
