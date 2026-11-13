using System.Diagnostics;
using System.Text.Json;

namespace Karvon.Claude;

/// <summary>
/// Karvon's strict sandbox: the project and what a build needs, and nothing else of the user's. A command an agent runs
/// sees the project (it may change it), the toolchains, the package caches and git's own settings (read only), and the
/// system's programs and libraries (read only); the rest of the home folder, other disks and the user's sockets (the
/// keyring's, ssh-agent's) are not there for it at all. What holds secrets stays hidden inside what is readable.
/// The same rules keep the agents' own file tools (Read, Edit, Write, Grep, Glob) to the project.
/// Claude Code's sandbox is given them as its settings (<see cref="ClaudeSandbox.Settings(ProjectSandbox)"/>); the
/// Agents panel's shell runs in bubblewrap with them, without the network (<see cref="Wrap"/>).
/// </summary>
public sealed class ProjectSandbox
{
    /// <summary>Toolchains the home folder may hold (relative to it): their programs are read, never changed.</summary>
    static readonly string[] Toolchains =
    [
        ".dotnet", ".nvm", ".local/share/fnm", ".fnm", ".volta", ".bun", ".deno", ".cargo/bin", ".rustup", ".sdkman/candidates",
        ".jdks", ".pyenv", ".local/bin", ".local/lib", ".local/share/pnpm", "go/bin", ".go", ".arche", ".gradle/wrapper", ".m2/wrapper",
        ".cache/ms-playwright", ".konan",
    ];

    /// <summary>
    /// Package caches (relative to the home folder): what a build reads. Read only too: what is put in them would run in
    /// every project's build, outside the sandbox (a restore or an install goes through Karvon's tools).
    /// </summary>
    static readonly string[] Caches =
    [
        ".nuget/packages", ".npm", ".cache/pip", ".cache/yarn", ".yarn/berry/cache", ".m2/repository", ".gradle/caches", ".cargo/registry",
        ".cargo/git", "go/pkg/mod", ".cache/go-build", ".cache/deno", ".bun/install/cache",
    ];

    /// <summary>git's own settings (who commits, its aliases).</summary>
    static readonly string[] GitSettings = [".gitconfig", ".config/git"];

    /// <summary>Inside what is readable, what holds keys all the same (the .NET certificate stores: the development certificate's key).</summary>
    static readonly string[] SecretsInside = [".dotnet/corefx"];

    /// <summary>
    /// What in the project tells tools outside the sandbox what to run: git's settings and hooks, the agents' and the
    /// editors' settings. Read only in it: what a command put there would run later with the user's access (a hook when
    /// git_commit runs, a server when the next session starts).
    /// </summary>
    public static readonly string[] ProjectControls = [".git", ".claude", ".mcp.json", ".vscode", ".idea"];

    /// <summary>Of the system's places, those that hold the user's data, other disks or the user's sockets.</summary>
    static readonly string[] SystemHidden = ["/root", "/run/user", "/mnt", "/media", "/srv", "/var/log", "/var/mail", "/var/spool"];

    readonly List<string> _secrets;

