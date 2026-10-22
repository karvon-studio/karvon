using CommunityToolkit.Mvvm.ComponentModel;
using Karvon.Git;

namespace Karvon.ViewModels;

/// <summary>
/// A git repository of the solution, as Git Changes lists them: the one the solution is in, those in its folders, and
/// those of projects linked from other folders. Its branch, how far it is from its upstream, and its changes.
/// </summary>
public sealed partial class GitRepoItem(GitRepository repository) : ObservableObject
{
    public GitRepository Repository { get; } = repository;

    public string Root => Repository.Root;

    public string Name { get; } = Path.GetFileName(repository.Root.TrimEnd('/')) is { Length: > 0 } name ? name : repository.Root;

    [ObservableProperty]
    public partial GitStatus? Status { get; set; }

    public string Branch => Status?.Branch.Branch ?? "(detached HEAD)";

    public int Ahead => Status?.Branch.Ahead ?? 0;

    public int Behind => Status?.Branch.Behind ?? 0;

    public IReadOnlyList<GitFileChange> Changes => Status?.Changes ?? [];

    public bool HasStaged => Changes.Any(c => c.Staged);

    /// <summary>The scope chosen in the repository menu is this one alone.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    partial void OnStatusChanged(GitStatus? value)
    {
        OnPropertyChanged(nameof(Branch));
        OnPropertyChanged(nameof(Ahead));
        OnPropertyChanged(nameof(Behind));
    }

    /// <summary>Whether a file of the disk is in this repository's working tree.</summary>
    public bool Contains(string path) => path == Root || path.StartsWith(Root + "/", StringComparison.Ordinal);
}

public enum GitRowKind
{
    /// <summary>The folder the repositories share (shown when there are several).</summary>
    Root,
    Repository,
    Folder,
    File,
}

/// <summary>
/// A row of a Git Changes section, drawn as a tree as in Visual Studio: the shared folder, a repository, folders (a chain
/// of single folders shown as one, "Components/Camera"), and the changed files. The section shows the rows that are
/// visible, each indented by its depth.
/// </summary>
public sealed partial class GitTreeRow : ObservableObject
{
    GitTreeRow(GitRowKind kind, string name, GitRepoItem? repository, string path, bool staged, GitFileItem? item = null)
    {
        Kind = kind;
        Name = name;
        Repository = repository;
        Path = path;
        IsStaged = staged;
        Item = item;
    }

    public GitRowKind Kind { get; }

    public string Name { get; }

    public GitRepoItem? Repository { get; }

    /// <summary>The path inside the repository ("" for a repository and the shared folder).</summary>
    public string Path { get; }

    public bool IsStaged { get; }

    /// <summary>The change of a file row.</summary>
    public GitFileItem? Item { get; }

    public List<GitTreeRow> Children { get; } = [];

    public int Depth { get; private set; }

    public double Indent => Depth * 14;

    public bool IsExpandable => Kind != GitRowKind.File;

    /// <summary>Keeps a row collapsed across refreshes.</summary>
    public string Key => $"{(IsStaged ? "S" : "U")}|{Repository?.Root}|{Kind}|{Path}";

    [ObservableProperty]
    public partial bool IsExpanded { get; set; } = true;

