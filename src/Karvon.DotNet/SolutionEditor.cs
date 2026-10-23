using Karvon.Workspace;
using Microsoft.VisualStudio.SolutionPersistence.Model;
using Microsoft.VisualStudio.SolutionPersistence.Serializer;

namespace Karvon.DotNet;

/// <summary>
/// The changes Solution Explorer makes to a .sln/.slnx, made through Microsoft's own solution model, as Visual Studio
/// and <c>dotnet sln</c> make them.
/// </summary>
public sealed class SolutionEditor : ISolutionEditor
{
    public static SolutionEditor Instance { get; } = new();

    SolutionEditor()
    {
    }

    /// <summary>Why a solution folder cannot be named <paramref name="name"/>, or null when it can.</summary>
    public static string? ValidateFolderName(string name) =>
        name.Trim().Length == 0 ? "Enter a name."
        : name.Trim() != name || name is "." or ".." || name.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|', '\0']) >= 0
            ? $"'{name}' is not a valid solution folder name."
            : null;

    public Task AddProjectsAsync(string solutionPath, IEnumerable<string> projects, string? folder = null) =>
        EditAsync(solutionPath, (model, dir) =>
        {
            var parent = folder is null ? null : model.FindFolder(folder) ?? model.AddFolder(folder);
            foreach (var project in projects)
                if (ProjectAt(model, dir, project) is null)
                    model.AddProject(ModelPath(dir, project), null, parent);
        });

    public Task RemoveProjectAsync(string solutionPath, string project) =>
        EditAsync(solutionPath, (model, dir) =>
        {
            if (ProjectAt(model, dir, project) is { } found)
                model.RemoveProject(found);
        });

    public Task MoveProjectAsync(string solutionPath, string from, string to) =>
        EditAsync(solutionPath, (model, dir) =>
        {
            var project = ProjectAt(model, dir, from) ?? throw new InvalidOperationException($"{Path.GetFileName(from)} is not in the solution.");
            project.FilePath = ModelPath(dir, to);
            project.DisplayName = Path.GetFileNameWithoutExtension(to);
        });

    public async Task<string> AddFolderAsync(string solutionPath, string? parent, string name)
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

    public async Task<string> RenameFolderAsync(string solutionPath, string folder, string name)
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

    public Task RemoveFolderAsync(string solutionPath, string folder) =>
        EditAsync(solutionPath, (model, _) =>
        {
            if (model.FindFolder(folder) is { } found)
                model.RemoveFolder(found);
        });

    public Task AddFilesAsync(string solutionPath, string folder, IEnumerable<string> files) =>
        EditAsync(solutionPath, (model, dir) =>
        {
            var found = model.FindFolder(folder) ?? model.AddFolder(folder);
            foreach (var file in files)
                if (FileIn(found, dir, file) is null)
                    found.AddFile(ModelPath(dir, file));
        });

    public Task RemoveFileAsync(string solutionPath, string folder, string file) =>
        EditAsync(solutionPath, (model, dir) =>
        {
            if (model.FindFolder(folder) is { } found && FileIn(found, dir, file) is { } entry)
                found.RemoveFile(entry);
        });

    public Task MoveFileAsync(string solutionPath, string folder, string from, string to) =>
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
        // Writing locks the file (flock on Unix): a reader holding it a moment (the language server, a restore) makes it
        // fail, so the edit is made again from the file as it is then.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var model = await serializer.OpenAsync(solutionPath, CancellationToken.None);
                change(model, Path.GetDirectoryName(solutionPath)!);
                await serializer.SaveAsync(solutionPath, model, CancellationToken.None);
                return;
            }
            catch (IOException) when (attempt < 20 && File.Exists(solutionPath))
            {
                await Task.Delay(100);
            }
            catch (Exception ex) when (ex is SolutionException or ArgumentException)
            {
                throw new InvalidOperationException(ex.Message, ex);
            }
        }
    }

    /// <summary>A path as the model keeps it: relative to the solution's folder (each format writes its own separators).</summary>
    static string ModelPath(string dir, string path) => Path.GetRelativePath(dir, path);

    static string FullPath(string dir, string modelPath) => Path.GetFullPath(Path.Combine(dir, modelPath.Replace('\\', '/')));

    static SolutionProjectModel? ProjectAt(SolutionModel model, string dir, string path) =>
        model.SolutionProjects.FirstOrDefault(p => FullPath(dir, p.FilePath) == Path.GetFullPath(path));

    static string? FileIn(SolutionFolderModel folder, string dir, string path) =>
        folder.Files?.FirstOrDefault(f => FullPath(dir, f) == Path.GetFullPath(path));
}
