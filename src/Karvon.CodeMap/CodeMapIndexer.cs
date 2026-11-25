using System.Diagnostics;
using System.Security.Cryptography;
using Karvon.Lsp;

namespace Karvon.CodeMap;

/// <summary>
/// Keeps a workspace's map as the workspace is: a scan lists its files (git's, without the ignored ones; else the folder's,
/// without build output and dependencies; files that are not text by their names), compares each with the map by its size
/// and time (then by its hash), and reads
/// again only the files that changed: their declarations from their language server's outline, with their doc comments.
/// A scan is cheap (a few milliseconds for a thousand files that did not change), so it runs whenever the map is about to be
/// read, after a save or a git change, and while the window shows; scans asked for while one runs make one more after it.
/// </summary>
public sealed class CodeMapIndexer(string root, CodeMapStore store, Func<string, CancellationToken, Task<IReadOnlyList<LspDocumentSymbol>?>> outline)
{
    /// <summary>Files larger than this are listed in the map, not read (generated code, data).</summary>
    const long MaxFileSize = 2 * 1024 * 1024;

    /// <summary>The declarations of a file the map keeps at most.</summary>
    const int MaxSymbols = 600;

    /// <summary>How long one file's outline is waited for.</summary>
    static readonly TimeSpan OutlineTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Folders never listed, whether git tracks what is in them or not: dependencies, tools' own.</summary>
    static readonly HashSet<string> NeverListed = new(StringComparer.Ordinal)
    {
        ".git", ".hg", ".svn", ".vs", ".vscode-test", "obj", "node_modules", "__pycache__", ".venv", "venv",
        ".next", ".nuxt", ".gradle", ".pytest_cache", ".mypy_cache",
    };

    /// <summary>Folders not listed unless git tracks what is in them: those, and build output and tools' own by their usual names.</summary>
    public static readonly IReadOnlySet<string> SkippedFolders = new HashSet<string>(NeverListed, StringComparer.Ordinal)
    {
        ".idea", "bin", "target", "dist", "coverage",
    };

    /// <summary>Files listed but never read: lock files, minified and generated ones.</summary>
    static bool IsSkippedFile(string name) =>
        name is "package-lock.json" or "yarn.lock" or "pnpm-lock.yaml" or "Cargo.lock" or "poetry.lock" or "packages.lock.json" or "go.sum"
        || name.EndsWith(".min.js", StringComparison.Ordinal) || name.EndsWith(".min.css", StringComparison.Ordinal)
        || name.EndsWith(".map", StringComparison.Ordinal);

    /// <summary>How long a file whose outline did not come (its server busy or failing) waits before it is asked again.</summary>
    static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(2);

    /// <summary>Outlines that did not come in a row: after this many, a scan asks no more (the server is not answering).</summary>
    const int MaxFailures = 3;

    readonly Lock _lock = new();
    readonly CancellationTokenSource _stopping = new();

    /// <summary>Files whose language no server serves this session: their outline is not asked for again until they change.</summary>
    readonly HashSet<string> _unserved = new(StringComparer.Ordinal);

    /// <summary>Files whose outline did not come (or whose server was not running), and when: asked again a while later.</summary>
    readonly Dictionary<string, DateTime> _failed = new(StringComparer.Ordinal);

    /// <summary>How long a server that is not running waits before its files are asked about again (it may have started).</summary>
    static readonly TimeSpan WaitForServer = TimeSpan.FromSeconds(20);

    /// <summary>Files whose server was not running, by the server; and when each server's files are asked about again.</summary>
    readonly Dictionary<string, string> _waiting = new(StringComparer.Ordinal);
    readonly Dictionary<string, DateTime> _serverRetry = new(StringComparer.Ordinal);

    /// <summary>The server not running of each extension seen: its other files are not asked about while it waits.</summary>
    readonly Dictionary<string, string> _serverOfExtension = new(StringComparer.OrdinalIgnoreCase);

    volatile bool _retryServers;

    int _failuresInRow;

    Task? _running;
    bool _again;

    public string Root { get; } = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    public CodeMapStore Store => store;

    /// <summary>Whether the map was made once at least (a scan went to its end).</summary>
    public bool IsComplete { get; private set; }

    /// <summary>A scan's progress: the files read so far, and how many it has to read.</summary>
    public (int Done, int Total) Progress { get; private set; }

    public bool IsScanning => _running is { IsCompleted: false };

