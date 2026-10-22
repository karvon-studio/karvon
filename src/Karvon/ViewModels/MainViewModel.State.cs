using CommunityToolkit.Mvvm.ComponentModel;
using Karvon.Editor;
using Karvon.Services;
using Karvon.Workspace;

namespace Karvon.ViewModels;

/// <summary>Restoring and saving everything about the open solution in its .vs folder (see Services.SolutionState).</summary>
public partial class MainViewModel
{
    SolutionState _state = new();
    bool _restoringState;

    /// <summary>Whether opening a solution brings back its saved state (tests turn it off to start clean).</summary>
    public bool RestoresSolutionState { get; set; } = true;

    /// <summary>The node selected in Solution Explorer (bound to the tree).</summary>
    [ObservableProperty]
    public partial ExplorerNode? SelectedExplorerNode { get; set; }

    void RestoreSolutionState(SolutionInfo solution, SolutionState state)
    {
        Configuration = state.Configuration;
        LoadDebugState(state);
        foreach (var p in solution.Projects.Where(p => p.IsRunnable))
            StartupProjects.Add(p);
        // The launch profile comes back with the startup project (OnStartupProjectChanged reads it from the state).
        StartupProject = StartupProjects.FirstOrDefault(p => p.FilePath == state.StartupProject)
            ?? StartupProjects.FirstOrDefault(p => p.IsWeb)
            ?? StartupProjects.FirstOrDefault();

        DocumentGroupsVertical = state.DocumentGroupsVertical;
        var floating = new Dictionary<int, DocumentGroup>();
        var front = new List<(DocumentGroup Group, DocumentTab Doc)>();
        foreach (var saved in state.Documents.Where(d => File.Exists(d.Path)))
        {
            // Groups of the main window come back in order; each floating window as a window of its own.
            var group = saved.Group >= 0 ? DocumentGroupAt(Math.Min(saved.Group, 8)) : floating.GetValueOrDefault(saved.Group);
            DocumentViewModel? doc;
            using (PlaceNewDocuments(group ?? DocumentGroups[0], int.MaxValue))
                doc = OpenDocument(saved.Path, activate: false);
            if (doc is null)
                continue;
            if (saved.Group < 0 && group is null)
            {
                FloatDocument(doc);
                if (GroupOf(doc) is { IsFloating: true } window)
                    floating[saved.Group] = window;
            }
            doc.IsPinned = saved.Pinned;
            doc.Host.RestoreViewState(new EditorViewState(saved.CaretLine, saved.CaretColumn, saved.FirstVisibleLine, saved.Collapsed));
            if (saved.Front && GroupOf(doc) is { } its)
                front.Add((its, doc));
        }
        foreach (var empty in DocumentGroups.Where(g => g.Tabs.Count == 0).ToList())
            RemoveEmptyGroup(empty);
        foreach (var (group, doc) in front)
            group.Active = doc;
        var root = solution.Root;
        foreach (var key in state.ExpandedNodes)
            if (FindExplorerNode(root, key) is { } node)
            {
                ExpandAncestors(root, node);
                node.IsExpanded = true;
            }
        if (state.SelectedNode is { } selected && FindExplorerNode(root, selected) is { } selectedNode)
        {
            ExpandAncestors(root, selectedNode);
            SelectedExplorerNode = selectedNode;
            ExplorerSelectRequested?.Invoke(selectedNode);
        }
        // Last: selecting a file in Solution Explorer may open it.
        ActiveDocument = Editors.FirstOrDefault(d => d.FilePath == state.ActiveDocument) ?? Documents.FirstOrDefault();
    }

    void CaptureSolutionState(SolutionState state)
    {
        // In the order of their groups' tabs, so each group comes back as it was.
        state.Documents = AllGroups.SelectMany(g => g.Tabs).OfType<DocumentViewModel>().Select(d =>
        {
            var view = d.Host.CaptureViewState();
            var group = GroupOf(d);
            return new SolutionState.DocumentState
            {
                Path = d.FilePath,
                CaretLine = view.CaretLine,
                CaretColumn = view.CaretColumn,
                FirstVisibleLine = view.FirstVisibleLine,
                Pinned = d.IsPinned,
                Collapsed = [.. view.Collapsed],
                Group = group is null ? 0 : group.IsFloating ? -1 - FloatingGroups.IndexOf(group) : DocumentGroups.IndexOf(group),
                Front = group?.Active == d,
            };
        }).ToList();
        state.DocumentGroupsVertical = DocumentGroupsVertical;
        state.ActiveDocument = ActiveEditor?.FilePath;
        state.StartupProject = StartupProject?.FilePath ?? state.StartupProject;
        state.LaunchProfile = LaunchProfile ?? state.LaunchProfile;
        state.Configuration = Configuration;
        SaveDebugState(state);
        // A Claude panel never opened this time keeps the conversation it would restore.
        if (_claude is { } claude)
            state.ClaudeConversation = claude.SessionId;
        if (ExplorerRoots.FirstOrDefault() is { } root)
        {
            var expanded = new List<string>();
            CollectExpanded(root, "", expanded);
            state.ExpandedNodes = expanded;
            state.SelectedNode = SelectedExplorerNode is { } node ? KeyOf(root, node) : null;
        }
    }

    /// <summary>Expanded nodes under <paramref name="node"/> (the solution itself is always expanded).</summary>
    static void CollectExpanded(ExplorerNode node, string folderPath, List<string> keys)
    {
        foreach (var child in node.Children)
        {
            var childFolder = child.Kind == NodeKind.SolutionFolder ? (folderPath.Length == 0 ? child.Name : folderPath + "/" + child.Name) : folderPath;
            if (child.IsExpanded && Key(child, childFolder) is { } key)
                keys.Add(key);
            // Only what has been loaded: a folder never expanded has nothing expanded inside.
            if (child.IsLoaded && child.Kind is not (NodeKind.Dependencies or NodeKind.DependencyGroup))
                CollectExpanded(child, childFolder, keys);
        }
    }

    static string? Key(ExplorerNode node, string solutionFolderPath) => node.Kind switch
    {
        NodeKind.SolutionFolder => "folder:" + solutionFolderPath,
        NodeKind.Project or NodeKind.Folder or NodeKind.File when node.Path is not null => node.Path,
        _ => null,
    };

    static string? KeyOf(ExplorerNode root, ExplorerNode target)
    {
        string? Search(ExplorerNode node, string folderPath)
        {
            foreach (var child in node.Children)
            {
                var childFolder = child.Kind == NodeKind.SolutionFolder ? (folderPath.Length == 0 ? child.Name : folderPath + "/" + child.Name) : folderPath;
                if (child == target)
                    return Key(child, childFolder);
                if ((child.IsLoaded || child.Kind == NodeKind.SolutionFolder) && Search(child, childFolder) is { } found)
                    return found;
            }
            return null;
        }
        return Search(root, "");
    }

    static ExplorerNode? FindExplorerNode(ExplorerNode root, string key)
    {
        if (!key.StartsWith("folder:", StringComparison.Ordinal))
            return root.FindByPath(key);
        var node = root;
        foreach (var name in key["folder:".Length..].Split('/'))
        {
            node = node.Children.FirstOrDefault(c => c.Kind == NodeKind.SolutionFolder && c.Name == name);
            if (node is null)
                return null;
        }
        return node;
    }
}
