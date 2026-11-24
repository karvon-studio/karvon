using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Karvon.CodeMap;

/// <summary>Who writes the map's summaries: no one, Claude (the user's Claude Code sign-in, its fast model) or a model of Ollama's.</summary>
public enum SummarySource
{
    None,
    Claude,
    Ollama,
}

/// <summary>The Code Map's settings, in codemap.json beside Karvon's: who writes the summaries, and Ollama's address and model.</summary>
public sealed class CodeMapSettings
{
    public const string DefaultOllamaUrl = "http://localhost:11434";
    public const string DefaultOllamaModel = "qwen2.5-coder:7b";

    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>None until the user chooses: summaries send the workspace's code to the model.</summary>
    public SummarySource Summaries { get; set; }

    public string OllamaUrl { get; set; } = DefaultOllamaUrl;

    public string OllamaModel { get; set; } = DefaultOllamaModel;

    public static CodeMapSettings Load(string file)
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<CodeMapSettings>(File.ReadAllText(file), Options) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    public void Save(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(this, Options));
    }
}

/// <summary>
/// Writes the map's summaries: a sentence for each file of code, then one for each folder from its files' and folders'
/// (the workspace's own last). Each is written for what it was written from (a file's hash, a folder's children), so only
/// what changed is written again. Files go to the model a batch at a time (their declarations and their start), folders
/// a level at a time; the model answers a line for each, its path, a tab and the sentence.
/// </summary>
/// <param name="ask">The model: a prompt's answer.</param>
public sealed class CodeSummarizer(CodeMapStore store, Func<string, CancellationToken, Task<string>> ask)
{
    /// <summary>A batch's files at most, and the characters of the prompt at most.</summary>
    const int BatchFiles = 20, BatchChars = 30_000;

    /// <summary>A file's characters a prompt gives at most (its start), and its declarations.</summary>
    const int FileChars = 2_400, FileDeclarations = 30;

    const string FileInstructions =
        "Below are source files of a code base. For each, write one sentence of at most 20 words saying what the file is for, "
        + "as a developer needs to know before opening it; do not start with \"This file\". Answer with one line per file and "
        + "nothing else: the file's path exactly as given, a tab, and the sentence.";

    const string FolderInstructions =
        "Below are folders of a code base, each with what its files and folders are for. For each folder, write one sentence of at "
        + "most 25 words saying what it is for; do not start with \"This folder\". Answer with one line per folder and nothing else: "
        + "the folder's path exactly as given (/ for the root), a tab, and the sentence.";

    /// <summary>The summaries written and to write in this run.</summary>
    public (int Done, int Total) Progress { get; private set; }

    /// <summary>Why the last run stopped, or null.</summary>
    public string? Problem { get; private set; }

    public event Action? ProgressChanged;

    /// <summary>Files that get a summary: code (of a language the map knows, or one a server outlined), and a README.</summary>
    public static bool IsSummarized(MapFile file) =>
        file.Hash.Length > 0 && (CodeText.IsCode(file.Language) || file.Outlined
            || Path.GetFileNameWithoutExtension(file.Path).Equals("README", StringComparison.OrdinalIgnoreCase));

    /// <summary>The summaries still to write: files whose summary is for another version of them.</summary>
    public static int Pending(IReadOnlyDictionary<string, MapFile> files) => files.Values.Count(f => IsSummarized(f) && f.SummaryHash != f.Hash);