    /// <summary>The scan running, or none: done when it is.</summary>
    public Task Idle => _running ?? Task.CompletedTask;

    /// <summary>No scan from now on (the workspace closes); the one running stops at its next file.</summary>
    public void Stop() => _stopping.Cancel();

    /// <summary>A server may have started (a file was opened): the files waiting for theirs are asked about at the next scan.</summary>
    public void RetryWaiting() => _retryServers = true;

    /// <summary>A scan changed the map (files read, gone): the window follows. Raised on the scan's thread.</summary>
    public event Action? Changed;

    /// <summary>A scan's progress changed. Raised on the scan's thread.</summary>
    public event Action? ProgressChanged;

    /// <summary>
    /// A scan now, or one more after the one running; done when the map is as the workspace was when this was asked (or the
    /// token says to stop waiting; the scan goes on).
    /// </summary>
    public Task ScanAsync(CancellationToken cancellationToken = default)
    {
        Task run;
        lock (_lock)
        {
            if (_stopping.IsCancellationRequested)
                return Task.CompletedTask;
            if (_running is { IsCompleted: false } running)
            {
                _again = true;
                run = running;
            }
            else
                run = _running = Task.Run(RunAsync);
        }
        return run.WaitAsync(cancellationToken);
    }

    async Task RunAsync()
    {
        while (true)
        {
            lock (_lock)
                _again = false;
            try
            {
                await ScanOnceAsync();
            }
            catch (Exception ex) when (_stopping.IsCancellationRequested
                || ex is ObjectDisposedException or OperationCanceledException or Microsoft.Data.Sqlite.SqliteException)
            {
                // Stopped (the workspace closed), or the map's file could not be written: the next scan tries again.
                return;
            }
            lock (_lock)
                if (!_again || _stopping.IsCancellationRequested)
                    return;
        }
    }

    async Task ScanOnceAsync()
    {
        var listed = ListFiles();
        var known = store.Files();
        var first = !IsComplete;
        var changed = false;
        _failuresInRow = 0;
        if (_retryServers)
        {
            _retryServers = false;
            _serverRetry.Clear();
        }
        foreach (var gone in known.Keys.Where(p => !listed.Contains(p)).ToList())
        {
            store.Remove(gone);
            changed = true;
        }
        var toRead = new List<(string Path, FileInfo Info)>();
        foreach (var path in listed)
        {
            var info = new FileInfo(Path.Combine(Root, path));
            if (!info.Exists)
                continue;
            if (known.TryGetValue(path, out var file) && file.Size == info.Length && file.Modified == info.LastWriteTimeUtc.Ticks && !NeedsOutline(file))
                continue;
            toRead.Add((path, info));
        }
        Progress = (0, toRead.Count);
        ProgressChanged?.Invoke();
        for (var i = 0; i < toRead.Count; i++)
        {
            _stopping.Token.ThrowIfCancellationRequested();
            var (path, info) = toRead[i];
            // A file read again only for its outline, whose server this scan found not running: left as it is.
            if (known.TryGetValue(path, out var unchanged) && unchanged.Size == info.Length && unchanged.Modified == info.LastWriteTimeUtc.Ticks
                && !NeedsOutline(unchanged))
                continue;
            try
            {
                if (await ReadAsync(path, info, known.GetValueOrDefault(path)))
                    changed = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Gone or locked meanwhile: the next scan sees it.
            }
            Progress = (i + 1, toRead.Count);
            if (i % 10 == 9 || i == toRead.Count - 1)
                ProgressChanged?.Invoke();
        }
        IsComplete = true;
        Progress = (0, 0);
        ProgressChanged?.Invoke();
        if (changed || first)
            Changed?.Invoke();
    }

    /// <summary>A file of code whose outline did not come last time, its server not known to be missing: asked again, a while after.</summary>
    bool NeedsOutline(MapFile file) =>
        !CodeText.IsData(file.Language) && !file.Outlined && file.Hash.Length > 0 && !_unserved.Contains(file.Path)
        && (!_failed.TryGetValue(file.Path, out var at) || DateTime.UtcNow > at)
        && (!_waiting.TryGetValue(file.Path, out var server) || DateTime.UtcNow > _serverRetry.GetValueOrDefault(server));

