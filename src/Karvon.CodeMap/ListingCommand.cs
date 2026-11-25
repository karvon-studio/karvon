using System.Text.RegularExpressions;

namespace Karvon.CodeMap;

/// <summary>
/// Whether a shell command only lists files the map has, as the map does: ls -R, tree, or find by name and type, with what
/// trims its output (head, sort, grep as a filter). One that does anything else is the shell's: the files read or searched
/// (xargs, -exec, a grep or cat beside it), deleted, measured or counted, the list written to a file or put in another
/// command; and so is one that asks for what the map does not have (other folders, sizes and times, files that are not code).
/// </summary>
static partial class ListingCommand
{
    /// <summary>find's tests and actions the map answers, and whether each has a value.</summary>
    static readonly Dictionary<string, bool> FindWords = new(StringComparer.Ordinal)
    {
        ["-type"] = true, ["-name"] = true, ["-iname"] = true, ["-path"] = true, ["-ipath"] = true, ["-wholename"] = true,
        ["-maxdepth"] = true, ["-mindepth"] = true,
        ["-not"] = false, ["!"] = false, ["-o"] = false, ["-or"] = false, ["-a"] = false, ["-and"] = false, ["("] = false, [")"] = false,
        ["-prune"] = false, ["-print"] = false,
    };

    /// <summary>tree's options that change how the names are shown, and whether each has a value.</summary>
    static readonly Dictionary<string, bool> TreeWords = new(StringComparer.Ordinal)
    {
        ["-L"] = true, ["-I"] = true, ["-P"] = true, ["-d"] = false, ["-f"] = false, ["-i"] = false, ["-C"] = false, ["-n"] = false, ["-F"] = false,
        ["--dirsfirst"] = false, ["--noreport"] = false, ["--gitignore"] = false,
    };

    /// <summary>What a command's output is redirected to, other than nowhere or its own output.</summary>
    [GeneratedRegex(@">(?!&\d|\s*/dev/null\b)|<")]
    private static partial Regex Redirects();

    /// <param name="commands">The simple commands of <paramref name="command"/>, in order, each with its words.</param>
    /// <param name="root">The workspace, where the command runs.</param>
    /// <param name="has">Whether the map has a file or a folder of this path (relative to the workspace, with /).</param>
    public static bool Is(string command, IReadOnlyList<IReadOnlyList<string>> commands, string root, Func<string, bool> has)
    {
        // Names put in another command's words, or the list kept in a file.
        if (command.Contains("$(", StringComparison.Ordinal) || command.Contains('`') || Redirects().IsMatch(command))
            return false;
        var folder = root;
        var lists = false;
        foreach (var words in commands)
        {
            if (words[0] is "cd" or "pushd" && words.Count == 2)
            {
                folder = Path.Combine(folder, words[1]);
                continue;
            }
            if (Paths(words) is { } paths)
            {
                if (!paths.All(p => InMap(Path.Combine(folder, p), root, has)))
                    return false;
                lists = true;
            }
            else if (!Trims(words))
                return false;
        }
        return lists;
    }

    /// <summary>The folders a listing lists (the one it runs in when it names none); null for any other command, or a listing of more than names.</summary>
    static List<string>? Paths(IReadOnlyList<string> words)
    {
        var paths = words[0] switch
        {
            "find" => Find(words),
            "tree" => Options(words, TreeWords),
            "ls" => Ls(words),
            _ => null,
        };
        return paths is { Count: 0 } ? ["."] : paths;
    }

    static List<string>? Find(IReadOnlyList<string> words)
    {
        var paths = new List<string>();
        var i = 1;
        for (; i < words.Count && !words[i].StartsWith('-') && words[i] is not ("!" or "("); i++)
            paths.Add(words[i]);
        // Up to the last -prune (and after a -not) is what is left out, which may be anything.
        var asked = words.ToList().LastIndexOf("-prune");
        var not = false;
        for (; i < words.Count; i++)
        {
            if (!FindWords.TryGetValue(words[i], out var hasValue))
                return null;
            if (words[i] is "-not" or "!")
            {
                not = true;
                continue;
            }
            if (hasValue)
            {
                if (++i == words.Count)
                    return null;
                // What is asked for is code, in the folders the map has.
                if (!not && i > asked && (words[i - 1].EndsWith("name", StringComparison.Ordinal) && !IsCodePattern(words[i])
                    || words[i - 1].EndsWith("path", StringComparison.Ordinal) && NamesSkipped(words[i])))
                    return null;
            }
            not = false;
        }
        return paths;
    }

    /// <summary>A name or a pattern whose extension, when it has one, is of a language the map names, outside the folders it leaves out.</summary>
    static bool IsCodePattern(string pattern)
    {
        if (CodeMapIndexer.SkippedFolders.Contains(pattern.Trim('*')))
            return false;
        var extension = Path.GetExtension(pattern);
        return extension.Length == 0 || extension.Contains('*') || CodeText.Language("a" + extension) is not null;
    }

    static bool NamesSkipped(string pattern) => pattern.Split('/').Any(CodeMapIndexer.SkippedFolders.Contains);

    static List<string>? Ls(IReadOnlyList<string> words)
    {
        var options = words.Skip(1).Where(w => w.StartsWith('-')).ToList();
        // Names only: no sizes, times or owners, and not by them.
        if (!options.Any(o => o.Contains('R') || o == "--recursive")
            || options.Any(o => o != "--recursive" && (o.StartsWith("--", StringComparison.Ordinal) || o[1..].Any(c => c is not ('R' or '1' or 'a' or 'A' or 'p' or 'F')))))
            return null;
        return [.. words.Skip(1).Where(w => !w.StartsWith('-'))];
    }

    static List<string>? Options(IReadOnlyList<string> words, Dictionary<string, bool> known)
    {
        var paths = new List<string>();
        for (var i = 1; i < words.Count; i++)
        {
            if (!words[i].StartsWith('-'))
                paths.Add(words[i]);
            else if (!known.TryGetValue(words[i], out var hasValue))
                return null;
            else if (hasValue)
                i++;
        }
        return paths;
    }

    /// <summary>A command that only shows less of a listing (or where it is): head, tail, sort, uniq, less, pwd, echo, ls, and grep as a filter.</summary>
    static bool Trims(IReadOnlyList<string> words) => words[0] switch
    {
        "head" or "tail" or "sort" or "uniq" or "less" or "cat" => words.Skip(1).All(w => w.StartsWith('-') || w.All(char.IsDigit)),
        "pwd" or "echo" or "true" => true,
        "ls" => words.Count == 1,
        "grep" or "egrep" => words.Skip(1).Where(w => w.StartsWith('-')).All(w => w is "-v" or "-i" or "-E" or "-iE" or "-vE" or "-Ev" or "-iv" or "-vi" or "-F" or "-e")
            && words.Skip(1).Count(w => !w.StartsWith('-')) == 1,
        _ => false,
    };

    static bool InMap(string path, string root, Func<string, bool> has)
    {
        if (path.Contains('$') || path.Contains('*') || path.Contains('~'))
            return false;
        var relative = Path.GetRelativePath(root, Path.GetFullPath(path)).Replace('\\', '/').TrimEnd('/');
        return relative == "." || relative != ".." && !relative.StartsWith("../", StringComparison.Ordinal) && !Path.IsPathRooted(relative) && has(relative);
    }
}
