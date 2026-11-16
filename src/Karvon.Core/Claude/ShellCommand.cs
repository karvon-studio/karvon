using System.Text;
using System.Text.RegularExpressions;

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
            var words = all.SkipWhile(w => w is "do" or "then" or "else" or "sudo" || IsAssignment(w)).ToList();
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

    /// <summary>Whether <paramref name="command"/> is a loop that sleeps: it waits for something.</summary>
    public static bool Polls(string command)
    {
        var commands = Words(command).ToList();
        return commands.Any(c => c[0] is "for" or "while" or "until") && commands.Any(c => c[0] == "sleep");
    }

    /// <summary>The simple commands' words, without the assignments and the sudo before them.</summary>
    public static IEnumerable<List<string>> Words(string command) =>
        Commands(command).Select(c => c.Words.SkipWhile(w => w is "do" or "then" or "else" or "sudo" || IsAssignment(w)).ToList()).Where(w => w.Count > 0);

    /// <summary>A path a command was given (~ expanded), from the folder it runs in; null when the shell would expand it.</summary>
    public static string? PathOf(string word, string? folder) => Resolve(folder, word);

    /// <summary>
    /// A PowerShell command's simple commands, each with its words: split at ; | &amp; and new lines, and where a command
    /// starts inside another (after a ( that is not a method's, a { or an =), quotes kept together, ` its escape (a \ is
    /// a path's, not one), a method's ( ) and the commas between words, and a redirection's target left out.
    /// </summary>
    public static IEnumerable<List<string>> PowerShellWords(string command)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        var inWord = false;
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
            if (c == '`' && i + 1 < command.Length)
            {
                // Before a new line it carries the command on; before anything else that character is taken as it is.
                if (command[i + 1] is '\r' or '\n')
                    EndWord();
                else
                {
                    word.Append(command[i + 1]);
                    inWord = true;
                }
                i++;
                continue;
            }
            if (c == '>')
            {
                // A redirection (> file, 2> file, *>> file, 2>&1): its target is not a word of the command.
                if (inWord && word.ToString() is var stream && (stream == "*" || stream.All(char.IsDigit)))
                {
                    word.Clear();
                    inWord = false;
                }
                EndWord();
                while (i + 1 < command.Length && command[i + 1] == '>')
                    i++;
                if (i + 2 < command.Length && command[i + 1] == '&' && char.IsDigit(command[i + 2]))
                    i += 2;
                else
                    skipNext = true;
                continue;
            }
            // A method's arguments are its words ([IO.File]::ReadAllText(path)); any other ( starts a command of its own.
            var call = c == '(' && inWord && word.ToString().Contains("::", StringComparison.Ordinal);
            if (c is ';' or '\n' or '|' or '&' or '{' or '=' || c == '(' && !call)
            {
                EndWord();
                skipNext = false;
                if (words.Count > 0)
                    yield return words;
                words = [];
                continue;
            }
            if (char.IsWhiteSpace(c) || c is '(' or ')' or '}' or ',')
            {
                EndWord();
                continue;
            }
            word.Append(c);
            inWord = true;
        }
        EndWord();
        if (words.Count > 0)
            yield return words;
    }

    /// <summary>
    /// A path a PowerShell command was given, from the folder it runs in: ~, $HOME, $PWD and $env: variables put in, and a
    /// \ a separator on every system, as PowerShell reads it; null when only PowerShell can tell ($file).
    /// </summary>
    public static string? PowerShellPathOf(string word, string? folder)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        word = PowerShellVariable.Replace(word, m =>
        {
            var name = m.Groups["name"].Value;
            if (name.Equals("HOME", StringComparison.OrdinalIgnoreCase))
                return home;
            if (name.Equals("PWD", StringComparison.OrdinalIgnoreCase))
                return folder ?? m.Value;
            return Environment.GetEnvironmentVariable(name[4..]) is { Length: > 0 } value ? value : m.Value;
        });
        if (word == "~" || word.StartsWith("~/", StringComparison.Ordinal) || word.StartsWith("~\\", StringComparison.Ordinal))
            word = home + word[1..];
        if (Path.DirectorySeparatorChar != '\\')
            word = word.Replace('\\', '/');
        return Resolve(folder, word);
    }

    /// <summary>$HOME, $PWD, $env:NAME, and the same in braces (${env:NAME}).</summary>
    static readonly Regex PowerShellVariable = new(@"\$(?:\{(?<name>HOME|PWD|env:\w+)\}|(?<name>HOME|PWD|env:\w+)\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

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
