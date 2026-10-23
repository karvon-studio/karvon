namespace Karvon.Workspace;

/// <summary>
/// How a solution's module changes its solution file (.NET's .sln and .slnx): what Solution Explorer's commands (Add >
/// New / Existing Project, New Solution Folder, solution items, Remove, Rename) change in it. Solution folders are named
/// by their path in the solution: "/Solution Items/", "/src/libs/". A change that cannot be made throws
/// <see cref="InvalidOperationException"/> or <see cref="IOException"/>, with why.
/// </summary>
public interface ISolutionEditor
{
    /// <summary>The folder Visual Studio adds solution items to when they are added to the solution itself.</summary>
    public const string SolutionItems = "/Solution Items/";

    /// <summary>Adds projects (skipping those already in it) to the solution, in a solution folder when one is given.</summary>
    Task AddProjectsAsync(string solution, IEnumerable<string> projects, string? folder = null);

    Task RemoveProjectAsync(string solution, string project);

    /// <summary>Points the solution at a project's new file (Rename on a project renames its project file).</summary>
    Task MoveProjectAsync(string solution, string from, string to);

    /// <summary>Adds a solution folder named <paramref name="name"/> under <paramref name="parent"/> (null: the solution) and returns its path.</summary>
    Task<string> AddFolderAsync(string solution, string? parent, string name);

    /// <summary>Renames a solution folder; returns its new path.</summary>
    Task<string> RenameFolderAsync(string solution, string folder, string name);

    /// <summary>Removes a solution folder with everything in it (its projects stay on disk).</summary>
    Task RemoveFolderAsync(string solution, string folder);

    /// <summary>Adds files to a solution folder (created when it is not there yet) as solution items; they stay where they are.</summary>
    Task AddFilesAsync(string solution, string folder, IEnumerable<string> files);

    Task RemoveFileAsync(string solution, string folder, string file);

    /// <summary>A solution item's new name or place.</summary>
    Task MoveFileAsync(string solution, string folder, string from, string to);
}
