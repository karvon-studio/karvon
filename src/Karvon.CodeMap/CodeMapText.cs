using System.Text;

namespace Karvon.CodeMap;

/// <summary>The map as Claude reads it: a folder's tree with what each part is for, a file's declarations, a search's hits.</summary>
public static class CodeMapText
{
    /// <summary>A folder's files listed at most, the rest counted.</summary>
    const int FilesPerFolder = 30;

    /// <summary>
    /// A folder's tree, <paramref name="depth"/> levels down: each folder with its files' count and its summary, each file
    /// with its summary (or its first declarations, without one); at most <paramref name="maxLines"/> lines, what is left
    /// said with the path to ask for.
    /// </summary>
    public static string Tree(CodeMapStore store, string root, string path, int depth, int maxLines = 300)
    {
        path = path.Trim('/');
        var files = store.Files();
        var folders = store.Folders();
        var under = files.Keys.Where(p => path.Length == 0 || p.StartsWith(path + "/", StringComparison.Ordinal)).ToList();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var file in under)
            for (var folder = Parent(file); ; folder = Parent(folder))
            {
                counts[folder] = counts.GetValueOrDefault(folder) + 1;
                if (folder.Length <= path.Length)
                    break;
            }
        var text = new StringBuilder();
        var lines = 0;
        var cut = false;
        void Line(int level, string line)
        {
            if (lines == maxLines)
            {
                cut = true;
                return;
            }
            text.Append(' ', level * 2).Append(line).Append('\n');
            lines++;
        }
        var name = path.Length == 0 ? Path.GetFileName(root) : path;
        Line(0, $"{name}/ ({Files(counts.GetValueOrDefault(path))}){Summary(folders.GetValueOrDefault(path)?.Summary)}");
        Walk(path, 1);
        if (cut)
            text.Append("… more than ").Append(maxLines).Append(" lines: ask code_map for a folder above (path), or code_search.\n");
        return text.ToString().TrimEnd();

        void Walk(string folder, int level)
        {
            if (level > depth || cut)
                return;
            var subfolders = counts.Keys.Where(f => f.Length > 0 && Parent(f) == folder && f != folder).Order(StringComparer.Ordinal).ToList();
            foreach (var sub in subfolders)
            {
                Line(level, $"{Path.GetFileName(sub)}/ ({Files(counts[sub])}){Summary(folders.GetValueOrDefault(sub)?.Summary)}");
                Walk(sub, level + 1);
            }
            var inFolder = under.Where(f => Parent(f) == folder).Order(StringComparer.Ordinal).ToList();
            foreach (var file in inFolder.Take(FilesPerFolder))
                Line(level, Path.GetFileName(file) + About(store, files[file]));
            if (inFolder.Count > FilesPerFolder)
                Line(level, $"… {inFolder.Count - FilesPerFolder} more files (code_map path={folder}/ depth=1)");
        }
    }

    /// <summary>A file's summary and declarations: each with its line, its signature and its doc comment's first sentence.</summary>
    public static string Outline(CodeMapStore store, MapFile file, int maxLines = 300)
    {
        var text = new StringBuilder($"{file.Path} ({file.Lines} lines{(file.Language is { } l ? ", " + l : "")}){Summary(file.Summary)}\n");
        var symbols = store.Symbols(file.Path);
        if (symbols.Count == 0)
            text.Append(file.Outlined ? "No declarations.\n" : "Its declarations are not known (no language server reads this kind of file).\n");
        foreach (var symbol in symbols.Take(maxLines))
            text.Append(' ', 2 + symbol.Depth * 2).Append(symbol.Line).Append(": ").Append(symbol.Kind).Append(' ')
                .Append(symbol.Detail ?? symbol.Name).Append(symbol.Doc is { } doc ? " — " + doc : "").Append('\n');
        if (symbols.Count > maxLines)
            text.Append($"… {symbols.Count - maxLines} more declarations: code_outline lists them all.\n");
        return text.ToString().TrimEnd();
    }

    /// <summary>A search's hits, a line each: where, what, and its doc comment or summary.</summary>
    public static string Hits(IReadOnlyList<MapHit> hits)
    {
        var text = new StringBuilder();
        foreach (var hit in hits)
        {
            if (hit.IsFolder)
                text.Append(hit.Path.Length == 0 ? "/" : hit.Path + "/");
            else if (hit.IsFile)
                text.Append(hit.Path);
            else
                text.Append(hit.Path).Append(':').Append(hit.Line).Append(": ").Append(hit.Kind).Append(' ')
                    .Append(hit.Container is { } c ? c + "." : "").Append(hit.Detail ?? hit.Name);
            text.Append(Summary(hit.Text)).Append('\n');
        }
        return text.ToString().TrimEnd();
    }

    /// <summary>What a file is: its summary, else its first declarations.</summary>
    static string About(CodeMapStore store, MapFile file)
    {
        if (file.Summary is { Length: > 0 } summary)
            return " — " + summary;
        var top = store.Symbols(file.Path).Where(s => s.Depth == 0).Take(3).Select(s => $"{s.Kind} {s.Name}").ToList();
        return top.Count > 0 ? ": " + string.Join(", ", top) : "";
    }

    static string Summary(string? summary) => summary is { Length: > 0 } ? " — " + summary : "";

    static string Files(int count) => count == 1 ? "1 file" : $"{count} files";

    static string Parent(string path) => path.LastIndexOf('/') is var slash and > 0 ? path[..slash] : "";
}