    /// <param name="workspace">The project's folder: read and written.</param>
    /// <param name="readFolders">Folders of the user's the agents may read besides (MCP Servers > karvon).</param>
    /// <param name="writeFolders">Folders of the user's the agents may change besides.</param>
    /// <param name="home">The home folder (the tests' own); the user's when null.</param>
    /// <param name="pathVariable">The PATH whose folders under the home folder are the programs a command finds; the process's when null.</param>
    /// <param name="extraReadable">What the agent's own shell needs besides (Claude Code's shell snapshots).</param>
    public ProjectSandbox(string workspace, IEnumerable<string>? readFolders = null, IEnumerable<string>? writeFolders = null, string? home = null,
        string? pathVariable = null, IEnumerable<string>? extraReadable = null)
    {
        Home = Full(home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        Workspace = Full(workspace);
        _secrets = [.. SecretFiles.UserPlaces(Home), .. SecretsInside.Select(p => Full(Path.Combine(Home, p)))];
        var refused = new List<string>();

        IEnumerable<string> FromHome(IEnumerable<string> relative) => relative.Select(p => Full(Path.Combine(Home, p))).Where(Exists);

        // The folders on the PATH under the home folder: the programs a command is found among.
        var onPath = (pathVariable ?? Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(Path.IsPathFullyQualified).Select(Full).Where(p => Under(p, Home) && p != Home && Directory.Exists(p));

        var readable = new List<string> { Workspace };
        readable.AddRange(FromHome(Toolchains));
        readable.AddRange(FromHome(Caches));
        readable.AddRange(FromHome(GitSettings));
        readable.AddRange(onPath);
        readable.AddRange((extraReadable ?? []).Select(Full).Where(Exists));
        readable.AddRange(Granted(readFolders, refused));
        Readable = Reduce(readable.SelectMany(Open));

        Writable = Reduce(new[] { Workspace }.Concat(Granted(writeFolders, refused)).SelectMany(Open));
        Refused = refused;
        Hidden = [Home, .. SystemHidden.Where(Directory.Exists)];
    }

    public string Workspace { get; }

    public string Home { get; }

    /// <summary>What no command sees (unless it is <see cref="Readable"/>): the home folder, other disks, the user's sockets.</summary>
    public IReadOnlyList<string> Hidden { get; }

    /// <summary>What is read within <see cref="Hidden"/>: the project, toolchains, caches, git's settings; none of it holding a secret.</summary>
    public IReadOnlyList<string> Readable { get; }

    /// <summary>What is changed: the project, and the folders the user gave.</summary>
    public IReadOnlyList<string> Writable { get; }

    /// <summary>The folders the user gave that are not given: they are, or hold, what holds secrets (the home folder itself, ~/.ssh).</summary>
    public IReadOnlyList<string> Refused { get; }

    /// <summary>The project's controls (<see cref="ProjectControls"/>), there or not: read only.</summary>
    public IReadOnlyList<string> Protected => [.. ProjectControls.Select(c => Path.Combine(Workspace, c))];

    /// <summary>Whether a command or a file tool may read <paramref name="path"/>.</summary>
    public bool CanRead(string path)
    {
        var full = Full(path);
        if (_secrets.Any(s => Under(full, s)))
            return false;
        if (Readable.Any(r => Under(full, r)))
            return true;
        return !Hidden.Any(h => Under(full, h));
    }

    /// <summary>Whether a command or a file tool may change <paramref name="path"/>.</summary>
    public bool CanWrite(string path)
    {
        var full = Full(path);
        return !_secrets.Any(s => Under(full, s)) && !Protected.Any(p => Under(full, p)) && Writable.Any(w => Under(full, w));
    }

    /// <summary>The tools whose paths are kept to the project (Claude Code's own, and the Agents panel's).</summary>
    public const string FileTools = "Read|Edit|MultiEdit|Write|NotebookEdit|Grep|Glob";

    /// <summary>Why a file tool's call reaches outside what it may (a file to read or change elsewhere); null when it may.</summary>
    public string? Check(string tool, JsonElement input)
    {
        var path = Str(input, "file_path") ?? Str(input, "notebook_path") ?? (tool is "Grep" or "Glob" ? Str(input, "path") : null);
        if (path is null)
            return null;
        string full;
        try
        {
            full = Path.GetFullPath(path.StartsWith('~') ? Home + path[1..] : path, Workspace);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        var writes = tool is "Edit" or "MultiEdit" or "Write" or "NotebookEdit";
        if (writes ? CanWrite(full) : CanRead(full))
            return null;
        if (writes && Protected.Any(p => Under(full, p)))
            return $"Karvon's sandbox keeps {path} read only: it tells tools outside the sandbox what to run (git's settings and hooks, "
                + "the agents' and the editors' settings). git goes through the git_ tools; for a change to it, ask the user.";
        return writes
            ? $"Karvon's sandbox keeps Claude to the project: {path} is outside it, so it is not changed here. {Advice}"
            : $"Karvon's sandbox keeps Claude to the project: {path} is outside it, so it is not read here. A library's or the SDK's code is "
              + $"read with code_definition or code_read_symbol. {Advice}";
    }

    const string Advice = "For a folder of the user's that the work needs, ask the user to add it in MCP Servers > karvon (Folders Claude may read "
        + "or change); for a command that needs more than the project, run_outside_sandbox asks the user to run it outside the sandbox.";

    /// <summary>Why commands cannot run in it on this machine (bubblewrap missing, or refused its namespace); null when they can.</summary>
    public static string? Problem() => OperatingSystem.IsLinux() ? ClaudeSandbox.BubblewrapProblem() : "Karvon's own sandbox is bubblewrap's, on Linux.";

    /// <summary>
    /// <paramref name="command"/> in bubblewrap with these rules, run by bash in the project: the project read and written,
    /// the toolchains, caches and the system read only, the rest of the home folder and the user's places and sockets not
    /// there, a /tmp of its own, no network, nothing else of the machine's processes; its environment the caller's.
    /// </summary>
    public ProcessStartInfo Wrap(string command, string bubblewrap = "bwrap")
    {
        var start = new ProcessStartInfo(bubblewrap) { WorkingDirectory = Workspace, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in Arguments())
            start.ArgumentList.Add(argument);
        start.ArgumentList.Add("--");
        start.ArgumentList.Add("/bin/bash");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(command);
        // The sockets it would look for are not there; its temporary files are in its own /tmp.
        foreach (var name in new[] { "XDG_RUNTIME_DIR", "DBUS_SESSION_BUS_ADDRESS", "SSH_AUTH_SOCK", "GPG_AGENT_INFO", "GNOME_KEYRING_CONTROL" })
            start.Environment.Remove(name);
        start.Environment["TMPDIR"] = "/tmp";
        return start;
    }

    /// <summary>bubblewrap's arguments for these rules (before the command).</summary>
    public List<string> Arguments()
    {
        List<string> args =
        [
            "--die-with-parent", "--new-session", "--unshare-all",
            "--ro-bind", "/", "/",
            "--dev", "/dev",
            "--proc", "/proc",
            "--tmpfs", "/tmp",
        ];
        foreach (var hidden in Hidden)
            args.AddRange(["--tmpfs", hidden]);
        // Back inside what was hidden: what is read, then what is written (a written folder inside a read one stays written).
        foreach (var path in Readable.Where(Exists))
            args.AddRange(["--ro-bind", path, path]);
        foreach (var path in Writable.Where(Exists))
            args.AddRange(["--bind", path, path]);
        // The project's controls read only again, inside what is written.
        foreach (var path in Protected.Where(Exists))
            args.AddRange(["--ro-bind", path, path]);
        args.AddRange(["--chdir", Workspace]);
        return args;
    }

    /// <summary>The user's folders, those that are or hold secrets (or the home folder itself) refused.</summary>
    IEnumerable<string> Granted(IEnumerable<string>? folders, List<string> refused)
    {
        foreach (var folder in folders ?? [])
        {
            if (string.IsNullOrWhiteSpace(folder))
                continue;
            var full = Full(folder.StartsWith('~') ? Home + folder[1..] : folder);
            if (full == Home || full == "/" || _secrets.Any(s => Under(full, s)) || !Exists(full))
            {
                refused.Add(folder);
                continue;
            }
            yield return full;
        }
    }

    /// <summary>
    /// A path as it may be opened: itself when nothing inside it holds secrets, else what is in it but those (a folder
    /// opened would open its secrets too, whatever else hides them).
    /// </summary>
    IEnumerable<string> Open(string path)
    {
        if (_secrets.Any(s => Under(path, s)))
            yield break;
        if (!_secrets.Any(s => Under(s, path)) || !Directory.Exists(path))
        {
            yield return path;
            yield break;
        }
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(path).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        foreach (var entry in entries)
            foreach (var opened in Open(Full(entry)))
                yield return opened;
    }

    /// <summary>The paths without those inside another of them, in order.</summary>
    static List<string> Reduce(IEnumerable<string> paths)
    {
        var all = paths.Distinct(StringComparer.Ordinal).ToList();
        return [.. all.Where(p => !all.Any(o => o != p && Under(p, o)))];
    }

    static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    static bool Under(string path, string folder) =>
        path == folder || path.StartsWith(folder.EndsWith(Path.DirectorySeparatorChar) ? folder : folder + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    static string? Str(JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
