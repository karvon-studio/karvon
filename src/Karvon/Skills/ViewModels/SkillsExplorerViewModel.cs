using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Karvon.Services;
using Karvon.ViewModels;
using Karvon.Ui;

namespace Karvon.Skills.ViewModels;

/// <summary>What the Skills explorer asks of Karvon: the folder, Claude Code's folder, the settings, the document well, the editor.</summary>
public interface ISkillsHost
{
    /// <summary>The solution's folder (its .claude and memory files), or null.</summary>
    string? Workspace { get; }

    /// <summary>Claude Code's configuration folder: ~/.claude, or the chosen account's.</summary>
    string ClaudeDirectory { get; }

    /// <summary>The marketplace's sources, the memory files' source and whether they are kept in step.</summary>
    Settings Settings { get; }

    void SaveSettings();

    IEnumerable<DocumentTab> Documents { get; }

    void ShowDocument(DocumentTab document);

    void CloseDocument(DocumentTab document);

    /// <summary>A file in the editor, at a line when one is given.</summary>
    void OpenFile(string path, int line = 0);

    void AskClaude(string prompt);

    void OpenUrl(string url);

    /// <summary>An MCP server (by its URL) in Claude Code's settings for every project, added when it is not there; why not, or null.</summary>
    Task<string?> EnsureClaudeMcpServerAsync(string name, string url);

    /// <summary>A line in the status bar ("Wrote AGENTS.md").</summary>
    void Status(string text);
}

/// <summary>A row of the Skills tree.</summary>
public abstract partial class SkillsTreeNode : ObservableObject
{
    [ObservableProperty]
    public partial bool IsExpanded { get; set; }
}

/// <summary>Skills, Subagents, Commands, Hooks, Memory files.</summary>
public sealed partial class SkillsGroupNode(string kind, string title) : SkillsTreeNode
{
    public string Kind { get; } = kind;

    public string Title { get; } = title;

    public string Icon => Kind switch
    {
        "skills" => "Sparkle",
        "subagents" => "Claude",
        "commands" => "CodeBrackets",
        "hooks" => "Bolt",
        _ => "md",
    };

    public ObservableCollection<SkillsTreeNode> Items { get; } = [];

    [ObservableProperty]
    public partial string Detail { get; set; } = "";
}

/// <summary>The marketplace's row, first in the tree.</summary>
public sealed class SkillsMarketplaceNode(SkillsExplorerViewModel explorer) : SkillsTreeNode
{
    public IRelayCommand OpenCommand { get; } = new RelayCommand(() => explorer.OpenMarketplace());
}

/// <summary>A skill: its name, where it is, how many of its checks fail.</summary>
public sealed class SkillNode(SkillsExplorerViewModel explorer, Skill skill, int problems) : SkillsTreeNode
{
    public Skill Skill { get; } = skill;

    public string Name => Skill.Name;

    public int Problems { get; } = problems;

    public bool HasProblems => Problems > 0;

    public string Detail => Skill.Where + (Problems > 0 ? $" · {Problems} problem{(Problems == 1 ? "" : "s")}" : "");

    public string ToolTip => Skill.Description.Length > 0 ? Skill.Description : "(no description)";

    public IRelayCommand OpenCommand { get; } = new RelayCommand(() => explorer.Open(skill));

    public IRelayCommand EditCommand { get; } = new RelayCommand(() => explorer.Host.OpenFile(skill.File));
}

/// <summary>A subagent, a command, a hook or a memory file: a file opened in the editor.</summary>
public sealed class SkillsFileNode(SkillsExplorerViewModel explorer, string name, string detail, string file, string icon) : SkillsTreeNode
{
    public string Name { get; } = name;

    public string Detail { get; } = detail;

    public string File { get; } = file;

    public string Icon { get; } = icon;

    public IRelayCommand OpenCommand { get; } = new RelayCommand(() =>
    {
        if (System.IO.File.Exists(file))
            explorer.Host.OpenFile(file);
    });
}

/// <summary>A skill's page in the document well: its checks, "Try a prompt", its files.</summary>
public sealed class SkillDocument(SkillPageViewModel page) : DocumentTab
{
    public SkillPageViewModel Page { get; } = page;

    public override string Key => "skill:" + Page.Skill.Id;

    public override string Title => Page.Skill.Name;

    public override string ToolTip => Page.Skill.Folder;
}

/// <summary>The skills marketplace's page.</summary>
public sealed class SkillsMarketplaceDocument(SkillsMarketplaceViewModel marketplace) : DocumentTab
{
    public SkillsMarketplaceViewModel Marketplace { get; } = marketplace;

    public override string Key => "skills:marketplace";

    public override string Title => "Skills Marketplace";

    public override string ToolTip => "Skills from GitHub repositories and teams' registries";
}

