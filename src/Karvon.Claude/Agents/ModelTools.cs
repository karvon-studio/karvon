using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Karvon.ViewModels;

namespace Karvon.Claude;

/// <summary>
/// The tools a model Karvon drives itself works with, named and shaped as Claude Code's (Read, Write, Edit, Glob, Grep, Bash
/// or PowerShell), so that the panel shows their cards, their diffs and their questions as it shows Claude's, and Karvon's
/// secret guard reads their calls as it reads Claude's.
/// </summary>
public static class ModelTools
{
    /// <summary>The shell's tool: PowerShell on Windows, Bash elsewhere.</summary>
    public static string Shell => OperatingSystem.IsWindows() ? "PowerShell" : "Bash";

    /// <summary>The tools that change files (asked for unless edits are accepted).</summary>
    public static readonly IReadOnlySet<string> Changing = new HashSet<string>(StringComparer.Ordinal) { "Write", "Edit" };

    static readonly string[] Skipped = [".git", "node_modules", "bin", "obj", ".vs", ".idea", "dist", "build", "target", "__pycache__", ".venv"];

    const int MaxOutput = 30_000;

    /// <summary>The functions' definitions, as Chat Completions takes them.</summary>
    public static IEnumerable<JsonObject> Definitions()
    {
        yield return Function("Read", "Reads a file of the workspace; its lines come numbered. Read a file before you edit it.",
            Schema(("file_path", "string", "The file's absolute path", true), ("offset", "integer", "The first line to read (1-based)", false),
                ("limit", "integer", "How many lines to read (2000 at most)", false)));
        yield return Function("Write", "Writes a whole file (a new one, or one rewritten from scratch). To change part of a file use Edit.",
            Schema(("file_path", "string", "The file's absolute path", true), ("content", "string", "The file's whole new text", true)));
        yield return Function("Edit", "Replaces text in a file: old_string must be found exactly once (with its indentation), unless replace_all.",
            Schema(("file_path", "string", "The file's absolute path", true), ("old_string", "string", "The exact text to replace", true),
                ("new_string", "string", "The text to put in its place", true), ("replace_all", "boolean", "Replace every occurrence", false)));
        yield return Function("Glob", "Finds files by a glob pattern (**/*.cs, src/**/test_*.py).",
            Schema(("pattern", "string", "The glob pattern", true), ("path", "string", "The folder to search in (the workspace by default)", false)));
        yield return Function("Grep", "Searches files' text for a regular expression; matches come as path:line:text.",
            Schema(("pattern", "string", "The regular expression", true), ("path", "string", "A folder or file to search (the workspace by default)", false),
                ("glob", "string", "Only the files whose names match this glob (*.cs)", false)));
        yield return Function(Shell, OperatingSystem.IsWindows()
                ? "Runs a PowerShell command in the workspace and gives its output (two minutes at most)."
                : "Runs a bash command in the workspace and gives its output (two minutes at most).",
            Schema(("command", "string", "The command", true), ("description", "string", "What it does, in a few words", false)));
    }