    /// <summary>The summaries the map's files and folders lack (or that are for what they were), written; the model's errors stop it and say why.</summary>
    public async Task RunAsync(string root, CancellationToken cancellationToken)
    {
        Problem = null;
        try
        {
            var files = store.Files();
            var todo = files.Values.Where(f => IsSummarized(f) && f.SummaryHash != f.Hash).OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
            Progress = (0, todo.Count);
            ProgressChanged?.Invoke();
            foreach (var batch in Batches(todo, f => FilePrompt(root, f)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var answer = await ask(FileInstructions + "\n\n" + string.Join("\n\n", batch.Select(b => b.Prompt)), cancellationToken);
                var sentences = Lines(answer, batch.Select(b => b.Item.Path));
                foreach (var (file, _) in batch)
                    // A file the model left out gets its name, so it is not asked again until it changes.
                    store.SetSummary(file.Path, file.Hash, sentences.GetValueOrDefault(file.Path) ?? "");
                Progress = (Progress.Done + batch.Count, Progress.Total);
                ProgressChanged?.Invoke();
            }
            await FoldersAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Problem = ex.Message;
            ProgressChanged?.Invoke();
        }
    }

    /// <summary>The folders' summaries, the deepest first, from their files' and folders' as they are now.</summary>
    async Task FoldersAsync(CancellationToken cancellationToken)
    {
        var files = store.Files().Values.Where(f => f.Summary is { Length: > 0 }).ToList();
        var folders = store.Folders();
        var children = new Dictionary<string, SortedDictionary<string, string>>(StringComparer.Ordinal);
        void Add(string folder, string entry, string summary)
        {
            if (!children.TryGetValue(folder, out var entries))
                children[folder] = entries = new SortedDictionary<string, string>(StringComparer.Ordinal);
            entries[entry] = summary;
        }
        foreach (var file in files)
            Add(Parent(file.Path), Path.GetFileName(file.Path), file.Summary!);
        // Every folder above a summarized file, so that each has its entry in its parent.
        foreach (var folder in children.Keys.ToList())
            for (var f = folder; f.Length > 0; f = Parent(f))
                Add(Parent(f), Path.GetFileName(f) + "/", "");
        foreach (var gone in folders.Keys.Where(f => !children.ContainsKey(f)))
            store.RemoveFolder(gone);
        var levels = children.Keys.GroupBy(f => f.Length == 0 ? 0 : f.Count(c => c == '/') + 1).OrderByDescending(g => g.Key);
        var written = store.Folders().ToDictionary(f => f.Key, f => f.Value.Summary ?? "", StringComparer.Ordinal);
        foreach (var level in levels)
        {
            var todo = new List<(string Folder, string Hash, string Entries)>();
            foreach (var folder in level.OrderBy(f => f, StringComparer.Ordinal))
            {
                var entries = string.Join("\n", children[folder].Take(60).Select(e =>
                    e.Key.EndsWith('/') ? $"{e.Key} — {written.GetValueOrDefault(Join(folder, e.Key.TrimEnd('/')), "")}" : $"{e.Key} — {e.Value}"));
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(entries)), 0, 12);
                if (folders.GetValueOrDefault(folder)?.SummaryHash != hash)
                    todo.Add((folder, hash, entries));
            }
            Progress = (Progress.Done, Progress.Total + todo.Count);
            foreach (var batch in Batches(todo, t => $"<folder path=\"{(t.Folder.Length == 0 ? "/" : t.Folder)}\">\n{t.Entries}\n</folder>"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var answer = await ask(FolderInstructions + "\n\n" + string.Join("\n\n", batch.Select(b => b.Prompt)), cancellationToken);
                var sentences = Lines(answer, batch.Select(b => b.Item.Folder.Length == 0 ? "/" : b.Item.Folder));
                foreach (var (item, _) in batch)
                {
                    var summary = sentences.GetValueOrDefault(item.Folder.Length == 0 ? "/" : item.Folder) ?? "";
                    store.SetFolderSummary(item.Folder, item.Hash, summary);
                    written[item.Folder] = summary;
                }
                Progress = (Progress.Done + batch.Count, Progress.Total);
                ProgressChanged?.Invoke();
            }
        }
    }

    static string Parent(string path) => path.LastIndexOf('/') is var slash and > 0 ? path[..slash] : "";

    static string Join(string folder, string name) => folder.Length == 0 ? name : folder + "/" + name;

    /// <summary>A file as a prompt gives it: its declarations, then its start.</summary>
    string FilePrompt(string root, MapFile file)
    {
        string text;
        try
        {
            using var reader = new StreamReader(Path.Combine(root, file.Path));
            var buffer = new char[FileChars];
            var read = reader.ReadBlock(buffer, 0, FileChars);
            text = new string(buffer, 0, read) + (reader.EndOfStream ? "" : "\n…");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            text = "";
        }
        var declarations = store.Symbols(file.Path).Where(s => s.Depth <= 1).Take(FileDeclarations)
            .Select(s => $"{s.Kind} {(s.Container is { } c ? c + "." : "")}{s.Name}").ToList();
        return $"<file path=\"{file.Path}\"{(file.Language is { } l ? $" language=\"{l}\"" : "")}>\n"
            + (declarations.Count > 0 ? "Declarations: " + string.Join("; ", declarations) + "\n" : "")
            + text + "\n</file>";
    }

    /// <summary>Items in batches of a prompt's size.</summary>
    static IEnumerable<List<(T Item, string Prompt)>> Batches<T>(IEnumerable<T> items, Func<T, string> prompt)
    {
        var batch = new List<(T, string)>();
        var chars = 0;
        foreach (var item in items)
        {
            var text = prompt(item);
            if (batch.Count > 0 && (batch.Count == BatchFiles || chars + text.Length > BatchChars))
            {
                yield return batch;
                batch = [];
                chars = 0;
            }
            batch.Add((item, text));
            chars += text.Length;
        }
        if (batch.Count > 0)
            yield return batch;
    }

    /// <summary>The model's lines, by the paths asked for: a path, then a tab (or a colon, a dash), then the sentence.</summary>
    internal static Dictionary<string, string> Lines(string answer, IEnumerable<string> paths)
    {
        var wanted = paths.OrderByDescending(p => p.Length).ToList();
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in answer.Split('\n'))
        {
            var line = raw.Trim().TrimStart('-', '*', ' ').Replace("`", "").Replace("**", "");
            foreach (var path in wanted)
            {
                if (!line.StartsWith(path, StringComparison.Ordinal) || found.ContainsKey(path))
                    continue;
                var rest = line[path.Length..];
                if (rest.Length > 0 && !char.IsWhiteSpace(rest[0]) && rest[0] is not (':' or '-' or '–' or '—' or '|'))
                    continue;
                var sentence = rest.TrimStart(' ', '\t', ':', '-', '–', '—', '|').Trim().Trim('"');
                if (sentence.Length > 0)
                    found[path] = sentence.Length > 240 ? sentence[..239].TrimEnd() + "…" : sentence;
                break;
            }
        }
        return found;
    }
}
