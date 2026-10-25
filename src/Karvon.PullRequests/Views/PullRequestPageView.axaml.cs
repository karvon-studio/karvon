using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Media;
using Karvon.PullRequests.ViewModels;

namespace Karvon.PullRequests.Views;

/// <summary>A pull request's page (see <see cref="PullRequestPageViewModel"/>): a file double-clicked (or Enter) opens as a diff.</summary>
public partial class PullRequestPageView : UserControl
{
    public PullRequestPageView() => InitializeComponent();

    PullRequestPageViewModel? Vm => DataContext as PullRequestPageViewModel;

    void OnFileDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (FileList.SelectedItem is FileRow row)
            _ = Vm?.OpenFileAsync(row);
    }

    void OnFileKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && FileList.SelectedItem is FileRow row)
        {
            _ = Vm?.OpenFileAsync(row);
            e.Handled = true;
        }
    }
}

public static class PullRequestConverters
{
    public static readonly IValueConverter LetterBrush = new FuncValueConverter<string, IBrush>(letter => new SolidColorBrush(Color.Parse(letter switch
    {
        "A" => "#73C991",
        "D" => "#F14C4C",
        "R" or "C" => "#4DAAFC",
        _ => "#E2C08D",
    })));
}
