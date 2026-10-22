namespace Karvon.ViewModels;

/// <summary>The Markdown editor's preview (Visual Studio 2022's): shown or not, and where; the same for every Markdown file.</summary>
public partial class MainViewModel
{
    public bool MarkdownPreview => Settings.MarkdownPreview;

    public bool MarkdownPreviewBelow => Settings.MarkdownPreviewBelow;

    public void ToggleMarkdownPreview()
    {
        Settings.MarkdownPreview = !Settings.MarkdownPreview;
        Settings.Save();
        OnPropertyChanged(nameof(MarkdownPreview));
    }

    public void ToggleMarkdownPreviewBelow()
    {
        Settings.MarkdownPreviewBelow = !Settings.MarkdownPreviewBelow;
        Settings.Save();
        OnPropertyChanged(nameof(MarkdownPreviewBelow));
    }
}
