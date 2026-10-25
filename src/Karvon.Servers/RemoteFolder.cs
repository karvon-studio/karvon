using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Karvon.Servers;

/// <summary>A file or folder of a machine, as its folder's listing says.</summary>
public sealed record RemoteFileInfo(string Name, bool IsFolder, long Size, DateTime Modified, bool IsLink = false);

/// <summary>A machine's files, as a remote folder reads and writes them: over SFTP (the tests': a folder here).</summary>
public interface IRemoteFiles
{
    Task<IReadOnlyList<RemoteFileInfo>> ListAsync(string folder, CancellationToken ct);

    /// <summary>A file's size and time there; null when there is none.</summary>
    Task<RemoteFileInfo?> InfoAsync(string path, CancellationToken ct);

    Task DownloadAsync(string path, Stream to, CancellationToken ct);

    /// <summary>A file written there; its time there after.</summary>
    Task<DateTime> UploadAsync(Stream from, string path, CancellationToken ct);

    Task DeleteAsync(string path, CancellationToken ct);

    Task CreateFolderAsync(string path, CancellationToken ct);
}

/// <summary>A machine's files over the Servers' SFTP connection.</summary>
sealed class SftpRemoteFiles(SftpClient sftp) : IRemoteFiles
{
    public async Task<IReadOnlyList<RemoteFileInfo>> ListAsync(string folder, CancellationToken ct)
    {
        var all = new List<RemoteFileInfo>();
        await foreach (var file in sftp.ListDirectoryAsync(folder, ct))
            if (file.Name is not ("." or ".."))
                all.Add(new RemoteFileInfo(file.Name, file.IsDirectory, file.Length, file.LastWriteTimeUtc, file.IsSymbolicLink));
        return all;
    }

    public async Task<RemoteFileInfo?> InfoAsync(string path, CancellationToken ct)
    {
        if (!await sftp.ExistsAsync(path, ct))
            return null;
        var attributes = await sftp.GetAttributesAsync(path, ct);
        return new RemoteFileInfo(path[(path.LastIndexOf('/') + 1)..], attributes.IsDirectory, attributes.Size, attributes.LastWriteTimeUtc);
    }

    public Task DownloadAsync(string path, Stream to, CancellationToken ct) => sftp.DownloadFileAsync(path, to, ct);

    public async Task<DateTime> UploadAsync(Stream from, string path, CancellationToken ct)
    {
        await sftp.UploadFileAsync(from, path, ct);
        return (await sftp.GetAttributesAsync(path, ct)).LastWriteTimeUtc;
    }

    public Task DeleteAsync(string path, CancellationToken ct) => sftp.DeleteFileAsync(path, ct);

    public Task CreateFolderAsync(string path, CancellationToken ct) => sftp.CreateDirectoryAsync(path, ct);
}

/// <summary>What a sync did: files brought or sent, files deleted, those left as they are and why.</summary>
public sealed record RemoteSync(int Copied, int Deleted, IReadOnlyList<string> Conflicts, IReadOnlyList<string> Left)
{
    public static readonly RemoteSync None = new(0, 0, [], []);
}

/// <summary>
/// A machine's folder copied here to open in Karvon (VS Code's Remote SSH, without a server of its own there): what
/// changed there comes here (Pull), what changed here goes there (Push). A file changed on both sides since is left as
/// it is on each, a conflict to say. What is built or installed (.git, node_modules, bin, obj, target, .venv...) and
/// files over 5 MB stay there. Each file's size and time there, and its time here once copied, are kept in a manifest
/// beside the copy, which also says whose copy it is.
/// </summary>
public sealed class RemoteFolder
{
    public static readonly IReadOnlySet<string> Skipped = new HashSet<string>(StringComparer.Ordinal)
    {
        ".git", ".hg", ".svn", "node_modules", "bin", "obj", "target", ".venv", "venv", "__pycache__", ".gradle", ".idea", ".vs", ".next", ".nuxt", ".cache", ".terraform",
    };

    public const long LargestFile = 5_000_000;

    public const int MostFiles = 20_000;

    sealed record Entry(long Size, DateTime Remote, DateTime Local);

    readonly Dictionary<string, Entry> _files = new(StringComparer.Ordinal);

    RemoteFolder(string server, string remoteRoot, string localRoot)
    {
        Server = server;
        RemoteRoot = remoteRoot.Length > 1 ? remoteRoot.TrimEnd('/') : remoteRoot;
        LocalRoot = Path.GetFullPath(localRoot).TrimEnd(Path.DirectorySeparatorChar);
    }