/// <summary>
/// The Skills tool window: the skills Claude Code loads (the folder's, the user's, the plugins') with their checks,
/// the subagents, commands and hooks of the folder and the user, and the memory files: AGENTS.md and GEMINI.md written
/// from CLAUDE.md for the agents that read those, kept in step as it changes. A skill opens as a page; the marketplace
/// installs skills from GitHub and teams' registries after showing what they ask for.
/// </summary>
public sealed partial class SkillsExplorerViewModel : ObservableObject, IDisposable
{
    FileSystemWatcher? _watcher;

    public SkillsExplorerViewModel(ISkillsHost host)
    {
        Host = host;
        Library = new SkillLibrary(host.Workspace, host.ClaudeDirectory);
        Refresh();
    }

    internal ISkillsHost Host { get; }

    internal SkillLibrary Library { get; private set; }

    public ObservableCollection<SkillsTreeNode> Roots { get; } = [];

    /// <summary>The folder's memory files, or null without a solution.</summary>
    public MemoryFiles? Memory => Library.Workspace is { } folder
        ? new MemoryFiles(folder, Host.Settings.SkillsMemorySource is { Length: > 0 } source ? source : "CLAUDE.md",
            () => [.. Library.Skills().Where(s => s.Scope == SkillScope.Project)])
        : null;

    /// <summary>The dialog over the window (a new skill's name, a confirmation), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDialog))]
    public partial OverlayDialog? Dialog { get; set; }

    public bool HasDialog => Dialog is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    public bool HasError => Error is not null;

    [RelayCommand]
    void DismissError() => Error = null;

    /// <summary>Reads everything again (another folder, another account, files changed); the groups stay open or closed.</summary>
    [RelayCommand]
    public void Refresh()
    {
        if (Library.Workspace != Host.Workspace || Library.ClaudeDirectory != Host.ClaudeDirectory)
            Library = new SkillLibrary(Host.Workspace, Host.ClaudeDirectory);
        Watch();
        var open = Roots.OfType<SkillsGroupNode>().ToDictionary(g => g.Kind, g => g.IsExpanded);
        var roots = new List<SkillsTreeNode> { Roots.OfType<SkillsMarketplaceNode>().FirstOrDefault() ?? new SkillsMarketplaceNode(this) };

        var skills = Library.Skills();
        var group = new SkillsGroupNode("skills", "Skills");
        var problems = 0;
        foreach (var skill in skills)
        {
            var failed = SkillLibrary.Checks(skill).Count(c => !c.Passed);
            problems += failed > 0 ? 1 : 0;
            group.Items.Add(new SkillNode(this, skill, failed));
        }
        group.Detail = skills.Count == 0 ? "none yet: New Skill…" : $"{skills.Count}" + (problems > 0 ? $" · {problems} with problems" : "");
        roots.Add(group);

        void Files(string kind, string title, IEnumerable<SkillsFileNode> nodes)
        {
            var files = new SkillsGroupNode(kind, title);
            foreach (var node in nodes)
                files.Items.Add(node);
            files.Detail = files.Items.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (files.Items.Count > 0)
                roots.Add(files);
        }
        static string Owner(SkillScope scope) => scope == SkillScope.Project ? "this folder" : "yours";
        Files("subagents", "Subagents", Library.Subagents().Select(a => new SkillsFileNode(this, a.Name, Owner(a.Scope) + (a.Description.Length > 0 ? " · " + a.Description : ""), a.File, "Claude")));
        Files("commands", "Commands", Library.Commands().Select(c => new SkillsFileNode(this, c.Name, Owner(c.Scope) + (c.Description.Length > 0 ? " · " + c.Description : ""), c.File, "CodeBrackets")));
        Files("hooks", "Hooks", Library.Hooks().Select(h => new SkillsFileNode(this, $"{h.Event} · {h.Matcher}", h.Command, h.File, "Bolt")));

        if (Memory is { } memory)
        {
            var files = new SkillsGroupNode("memory", "Memory files") { Detail = "from " + memory.Source };
            files.Items.Add(new SkillsFileNode(this, memory.Source, memory.HasSource ? "the source" : "missing: the others come from it", memory.PathOf(memory.Source), "md"));
            foreach (var target in memory.Targets)
                files.Items.Add(new SkillsFileNode(this, target, StateText(memory.StateOf(target)), memory.PathOf(target), "md"));
            roots.Add(files);
        }

        foreach (var node in roots.OfType<SkillsGroupNode>())
            node.IsExpanded = open.GetValueOrDefault(node.Kind, node.Kind == "skills");
        Roots.Clear();
        foreach (var node in roots)
            Roots.Add(node);
        foreach (var page in Host.Documents.OfType<SkillDocument>().ToList())
            if (skills.FirstOrDefault(s => s.Id == page.Page.Skill.Id) is { } still)
                page.Page.Reload(still);
            else
                Host.CloseDocument(page);
        OnPropertyChanged(nameof(Memory));
    }

    static string StateText(MemoryState state) => state switch
    {
        MemoryState.Missing => "not generated",
        MemoryState.UpToDate => "generated, up to date",
        MemoryState.OutOfDate => "generated, out of date",
        _ => "written by hand",
    };

