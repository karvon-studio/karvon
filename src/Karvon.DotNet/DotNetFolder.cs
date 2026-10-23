using System.Text.Json.Nodes;
using Karvon.Build;
using Karvon.DotNet.Debugging;
using Karvon.Modules;
using Karvon.Workspace;

namespace Karvon.DotNet;

/// <summary>
/// A folder opened as it is with .NET in it (a repository with its backend's project and no solution at its root): its
/// solutions and projects, in it and up to two folders down, and their tasks. dotnet build is the build (the folder's one
/// solution, else each project, one after another), dotnet test each test project's, and dotnet run each project's that
/// runs, by its launch profile; F5 debugs one with netcoredbg once its build succeeded. They are the folder's own build,
/// test and run (the defaults) only for .NET in it or a folder down: one deeper (a sample in a Rust repository) leaves
/// those to the folder's own language.
/// </summary>
public static class DotNetFolder
{
    public const string Source = "dotnet";

    static readonly HashSet<string> Skipped = new(StringComparer.Ordinal) { "node_modules", "bin", "obj", "dist", "build", "out", "packages" };

    /// <summary>The solutions (.sln, .slnx) and the projects in a folder, in it and up to two folders down.</summary>
    public static (IReadOnlyList<string> Solutions, IReadOnlyList<ProjectInfo> Projects) Find(string folder)
    {
        List<string> solutions = [];
        List<ProjectInfo> projects = [];
        foreach (var dir in Folders(folder, 2).Prepend(folder))
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var file in files.Order(StringComparer.Ordinal))
                if (SolutionLoader.IsSolutionFile(file))
                    solutions.Add(file);
                else if (SolutionLoader.IsProjectFile(file))
                    projects.Add(ProjectReader.Load(Path.GetFileNameWithoutExtension(file), file));
        }
        return (solutions, projects);
    }

    /// <summary>Whether a folder has .NET in it.</summary>
    public static bool Has(string folder) => Find(folder) is var (solutions, projects) && (solutions.Count > 0 || projects.Count > 0);

    public static IReadOnlyList<WorkspaceTask> Tasks(string folder)
    {
        var (solutions, projects) = Find(folder);
        if (solutions.Count == 0 && projects.Count == 0)
            return [];
        // As dotnet builds without -c, Debug: F5 debugs what the build made.
        static List<string> Build(string target) => ["build", target, "-nologo", "-v:minimal", "-clp:NoSummary;ForceNoAlign"];
        List<WorkspaceTask> tasks = [];
        IReadOnlyList<string> targets = solutions.Count == 1 ? solutions : projects.Count > 0 ? [.. projects.Select(p => p.FilePath)] : solutions;
        // In the folder or a folder down: its own.
        var root = Path.TrimEndingDirectorySeparator(folder);
        bool Same(string? a) => a is not null && string.Equals(a, root, Services.FilePaths.Comparison);
        bool Own(string file) => Path.GetDirectoryName(file) is { } dir && (Same(dir) || Same(Path.GetDirectoryName(dir)));
        if (targets.Count == 1)
            tasks.Add(new("dotnet: build", WorkspaceTaskKind.Build, DotnetCli.Executable, Build(targets[0]), folder) { Source = Source, IsDefault = Own(targets[0]) });
        else
        {
            var each = targets.Select(t => new WorkspaceTask($"dotnet: build - {Path.GetFileNameWithoutExtension(t)}", WorkspaceTaskKind.Build,
                DotnetCli.Executable, Build(t), Path.GetDirectoryName(t)!) { Source = Source }).ToList();
            tasks.Add(new("dotnet: build", WorkspaceTaskKind.Build, "", [], folder)
            {
                Source = Source,
                IsDefault = targets.Any(Own),
                DependsOn = [.. each.Select(t => t.Name)],
                DependsInSequence = true,
            });
            tasks.AddRange(each);
        }
        string BuildOf(ProjectInfo project) =>
            targets.Count > 1 && targets.Contains(project.FilePath) ? $"dotnet: build - {project.Name}" : "dotnet: build";

        var tests = projects.Where(p => p.IsTestProject).ToList();
        foreach (var project in tests)
            tasks.Add(new($"dotnet: test - {project.Name}", WorkspaceTaskKind.Test, DotnetCli.Executable, ["test", project.FilePath, "-nologo"], project.Directory)
            {
                Source = Source,
                IsDefault = project == tests[0] && Own(project.FilePath),
                Environment = DotNetDebugger.OutputEnvironment,
            });

        // The app first, as Visual Studio picks the startup project: a web project, else the first that runs.
        var runnable = projects.Where(p => p.IsRunnable).OrderByDescending(p => p.IsWeb).ToList();
        foreach (var project in runnable)
        {
            var profile = project.LaunchProfiles.FirstOrDefault();
            var name = $"dotnet: run - {project.Name}";
            var debug = new JsonObject
            {
                ["type"] = "dotnet",
                ["request"] = "launch",
                ["name"] = name,
                ["projectPath"] = project.FilePath,
                ["configuration"] = "Debug",
                ["preLaunchTask"] = BuildOf(project),
            };
            if (profile is not null)
                debug["launchProfile"] = profile.Name;
            tasks.Add(new(name, WorkspaceTaskKind.Run, DotnetCli.Executable,
                ["run", "--project", project.FilePath, .. profile is not null ? ["--launch-profile", profile.Name] : new[] { "--no-launch-profile" }], project.Directory)
            {
                Source = Source,
                IsDefault = project == runnable[0] && Own(project.FilePath),
                Environment = DotNetDebugger.OutputEnvironment,
                ServerReadyAction = profile is { LaunchBrowser: true } ? DotNetDebugger.BrowserAt(profile) : null,
                Debug = new DebugLaunch(name, debug) { Source = Source },
            });
        }
        return tasks;
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
            if (name.StartsWith('.') || Skipped.Contains(name))
                continue;
            yield return child;
            foreach (var deeper in Folders(child, depth - 1))
                yield return deeper;
        }
    }
}