    public string Icon => Kind switch
    {
        GitRowKind.Repository => "Repo",
        GitRowKind.Root or GitRowKind.Folder => "material:" + Views.MaterialIcons.ForFolder(Name.Split('/')[^1], IsExpanded),
        _ => "material:" + Views.MaterialIcons.ForFile(Name),
    };

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(Icon));

    public string Letter => Item?.Letter ?? "";

    public string LetterColor => Item?.LetterColor ?? "#00000000";

    public string? ToolTip => Kind switch
    {
        GitRowKind.File => Item!.Change.RelativePath,
        GitRowKind.Repository => Repository!.Root,
        _ => null,
    };

    /// <summary>The tree of one section from its changes; with several repositories, under the folder they share.</summary>
    public static List<GitTreeRow> Build(IEnumerable<GitFileItem> items, bool staged, bool showRoot)
    {
        var repositories = new List<GitTreeRow>();
        foreach (var group in items.Where(i => i.Repository is not null).GroupBy(i => i.Repository!).OrderBy(g => g.Key.Name, StringComparer.OrdinalIgnoreCase))
        {
            var repo = new GitTreeRow(GitRowKind.Repository, group.Key.Name, group.Key, "", staged);
            foreach (var item in group)
            {
                var parts = item.Change.RelativePath.Replace('\\', '/').Split('/');
                var parent = repo;
                for (var i = 0; i < parts.Length - 1; i++)
                {
                    var path = string.Join("/", parts, 0, i + 1);
                    var folder = parent.Children.FirstOrDefault(n => n.Kind == GitRowKind.Folder && n.Path == path);
                    if (folder is null)
                        parent.Children.Add(folder = new GitTreeRow(GitRowKind.Folder, parts[i], group.Key, path, staged));
                    parent = folder;
                }
                parent.Children.Add(new GitTreeRow(GitRowKind.File, parts[^1], group.Key, item.Change.RelativePath, staged, item));
            }
            repositories.Add(Tidy(repo));
        }
        if (!showRoot || repositories.Count == 0)
            return repositories;
        var root = new GitTreeRow(GitRowKind.Root, CommonFolder(repositories.Select(r => r.Repository!.Root)), null, "", staged);
        root.Children.AddRange(repositories);
        return [root];
    }

    /// <summary>Folders first, then files, each by name; a folder holding only one folder joins it.</summary>
    static GitTreeRow Tidy(GitTreeRow node)
    {
        for (var i = 0; i < node.Children.Count; i++)
        {
            var child = node.Children[i];
            if (child.Kind != GitRowKind.Folder)
                continue;
            while (child.Children is [{ Kind: GitRowKind.Folder } only])
            {
                var joined = new GitTreeRow(GitRowKind.Folder, child.Name + "/" + only.Name, only.Repository, only.Path, only.IsStaged);
                joined.Children.AddRange(only.Children);
                child = joined;
            }
            node.Children[i] = Tidy(child);
        }
        var sorted = node.Children.OrderBy(n => n.Kind == GitRowKind.File).ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToList();
        node.Children.Clear();
        node.Children.AddRange(sorted);
        return node;
    }

    /// <summary>The deepest folder all the repositories are in.</summary>
    static string CommonFolder(IEnumerable<string> roots)
    {
        var parents = roots.Select(r => (System.IO.Path.GetDirectoryName(r) ?? r).Split('/')).ToList();
        var common = parents[0].ToList();
        foreach (var parts in parents.Skip(1))
        {
            var n = 0;
            while (n < common.Count && n < parts.Length && common[n] == parts[n])
                n++;
            common.RemoveRange(n, common.Count - n);
        }
        return string.Join("/", common) is { Length: > 0 } folder ? folder : "/";
    }

    /// <summary>These rows and the rows under them that are visible (their parents expanded), with their depth.</summary>
    public static IEnumerable<GitTreeRow> Visible(IEnumerable<GitTreeRow> rows, int depth = 0)
    {
        foreach (var row in rows)
        {
            row.Depth = depth;
            yield return row;
            if (row.IsExpanded)
                foreach (var child in Visible(row.Children, depth + 1))
                    yield return child;
        }
    }

    /// <summary>Every row under (and including) these.</summary>
    public static IEnumerable<GitTreeRow> All(IEnumerable<GitTreeRow> rows) => rows.SelectMany(r => new[] { r }.Concat(All(r.Children)));

    /// <summary>The file rows under (and including) this one.</summary>
    public IEnumerable<GitFileItem> Files() => All([this]).Select(r => r.Item).OfType<GitFileItem>();
}