    /// <summary>The Servers' machine it is a copy of.</summary>
    public string Server { get; }

    public string RemoteRoot { get; }

    public string LocalRoot { get; }

    public string ManifestFile => LocalRoot + ".karvon-remote.json";

    /// <summary>Where a machine's folder is copied: &lt;root&gt;/&lt;machine&gt;/&lt;its name&gt;-&lt;its path's hash&gt;.</summary>
    public static RemoteFolder For(string root, string server, string remoteRoot)
    {
        var name = remoteRoot.TrimEnd('/').Split('/').LastOrDefault(p => p.Length > 0) ?? server;
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(remoteRoot)))[..8];
        var folder = new RemoteFolder(server, remoteRoot, Path.Combine(root, Safe(server), $"{Safe(name)}-{hash}"));
        return Load(folder.ManifestFile) ?? folder;
    }

    static string Safe(string name) => string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_')).Trim('.');

    /// <summary>The copy a folder here is (its manifest beside it), or null.</summary>
    public static RemoteFolder? Of(string localRoot) => Load(Path.GetFullPath(localRoot).TrimEnd(Path.DirectorySeparatorChar) + ".karvon-remote.json");

    static RemoteFolder? Load(string manifest)
    {
        try
        {
            if (!File.Exists(manifest))
                return null;
            using var document = JsonDocument.Parse(File.ReadAllText(manifest));
            var root = document.RootElement;
            var folder = new RemoteFolder(root.GetProperty("server").GetString()!, root.GetProperty("remote").GetString()!, manifest[..^".karvon-remote.json".Length]);
            foreach (var file in root.GetProperty("files").EnumerateObject())
                folder._files[file.Name] = new Entry(file.Value.GetProperty("size").GetInt64(), file.Value.GetProperty("remote").GetDateTime(), file.Value.GetProperty("local").GetDateTime());
            return folder;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ManifestFile)!);
        using var stream = File.Create(ManifestFile);
        using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        json.WriteStartObject();
        json.WriteString("server", Server);
        json.WriteString("remote", RemoteRoot);
        json.WriteStartObject("files");
        foreach (var (path, entry) in _files.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            json.WriteStartObject(path);
            json.WriteNumber("size", entry.Size);
            json.WriteString("remote", entry.Remote);
            json.WriteString("local", entry.Local);
            json.WriteEndObject();
        }
        json.WriteEndObject();
        json.WriteEndObject();
    }

    /// <summary>A path of the copy (a file, a folder, the copy itself) as it is on the machine; null when it is not in the copy.</summary>
    public string? RemotePathOf(string local)
    {
        var full = Path.GetFullPath(local).TrimEnd(Path.DirectorySeparatorChar);
        if (full == LocalRoot)
            return RemoteRoot;
        if (!full.StartsWith(LocalRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return null;
        return (RemoteRoot == "/" ? "" : RemoteRoot) + "/" + full[(LocalRoot.Length + 1)..].Replace(Path.DirectorySeparatorChar, '/');
    }

    /// <summary>A path on the machine as the copy has it (one outside the folder as it is).</summary>
    public string LocalPathOf(string remote)
    {
        if (remote == RemoteRoot)
            return LocalRoot;
        var prefix = RemoteRoot == "/" ? "/" : RemoteRoot + "/";
        return remote.StartsWith(prefix, StringComparison.Ordinal) ? Path.Combine(LocalRoot, remote[prefix.Length..].Replace('/', Path.DirectorySeparatorChar)) : remote;
    }

    static bool IsSkipped(string relative) => relative.Split('/').Any(Skipped.Contains);

    string Here(string relative) => Path.Combine(LocalRoot, relative.Replace('/', Path.DirectorySeparatorChar));

    string There(string relative) => (RemoteRoot == "/" ? "" : RemoteRoot) + "/" + relative;

    /// <summary>What changed there since comes here: new and changed files copied, those deleted there deleted here (unless changed here).</summary>
    public async Task<RemoteSync> PullAsync(IRemoteFiles files, CancellationToken ct)
    {
        var (listed, left, complete) = await ListAsync(files, ct);
        var conflicts = new List<string>();
        var (copied, deleted) = (0, 0);
        Directory.CreateDirectory(LocalRoot);
        foreach (var (relative, info) in listed)
        {
            ct.ThrowIfCancellationRequested();
            var local = Here(relative);
            var known = _files.GetValueOrDefault(relative);
            var exists = File.Exists(local);
            var changedThere = known is null || known.Size != info.Size || known.Remote != info.Modified;
            if (!changedThere && exists)
                continue;
            var changedHere = exists && (known is null || File.GetLastWriteTimeUtc(local) != known.Local);
            if (changedHere)
            {
                // Already the same (a copy made before the manifest knew it): known from now on.
                if (known is null && new FileInfo(local).Length == info.Size && await SameAsync(files, relative, local, ct))
                {
                    _files[relative] = new Entry(info.Size, info.Modified, File.GetLastWriteTimeUtc(local));
                    continue;
                }
                conflicts.Add(relative);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(local)!);
            var temporary = local + ".karvon-part";
            await using (var stream = File.Create(temporary))
                await files.DownloadAsync(There(relative), stream, ct);
            File.Move(temporary, local, overwrite: true);
            _files[relative] = new Entry(info.Size, info.Modified, File.GetLastWriteTimeUtc(local));
            copied++;
        }
        // Gone there (and the listing was whole, so it is not just unlisted): gone here, unless it changed here since.
        if (complete)
            foreach (var (relative, known) in _files.Where(f => !listed.ContainsKey(f.Key)).ToList())
            {
                var local = Here(relative);
                if (File.Exists(local) && File.GetLastWriteTimeUtc(local) != known.Local)
                {
                    conflicts.Add(relative);
                    continue;
                }
                if (File.Exists(local))
                {
                    File.Delete(local);
                    deleted++;
                }
                _files.Remove(relative);
            }
        Save();
        return new RemoteSync(copied, deleted, conflicts, left);
    }

    async Task<bool> SameAsync(IRemoteFiles files, string relative, string local, CancellationToken ct)
    {
        using var there = new MemoryStream();
        await files.DownloadAsync(There(relative), there, ct);
        var here = await File.ReadAllBytesAsync(local, ct);
        return there.ToArray().SequenceEqual(here);
    }

    /// <summary>The folder's files there (by their path in it), without the skipped folders, links and large files; whether that is all of them.</summary>
    async Task<(Dictionary<string, RemoteFileInfo> Files, List<string> Left, bool Complete)> ListAsync(IRemoteFiles files, CancellationToken ct)
    {
        var found = new Dictionary<string, RemoteFileInfo>(StringComparer.Ordinal);
        var left = new List<string>();
        var folders = new Queue<string>([""]);
        var complete = true;
        while (folders.TryDequeue(out var folder))
        {
            foreach (var info in await files.ListAsync(folder.Length == 0 ? RemoteRoot : There(folder), ct))
            {
                var relative = folder.Length == 0 ? info.Name : folder + "/" + info.Name;
                if (info.IsLink || Skipped.Contains(info.Name))
                    continue;
                if (info.IsFolder)
                    folders.Enqueue(relative);
                else if (info.Size > LargestFile)
                    left.Add($"{relative} ({RemoteCommands.Size(info.Size)})");
                else if (found.Count >= MostFiles)
                    complete = false;
                else
                    found[relative] = info;
            }
        }
        if (!complete)
            left.Add($"files past the first {MostFiles:N0}");
        return (found, left, complete);
    }

    /// <summary>What changed here since it was copied: new and changed files, and those deleted (by their path in the folder).</summary>
    public IReadOnlyList<(string Path, bool Deleted)> LocalChanges()
    {
        var changes = new List<(string, bool)>();
        if (!Directory.Exists(LocalRoot))
            return changes;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var folders = new Stack<string>([LocalRoot]);
        while (folders.TryPop(out var folder))
        {
            foreach (var dir in Directory.EnumerateDirectories(folder))
                if (!Skipped.Contains(Path.GetFileName(dir)) && !new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint))
                    folders.Push(dir);
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                if (file.EndsWith(".karvon-part", StringComparison.Ordinal))
                    continue;
                var relative = file[(LocalRoot.Length + 1)..].Replace(Path.DirectorySeparatorChar, '/');
                seen.Add(relative);
                var known = _files.GetValueOrDefault(relative);
                if (known is null ? new FileInfo(file).Length <= LargestFile : File.GetLastWriteTimeUtc(file) != known.Local)
                    changes.Add((relative, false));
            }
        }
        foreach (var relative in _files.Keys.Where(k => !seen.Contains(k) && !IsSkipped(k)))
            changes.Add((relative, true));
        return changes;
    }

    /// <summary>What changed here goes there: a file changed there since (or made there meanwhile) is left, a conflict.</summary>
    public async Task<RemoteSync> PushAsync(IRemoteFiles files, CancellationToken ct)
    {
        var conflicts = new List<string>();
        var (copied, deleted) = (0, 0);
        var made = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (relative, gone) in LocalChanges())
        {
            ct.ThrowIfCancellationRequested();
            var remote = There(relative);
            var info = await files.InfoAsync(remote, ct);
            var known = _files.GetValueOrDefault(relative);
            if (known is null ? info is not null : info is not null && (info.Size != known.Size || info.Modified != known.Remote))
            {
                conflicts.Add(relative);
                continue;
            }
            if (gone)
            {
                if (info is not null)
                {
                    await files.DeleteAsync(remote, ct);
                    deleted++;
                }
                _files.Remove(relative);
                continue;
            }
            // Its folders there first.
            var parts = relative.Split('/');
            for (var i = 1; i < parts.Length; i++)
            {
                var folder = string.Join('/', parts[..i]);
                if (made.Add(folder) && await files.InfoAsync(There(folder), ct) is null)
                    await files.CreateFolderAsync(There(folder), ct);
            }
            var local = Here(relative);
            DateTime modified;
            await using (var stream = File.OpenRead(local))
                modified = await files.UploadAsync(stream, remote, ct);
            _files[relative] = new Entry(new FileInfo(local).Length, modified, File.GetLastWriteTimeUtc(local));
            copied++;
        }
        if (copied + deleted > 0)
            Save();
        return new RemoteSync(copied, deleted, conflicts, []);
    }

    /// <summary>A conflict settled with this side's: the file sent there (deleted there, when it is gone here).</summary>
    public async Task PushOneAsync(IRemoteFiles files, string relative, CancellationToken ct)
    {
        var local = Here(relative);
        if (!File.Exists(local))
        {
            if (await files.InfoAsync(There(relative), ct) is not null)
                await files.DeleteAsync(There(relative), ct);
            _files.Remove(relative);
        }
        else
        {
            DateTime modified;
            await using (var stream = File.OpenRead(local))
                modified = await files.UploadAsync(stream, There(relative), ct);
            _files[relative] = new Entry(new FileInfo(local).Length, modified, File.GetLastWriteTimeUtc(local));
        }
        Save();
    }

    /// <summary>A conflict settled with the machine's: the file brought here (deleted here, when it is gone there).</summary>
    public async Task PullOneAsync(IRemoteFiles files, string relative, CancellationToken ct)
    {
        var local = Here(relative);
        if (await files.InfoAsync(There(relative), ct) is not { } info)
        {
            File.Delete(local);
            _files.Remove(relative);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(local)!);
            await using (var stream = File.Create(local + ".karvon-part"))
                await files.DownloadAsync(There(relative), stream, ct);
            File.Move(local + ".karvon-part", local, overwrite: true);
            _files[relative] = new Entry(info.Size, info.Modified, File.GetLastWriteTimeUtc(local));
        }
        Save();
    }

    /// <summary>A command run there, in the folder of the copy it was to run in: as a POSIX shell reads it (a login shell, its PATH).</summary>
    public string CommandThere(string program, IEnumerable<string> arguments, string localFolder, IReadOnlyDictionary<string, string>? environment)
    {
        string Map(string text) => text.Replace(LocalRoot, RemoteRoot, StringComparison.Ordinal);
        // A program found here (a full path of this machine: dotnet.exe on Windows) is found there by its name.
        if (Path.IsPathRooted(program) && RemotePathOf(program) is null)
            program = OperatingSystem.IsWindows() ? Path.GetFileNameWithoutExtension(program) : Path.GetFileName(program);
        var variables = (environment ?? new Dictionary<string, string>()).Where(v => v.Key != "PATH").Select(v => $"{v.Key}={Quote(Map(v.Value))}").ToList();
        var line = $"cd {Quote(RemotePathOf(localFolder) ?? RemoteRoot)} && {(variables.Count > 0 ? "env " + string.Join(' ', variables) + " " : "")}exec {Quote(Map(program))}";
        foreach (var argument in arguments)
            line += " " + Quote(Map(argument));
        return "sh -lc " + Quote(line);
    }

    /// <summary>A word as a POSIX shell takes it, whatever is in it.</summary>
    public static string Quote(string word) => "'" + word.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
