namespace Karvon.Workspace;

public sealed class SolutionInfo
{
    /// <summary>The .sln/.slnx, the project file when a project was opened on its own (as VS allows), or a folder.</summary>
    public required string FilePath { get; init; }

    /// <summary>A folder opened as it is (VS's Open Folder): no solution, no project of its own.</summary>
    public bool IsFolder { get; init; }

    public bool IsProjectOnly => !IsFolder && SolutionLoader.IsProjectFile(FilePath);

    /// <summary>A .sln or .slnx: what Solution Explorer's solution commands change.</summary>
    public bool IsSolutionFile => !IsFolder && SolutionLoader.IsSolutionFile(FilePath);

    public string Name => IsFolder ? Path.GetFileName(FilePath.TrimEnd(Path.DirectorySeparatorChar)) : Path.GetFileNameWithoutExtension(FilePath);
    public string Directory => IsFolder ? FilePath : Path.GetDirectoryName(FilePath)!;
    public required IReadOnlyList<ProjectInfo> Projects { get; init; }
    public required ExplorerNode Root { get; init; }
}

/// <summary>
/// A folder opened as it is, and the folders and files Solution Explorer shows under a project or a folder; a solution
/// or a project is its module's to read (.NET's: .sln, .slnx and project files).
/// </summary>
public static class SolutionLoader
{
    static readonly HashSet<string> HiddenDirectories = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", "node_modules" };

    static readonly string[] ProjectExtensions = [".csproj", ".fsproj", ".vbproj"];

    public static bool IsProjectFile(string path) => ProjectExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static bool IsSolutionFile(string path) =>
        path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);

    /// <summary>A folder opened as it is: its files and folders under it, as VS's Folder View shows them.</summary>
    public static SolutionInfo LoadFolder(string folder)
    {
        folder = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
        var root = new ExplorerNode(NodeKind.Solution, Path.GetFileName(folder), folder, () => DirectoryChildren(folder)) { IsFolderRoot = true };
        root.IsExpanded = true;
        return new SolutionInfo { FilePath = folder, IsFolder = true, Projects = [], Root = root };
    }

    public static IEnumerable<ExplorerNode> DirectoryChildren(string directory, bool isProjectRoot = false)
    {
        var dirs = new DirectoryInfo(directory).EnumerateDirectories()
            .Where(d => !d.Name.StartsWith('.') && !(isProjectRoot && HiddenDirectories.Contains(d.Name)) && d.Name != "node_modules")
            .OrderBy(d => SpecialFolderRank(d.Name, isProjectRoot))
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Select(d => new ExplorerNode(NodeKind.Folder, d.Name, d.FullName, () => DirectoryChildren(d.FullName)));

        var fileNames = new DirectoryInfo(directory).EnumerateFiles()
            .Where(f => !f.Name.StartsWith('.') && !(isProjectRoot && f.Extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)))
            .Select(f => f.Name)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);

        var files = FileNesting.Nest(fileNames).Select(entry =>
        {
            var node = new ExplorerNode(NodeKind.File, entry.Name, Path.Combine(directory, entry.Name));
            foreach (var child in entry.Children)
                node.Children.Add(new ExplorerNode(NodeKind.File, child, Path.Combine(directory, child)));
            return node;
        });
        return dirs.Concat(files);
    }

    static int SpecialFolderRank(string name, bool isProjectRoot) => !isProjectRoot ? 0 : name switch
    {
        "Properties" => 0,
        "wwwroot" => 1,
        _ => 2,
    };
}
