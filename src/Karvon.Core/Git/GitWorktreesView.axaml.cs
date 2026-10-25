using Avalonia.Controls;
using Avalonia.Input;

namespace Karvon.Git;

/// <summary>A repository's worktrees (see <see cref="GitWorktreesViewModel"/>): one double-clicked opens.</summary>
public partial class GitWorktreesView : UserControl
{
    public GitWorktreesView() => InitializeComponent();

    void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is GitWorktreesViewModel vm && WorktreeList.SelectedItem is GitWorktree worktree)
            vm.OpenCommand.Execute(worktree);
    }
}
