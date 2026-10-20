using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using VSL.ViewModels;
using VSL.Views;
using static VSL.Tests.UiTests;

namespace VSL.Tests;

/// <summary>Markdown files open in Visual Studio 2022's Markdown editor: the text, and its preview beside it.</summary>
public class MarkdownPreviewTests
{
    /// <summary>A README with most of what README files have, a screenshot and a guide beside it, and sections to scroll.</summary>
    static string NewReadme()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vsl-md-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "docs"));
        using (var shot = new RenderTargetBitmap(new PixelSize(40, 20)))
        {
            using (var context = shot.CreateDrawingContext())
                context.FillRectangle(Brushes.Orange, new Rect(0, 0, 40, 20));
            shot.Save(Path.Combine(dir, "docs", "shot.png"));
        }
        File.WriteAllText(Path.Combine(dir, "docs", "guide.md"), "# Guide\n");
        var lines = new List<string>
        {
            "# Shop",
            "",
            "A **fast** shop with ~~no~~ *some* `code` and a [guide](docs/guide.md).",
            "",
            "![Screenshot](docs/shot.png)",
            "",
            "## Tasks",
            "",
            "- [x] Cart",
            "- [ ] Checkout",
            "  1. Pay",
            "",
            "| Name | Price |",
            "|:-----|------:|",
            "| Tea  | 3 |",
            "",
            "```csharp",
            "var x = 1;",
            "```",
            "",
            "> Quoted",
            "",
            "![badge](https://img.shields.io/badge/build-passing-green)",
        };
        for (var i = 1; i <= 40; i++)
            lines.AddRange(["", $"## Section {i}", "", $"Text of section {i}, long enough to wrap in a narrow preview pane of the window."]);
        var readme = Path.Combine(dir, "README.md");
        File.WriteAllText(readme, string.Join("\n", lines) + "\n");
        return readme;
    }

    static EditorArea AreaOf(Window window, DocumentViewModel doc) =>
        window.GetVisualDescendants().OfType<EditorArea>().Single(a => a.Host == doc.Host && a.IsEffectivelyVisible);

    static SelectableTextBlock TextBlock(MarkdownPreview preview, string text) =>
        preview.GetVisualDescendants().OfType<SelectableTextBlock>().First(b => (b.Inlines?.Text ?? b.Text) == text);

    [AvaloniaFact]
    public async Task A_markdown_file_shows_its_preview_beside_the_editor()
    {
        var readme = NewReadme();
        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1500, Height = 950 };
        window.Show();
        var doc = vm.OpenDocument(readme)!;
        await WaitUntil(() => window.GetVisualDescendants().OfType<EditorArea>().Any(a => a.Host == doc.Host && a.Preview.Blocks.Count > 0), TimeSpan.FromSeconds(5));
        var area = AreaOf(window, doc);
        var preview = area.Preview;
        Assert.True(preview.IsEffectivelyVisible);
        Assert.Equal(1, Grid.GetColumn(window.GetVisualDescendants().OfType<GridSplitter>().First(s => s.GetVisualParent()?.GetVisualParent() == area || s.FindAncestorOfType<EditorArea>() == area)));

        // What a README has: headings, emphasis, strikethrough, code, links, a screenshot, task lists, tables, code blocks.
        TextBlock(preview, "Shop");
        var intro = TextBlock(preview, "A fast shop with no some code and a guide.");
        Assert.Contains(intro.Inlines!.OfType<Run>(), r => r.Text == "fast" && r.FontWeight == FontWeight.SemiBold);
        Assert.Contains(intro.Inlines!.OfType<Run>(), r => r.Text == "no" && r.TextDecorations == TextDecorations.Strikethrough);
        var image = preview.GetVisualDescendants().OfType<Image>().Single();
        Assert.Equal(new PixelSize(40, 20), Assert.IsAssignableFrom<Bitmap>(image.Source).PixelSize);
        Assert.Equal([true, false], preview.GetVisualDescendants().OfType<CheckBox>().Select(c => c.IsChecked));
        Assert.Contains(preview.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "1.");
        Assert.Equal(TextAlignment.Right, TextBlock(preview, "3").TextAlignment);
        TextBlock(preview, "var x = 1;");
        // A remote image is its text: nothing is fetched for a preview.
        Assert.Contains(preview.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "badge");
        Snapshot(window, "markdown-preview");

        // It redraws as the text changes.
        doc.Host.Document.Replace(0, "# Shop".Length, "# Store");
        await WaitUntil(() => preview.GetVisualDescendants().OfType<SelectableTextBlock>().Any(b => b.Inlines?.Text == "Store"), TimeSpan.FromSeconds(5));

        // It follows the editor: scrolled to a section, the preview shows that section at its top.
        var editor = doc.Host.PrimaryView;
        var line = doc.Host.Document.Lines.First(l => doc.Host.Document.GetText(l) == "## Section 30").LineNumber;
        var scroller = editor.GetVisualDescendants().OfType<ScrollViewer>().First();
        scroller.Offset = new Vector(0, editor.TextArea.TextView.GetVisualTopByDocumentLine(line));
        await WaitUntil(() => preview.Scroller.Offset.Y > 0, TimeSpan.FromSeconds(5));
        var section = TextBlock(preview, "Section 30");
        var sectionTop = section.TranslatePoint(new Point(0, 0), preview)!.Value.Y;
        Assert.InRange(sectionTop, -10, 60);

        // #anchors scroll the preview; a link to a file beside it opens the file.
        Assert.True(preview.ScrollTo("tasks"));
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(100));
        Assert.InRange(TextBlock(preview, "Tasks").TranslatePoint(new Point(0, 0), preview)!.Value.Y, -10, 60);
        Assert.True(preview.ScrollTo("shop") || preview.ScrollTo("store"));
        await WaitUntil(() => false, TimeSpan.FromMilliseconds(100));
        intro = TextBlock(preview, "A fast shop with no some code and a guide.");
        var guide = intro.TextLayout.HitTestTextPosition(intro.Inlines!.Text!.IndexOf("guide", StringComparison.Ordinal) + 2);
        var click = intro.TranslatePoint(guide.Center, window)!.Value;
        window.MouseDown(click, Avalonia.Input.MouseButton.Left);
        window.MouseUp(click, Avalonia.Input.MouseButton.Left);
        Assert.Equal("guide.md", vm.ActiveEditor?.FileName);

        // The toolbar: the preview below the editor, then none; kept in the settings.
        vm.OpenDocument(readme);
        area = AreaOf(window, doc);
        area.PositionButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(vm.Settings.MarkdownPreviewBelow);
        Assert.Equal(2, Grid.GetRow(area.Preview));
        area.PreviewButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(vm.Settings.MarkdownPreview);
        Assert.False(area.Preview.IsVisible);
        area.PreviewButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(area.Preview.IsVisible);

        // Other files have neither the toolbar nor a preview.
        var code = Path.Combine(Path.GetDirectoryName(readme)!, "Program.cs");
        File.WriteAllText(code, "class Program { }\n");
        var other = vm.OpenDocument(code)!;
        await WaitUntil(() => window.GetVisualDescendants().OfType<EditorArea>().Any(a => a.Host == other.Host), TimeSpan.FromSeconds(5));
        Assert.False(AreaOf(window, other).Preview.IsVisible);
        window.Close();
    }
}