    /// <summary>A file read into the map (or listed only, when it is too large or not text): whether the map changed.</summary>
    async Task<bool> ReadAsync(string path, FileInfo info, MapFile? before)
    {
        var language = CodeText.Language(path);
        var bytes = info.Length > MaxFileSize || IsSkippedFile(Path.GetFileName(path)) ? null : await File.ReadAllBytesAsync(info.FullName);
        if (bytes is null || Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, 8000)) >= 0)
        {
            store.Put(new MapFile(path, info.Length, info.LastWriteTimeUtc.Ticks, "", language, 0, false), []);
            return before is null || before.IsRead;
        }
        var hash = Convert.ToHexString(SHA256.HashData(bytes), 0, 12);
        if (before is not null && before.Hash != hash)
            _unserved.Remove(path);
        // Touched, not changed: the same declarations, a new time.
        if (before is not null && before.Hash == hash && !NeedsOutline(before))
        {
            store.Put(before with { Size = info.Length, Modified = info.LastWriteTimeUtc.Ticks }, store.Symbols(path));
            return false;
        }
        var lines = System.Text.Encoding.UTF8.GetString(bytes).Split('\n');
        var symbols = new List<MapSymbol>();
        var outlined = false;
        var extension = Path.GetExtension(path);
        // A file of an extension whose server was found not running a moment ago waits with the others.
        if (extension.Length > 0 && _serverOfExtension.TryGetValue(extension, out var waitingFor) && DateTime.UtcNow < _serverRetry.GetValueOrDefault(waitingFor))
            _waiting[path] = waitingFor;
        // Any file but data and prose: a module's server may serve a language the map does not name.
        else if (!CodeText.IsData(language) && !_unserved.Contains(path) && _failuresInRow < MaxFailures)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
                timeout.CancelAfter(OutlineTimeout);
                if (await outline(info.FullName, timeout.Token) is { } found)
                {
                    Flatten(found, null, 0, symbols, lines);
                    outlined = true;
                    _failed.Remove(path);
                    _waiting.Remove(path);
                    _failuresInRow = 0;
                }
                else
                    _unserved.Add(path);
            }
            catch (LanguageServerNotRunningException ex)
            {
                // Read once its server runs (the user opened a file of its language, or Claude asked about one).
                _waiting[path] = ex.Server;
                _serverRetry[ex.Server] = DateTime.UtcNow + WaitForServer;
                if (extension.Length > 0)
                    _serverOfExtension[extension] = ex.Server;
            }
            catch (Exception ex) when (ex is OperationCanceledException or LspException or IOException or InvalidOperationException)
            {
                // The server did not answer: asked again a while later.
                _failed[path] = DateTime.UtcNow + RetryAfter;
                _failuresInRow++;
            }
        }
        // Asked again for its outline, which did not come: the map has what it had.
        if (before is not null && before.Hash == hash && !outlined)
        {
            if (before.Size != info.Length || before.Modified != info.LastWriteTimeUtc.Ticks)
                store.Put(before with { Size = info.Length, Modified = info.LastWriteTimeUtc.Ticks }, store.Symbols(path));
            return false;
        }
        store.Put(new MapFile(path, info.Length, info.LastWriteTimeUtc.Ticks, hash, language, lines.Length, outlined), symbols);
        return true;
    }

    /// <summary>
    /// An outline as the map keeps it: the types and their members, a namespace's or module's declarations as its own, not
    /// what is inside a function (its locals); a member's container is the names of the types it is in.
    /// </summary>
    static void Flatten(IReadOnlyList<LspDocumentSymbol> level, string? container, int depth, List<MapSymbol> into, string[] lines)
    {
        foreach (var symbol in level)
        {
            if (into.Count >= MaxSymbols)
                return;
            if (symbol.Kind is LspSymbolKind.Namespace or LspSymbolKind.Package or LspSymbolKind.Module or LspSymbolKind.File)
            {
                Flatten(symbol.Children, container, depth, into, lines);
                continue;
            }
            if (depth > 0 && IsValue(symbol.Kind))
                continue;
            var name = BaseName(symbol.Name);
            var start = symbol.Range.Start.Line;
            into.Add(new MapSymbol(Kind(symbol.Kind), name, container, Signature(symbol), symbol.SelectionRange.Start.Line + 1, symbol.Range.End.Line + 1,
                CodeText.Doc(lines, start, symbol.Range.End.Line), depth));
            if (depth < 3 && !IsCallable(symbol.Kind))
                Flatten(symbol.Children, container is null ? name : container + "." + name, depth + 1, into, lines);
        }
    }

    static bool IsCallable(LspSymbolKind kind) => kind is LspSymbolKind.Method or LspSymbolKind.Function or LspSymbolKind.Constructor or LspSymbolKind.Operator;

    static bool IsValue(LspSymbolKind kind) => kind is LspSymbolKind.Variable or LspSymbolKind.String or LspSymbolKind.Number or LspSymbolKind.Boolean
        or LspSymbolKind.Array or LspSymbolKind.Object or LspSymbolKind.Key or LspSymbolKind.Null or LspSymbolKind.TypeParameter;

    static string Kind(LspSymbolKind kind) => kind switch
    {
        LspSymbolKind.EnumMember => "enum member",
        _ => kind.ToString().ToLowerInvariant(),
    };

    /// <summary>A declaration's name without its parameters, type parameters or type (Roslyn's: Add(int, int) : int).</summary>
    static string BaseName(string name)
    {
        var end = name.IndexOfAny(['(', '<', '[', ' ', ':']);
        return (end > 0 ? name[..end] : name).Trim();
    }

    /// <summary>A declaration's signature: Roslyn's name has it (Add(int, int) : int), other servers' detail.</summary>
    static string? Signature(LspDocumentSymbol symbol)
    {
        var name = symbol.Name.Trim();
        if (name.Length > BaseName(name).Length)
            return name.Length > 300 ? name[..299] + "…" : name;
        return symbol.Detail is { Length: > 0 } detail ? (detail.Length > 300 ? detail[..299] + "…" : detail) : null;
    }

    // ---- The workspace's files --------------------------------------------------------------------------------

    /// <summary>The workspace's files, relative with /: git's (tracked, and new ones not ignored) in a repository, else the folder's.</summary>
    HashSet<string> ListFiles()
    {
        var files = new HashSet<string>(StringComparer.Ordinal);
        return AddGitFiles(Root, "", files) ? files : WalkFiles();
    }

    /// <summary>
    /// git's files of a folder, its submodules' too: the tracked ones wherever they are (a project's own dist or bin), and
    /// new ones that are not ignored, without build output. False when git does not say (no git, not a repository).
    /// </summary>
    bool AddGitFiles(string folder, string prefix, HashSet<string> files)
    {
        if (Git(folder, "ls-files", "-z", "--stage") is not { } tracked || Git(folder, "ls-files", "-z", "--others", "--exclude-standard") is not { } others)
            return false;
        void Add(string path, IReadOnlySet<string> skipped)
        {
            if (!InFolder(prefix + path, skipped) && File.Exists(Path.Combine(folder, path)))
                files.Add(prefix + path);
        }
        foreach (var entry in tracked)
        {
            // The mode, the object, the stage, a tab and the path; a submodule's mode is 160000, and its files are its repository's.
            var path = entry[(entry.IndexOf('\t') + 1)..];
            if (!entry.StartsWith("160000 ", StringComparison.Ordinal))
                Add(path, NeverListed);
            else if (Path.Exists(Path.Combine(folder, path, ".git")))
                AddGitFiles(Path.Combine(folder, path), prefix + path + "/", files);
        }
        foreach (var path in others)
            Add(path, SkippedFolders);
        return true;
    }

    static bool InFolder(string path, IReadOnlySet<string> folders) => path.Split('/').SkipLast(1).Any(folders.Contains);

    /// <summary>What git prints in a folder, at each NUL; null when it cannot.</summary>
    static string[]? Git(string folder, params string[] arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = folder,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in (string[])["-c", "core.quotepath=off", .. arguments])
                psi.ArgumentList.Add(argument);
            using var process = Process.Start(psi);
            if (process is null)
                return null;
            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30_000))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }
            return process.ExitCode == 0 ? output.Result.Split('\0', StringSplitOptions.RemoveEmptyEntries) : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    HashSet<string> WalkFiles()
    {
        var files = new HashSet<string>(StringComparer.Ordinal);
        var folders = new Stack<string>([Root]);
        while (folders.Count > 0)
        {
            var folder = folders.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(folder))
                    files.Add(Path.GetRelativePath(Root, file).Replace('\\', '/'));
                foreach (var sub in Directory.EnumerateDirectories(folder))
                    if (!SkippedFolders.Contains(Path.GetFileName(sub)) && !new DirectoryInfo(sub).Attributes.HasFlag(FileAttributes.ReparsePoint))
                        folders.Push(sub);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return files;
    }
}