    [RelayCommand]
    void CollapseAll()
    {
        foreach (var node in Roots)
            node.IsExpanded = false;
    }

    // ---- Pages -------------------------------------------------------------------------------------------------

    public SkillDocument Open(Skill skill)
    {
        var document = Host.Documents.OfType<SkillDocument>().FirstOrDefault(d => d.Page.Skill.Id == skill.Id)
            ?? new SkillDocument(new SkillPageViewModel(this, skill));
        Host.ShowDocument(document);
        return document;
    }

    [RelayCommand]
    void ShowMarketplace() => OpenMarketplace();

    public SkillsMarketplaceDocument OpenMarketplace()
    {
        var document = Host.Documents.OfType<SkillsMarketplaceDocument>().FirstOrDefault() ?? new SkillsMarketplaceDocument(new SkillsMarketplaceViewModel(this));
        Host.ShowDocument(document);
        return document;
    }

    // ---- New skill, memory files ---------------------------------------------------------------------------------

    const string ForFolder = "This folder (.claude/skills)";

    [RelayCommand]
    async Task NewSkill()
    {
        var places = Library.Workspace is null ? null : new[] { ForFolder, "Your skills (every project)" };
        if (await ShowAsync(new InputDialog("New skill", "Its name: lowercase words with hyphens", "release-notes", "Create", places)) is not { } dialog)
            return;
        var name = SkillLibrary.NameFor(dialog.Text);
        if (name.Length == 0)
        {
            Error = "A skill's name has letters or digits.";
            return;
        }
        try
        {
            var file = Library.Create(name, forFolder: places is not null && dialog.Choice == ForFolder);
            Refresh();
            if (Library.Skills().FirstOrDefault(s => s.File == file) is { } skill)
                Open(skill);
            Host.OpenFile(file, 3);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Error = $"The skill was not made: {ex.Message}";
        }
    }

    /// <summary>Writes AGENTS.md and GEMINI.md from the source; asks before replacing what was written by hand.</summary>
    [RelayCommand]
    public async Task GenerateMemoryFiles()
    {
        if (Memory is not { } memory)
        {
            Error = "Open a solution: its folder's memory files are written.";
            return;
        }
        if (!memory.HasSource)
        {
            Error = $"There is no {memory.Source} to write the others from.";
            return;
        }
        try
        {
            var (written, left) = memory.Sync(createMissing: true, replaceHandWritten: false);
            if (left.Count > 0 && await ConfirmAsync($"{string.Join(" and ", left)} {(left.Count == 1 ? "was" : "were")} written by hand",
                    $"Replace {(left.Count == 1 ? "it" : "them")} with what Karvon writes from {memory.Source}? What {(left.Count == 1 ? "it says" : "they say")} now is lost.", "Replace", danger: true))
                written = [.. written, .. memory.Sync(createMissing: true, replaceHandWritten: true).Written];
            Host.Status(written.Count == 0 ? "The memory files are up to date." : $"Wrote {string.Join(" and ", written)} from {memory.Source}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = $"The memory files were not written: {ex.Message}";
        }
        Refresh();
    }

    /// <summary>The source changed: the generated files follow (those written by hand, or not there yet, are left).</summary>
    void KeepInStep()
    {
        if (!Host.Settings.SkillsSyncMemoryFiles || Memory is not { } memory)
            return;
        try
        {
            if (memory.Sync(createMissing: false, replaceHandWritten: false).Written.Count > 0)
                Dispatcher.UIThread.Post(Refresh);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Being written meanwhile: the next change writes them.
        }
    }

    void Watch()
    {
        var folder = Library.Workspace;
        if (_watcher?.Path == folder && _watcher?.Filter == Memory?.Source)
            return;
        _watcher?.Dispose();
        _watcher = null;
        if (folder is null || !Directory.Exists(folder) || Memory is not { } memory)
            return;
        _watcher = new FileSystemWatcher(folder, memory.Source) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
        _watcher.Changed += (_, _) => KeepInStep();
        _watcher.Created += (_, _) => KeepInStep();
        _watcher.Renamed += (_, _) => KeepInStep();
        _watcher.EnableRaisingEvents = true;
        KeepInStep();
    }

    // ---- Dialogs -----------------------------------------------------------------------------------------------

    internal async Task<T?> ShowAsync<T>(T dialog) where T : OverlayDialog
    {
        while (Dialog is { } open)
            await open.Result;
        Dialog = dialog;
        try
        {
            return await dialog.Result ? dialog : null;
        }
        finally
        {
            if (Dialog == dialog)
                Dialog = null;
        }
    }

    internal async Task<bool> ConfirmAsync(string title, string message, string confirm, bool danger = false) =>
        await ShowAsync(new ConfirmDialog(title, message, confirm, danger)) is not null;

    public void Dispose()
    {
        Dialog?.Cancel();
        _watcher?.Dispose();
    }
}
