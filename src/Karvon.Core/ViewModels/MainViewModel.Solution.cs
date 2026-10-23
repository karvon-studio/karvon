using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Karvon.Build;
using Karvon.Git;
using Karvon.Services;
using Karvon.Workspace;

namespace Karvon.ViewModels;

/// <summary>A project Add > Project Reference offers: one of the solution's, and whether it is referenced now.</summary>
public sealed record ProjectReferenceChoice(string Name, string Path, bool IsReferenced);

/// <summary>
/// Solution Explorer's commands on the solution, its solution folders, projects and dependencies, as Visual Studio's
/// context menus have them: Add > New / Existing Project, New Solution Folder and solution items, Remove, Rename, project
/// references and packages, user secrets, restore, a Git repository. The solution file changes through
/// <see cref="SolutionEditor"/>; then the tree is read again, with what was expanded still expanded.
/// </summary>
public partial class MainViewModel
{
    Task _solutionReload = Task.CompletedTask;

    /// <summary>The solution's and its projects' files as Karvon last read them: a change on disk to any of them reads them again.</summary>
    readonly Dictionary<string, DateTime> _structureRead = [];

    /// <summary>A solution file to change: a project opened on its own has none, nor a folder.</summary>
    public bool HasSolutionFile => Solution is { IsSolutionFile: true };

    /// <summary>
    /// Reads the solution again after it or a project file changed (here or on disk), keeping what is expanded, and selects
    /// <paramref name="select"/>: a file or folder's path, or a solution folder's path in the solution ("/libs/").
    /// </summary>
    public Task ReloadSolutionAsync(string? select = null) => _solutionReload = ReloadAfterAsync(_solutionReload, select);

    async Task ReloadAfterAsync(Task previous, string? select)
    {
        await previous;
        if (Solution is not { } solution || ExplorerRoots.FirstOrDefault() is not { } old)
            return;
        SolutionInfo reloaded;
        try
        {
            reloaded = await SolutionLoader.LoadAsync(solution.FilePath);
        }
        catch (Exception ex)
        {
            SetStatus("The solution could not be read again: " + ex.Message);
            return;
        }
        if (Solution != solution)
            return;
        var expanded = new List<string>();
        CollectExpanded(old, "", expanded);
        var selected = select is not null ? (select.EndsWith('/') ? "folder:" + select.Trim('/') : select)
            : SelectedExplorerNode is { } node ? KeyOf(old, node) : null;
        var startup = StartupProject?.FilePath;
        var profile = LaunchProfile;
        Solution = reloaded;
        StartupProjects.Clear();
        foreach (var p in reloaded.Projects.Where(p => p.IsRunnable))
            StartupProjects.Add(p);
        StartupProject = StartupProjects.FirstOrDefault(p => p.FilePath == startup)
            ?? StartupProjects.FirstOrDefault(p => p.IsWeb)
            ?? StartupProjects.FirstOrDefault();
        if (profile is not null && LaunchProfiles.Contains(profile))
            LaunchProfile = profile;
        AddWorkspaceProjects(reloaded);
        NoteStructureRead(reloaded);
        reloaded.Root.RefreshGitBadges();
        ExplorerRoots.Clear();
        ExplorerRoots.Add(reloaded.Root);
        foreach (var key in expanded)
            if (FindExplorerNode(reloaded.Root, key) is { } again)
                again.IsExpanded = true;
        if (selected is not null && FindExplorerNode(reloaded.Root, selected) is { } target)
        {
            ExpandAncestors(reloaded.Root, target);
            SelectedExplorerNode = target;
            ExplorerSelectRequested?.Invoke(target);
        }
        // Its language server loads the projects it does not have yet (added, or a project file renamed).
        SolutionReloaded?.Invoke();
    }

    public event Action? SolutionReloaded;

