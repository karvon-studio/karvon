using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Karvon.ViewModels;

namespace Karvon.Git;

/// <summary>A commit of an interactive rebase: what is done with it, and (reworded) its new message.</summary>
public sealed partial class RebaseStep(GitCommit commit) : ObservableObject
{
    public static IReadOnlyList<string> Actions { get; } = ["pick", "reword", "edit", "squash", "fixup", "drop"];

    public GitCommit Commit => commit;

    public string ShortHash => commit.ShortHash;

    public string Subject => commit.Subject;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReword), nameof(IsDropped))]
    public partial string Action { get; set; } = "pick";

    public bool IsReword => Action == "reword";

    public bool IsDropped => Action == "drop";

    /// <summary>Its message as it is, then as reworded.</summary>
    public string OriginalMessage { get; set; } = commit.Subject;

    [ObservableProperty]
    public partial string Message { get; set; } = commit.Subject;
}

/// <summary>
/// An interactive rebase of the branch from a commit (git rebase -i, without its editor): the commits after it, oldest
/// first as git applies them, each picked, reworded (a message of its own), stopped at to edit, squashed or fixed up
/// into the one before, or dropped, in the order they are put in. A conflict or an edit stops it: Git Changes goes on
/// with it, or aborts it.
/// </summary>
public sealed partial class GitRebaseViewModel(MainViewModel vm, GitRepository repository, GitCommit? onto) : DocumentTab
{
    public GitRepository Repository => repository;

    public override string Key => "git-rebase:" + repository.Root;

    public override string Title => "Interactive Rebase";

    public override string ToolTip => onto is null ? "From the first commit" : $"Onto {onto.ShortHash} ({onto.Subject})";

    public string Heading => onto is null ? "The branch's commits, from the first, oldest first" : $"The commits after {onto.ShortHash} ({onto.Subject}), oldest first";

    public ObservableCollection<RebaseStep> Steps { get; } = [];

    [ObservableProperty]
    public partial RebaseStep? Selected { get; set; }

    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>Merges among them: a rebase makes them a line of their commits.</summary>
    [ObservableProperty]
    public partial bool HasMerges { get; private set; }

    public async Task LoadAsync()
    {
        Steps.Clear();
        var commits = await repository.CommitsSinceAsync(onto?.Hash);
        HasMerges = commits.Any(c => c.IsMerge);
        foreach (var commit in commits.Where(c => !c.IsMerge))
        {
            var step = new RebaseStep(commit);
            if (await repository.CommitDetailsAsync(commit) is { } details)
                step.OriginalMessage = step.Message = details.Message;
            Steps.Add(step);
        }
    }

    [RelayCommand]
    void MoveUp(RebaseStep? step) => Move(step ?? Selected, -1);

    [RelayCommand]
    void MoveDown(RebaseStep? step) => Move(step ?? Selected, 1);

    void Move(RebaseStep? step, int by)
    {
        if (step is null || Steps.IndexOf(step) is var at && (at + by < 0 || at + by >= Steps.Count))
            return;
        Steps.Move(at, at + by);
        Selected = step;
    }

    /// <summary>The todo git is given: its lines, a rewording an exec after its pick (git's editor never opens).</summary>
    internal string Todo(string messages)
    {
        var todo = new StringBuilder();
        var n = 0;
        foreach (var step in Steps)
        {
            switch (step.Action)
            {
                case "reword" when step.Message.Trim() != step.OriginalMessage.Trim() && step.Message.Trim().Length > 0:
                    var file = Path.Combine(messages, $"message-{++n}.txt");
                    File.WriteAllText(file, step.Message.Trim() + "\n");
                    todo.Append($"pick {step.Commit.Hash} {step.Subject}\n");
                    todo.Append($"exec git commit --amend --allow-empty --no-verify -F '{file.Replace('\\', '/')}'\n");
                    break;
                case "reword":
                    todo.Append($"pick {step.Commit.Hash} {step.Subject}\n");
                    break;
                default:
                    todo.Append($"{step.Action} {step.Commit.Hash} {step.Subject}\n");
                    break;
            }
        }
        return todo.ToString();
    }

    [RelayCommand]
    public async Task StartAsync()
    {
        var first = Steps.FirstOrDefault(s => !s.IsDropped);
        if (first is { Action: "squash" or "fixup" })
        {
            Error = $"{first.ShortHash} is the first commit kept: there is none before it to {first.Action} it into.";
            return;
        }
        if (Steps.All(s => s.IsDropped) && vm.Dialogs is { } dialogs
            && await dialogs.ConfirmAsync("Interactive Rebase", "Every commit is dropped: the branch goes back to where they started. Rebase?") != true)
            return;
        Error = null;
        var folder = Path.Combine(Path.GetTempPath(), "karvon-rebase-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var todo = Path.Combine(folder, "git-rebase-todo");
        File.WriteAllText(todo, Todo(folder));
        vm.SaveAll();
        var done = await vm.HistoryOperationAsync("Interactive rebase", () => repository.RebaseInteractiveAsync(onto?.Hash, todo), workingTree: true);
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
        }
        if (done || repository.InProgress() == GitOperation.Rebase)
            vm.CloseDocumentCommand.Execute(this);
        else
            Error = "The rebase did not start: see Output (Git).";
    }
}
