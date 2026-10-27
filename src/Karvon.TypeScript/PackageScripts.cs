using System.Text.Json;
using Karvon.Modules;

namespace Karvon.TypeScript;

/// <summary>
/// The scripts of a folder's package.json files (its own, and those of its packages up to two folders down) as tasks, run
/// by the package manager the folder uses (pnpm, yarn or bun by its lock file or packageManager, npm otherwise): build is
/// the build task, test the test task, start, dev, serve and preview run the app, a watch keeps running beside the rest.
/// </summary>
public static class PackageScripts
{
    public const string Source = "package.json";

    static readonly HashSet<string> Skipped = new(StringComparer.Ordinal) { "node_modules", ".git", ".vs", ".vscode", "dist", "build", "out", "coverage", "bin", "obj" };

    static readonly JsonDocumentOptions Options = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static IReadOnlyList<WorkspaceTask> Read(string workspace)
    {
        var tasks = new List<WorkspaceTask>();
        foreach (var file in PackageFiles(workspace))
            tasks.AddRange(ScriptsOf(workspace, file));
        return tasks;
    }

    /// <summary>
    /// The folder's packages (frontend/, apps/web/...) as projects: its own package.json is the folder itself, and a
    /// package in another one's folder is in that one's files.
    /// </summary>
    public static IReadOnlyList<WorkspaceProject> Projects(string workspace)
    {
        var projects = new List<WorkspaceProject>();
        foreach (var file in PackageFiles(workspace))
        {
            var dir = Path.GetDirectoryName(file)!;
            if (dir == workspace.TrimEnd(Path.DirectorySeparatorChar) || projects.Any(p => Services.FilePaths.IsUnder(dir, p.Directory)))
                continue;
            JsonElement package;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file), Options);
                package = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                package = default;
            }
            projects.Add(new WorkspaceProject(Path.GetFileName(dir), dir, file)
            {
                Kind = package.ValueKind == JsonValueKind.Object ? PackageManager(workspace, dir, package) : "npm",
            });
        }
        return projects;
    }

    /// <summary>The folder's package.json, then its packages' (frontend/, apps/web/...), in order.</summary>
    internal static IEnumerable<string> PackageFiles(string workspace)
    {
        var root = Path.Combine(workspace, "package.json");
        if (File.Exists(root))
            yield return root;
        foreach (var dir in Folders(workspace, 2))
        {
            var file = Path.Combine(dir, "package.json");
            if (File.Exists(file))
                yield return file;
        }
    }

    static IEnumerable<string> Folders(string dir, int depth)
    {
        if (depth == 0)
            yield break;
        string[] children;
        try
        {
            children = Directory.GetDirectories(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        foreach (var child in children.Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(child);
            if (Skipped.Contains(name) || name.StartsWith('.'))
                continue;
            yield return child;
            foreach (var below in Folders(child, depth - 1))
                yield return below;
        }
    }

    static IEnumerable<WorkspaceTask> ScriptsOf(string workspace, string file)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(file), Options);
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("scripts", out var scripts) || scripts.ValueKind != JsonValueKind.Object)
            return [];
        var dir = Path.GetDirectoryName(file)!;
        var relative = Path.GetRelativePath(workspace, dir).Replace('\\', '/');
        var isRoot = relative == ".";
        var manager = PackageManager(workspace, dir, doc.RootElement);
        // The app runs by its dev script when it has one (start is often the production server).
        var run = scripts.TryGetProperty("dev", out _) ? "dev" : "start";
        var tasks = new List<WorkspaceTask>();
        foreach (var script in scripts.EnumerateObject())
        {
            var (kind, background) = KindOf(script.Name);
            var name = isRoot ? $"{manager}: {script.Name}" : $"{manager}: {script.Name} - {relative}";
            // The app: Hot Reload by the agent, unless it reloads by itself (a dev server, a watcher); bun is no Node.js.
            var app = kind == WorkspaceTaskKind.Run && !background;
            var reloadsItself = app && script.Value.ValueKind == JsonValueKind.String && NodeHotReload.ReloadsItself(script.Value.GetString()!);
            tasks.Add(new WorkspaceTask(name, kind, manager, ["run", script.Name], dir)
            {
                Source = Source,
                // The app and the tests are debugged as they run (F5 on the toolbar's choice).
                Debug = kind is WorkspaceTaskKind.Run or WorkspaceTaskKind.Test && !background ? JsDebugger.ForScript(name, manager, script.Name, dir, reloadsItself) : null,
                HotReloads = reloadsItself,
                HotReloadEnvironment = app && !reloadsItself && manager != "bun"
                    ? NodeHotReload.Environment(workspace, Environment.GetEnvironmentVariable("NODE_OPTIONS"))
                    : null,
                // The folder's own build, test and app are the ones Build, Run All Tests and Start run.
                IsDefault = isRoot && (script.Name is "build" or "test" || script.Name == run),
                IsBackground = background,
            });
        }
        return tasks;
    }

    static (WorkspaceTaskKind Kind, bool Background) KindOf(string script)
    {
        var name = script.Split(':')[0];
        if (script.Contains("watch", StringComparison.OrdinalIgnoreCase))
            return (WorkspaceTaskKind.Other, true);
        return name switch
        {
            "build" => (WorkspaceTaskKind.Build, false),
            "test" => (WorkspaceTaskKind.Test, false),
            "start" or "dev" or "serve" or "preview" => (WorkspaceTaskKind.Run, false),
            _ => (WorkspaceTaskKind.Other, false),
        };
    }

    /// <summary>The package manager of a package: its packageManager field, else the lock file beside it or at the folder's root.</summary>
    /// <summary>The package manager of a package's folder (its packageManager, else its lock file): npm, pnpm, yarn or bun.</summary>
    internal static string ManagerOf(string dir)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "package.json")), Options);
            return PackageManager(dir, dir, doc.RootElement);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return "npm";
        }
    }

    static string PackageManager(string workspace, string dir, JsonElement package)
    {
        if (package.TryGetProperty("packageManager", out var declared) && declared.ValueKind == JsonValueKind.String
            && declared.GetString()!.Split('@')[0] is var name && name is "pnpm" or "yarn" or "bun" or "npm")
            return name;
        foreach (var folder in new[] { dir, workspace })
        {
            if (File.Exists(Path.Combine(folder, "pnpm-lock.yaml")))
                return "pnpm";
            if (File.Exists(Path.Combine(folder, "yarn.lock")))
                return "yarn";
            if (File.Exists(Path.Combine(folder, "bun.lock")) || File.Exists(Path.Combine(folder, "bun.lockb")))
                return "bun";
            if (File.Exists(Path.Combine(folder, "package-lock.json")))
                return "npm";
        }
        return "npm";
    }
}