    static JsonObject Function(string name, string description, JsonObject parameters) => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject { ["name"] = name, ["description"] = description, ["parameters"] = parameters },
    };

    static JsonObject Schema(params (string Name, string Type, string Description, bool Required)[] properties) => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject(properties.Select(p => KeyValuePair.Create(p.Name, (JsonNode?)new JsonObject { ["type"] = p.Type, ["description"] = p.Description }))),
        ["required"] = new JsonArray([.. properties.Where(p => p.Required).Select(p => (JsonNode)p.Name)]),
    };

    /// <summary>Runs one of these tools: its output and whether it failed.</summary>
    public static async Task<(string Output, bool IsError)> RunAsync(string name, JsonElement input, string workspace, CancellationToken cancellationToken)
    {
        try
        {
            return name switch
            {
                "Read" => (Read(Path(input, "file_path", workspace), Int(input, "offset"), Int(input, "limit")), false),
                "Write" => (Write(Path(input, "file_path", workspace), Required(input, "content")), false),
                "Edit" => (Edit(Path(input, "file_path", workspace), Required(input, "old_string"), Required(input, "new_string"), ClaudeJson.Bool(input, "replace_all")), false),
                "Glob" => (Glob(Required(input, "pattern"), Folder(input, workspace)), false),
                "Grep" => (Grep(Required(input, "pattern"), Folder(input, workspace), ClaudeJson.Str(input, "glob")), false),
                "Bash" or "PowerShell" => await RunCommandAsync(Required(input, "command"), workspace, cancellationToken),
                _ => ($"There is no tool named {name}.", true),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or RegexParseException)
        {
            return (ex.Message, true);
        }
    }

    static string Required(JsonElement input, string name) =>
        ClaudeJson.Str(input, name) ?? throw new ArgumentException($"{name} is missing.");

    static int? Int(JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var value) && value.TryGetInt32(out var n) ? n : null;

    static string Path(JsonElement input, string name, string workspace) => System.IO.Path.GetFullPath(Required(input, name), workspace);

    static string Folder(JsonElement input, string workspace) =>
        ClaudeJson.Str(input, "path") is { Length: > 0 } path ? System.IO.Path.GetFullPath(path, workspace) : workspace;

    static string Read(string file, int? offset, int? limit)
    {
        if (Directory.Exists(file))
            return $"{file} is a folder.";
        var lines = File.ReadAllLines(file);
        var first = Math.Max(1, offset ?? 1);
        var count = Math.Clamp(limit ?? 2000, 1, 2000);
        var shown = lines.Skip(first - 1).Take(count).Select((l, i) => $"{first + i,6}\t{(l.Length > 2000 ? l[..2000] + "…" : l)}");
        var text = string.Join("\n", shown);
        if (lines.Length == 0)
            return "(The file is empty.)";
        return first - 1 + count < lines.Length ? text + $"\n… {lines.Length - (first - 1 + count)} more lines (read on with offset)" : text;
    }

    static string Write(string file, string content)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        var existed = File.Exists(file);
        File.WriteAllText(file, content);
        return existed ? $"Wrote {file}." : $"Created {file}.";
    }

    static string Edit(string file, string oldString, string newString, bool replaceAll)
    {
        var text = File.ReadAllText(file);
        if (oldString.Length == 0)
            throw new ArgumentException("old_string is empty: to write a whole file use Write.");
        // Files with Windows line ends are edited with the model's \n too.
        var crlf = text.Contains("\r\n", StringComparison.Ordinal) && !oldString.Contains("\r\n", StringComparison.Ordinal);
        if (crlf)
        {
            oldString = oldString.Replace("\n", "\r\n", StringComparison.Ordinal);
            newString = newString.Replace("\n", "\r\n", StringComparison.Ordinal);
        }
        var count = Occurrences(text, oldString);
        if (count == 0)
            throw new ArgumentException("old_string was not found in the file. Read it again and copy the text exactly, with its indentation.");
        if (count > 1 && !replaceAll)
            throw new ArgumentException($"old_string was found {count} times: give more of the text around it, or set replace_all.");
        var changed = replaceAll ? text.Replace(oldString, newString, StringComparison.Ordinal) : ReplaceFirst(text, oldString, newString);
        File.WriteAllText(file, changed);
        return replaceAll && count > 1 ? $"Replaced {count} occurrences in {file}." : $"Edited {file}.";
    }

    static int Occurrences(string text, string part)
    {
        var count = 0;
        for (var at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    static string ReplaceFirst(string text, string oldString, string newString)
    {
        var at = text.IndexOf(oldString, StringComparison.Ordinal);
        return text[..at] + newString + text[(at + oldString.Length)..];
    }

    /// <summary>A glob as a regular expression over '/'-separated relative paths (** any folders, * and ? within a name).</summary>
    internal static Regex GlobRegex(string glob)
    {
        var pattern = new StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                var slash = i + 2 < glob.Length && glob[i + 2] == '/';
                pattern.Append(slash ? "(?:.*/)?" : ".*");
                i += slash ? 2 : 1;
            }
            else if (c == '*')
                pattern.Append("[^/]*");
            else if (c == '?')
                pattern.Append("[^/]");
            else
                pattern.Append(Regex.Escape(c.ToString()));
        }
        return new Regex(pattern.Append('$').ToString(), OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase : RegexOptions.None);
    }

    static IEnumerable<string> Files(string folder)
    {
        var pending = new Stack<string>([folder]);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            IEnumerable<string> files, folders;
            try
            {
                files = Directory.EnumerateFiles(current).ToList();
                folders = Directory.EnumerateDirectories(current).Where(d => !Skipped.Contains(System.IO.Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var file in files)
                yield return file;
            foreach (var sub in folders.Reverse())
                pending.Push(sub);
        }
    }

    static string Relative(string folder, string file) => System.IO.Path.GetRelativePath(folder, file).Replace('\\', '/');

    static string Glob(string pattern, string folder)
    {
        var regex = GlobRegex(pattern.Replace('\\', '/').TrimStart('/'));
        var found = Files(folder).Where(f => regex.IsMatch(Relative(folder, f))).Take(500).ToList();
        return found.Count == 0 ? "No files found" : string.Join("\n", found);
    }

    static string Grep(string pattern, string path, string? glob)
    {
        var regex = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(2));
        var names = glob is { Length: > 0 } ? GlobRegex(glob.Contains('/') ? glob : "**/" + glob) : null;
        var files = File.Exists(path) ? [path] : Files(path).Where(f => names is null || names.IsMatch(Relative(path, f)));
        var matches = new List<string>();
        foreach (var file in files)
        {
            if (matches.Count >= 200)
                break;
            string[] lines;
            try
            {
                if (new FileInfo(file).Length > 2_000_000)
                    continue;
                lines = File.ReadAllLines(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            // Binary files hold NUL characters: not searched.
            if (lines.Any(l => l.Contains('\0')))
                continue;
            for (var i = 0; i < lines.Length && matches.Count < 200; i++)
                if (regex.IsMatch(lines[i]))
                    matches.Add($"{file}:{i + 1}:{(lines[i].Length > 300 ? lines[i][..300] + "…" : lines[i])}");
        }
        return matches.Count == 0 ? "No matches" : string.Join("\n", matches) + (matches.Count >= 200 ? "\n… (more matches: narrow the search)" : "");
    }

    static async Task<(string, bool)> RunCommandAsync(string command, string workspace, CancellationToken cancellationToken)
    {
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("powershell.exe") { ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", command } }
            : new ProcessStartInfo("/bin/bash") { ArgumentList = { "-c", command } };
        psi.WorkingDirectory = workspace;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.RedirectStandardInput = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        // The shell's environment has none of the secrets Karvon's has.
        ClaudeEnvironment.Clean(psi.Environment, []);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("The shell could not be started.");
        process.StandardInput.Close();
        var output = new StringBuilder();
        void Add(string? line)
        {
            if (line is null)
                return;
            lock (output)
                if (output.Length < MaxOutput * 2)
                    output.AppendLine(line);
        }
        process.OutputDataReceived += (_, e) => Add(e.Data);
        process.ErrorDataReceived += (_, e) => Add(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            if (cancellationToken.IsCancellationRequested)
                throw;
            return (Cut(output.ToString()) + "\n(Stopped: it ran for two minutes.)", true);
        }
        await process.WaitForExitAsync(CancellationToken.None);
        var text = Cut(output.ToString().TrimEnd());
        return process.ExitCode == 0 ? (text, false) : ((text.Length > 0 ? text + "\n" : "") + $"Exit code {process.ExitCode}", true);
    }

    static string Cut(string text) => text.Length > MaxOutput ? text[..(MaxOutput / 2)] + "\n… (output cut) …\n" + text[^(MaxOutput / 2)..] : text;
}
