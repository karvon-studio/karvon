using Avalonia.Controls;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Folding;
using AvaloniaEdit.TextMate;

namespace Karvon.Editor;

/// <summary>
/// Window > Split: a second pane on the same document above the first, each scrolled and folded on its own, both
/// editing the same text. Commands, the caret and the status bar follow the pane last clicked or tabbed into.
/// </summary>
public sealed partial class EditorHost
{
    TextEditor? _second;
    TextMate.Installation? _secondTextMate;
    GridSplitter? _splitter;

    /// <summary>What the document's tab shows: the editor, or both panes and the splitter between them.</summary>
    public Control View => _viewGrid;

    public bool IsSplit => _second is not null;

    /// <summary>The pane added by Window > Split (null when there is one).</summary>
    public TextEditor? SecondView => _second;

    /// <summary>The pane the document opened with.</summary>
    public TextEditor PrimaryView => _primary;

    void Activate(TextEditor editor)
    {
        if (_active == editor || editor != _primary && editor != _second)
            return;
        _active = editor;
        CaretMoved?.Invoke();
    }

    public void ToggleSplit()
    {
        if (IsSplit)
            RemoveSplit();
        else
            Split();
    }

    void Split()
    {
        var second = _second = CreateView(_primary.FontSize, out _secondTextMate);
        _secondFolding = InstallFolding(second);
        if (_semantic is not null)
            second.TextArea.TextView.LineTransformers.Add(_semantic);
        AddGitMargin(second);
        AddDebugAdornments(second);
        AddBlameMargin(second);
        ApplyOutlines(Outlining.Scan(Document, FilePath));
        if (_lsp is not null)
            ScheduleOutlining(0);

        _splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Rows, Background = Avalonia.Media.Brushes.Transparent };
        _viewGrid.RowDefinitions = new RowDefinitions("*,5,*");
        Grid.SetRow(second, 0);
        Grid.SetRow(_splitter, 1);
        Grid.SetRow(_primary, 2);
        _viewGrid.Children.Insert(0, second);
        _viewGrid.Children.Insert(1, _splitter);

        // The new pane opens on what the first one shows.
        second.TextArea.Caret.Position = _primary.TextArea.Caret.Position;
        var line = _primary.TextArea.Caret.Line;
        Dispatcher.UIThread.Post(() => second.ScrollTo(line, 1), DispatcherPriority.Loaded);
    }

    void RemoveSplit()
    {
        if (_second is not { } second)
            return;
        // The pane the user worked in keeps the caret.
        if (_active == second)
        {
            _primary.TextArea.Caret.Position = second.TextArea.Caret.Position;
            _active = _primary;
        }
        _second = null;
        _viewGrid.Children.Remove(second);
        if (_splitter is not null)
            _viewGrid.Children.Remove(_splitter);
        _splitter = null;
        _viewGrid.RowDefinitions = new RowDefinitions("*");
        Grid.SetRow(_primary, 0);
        if (_secondFolding is not null)
            FoldingManager.Uninstall(_secondFolding);
        _secondFolding = null;
        _secondTextMate?.Dispose();
        _secondTextMate = null;
        second.TextArea.TextView.LineTransformers.Clear();
        second.Document = null;
        CaretMoved?.Invoke();
    }
}
