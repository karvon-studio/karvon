using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Karvon.Claude;

/// <summary>
/// The files no agent is given, whatever the settings say: Postman's environments and globals (told by their names, or kept
/// here when they are opened under another), Karvon's vault and the system's keyrings. Karvon's tools, the guard of the
/// agents' own and what goes with a prompt all ask here. What keeps the values themselves from any program is that Karvon
/// writes them to no file (<see cref="Karvon.Services.SealedStore"/>).
/// </summary>
public static partial class SealedFiles
{
    static readonly StringComparison Comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    static readonly string[] Extensions = [".postman_environment.json", ".postman_globals.json"];

    static readonly ConcurrentDictionary<string, byte> Kept = new(OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);

    /// <summary>A file that is one, under a name that does not say so.</summary>
    public static void Add(string path) => Kept[Path.GetFullPath(path)] = 0;

    /// <summary>Whether a file's name says that it is one.</summary>
    public static bool IsName(string fileName) => Extensions.Any(e => fileName.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    public static bool Is(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
            if (Told(full))
                return true;
            // A link to one is one.
            return File.Exists(full) && File.ResolveLinkTarget(full, returnFinalTarget: true) is { } target && Told(Path.GetFullPath(target.FullName));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    static bool Told(string full)
    {
        if (IsName(full) || Kept.ContainsKey(full))
            return true;
        var vault = Path.Combine(Path.GetFullPath(Services.Settings.ConfigDirectory), "vault.db");
        if (full.StartsWith(vault, Comparison))
            return true;
        var keyrings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "keyrings");
        return full.Equals(keyrings, Comparison) || full.StartsWith(keyrings + Path.DirectorySeparatorChar, Comparison);
    }

    /// <summary>The folder's files that are ones (dependencies and build output left out), for a sandbox to hide.</summary>
    public static IReadOnlyList<string> In(string folder)
    {
        var found = new List<string>(Kept.Keys.Where(k => k.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar, Comparison) && File.Exists(k)));
        var entries = 0;
        void Read(string at, int depth)
        {
            try
            {
                foreach (var entry in new DirectoryInfo(at).EnumerateFileSystemInfos())
                {
                    if (++entries > 50_000)
                        return;
                    if (entry is DirectoryInfo)
                    {
                        if (depth < 8 && entry.Name is not (".git" or "node_modules" or "bin" or "obj" or "target" or ".venv" or "venv") && !entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                            Read(entry.FullName, depth + 1);
                    }
                    else if (IsName(entry.Name))
                        found.Add(entry.FullName);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        Read(folder, 0);
        return [.. found.Distinct()];
    }

    public const string Reason = "It is a Postman environment (or Karvon's vault): Karvon keeps it from every agent, whatever the settings. "
        + "Use its variables by name ({{name}}) in requests; ask the user for what they hold only if you must, never for a secret.";

    /// <summary>The parts of a tool's input that name files.</summary>
    static readonly HashSet<string> PathNames = new(StringComparer.OrdinalIgnoreCase)
        { "path", "paths", "file", "files", "file_path", "notebook_path", "document", "folder", "source", "volume", "volumes", "mounts", "target", "output", "data" };

    /// <summary>Why a call of a tool of Karvon's may not run: a file it names is one; null when none is.</summary>
    public static string? Refusal(JsonElement input, string? workspace)
    {
        bool Names(JsonElement value, bool isPath) => value.ValueKind switch
        {
            JsonValueKind.Object => value.EnumerateObject().Any(p => Names(p.Value, PathNames.Contains(p.Name))),
            JsonValueKind.Array => value.EnumerateArray().Any(v => Names(v, isPath)),
            JsonValueKind.String => isPath && value.GetString() is { Length: > 0 and < 4096 } text && !text.Contains('\n') && NamesOne(text),
            _ => false,
        };
        bool NamesOne(string text)
        {
            // A mount is source:target.
            foreach (var part in text.Split(':').Prepend(text))
                if (part.Length > 1 && (IsName(part) || (workspace is not null || Path.IsPathFullyQualified(part)) && Is(workspace is null ? part : Combined(part, workspace))))
                    return true;
            return false;
        }
        return Names(input, false) ? Reason : null;
    }

    static string Combined(string path, string workspace)
    {
        try
        {
            return Path.GetFullPath(path.StartsWith('~') ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..] : path, workspace);
        }
        catch (ArgumentException)
        {
            return path;
        }
    }

    /// <summary>
    /// What a tool gives an agent, without what it shows of these files: a diff's part about one, and the lines a search
    /// found in one (path:line: text).
    /// </summary>
    public static string Filter(string text)
    {
        var names = Kept.Keys.Select(Path.GetFileName).OfType<string>().Where(n => text.Contains(n, StringComparison.Ordinal)).ToList();
        if (names.Count == 0 && !Extensions.Any(e => text.Contains(e, StringComparison.OrdinalIgnoreCase)))
            return text;
        bool Holds(string line) => Extensions.Any(e => line.Contains(e, StringComparison.OrdinalIgnoreCase)) || names.Any(n => line.Contains(n, StringComparison.Ordinal));
        var kept = new StringBuilder(text.Length);
        var skipping = false;
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var start = DiffStart().Match(line);
            if (start.Success)
            {
                // A part starts with "diff --git", or with "---" and "+++" when there is no such line before it.
                skipping = Holds(line) || (line.TrimStart().StartsWith("--- ", StringComparison.Ordinal) && i + 1 < lines.Length && lines[i + 1].TrimStart().StartsWith("+++ ", StringComparison.Ordinal) && Holds(lines[i + 1]))
                    || (skipping && !line.TrimStart().StartsWith("diff ", StringComparison.Ordinal));
                if (skipping && Holds(line))
                {
                    kept.Append(line.TrimEnd('\r')).Append("  (Karvon keeps it from agents)\n");
                    continue;
                }
            }
            if (skipping)
                continue;
            kept.Append(Found().Replace(line, m => m.Groups[1].Value + ": (Karvon keeps it from agents)"));
            if (i < lines.Length - 1)
                kept.Append('\n');
        }
        var result = kept.ToString();
        foreach (var name in names)
            result = Regex.Replace(result, "(?m)^(.*" + Regex.Escape(name) + @")[:\-]\d+[:\-].*$", m => m.Groups[1].Value + ": (Karvon keeps it from agents)");
        return result;
    }

    /// <summary>A prompt without the @ before such a file (Claude Code puts in a mentioned file's text by itself).</summary>
    public static string Unmention(string prompt, string? workspace) => prompt.Contains('@')
        ? Mention().Replace(prompt, m => (m.Groups[1].Value + m.Groups[2].Value) is { Length: > 0 } path && (IsName(Trimmed(path)) || Is(workspace is null ? Trimmed(path) : Combined(Trimmed(path), workspace))) ? m.Value[1..] : m.Value)
        : prompt;

    /// <summary>A mention's path without the lines after it (#L3-9) and the mark a sentence ends with.</summary>
    static string Trimmed(string path) => Regex.Replace(path, @"(#L?\d+(-L?\d+)?)?[.,;:!?)]*$", "");

    [GeneratedRegex(@"^\s*(?:diff --(?:git|cc|combined) |--- \S)")]
    private static partial Regex DiffStart();

    /// <summary>path:12:text, path-12-text, path:text, as grep and its like write them.</summary>
    [GeneratedRegex(@"^(.*?\.postman_(?:environment|globals)\.json)(?:[:\-]\d+)?[:\-].*$", RegexOptions.IgnoreCase)]
    private static partial Regex Found();

    [GeneratedRegex(@"@(?:""([^""\n]+)""|([^\s""]+))")]
    private static partial Regex Mention();
}
