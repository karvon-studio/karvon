using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using AvaloniaEdit.Search;
using CommunityToolkit.Mvvm.Input;

namespace Karvon.Editor;

/// <summary>
/// The editors' find panel (AvaloniaEdit's): its X closes it. The panel's own close command looks for its handler from
/// the element it saw take focus last, and with Avalonia 12 it sees none, so the X stayed disabled and did nothing.
/// </summary>
public static class FindPanel
{
    static int _installed;

    /// <summary>Every find panel's X from now on, the code editors', the SQL editor's and the diffs' (once).</summary>
    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) == 1)
            return;
        TemplatedControl.TemplateAppliedEvent.AddClassHandler<SearchPanel>((panel, _) =>
        {
            foreach (var button in panel.GetVisualDescendants().OfType<Button>())
                if (button.Command == SearchCommands.CloseSearchPanel)
                    button.Command = new RelayCommand(panel.Close);
        });
    }
}