    /// <summary>
    /// The modules' projects beside the solution's (a frontend's package.json folder) under the solution, after its own:
    /// those that no project of the solution holds in its folder, where they show already.
    /// </summary>
    void AddWorkspaceProjects(SolutionInfo solution)
    {
        if (solution.IsFolder)
            return;
        foreach (var module in Modules)
        {
            IReadOnlyList<Modules.WorkspaceProject> projects;
            try
            {
                projects = module.WorkspaceProjects(solution.Directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                continue;
            }
            foreach (var project in projects)
            {
                if (solution.Projects.Any(p => Services.FilePaths.IsSameOrUnder(project.Directory, p.Directory))
                    || solution.Root.Children.Any(n => n.WorkspaceProject?.Directory == project.Directory))
                    continue;
                solution.Root.Children.Add(new ExplorerNode(NodeKind.Folder, project.Name, project.Directory,
                    () => SolutionLoader.DirectoryChildren(project.Directory, isProjectRoot: true))
                {
                    Detail = project.Kind is { Length: > 0 } kind ? $"({kind})" : null,
                    WorkspaceProject = project,
                });
            }
        }
    }

    void NoteStructureRead(SolutionInfo solution)
    {
        _structureRead.Clear();
        foreach (var file in solution.Projects.Select(p => p.FilePath).Prepend(solution.FilePath))
            _structureRead[file] = File.GetLastWriteTimeUtc(file);
    }

    /// <summary>Whether files changed on disk include the solution's or a project's file, changed since Karvon read it.</summary>
    bool ChangesStructure(IEnumerable<string> files) =>
        Solution is { } solution && files.Any(f => (f == solution.FilePath || solution.Projects.Any(p => p.FilePath == f))
            && (!_structureRead.TryGetValue(f, out var read) || File.GetLastWriteTimeUtc(f) != read));

    /// <summary>A change to the solution file; false, with the reason in the status bar, when it could not be made.</summary>
    async Task<bool> EditSolutionAsync(Func<string, Task> change)
    {
        if (Solution is not { IsSolutionFile: true } solution)
            return false;
        try
        {
            await change(solution.FilePath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException
                                       or ArgumentException or Microsoft.VisualStudio.SolutionPersistence.Model.SolutionException)
        {
            SetStatus(ex.Message);
            return false;
        }
    }

    // ---- Add ---------------------------------------------------------------------------------------------------

    /// <summary>Add > New Project...: the "Add a new project" wizard, then the project in the solution (in the solution folder clicked).</summary>
    public async Task<IReadOnlyList<string>> AddNewProjectAsync(ExplorerNode? node)
    {
        if (Dialogs is null || Solution is not { IsSolutionFile: true } solution)
            return [];
        var projects = await Dialogs.CreateProjectAsync(solution.FilePath);
        return await AddProjectsAsync(projects, node) ? projects : [];
    }

    /// <summary>Add > Existing Project...: project files from anywhere, added where they are.</summary>
    public async Task<IReadOnlyList<string>> AddExistingProjectsAsync(ExplorerNode? node)
    {
        if (Dialogs is null || Solution is not { IsSolutionFile: true } solution)
            return [];
        var picked = (await Dialogs.PickProjectsAsync()).Where(SolutionLoader.IsProjectFile).ToList();
        var projects = picked.Where(p => solution.Projects.All(known => known.FilePath != p)).ToList();
        if (projects.Count < picked.Count)
            SetStatus("The solution already has that project.");
        return await AddProjectsAsync(projects, node) ? projects : [];
    }

    async Task<bool> AddProjectsAsync(IReadOnlyList<string> projects, ExplorerNode? node)
    {
        var folder = node is { Kind: NodeKind.SolutionFolder } ? node.SolutionFolder : null;
        if (projects.Count == 0 || !await EditSolutionAsync(solution => SolutionEditor.AddProjectsAsync(solution, projects, folder)))
            return false;
        await ReloadSolutionAsync(projects[0]);
        SetStatus(projects.Count == 1 ? $"{Path.GetFileNameWithoutExtension(projects[0])} was added to the solution." : $"{projects.Count} projects were added to the solution.");
        return true;
    }

    /// <summary>Add > New Solution Folder: "NewFolder1" (or the next free number), then renamed in place.</summary>
    public async Task<ExplorerNode?> AddSolutionFolderAsync(ExplorerNode? node)
    {
        if (!HasSolutionFile || ExplorerRoots.FirstOrDefault() is not { } root)
            return null;
        var parent = node is { Kind: NodeKind.SolutionFolder } ? node : root;
        var taken = parent.Children.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var name = Enumerable.Range(1, 1000).Select(i => "NewFolder" + i).First(n => !taken.Contains(n));
        var path = "";
        if (!await EditSolutionAsync(async solution => path = await SolutionEditor.AddFolderAsync(solution, parent.SolutionFolder, name)))
            return null;
        await ReloadSolutionAsync(path);
        var created = FindExplorerNode(ExplorerRoots[0], "folder:" + path.Trim('/'));
        if (created is not null)
            ExplorerRenameRequested?.Invoke(created);
        return created;
    }

    /// <summary>
    /// Add > New Item on the solution or a solution folder: a file in the solution's folder, listed in the solution (in
    /// Solution Items on the solution itself), as VS adds solution items.
    /// </summary>
    async Task<string?> AddNewSolutionItemAsync(ExplorerNode node, ItemTemplate? template)
    {
        if (Dialogs is null || Solution is not { } solution)
            return null;
        var folder = node.SolutionFolder ?? SolutionEditor.SolutionItems;
        var choice = await Dialogs.ChooseNewItemAsync($"Add New Item - {folder.Trim('/').Split('/')[^1]}", solution.Directory, template ?? ItemTemplates.TextFile);
        if (choice is null || WriteItem(solution.Directory, choice.Template, choice.Name) is not { } path
            || !await EditSolutionAsync(sln => SolutionEditor.AddFilesAsync(sln, folder, [path])))
            return null;
        await ReloadSolutionAsync(path);
        OpenDocument(path);
        return path;
    }

    /// <summary>Add > Existing Item on the solution or a solution folder: the files, listed in the solution where they are.</summary>
    async Task<IReadOnlyList<string>> AddExistingSolutionItemsAsync(ExplorerNode node)
    {
        if (Dialogs is null)
            return [];
        var files = await Dialogs.PickFilesAsync("Add Existing Item");
        if (files.Count == 0 || !await EditSolutionAsync(sln => SolutionEditor.AddFilesAsync(sln, node.SolutionFolder ?? SolutionEditor.SolutionItems, files)))
            return [];
        await ReloadSolutionAsync(files[^1]);
        return files;
    }

    /// <summary>Add > New EditorConfig: the SDK's .editorconfig for .NET in the folder, opened.</summary>
    public async Task<string?> AddEditorConfigAsync(ExplorerNode? node)
    {
        if (FolderOf(node ?? ExplorerTarget()) is not { } folder)
            return null;
        var path = Path.Combine(folder, ".editorconfig");
        if (!File.Exists(path) && !await RunDotnetAsync(["new", "editorconfig", "--output", folder]))
            return null;
        OpenDocument(path);
        return path;
    }

    // ---- Remove and rename -------------------------------------------------------------------------------------

    /// <summary>Whether Remove (and not Delete) is what Del does: the node is the solution's (a project, a solution folder, a solution item).</summary>
    public static bool IsInSolution(ExplorerNode node) => node.Kind is NodeKind.Project or NodeKind.SolutionFolder || node.IsSolutionItem;

    /// <summary>Remove (Del) on a project, a solution folder or a solution item: out of the solution, after asking; the files stay.</summary>
    public async Task<bool> RemoveFromSolutionAsync(ExplorerNode? node)
    {
        if (Dialogs is null || !HasSolutionFile || node is null)
            return false;
        Func<string, Task>? change = node switch
        {
            { Kind: NodeKind.Project, Path: { } project } => solution => SolutionEditor.RemoveProjectAsync(solution, project),
            { Kind: NodeKind.SolutionFolder, SolutionFolder: { } folder } => solution => SolutionEditor.RemoveFolderAsync(solution, folder),
            { IsSolutionItem: true, Path: { } file } => solution => SolutionEditor.RemoveFileAsync(solution, node.SolutionFolder!, file),
            _ => null,
        };
        if (change is null)
            return false;
        var question = node.Kind == NodeKind.SolutionFolder ? $"'{node.Name}' and all its contents will be removed." : $"'{node.Name}' will be removed.";
        if (await Dialogs.ConfirmAsync("Karvon", question) != true || !await EditSolutionAsync(change))
            return false;
        await ReloadSolutionAsync();
        return true;
    }

    /// <summary>
    /// Rename on the solution (its file, and the .vs state with it), a solution folder, a project (its project file, and the
    /// references to it) or a solution item (the file, and the solution's entry).
    /// </summary>
    public async Task<bool> RenameInSolutionAsync(ExplorerNode node, string newName)
    {
        newName = newName.Trim();
        switch (node)
        {
            case { Kind: NodeKind.SolutionFolder, SolutionFolder: { } folder } when newName != node.Name:
                var renamed = "";
                if (!await EditSolutionAsync(async solution => renamed = await SolutionEditor.RenameFolderAsync(solution, folder, newName)))
                    return false;
                await ReloadSolutionAsync(renamed);
                return true;
            case { Kind: NodeKind.Project, Path: { } project }:
                return await RenameProjectAsync(project, newName);
            case { IsSolutionItem: true, Path: { } file } when newName != node.Name:
                var directory = Path.GetDirectoryName(file)!;
                if (ItemTemplates.Validate(directory, newName) is { } error)
                {
                    SetStatus(error);
                    return false;
                }
                var target = Path.Combine(directory, newName);
                if (!MovePaths([(file, target)]))
                    return false;
                if (!await EditSolutionAsync(solution => SolutionEditor.MoveFileAsync(solution, node.SolutionFolder!, file, target)))
                {
                    MovePaths([(target, file)]);
                    return false;
                }
                await ReloadSolutionAsync(target);
                return true;
            case { Kind: NodeKind.Solution }:
                return await RenameSolutionAsync(newName);
        }
        return false;
    }

    async Task<bool> RenameProjectAsync(string project, string newName)
    {
        if (Solution is not { IsSolutionFile: true } solution || newName.Length == 0 || newName == Path.GetFileNameWithoutExtension(project))
            return false;
        var directory = Path.GetDirectoryName(project)!;
        var target = Path.Combine(directory, newName + Path.GetExtension(project));
        if (ItemTemplates.Validate(directory, Path.GetFileName(target)) is { } error)
        {
            SetStatus(error);
            return false;
        }
        var wasStartup = StartupProject?.FilePath == project;
        if (!MovePaths([(project, target)]))
            return false;
        if (!await EditSolutionAsync(sln => SolutionEditor.MoveProjectAsync(sln, project, target)))
        {
            MovePaths([(target, project)]);
            return false;
        }
        // The projects that reference it, at its new file.
        foreach (var other in solution.Projects.Where(p => p.ProjectReferences.Contains(project)))
            RetargetReference(other.FilePath, project, target);
        await ReloadSolutionAsync(target);
        if (wasStartup && StartupProjects.FirstOrDefault(p => p.FilePath == target) is { } renamed)
            StartupProject = renamed;
        return true;
    }

    /// <summary>The solution's file renamed: closed, renamed (its .vs state too) and opened again, as it is another solution now.</summary>
    async Task<bool> RenameSolutionAsync(string newName)
    {
        if (Solution is not { IsSolutionFile: true } solution || newName == solution.Name)
            return false;
        var target = Path.Combine(solution.Directory, newName + Path.GetExtension(solution.FilePath));
        if (ItemTemplates.Validate(solution.Directory, Path.GetFileName(target)) is { } error)
        {
            SetStatus(error);
            return false;
        }
        if (!await CloseSolutionCoreAsync())
            return false;
        try
        {
            File.Move(solution.FilePath, target);
            var state = Path.Combine(solution.Directory, ".vs", solution.Name);
            var renamedState = Path.Combine(solution.Directory, ".vs", newName);
            if (Directory.Exists(state) && !Directory.Exists(renamedState))
                Directory.Move(state, renamedState);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await OpenSolutionAsync(File.Exists(solution.FilePath) ? solution.FilePath : target);
            SetStatus(ex.Message);
            return false;
        }
        Settings.RemoveRecent(solution.FilePath);
        await OpenSolutionAsync(target);
        return true;
    }

    /// <summary>Points a project's ProjectReference at a renamed project file, leaving the rest of the file as it is written.</summary>
    static void RetargetReference(string projectFile, string from, string to)
    {
        var directory = Path.GetDirectoryName(projectFile)!;
        try
        {
            var bytes = File.ReadAllBytes(projectFile);
            var bom = bytes is [0xEF, 0xBB, 0xBF, ..];
            var text = Encoding.UTF8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
            var updated = ProjectReferenceInclude().Replace(text, m =>
            {
                var include = m.Groups[2].Value;
                if (Path.GetFullPath(Path.Combine(directory, include.Replace('\\', '/'))) != from)
                    return m.Value;
                var relative = Path.GetRelativePath(directory, to);
                return m.Groups[1].Value + (include.Contains('\\') ? relative.Replace('/', '\\') : relative) + m.Groups[3].Value;
            });
            if (updated != text)
                File.WriteAllText(projectFile, updated, new UTF8Encoding(bom));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The reference shows as missing in the project's Dependencies.
        }
    }

    [GeneratedRegex(@"(<ProjectReference\s+Include\s*=\s*"")([^""]+)("")")]
    private static partial Regex ProjectReferenceInclude();

    // ---- Dependencies ------------------------------------------------------------------------------------------

    /// <summary>Add > Project Reference...: the Reference Manager's projects, then dotnet add / remove reference for what changed.</summary>
    public async Task<bool> EditProjectReferencesAsync(ProjectInfo? project)
    {
        if (Dialogs is null || Solution is not { } solution || project is null)
            return false;
        var candidates = solution.Projects.Where(p => p.FilePath != project.FilePath)
            .Select(p => new ProjectReferenceChoice(p.Name, p.FilePath, project.ProjectReferences.Contains(p.FilePath)))
            .ToList();
        if (await Dialogs.ChooseProjectReferencesAsync(project.Name, candidates) is not { } chosen)
            return false;
        var added = chosen.Where(p => !project.ProjectReferences.Contains(p)).ToList();
        var removed = candidates.Where(c => c.IsReferenced && !chosen.Contains(c.Path)).Select(c => c.Path).ToList();
        if (added.Count == 0 && removed.Count == 0)
            return false;
        var ok = (added.Count == 0 || await RunDotnetAsync(["add", project.FilePath, "reference", .. added]))
            && (removed.Count == 0 || await RunDotnetAsync(["remove", project.FilePath, "reference", .. removed]));
        await ReloadSolutionAsync();
        return ok;
    }

    /// <summary>Remove on a project reference in Dependencies.</summary>
    public async Task<bool> RemoveProjectReferenceAsync(ProjectInfo project, string reference)
    {
        var ok = await RunDotnetAsync(["remove", project.FilePath, "reference", reference]);
        await ReloadSolutionAsync();
        return ok;
    }

    // ---- The solution ------------------------------------------------------------------------------------------

    /// <summary>Create Git Repository: git init in the solution's folder, the SDK's .gitignore for .NET when it has none, then Git Changes.</summary>
    public async Task<bool> CreateGitRepositoryAsync()
    {
        if (Solution is not { } solution || HasGit)
            return false;
        var init = await GitRepository.InitAsync(solution.Directory);
        if (!init.Success)
        {
            SetStatus("git init failed: " + init.Output.Trim());
            return false;
        }
        if (!File.Exists(Path.Combine(solution.Directory, ".gitignore")))
            await RunDotnetAsync(["new", "gitignore", "--output", solution.Directory]);
        CloseGit();
        await OpenGitAsync(solution);
        ShowToolWindow(ToolWindows.GitChanges);
        SetStatus("A Git repository was created in " + solution.Directory);
        return HasGit;
    }

    /// <summary>The dotnet CLI in the solution's folder, its output in Output (Build); false, with its last line in the status bar, when it failed.</summary>
    async Task<bool> RunDotnetAsync(IReadOnlyList<string> args)
    {
        if (Solution is not { } solution)
            return false;
        BuildOutput.Append("> dotnet " + string.Join(' ', args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)));
        var last = "";
        using var runner = ProcessRunner.Start(DotnetCli.Executable, args, solution.Directory, line =>
        {
            BuildOutput.Append(line);
            if (line.Trim().Length > 0)
                last = line.Trim();
        });
        if (await runner.WaitAsync() == 0)
            return true;
        SetStatus(last.Length > 0 ? last : $"dotnet {args[0]} failed.");
        return false;
    }
}
