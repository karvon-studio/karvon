using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using AvaloniaEdit.Search;
using CommunityToolkit.Mvvm.Input;

namespace Karvon.Editor;

/// <summary>
/// The editors' find panel (AvaloniaEdit's): its buttons, ↑ and ↓, Replace, Replace All and the X, do what they say. The
/// panel's own commands look for their handler from the element they saw take focus last, and with Avalonia 12 they see
/// none, so the buttons stayed disabled and did nothing (Enter, Shift+Enter, F3 and Escape do not go through them).
/// </summary>
public static class FindPanel
{
    static int _installed;

    /// <summary>Every find panel's buttons from now on, the code editors', the SQL editor's and the diffs' (once).</summary>
    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) == 1)
            return;
        TemplatedControl.TemplateAppliedEvent.AddClassHandler<SearchPanel>((panel, _) =>
        {
            foreach (var button in panel.GetVisualDescendants().OfType<Button>())
                if (Action(panel, button.Command) is { } action)
                    button.Command = new RelayCommand(action);
        });
    }

    static Action? Action(SearchPanel panel, System.Windows.Input.ICommand? command) =>
        command == SearchCommands.FindPrevious ? panel.FindPrevious
        : command == SearchCommands.FindNext ? () => panel.FindNext()
        : command == SearchCommands.ReplaceNext ? panel.ReplaceNext
        : command == SearchCommands.ReplaceAll ? panel.ReplaceAll
        : command == SearchCommands.CloseSearchPanel ? panel.Close
        : null;
}
