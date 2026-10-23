using Karvon.Workspace;
using Microsoft.VisualStudio.SolutionPersistence.Model;
using Microsoft.VisualStudio.SolutionPersistence.Serializer;

namespace Karvon.DotNet;

/// <summary>
/// Reads .sln and .slnx with Microsoft's own solution parser, or a project opened on its own, and builds Solution
/// Explorer's tree of them: solution folders, projects, each project's Dependencies and files.
/// </summary>
public static class SolutionReader
{
    /// <summary>What the .NET module opens: a solution, or a single project.</summary>
    public static bool Opens(string path) => SolutionLoader.IsSolutionFile(path) || SolutionLoader.IsProjectFile(path);

    /// <summary>A .sln, a .slnx, or a project opened on its own.</summary>
    public static async Task<SolutionInfo> LoadAsync(string solutionPath, CancellationToken ct = default)
    {
        if (SolutionLoader.IsProjectFile(solutionPath))
            return LoadProject(solutionPath);
        var serializer = SolutionSerializers.GetSerializerByMoniker(solutionPath)
            ?? throw new NotSupportedException($"Unsupported solution format: {solutionPath}");
        var model = await serializer.OpenAsync(solutionPath, ct);
        var dir = Path.GetDirectoryName(solutionPath)!;

        var projects = model.SolutionProjects
            .Select(p => ProjectInfo.Load(p.ActualDisplayName, Path.GetFullPath(Path.Combine(dir, p.FilePath.Replace('\\', '/')))))
            .ToList();
        var byPath = projects.ToDictionary(p => p.FilePath);

        IEnumerable<ExplorerNode> ChildrenOf(SolutionFolderModel? folder)
        {
            var folders = model.SolutionFolders.Where(f => f.Parent == folder)
                .OrderBy(f => f.ActualDisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(f => FolderNode(f));
            var projs = model.SolutionProjects.Where(p => p.Parent == folder)
                .OrderBy(p => p.ActualDisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(p => ProjectNode(byPath[Path.GetFullPath(Path.Combine(dir, p.FilePath.Replace('\\', '/')))], folder?.Path));
            return folders.Concat(projs);
        }

        ExplorerNode FolderNode(SolutionFolderModel f)
        {
            var node = new ExplorerNode(NodeKind.SolutionFolder, f.ActualDisplayName) { SolutionFolder = f.Path };
            foreach (var child in ChildrenOf(f))
                node.Children.Add(child);
            foreach (var file in f.Files ?? [])
            {
                var full = Path.GetFullPath(Path.Combine(dir, file.Replace('\\', '/')));
                node.Children.Add(new ExplorerNode(NodeKind.File, Path.GetFileName(full), full) { SolutionFolder = f.Path });
            }
            return node;
        }

        var root = new ExplorerNode(NodeKind.Solution, $"Solution '{Path.GetFileNameWithoutExtension(solutionPath)}'", solutionPath)
        {
            Detail = $"({projects.Count} of {projects.Count} projects)",
        };
        foreach (var child in ChildrenOf(null))
            root.Children.Add(child);
        root.IsExpanded = true;

        return new SolutionInfo { FilePath = solutionPath, Projects = projects, Root = root, Editor = SolutionEditor.Instance };
    }

    /// <summary>A project opened without a solution: VS shows it under an implicit solution of the same name.</summary>
    static SolutionInfo LoadProject(string projectPath)
    {
        if (!File.Exists(projectPath))
            throw new FileNotFoundException("The project file was not found.", projectPath);
        var project = ProjectInfo.Load(Path.GetFileNameWithoutExtension(projectPath), Path.GetFullPath(projectPath));
        var root = new ExplorerNode(NodeKind.Solution, $"Solution '{project.Name}'", projectPath) { Detail = "(1 of 1 project)" };
        root.Children.Add(ProjectNode(project));
        root.IsExpanded = true;
        return new SolutionInfo { FilePath = projectPath, Projects = [project], Root = root };
    }

    /// <summary>A project's node; <paramref name="solutionFolder"/> is the solution folder it is in (null: the solution itself).</summary>
    public static ExplorerNode ProjectNode(ProjectInfo project, string? solutionFolder = null) =>
        new(NodeKind.Project, project.Name, project.FilePath, () => ProjectChildren(project)) { Project = project, SolutionFolder = solutionFolder };

    static IEnumerable<ExplorerNode> ProjectChildren(ProjectInfo project)
    {
        yield return new ExplorerNode(NodeKind.Dependencies, "Dependencies", null, () => DependencyGroups(project));
        foreach (var node in SolutionLoader.DirectoryChildren(project.Directory, isProjectRoot: true))
            yield return node;
    }

    static IEnumerable<ExplorerNode> DependencyGroups(ProjectInfo project)
    {
        if (project.TargetFrameworks.Count > 0)
            yield return new ExplorerNode(NodeKind.DependencyGroup, "Frameworks", null, () =>
                project.TargetFrameworks.Select(t => new ExplorerNode(NodeKind.Framework, FrameworkName(project, t))));
        if (project.Packages.Count > 0)
            yield return new ExplorerNode(NodeKind.DependencyGroup, "Packages", null, () =>
                project.Packages.Select(p => new ExplorerNode(NodeKind.Package, p.Name) { Detail = p.Version is { Length: > 0 } v ? $"({v})" : null }));
        if (project.ProjectReferences.Count > 0)
            yield return new ExplorerNode(NodeKind.DependencyGroup, "Projects", null, () =>
                project.ProjectReferences.Select(p => new ExplorerNode(NodeKind.ProjectReference, Path.GetFileNameWithoutExtension(p), p)));
    }

    static string FrameworkName(ProjectInfo project, string tfm) =>
        project.IsWeb ? $"Microsoft.AspNetCore.App ({tfm})" : $"Microsoft.NETCore.App ({tfm})";
}
