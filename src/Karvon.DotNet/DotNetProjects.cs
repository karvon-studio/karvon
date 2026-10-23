using Karvon.Build;
using Karvon.Workspace;

namespace Karvon.DotNet;

/// <summary>What MSBuild says of a project, asked once for each version of its project file.</summary>
static class DotNetProjects
{
    /// <summary>The projects' output assemblies, by project file, configuration and when the project file changed.</summary>
    static readonly Dictionary<(string Project, string Configuration, DateTime Written), string> TargetPaths = [];

    /// <summary>The project's output assembly, as MSBuild computes it (honors AssemblyName, OutputPath, TFM).</summary>
    public static async Task<string?> TargetPathAsync(ProjectInfo project, string configuration)
    {
        var key = (project.FilePath, configuration, File.GetLastWriteTimeUtc(project.FilePath));
        lock (TargetPaths)
            if (TargetPaths.TryGetValue(key, out var cached))
                return cached;
        var lines = new List<string>();
        using var runner = ProcessRunner.Start(DotnetCli.Executable,
            ["msbuild", project.FilePath, "-getProperty:TargetPath", $"-p:Configuration={configuration}", "-nologo"],
            project.Directory, line =>
            {
                lock (lines)
                    lines.Add(line);
            });
        if (await runner.WaitAsync() != 0)
            return null;
        string? path;
        lock (lines)
            path = lines.Select(l => l.Trim()).LastOrDefault(l => l.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
        if (path is not null)
            lock (TargetPaths)
                TargetPaths[key] = path;
        return path;
    }
}
