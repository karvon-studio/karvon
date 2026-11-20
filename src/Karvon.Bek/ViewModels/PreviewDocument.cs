using System.ComponentModel;
using Karvon.ViewModels;

namespace Karvon.Bek.ViewModels;

/// <summary>
/// The Bek Preview in the document well, where a designer's preview is: in view beside the editor and Claude Code (a tool
/// window at the side would share Claude Code's group, and Claude's calls never hide the conversation). One at a time,
/// titled by its app; closing it stops the app.
/// </summary>
public sealed class PreviewDocument : DocumentTab
{
    public const string DocumentKey = "bek-preview";

    public PreviewDocument(PreviewViewModel preview)
    {
        Preview = preview;
        Preview.PropertyChanged += OnPreviewChanged;
    }

    public PreviewViewModel Preview { get; }

    public override string Key => DocumentKey;

    public override string Title => Preview.HasApp ? $"{Preview.Title} · Bek Preview" : "Bek Preview";

    public override string ToolTip => Preview.State?.App.Component ?? "Bek Preview";

    void OnPreviewChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PreviewViewModel.Title) or nameof(PreviewViewModel.HasApp))
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(ToolTip));
        }
    }

    /// <summary>Closed: its app stops, and its bek-preview ends.</summary>
    public override void Dispose()
    {
        Preview.PropertyChanged -= OnPreviewChanged;
        _ = Preview.StopAsync();
    }
}
