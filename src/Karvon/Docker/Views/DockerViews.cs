using Avalonia.Controls;
using Avalonia.Input.Platform;
using AvaloniaEdit;
using Karvon.Editor;

namespace Karvon.Docker.Views;

/// <summary>What the Docker Explorer's views share: copying, and the read-only editors for logs and inspect.</summary>
static class DockerViews
{
    public static void Copy(Control source, string? text)
    {
        if (text is { Length: > 0 } && TopLevel.GetTopLevel(source)?.Clipboard is { } clipboard)
            _ = clipboard.SetTextAsync(text);
    }

    /// <summary>A read-only JSON editor (inspect), highlighted as the editor highlights .json files.</summary>
    public static void SetUpJson(TextEditor editor)
    {
        SetUpReadOnly(editor);
        TextMateSupport.Install(editor, "inspect.json");
    }

    public static void SetUpReadOnly(TextEditor editor)
    {
        editor.IsReadOnly = true;
        editor.ShowLineNumbers = false;
        editor.Options.AllowScrollBelowDocument = false;
        editor.Options.EnableHyperlinks = true;
        editor.TextArea.TextView.LinkTextForegroundBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#4DAAFC"));
    }
}
