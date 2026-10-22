using Microsoft.VisualStudio.SolutionPersistence.Model;
using Microsoft.VisualStudio.SolutionPersistence.Serializer;

namespace VSL.Workspace;

/// <summary>
/// The changes Solution Explorer makes to a .sln/.slnx (Add > New / Existing Project, New Solution Folder, solution items,
/// Remove, Rename), made through Microsoft's own solution model, as Visual Studio and <c>dotnet sln</c> make them.
/// Solution folders are named by their path in the solution: "/Solution Items/", "/src/libs/".
/// </summary>
public static class SolutionEditor
{
    /// <summary>The folder VS adds solution items to when they are added to the solution itself.</summary>
    public const string SolutionItems = "/Solution Items/";

    /// <summary>Why a solution folder cannot be named <paramref name="name"/>, or null when it can.</summary>
    public static string? ValidateFolderName(string name) =>
        name.Trim().Length == 0 ? "Enter a name."
        : name.Trim() != name || name is "." or ".." || name.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|', '\0']) >= 0
            ? $"'{name}' is not a valid solution folder name."
            : null;

    /// <summary>Adds projects (skipping those already in it) to the solution, in a solution folder when one is given.</summary>
    public static Task AddProjectsAsync(string solutionPath, IEnumerable<string> projects, string? folder = null) =>
        EditAsync(solutionPath, (model, dir) =>
        {
            var parent = folder is null ? null : model.FindFolder(folder) ?? model.AddFolder(folder);
            foreach (var project in projects)
                if (ProjectAt(model, dir, project) is null)
                    model.AddProject(ModelPath(dir, project), null, parent);
        });

    public static Task RemoveProjectAsync(string solutionPath, string project) =>
        EditAsync(solutionPath, (model, dir) =>
        {
            if (ProjectAt(model, dir, project) is { } found)
                model.RemoveProject(found);
        });

    /// <summary>Points the solution at a project's new file (Rename on a project renames its project file).</summary>
    public static Task MoveProjectAsync(string solutionPath, string from, string to) =>
        EditAsync(solutionPath, (model, dir) =>
        {
            var project = ProjectAt(model, dir, from) ?? throw new InvalidOperationException($"{Path.GetFileName(from)} is not in the solution.");
            project.FilePath = ModelPath(dir, to);
            project.DisplayName = Path.GetFileNameWithoutExtension(to);
        });

    /// <summary>Adds a solution folder named <paramref name="name"/> under <paramref name="parent"/> (null: the solution) and returns its path.</summary>
    public static async Task<string> AddFolderAsync(string solutionPath, string? parent, string name)
    {
        if (ValidateFolderName(name) is { } error)
            throw new InvalidOperationException(error);
        var path = (parent ?? "/") + name + "/";
        await EditAsync(solutionPath, (model, _) =>
        {
            if (model.FindFolder(path) is not null)
                throw new InvalidOperationException($"A solution folder named '{name}' already exists here.");
            model.AddFolder(path);
        });
        return path;
    }

    /// <summary>Renames a solution folder; returns its new path.</summary>
    public static async Task<string> RenameFolderAsync(string solutionPath, string folder, string name)
    {
        if (ValidateFolderName(name) is { } error)
            throw new InvalidOperationException(error);
        var renamed = folder[..(folder.TrimEnd('/').LastIndexOf('/') + 1)] + name + "/";
        await EditAsync(solutionPath, (model, _) =>
        {
            var found = model.FindFolder(folder) ?? throw new InvalidOperationException("The solution folder is not in the solution any more.");
            if (renamed != folder && model.FindFolder(renamed) is not null)
                throw new InvalidOperationException($"A solution folder named '{name}' already exists here.");
            found.Name = name;
        });
        return renamed;
    }

    /// <summary>Removes a solution folder with everything in it (its projects stay on disk).</summary>
    public static Task RemoveFolderAsync(string solutionPath, string folder) =>
        EditAsync(solutionPath, (model, _) =>
        {
            if (model.FindFolder(folder) is { } found)
                model.RemoveFolder(found);
        });

    /// <summary>Adds files to a solution folder (created when it is not there yet) as solution items; they stay where they are.</summary>
    public static Task AddFilesAsync(string solutionPath, string folder, IEnumerable<string> files) =>
        EditAsync(solutionPath, (model, dir) =>
        {
            var found = model.FindFolder(folder) ?? model.AddFolder(folder);
            foreach (var file in files)
                if (FileIn(found, dir, file) is null)
                    found.AddFile(ModelPath(dir, file));
        });

    public static Task RemoveFileAsync(string solutionPath, string folder, string file) =>
        EditAsync(solutionPath, (model, dir) =>
        {
            if (model.FindFolder(folder) is { } found && FileIn(found, dir, file) is { } entry)
                found.RemoveFile(entry);
        });

    /// <summary>A solution item's new name or place.</summary>
    public static Task MoveFileAsync(string solutionPath, string folder, string from, string to) =>
        EditAsync(solutionPath, (model, dir) =>
        {
            if (model.FindFolder(folder) is not { } found)
                return;
            if (FileIn(found, dir, from) is { } entry)
                found.RemoveFile(entry);
            if (FileIn(found, dir, to) is null)
                found.AddFile(ModelPath(dir, to));
        });

    static async Task EditAsync(string solutionPath, Action<SolutionModel, string> change)
    {
        if (!SolutionLoader.IsSolutionFile(solutionPath))
            throw new InvalidOperationException("A project opened on its own has no solution file to change.");
        var serializer = SolutionSerializers.GetSerializerByMoniker(solutionPath)
            ?? throw new NotSupportedException($"Unsupported solution format: {solutionPath}");
        var model = await serializer.OpenAsync(solutionPath, CancellationToken.None);
        change(model, Path.GetDirectoryName(solutionPath)!);
        await serializer.SaveAsync(solutionPath, model, CancellationToken.None);
    }

    /// <summary>A path as the model keeps it: relative to the solution's folder (each format writes its own separators).</summary>
    static string ModelPath(string dir, string path) => Path.GetRelativePath(dir, path);

    static string FullPath(string dir, string modelPath) => Path.GetFullPath(Path.Combine(dir, modelPath.Replace('\\', '/')));

    static SolutionProjectModel? ProjectAt(SolutionModel model, string dir, string path) =>
        model.SolutionProjects.FirstOrDefault(p => FullPath(dir, p.FilePath) == Path.GetFullPath(path));

    static string? FileIn(SolutionFolderModel folder, string dir, string path) =>
        folder.Files?.FirstOrDefault(f => FullPath(dir, f) == Path.GetFullPath(path));
}
