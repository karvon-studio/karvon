using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Media;
using Karvon.Ui;

namespace Karvon.Git;

/// <summary>A repository's or a file's history (see <see cref="GitHistoryViewModel"/>): more commits load at the end of the list; a file opens as a diff.</summary>
public partial class GitHistoryView : UserControl
{
    GitHistoryViewModel? _vm;

    public GitHistoryView()
    {
        InitializeComponent();
        CommitList.AddHandler(ScrollViewer.ScrollChangedEvent, (_, _) => LoadMoreAtTheEnd());
        // A commit selected from elsewhere (Blame) comes into view.
        CommitList.SelectionChanged += (_, _) =>
        {
            if (CommitList.SelectedItem is { } row)
                CommitList.ScrollIntoView(row);
        };
        FileList.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && FileList.SelectedItem is GitCommitFile file)
                _vm?.OpenFileCommand.Execute(file);
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
            _vm.CopyRequested -= Copy;
        _vm = DataContext as GitHistoryViewModel;
        if (_vm is not null)
            _vm.CopyRequested += Copy;
    }

    void Copy(string text) => ExplorerViews.Copy(this, text);

    void LoadMoreAtTheEnd()
    {
        if (_vm is { HasMore: true } && CommitList.Scroll is ScrollViewer scroll && scroll.Offset.Y + scroll.Viewport.Height >= scroll.Extent.Height - 200)
            _ = _vm.LoadMoreAsync();
    }

    void OnFileDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (FileList.SelectedItem is GitCommitFile file)
            _vm?.OpenFileCommand.Execute(file);
    }
}

/// <summary>How the history's refs and files look.</summary>
public static class GitHistoryConverters
{
    public static readonly IValueConverter IsRemote = new FuncValueConverter<GitRefKind, bool>(k => k == GitRefKind.Remote);

    public static readonly IValueConverter IsTag = new FuncValueConverter<GitRefKind, bool>(k => k == GitRefKind.Tag);

    public static readonly IValueConverter LetterBrush = new FuncValueConverter<string, IBrush>(letter => new SolidColorBrush(Color.Parse(letter switch
    {
        "A" => "#73C991",
        "D" => "#F14C4C",
        "R" or "C" => "#4DAAFC",
        _ => "#E2C08D",
    })));
}
