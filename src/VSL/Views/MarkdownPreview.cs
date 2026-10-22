using System.Net;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.Input.Platform;
using AvaloniaEdit.Rendering;
using CommunityToolkit.Mvvm.Input;
using Markdig;
using Markdig.Extensions.AutoIdentifiers;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.Mathematics;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Extensions.Yaml;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using VSL.Editor;
using VSL.ViewModels;
using MdInline = Markdig.Syntax.Inlines.Inline;

namespace VSL.Views;

/// <summary>
/// The preview of a Markdown document beside its editor, as in Visual Studio 2022's Markdown editor: rendered natively
/// from Markdig's syntax tree (CommonMark with GitHub's tables, task lists, strikethrough, autolinks and heading anchors)
/// and redrawn as the text changes. It follows the editor's scrolling. Links open where they point: web pages in the
/// browser, files beside the document in VSL, #headings in the preview. Local images show; remote ones (and SVGs) show
/// their text, as nothing is fetched from the network for a preview.
/// </summary>
public sealed partial class MarkdownPreview : Decorator
{
    public static readonly StyledProperty<EditorHost?> HostProperty = AvaloniaProperty.Register<MarkdownPreview, EditorHost?>(nameof(Host));

    static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAutoIdentifiers(AutoIdentifierOptions.GitHub)
        .UseAdvancedExtensions()
        .UseYamlFrontMatter()
        .Build();

    static readonly FontFamily CodeFont = new("avares://VSL/Assets/Fonts#Cascadia Mono, DejaVu Sans Mono, monospace");
    static readonly IBrush Background1 = new SolidColorBrush(Color.Parse("#1E1E1E"));
    static readonly IBrush CodeBackground = new SolidColorBrush(Color.Parse("#252526"));
    static readonly IBrush InlineCodeBackground = new SolidColorBrush(Color.Parse("#33FFFFFF"));
    static readonly IBrush HeaderBackground = new SolidColorBrush(Color.Parse("#2D2D30"));
    static readonly IBrush LinkBrush = new SolidColorBrush(Color.Parse("#4DAAFC"));
    static readonly IBrush MutedBrush = new SolidColorBrush(Color.Parse("#A0A0A0"));
    static readonly IBrush RuleBrush = new SolidColorBrush(Color.Parse("#3F3F46"));
    static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico"];
    const double BodySize = 14;

    readonly ScrollViewer _scroll;
    readonly StackPanel _panel = new() { Spacing = 12, Margin = new Thickness(24, 16, 24, 40) };
    readonly DispatcherTimer _redraw = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly List<(int Line, Control Block)> _blocks = [];
    readonly Dictionary<string, Control> _anchors = new(StringComparer.Ordinal);
    readonly Dictionary<string, (DateTime Written, Bitmap? Image)> _images = new(StringComparer.Ordinal);
    EditorHost? _host;
    TextView? _textView;
    string? _rendered;
    int _listDepth;

    public MarkdownPreview()
    {
        _scroll = new ScrollViewer { Content = _panel, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Child = new Border { Background = Background1, Child = _scroll };
        ClipToBounds = true;
        _redraw.Tick += (_, _) =>
        {
            _redraw.Stop();
            Render();
        };
        // Links in the rendered text (see MarkdownView.MakeLinksClickable) come here.
        MarkdownView.SetLinkCommand(this, new RelayCommand<string>(OpenLink));
    }

    /// <summary>The document's editor: its text is what shows, and its scrolling is followed.</summary>
    public EditorHost? Host
    {
        get => GetValue(HostProperty);
        set => SetValue(HostProperty, value);
    }

    /// <summary>The rendered blocks with the 0-based source line each starts on (for following the editor).</summary>
    internal IReadOnlyList<(int Line, Control Block)> Blocks => _blocks;

    internal ScrollViewer Scroller => _scroll;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == HostProperty && IsShown())
            Attach(Host);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Attach(Host);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Attach(null);
    }

    bool IsShown() => TopLevel.GetTopLevel(this) is not null;

    void Attach(EditorHost? host)
    {
        if (host == _host)
            return;
        if (_host is { } old)
            old.Document.TextChanged -= OnTextChanged;
        if (_textView is { } view)
            view.ScrollOffsetChanged -= OnEditorScrolled;
        _host = host;
        _textView = null;
        _redraw.Stop();
        Clear();
        if (host is null)
            return;
        host.Document.TextChanged += OnTextChanged;
        _textView = host.PrimaryView.TextArea.TextView;
        _textView.ScrollOffsetChanged += OnEditorScrolled;
        Render();
    }

    void Clear()
    {
        _panel.Children.Clear();
        _blocks.Clear();
        _anchors.Clear();
        _rendered = null;
        foreach (var (_, image) in _images.Values)
            image?.Dispose();
        _images.Clear();
    }

    // Typing redraws once it pauses, not on every key.
    void OnTextChanged(object? sender, EventArgs e)
    {
        _redraw.Stop();
        _redraw.Start();
    }

    void OnEditorScrolled(object? sender, EventArgs e) => FollowEditor();

    /// <summary>Draws the document's text now (it is otherwise drawn when typing pauses).</summary>
    internal void Render()
    {
        if (_host is not { } host)
            return;
        var text = host.Document.Text;
        if (text == _rendered)
            return;
        _rendered = text;
        _panel.Children.Clear();
        _blocks.Clear();
        _anchors.Clear();
        _listDepth = 0;
        MarkdownDocument document;
        try
        {
            document = Markdown.Parse(text, Pipeline);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Markdig parses any text; should it ever fail, the preview says so instead of taking the editor down.
            _panel.Children.Add(new TextBlock { Text = ex.Message, Foreground = MutedBrush });
            return;
        }
        foreach (var block in document)
        {
            if (Build(block) is not { } control)
                continue;
            _panel.Children.Add(control);
            _blocks.Add((block.Line, control));
        }
        Dispatcher.UIThread.Post(FollowEditor, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Scrolls the preview to what the editor shows at its top: between the blocks that start before and after that
    /// line, in proportion; the top and the bottom of the document match exactly.
    /// </summary>
    internal void FollowEditor()
    {
        if (_textView is not { } view || _blocks.Count == 0 || view.Bounds.Height <= 0)
            return;
        var max = Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height);
        var top = view.ScrollOffset.Y;
        var scrollable = (ILogicalScrollable)view;
        if (top <= 0.5)
        {
            _scroll.Offset = new Vector(0, 0);
            return;
        }
        if (top + scrollable.Viewport.Height >= scrollable.Extent.Height - 0.5)
        {
            _scroll.Offset = new Vector(0, max);
            return;
        }
        var line = view.GetDocumentLineByVisualTop(top);
        var lineTop = view.GetVisualTopByDocumentLine(line.LineNumber);
        var position = line.LineNumber - 1 + Math.Clamp((top - lineTop) / Math.Max(1, view.DefaultLineHeight), 0, 1);
        var index = _blocks.FindLastIndex(b => b.Line <= position);
        if (index < 0)
        {
            _scroll.Offset = new Vector(0, 0);
            return;
        }
        var (startLine, block) = _blocks[index];
        var startY = block.Bounds.Y;
        var (endLine, endY) = index + 1 < _blocks.Count
            ? (_blocks[index + 1].Line, _blocks[index + 1].Block.Bounds.Y)
            : (_host!.Document.LineCount, _panel.Bounds.Height);
        var y = startY + (endY - startY) * (position - startLine) / Math.Max(1, endLine - startLine);
        _scroll.Offset = new Vector(0, Math.Clamp(y + _panel.Margin.Top - 8, 0, max));
    }

    // ---- Blocks ------------------------------------------------------------------------------------------------

    Control? Build(Block block) => block switch
    {
        HeadingBlock heading => HeadingOf(heading),
        ParagraphBlock paragraph => ParagraphOf(paragraph),
        YamlFrontMatterBlock yaml => CodeOf(yaml.Lines.ToString(), "yaml"),
        FencedCodeBlock fenced => CodeOf(fenced.Lines.ToString(), fenced.Info ?? ""),
        CodeBlock code => CodeOf(code.Lines.ToString(), ""),
        QuoteBlock quote => QuoteOf(quote),
        ListBlock list => ListOf(list),
        Table table => TableOf(table),
        ThematicBreakBlock => new Border { Height = 1, Background = RuleBrush, Margin = new Thickness(0, 6) },
        HtmlBlock html => HtmlOf(html.Lines.ToString()),
        LinkReferenceDefinitionGroup => null,
        FootnoteGroup footnotes => FootnotesOf(footnotes),
        ContainerBlock container => StackOf(container),
        LeafBlock { Inline: { } inline } => TextOf(inline),
        _ => null,
    };

    StackPanel StackOf(ContainerBlock container, double spacing = 12)
    {
        var panel = new StackPanel { Spacing = spacing };
        foreach (var child in container)
            if (Build(child) is { } control)
                panel.Children.Add(control);
        return panel;
    }

    Control HeadingOf(HeadingBlock heading)
    {
        var size = heading.Level switch { 1 => 26, 2 => 21, 3 => 17, 4 => 15, 5 => 14, _ => 13 };
        var text = TextOf(heading.Inline, size, FontWeight.SemiBold, heading.Level == 6 ? MutedBrush : null);
        Control control = heading.Level <= 2
            ? new Border { BorderBrush = RuleBrush, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 0, 0, 6), Child = text }
            : text;
        control.Margin = new Thickness(0, heading.Level <= 2 ? 12 : 6, 0, 0);
        if (heading.GetAttributes().Id is { Length: > 0 } id)
            _anchors.TryAdd(id, control);
        return control;
    }

    Control ParagraphOf(ParagraphBlock paragraph)
    {
        // An image alone in its paragraph (a screenshot) is a block of its own, as on GitHub.
        if (paragraph.Inline?.FirstChild is { } only && only.NextSibling is null)
        {
            if (only is LinkInline { IsImage: true } image)
                return ImageOf(image.Url, PlainText(image), image.Title, link: null);
            if (only is LinkInline { IsImage: false, FirstChild: LinkInline { IsImage: true, NextSibling: null } linked } link)
                return ImageOf(linked.Url, PlainText(linked), linked.Title, link.Url);
        }
        return TextOf(paragraph.Inline);
    }

    Control CodeOf(string text, string language)
    {
        text = text.TrimEnd('\n', '\r');
        var copy = new Button
        {
            Classes = { "tbi" },
            Content = new VsIcon { Kind = "Copy" },
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 4, 4, 0),
            Opacity = 0.75,
        };
        ToolTip.SetTip(copy, "Copy");
        copy.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(text);
        };
        var body = new SelectableTextBlock { Text = text, FontFamily = CodeFont, FontSize = 12.5, Margin = new Thickness(14, 12, 40, 12) };
        var grid = new Grid
        {
            Children =
            {
                MarkdownView.Sideways(body),
                copy,
            },
        };
        ToolTip.SetTip(body, language.Length > 0 ? language : null);
        return new Border { Background = CodeBackground, CornerRadius = new CornerRadius(4), BorderBrush = RuleBrush, BorderThickness = new Thickness(1), Child = grid };
    }

    Control QuoteOf(QuoteBlock quote)
    {
        var border = new Border
        {
            BorderBrush = RuleBrush,
            BorderThickness = new Thickness(4, 0, 0, 0),
            Padding = new Thickness(14, 2, 0, 2),
            Child = StackOf(quote, 8),
        };
        border.SetValue(TextElement.ForegroundProperty, MutedBrush);
        return border;
    }

    Control ListOf(ListBlock list)
    {
        var spacing = list.IsLoose ? 10 : 4;
        var panel = new StackPanel { Spacing = spacing };
        var number = list.IsOrdered && int.TryParse(list.OrderedStart, out var start) ? start : 1;
        _listDepth++;
        foreach (var item in list.OfType<ListItemBlock>())
        {
            Control marker = item.FirstOrDefault() is ParagraphBlock { Inline.FirstChild: TaskList task }
                ? new CheckBox { IsChecked = task.Checked, IsHitTestVisible = false, MinHeight = 0, MinWidth = 0, Padding = new Thickness(0), Margin = new Thickness(0, 1, 6, 0), VerticalAlignment = VerticalAlignment.Top }
                : new TextBlock
                {
                    Text = list.IsOrdered ? $"{number}." : (_listDepth % 3) switch { 1 => "•", 2 => "◦", _ => "▪" },
                    FontSize = BodySize,
                    LineHeight = BodySize * 1.5,
                    Foreground = MutedBrush,
                    MinWidth = list.IsOrdered ? 24 : 16,
                };
            number++;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            row.Children.Add(marker);
            var content = StackOf(item, spacing);
            Grid.SetColumn(content, 1);
            row.Children.Add(content);
            panel.Children.Add(row);
        }
        _listDepth--;
        panel.Margin = new Thickness(_listDepth > 0 ? 4 : 8, 0, 0, 0);
        return panel;
    }

    Control TableOf(Table table)
    {
        var rows = table.OfType<TableRow>().ToList();
        var columns = rows.Count == 0 ? 0 : rows.Max(r => r.Count);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(string.Join(",", Enumerable.Repeat("Auto", Math.Max(1, columns)))) };
        for (var r = 0; r < rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var c = 0; c < rows[r].Count; c++)
            {
                var cell = (TableCell)rows[r][c];
                var alignment = c < table.ColumnDefinitions.Count ? table.ColumnDefinitions[c].Alignment : null;
                Control content = cell.FirstOrDefault() is ParagraphBlock { } paragraph && cell.Count == 1
                    ? TextOf(paragraph.Inline, 13, rows[r].IsHeader ? FontWeight.SemiBold : FontWeight.Normal)
                    : StackOf(cell, 6);
                if (content is SelectableTextBlock text)
                    text.TextAlignment = alignment switch
                    {
                        TableColumnAlign.Center => TextAlignment.Center,
                        TableColumnAlign.Right => TextAlignment.Right,
                        _ => TextAlignment.Left,
                    };
                var border = new Border
                {
                    BorderBrush = RuleBrush,
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Padding = new Thickness(12, 5),
                    Background = rows[r].IsHeader ? HeaderBackground : null,
                    Child = content,
                };
                Grid.SetRow(border, r);
                Grid.SetColumn(border, c);
                grid.Children.Add(border);
            }
        }
        return MarkdownView.Sideways(new Border { BorderBrush = RuleBrush, BorderThickness = new Thickness(1, 1, 0, 0), Child = grid, HorizontalAlignment = HorizontalAlignment.Left });
    }

    Control FootnotesOf(FootnoteGroup footnotes)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new Border { Height = 1, Background = RuleBrush, Margin = new Thickness(0, 6) });
        foreach (var note in footnotes.OfType<Footnote>())
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            row.Children.Add(new TextBlock { Text = $"{note.Order}.", Foreground = MutedBrush, FontSize = 12, MinWidth = 22, LineHeight = 18 });
            var content = StackOf(note, 4);
            Grid.SetColumn(content, 1);
            row.Children.Add(content);
            panel.Children.Add(row);
            _anchors.TryAdd($"fn:{note.Order}", row);
        }
        return panel;
    }

    /// <summary>An HTML block, as README files have them: its images, and its text without the tags.</summary>
    Control? HtmlOf(string html)
    {
        if (html.TrimStart().StartsWith("<!--", StringComparison.Ordinal))
            return null;
        var centered = CenterAttribute().IsMatch(html);
        var panel = new StackPanel { Spacing = 8, HorizontalAlignment = centered ? HorizontalAlignment.Center : HorizontalAlignment.Stretch };
        var images = new WrapPanel { HorizontalAlignment = centered ? HorizontalAlignment.Center : HorizontalAlignment.Left };
        foreach (Match image in ImageTag().Matches(html))
        {
            var src = Attribute(image.Value, "src");
            if (src is null)
                continue;
            var control = ImageOf(src, Attribute(image.Value, "alt") ?? "", null, LinkAround(html, image.Index));
            control.Margin = new Thickness(0, 0, 6, 4);
            if (double.TryParse(Attribute(image.Value, "width"), out var width) && width > 0)
                control.MaxWidth = width;
            images.Children.Add(control);
        }
        if (images.Children.Count > 0)
            panel.Children.Add(images);
        var text = WebUtility.HtmlDecode(Whitespace().Replace(HtmlTag().Replace(html, " "), " ")).Trim();
        if (text.Length > 0)
            panel.Children.Add(new SelectableTextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                FontSize = BodySize,
                LineHeight = BodySize * 1.5,
                TextAlignment = centered ? TextAlignment.Center : TextAlignment.Left,
            });
        return panel.Children.Count == 0 ? null : panel;

        static string? Attribute(string tag, string name) =>
            Regex.Match(tag, $@"\b{name}\s*=\s*(""(?<v>[^""]*)""|'(?<v>[^']*)'|(?<v>[^\s>]+))", RegexOptions.IgnoreCase) is { Success: true } m
                ? WebUtility.HtmlDecode(m.Groups["v"].Value)
                : null;

        // <a href="..."><img ...></a>: the image is the link.
        static string? LinkAround(string html, int index)
        {
            var open = html.LastIndexOf("<a ", index, StringComparison.OrdinalIgnoreCase);
            var close = html.LastIndexOf("</a>", index, StringComparison.OrdinalIgnoreCase);
            return open >= 0 && open > close ? Attribute(html[open..index], "href") : null;
        }
    }

    // ---- Images ------------------------------------------------------------------------------------------------

    /// <summary>A local image, or its text (and title) for one that is remote or cannot be shown; clicking a linked one follows it.</summary>
    Control ImageOf(string? url, string alt, string? title, string? link)
    {
        Control control;
        if (LoadImage(url) is { } bitmap)
            control = new Image
            {
                Source = bitmap,
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly,
                MaxWidth = bitmap.Size.Width,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
        else
            control = new Border
            {
                BorderBrush = RuleBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 2),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock { Text = alt.Length > 0 ? alt : Path.GetFileName(url ?? "image"), Foreground = MutedBrush, FontSize = 12 },
            };
        ToolTip.SetTip(control, title is { Length: > 0 } ? title : url);
        var target = link ?? (control is Border ? url : null);
        if (target is { Length: > 0 })
        {
            control.Cursor = new Cursor(StandardCursorType.Hand);
            control.PointerReleased += (_, e) =>
            {
                if (e.InitialPressMouseButton != MouseButton.Left)
                    return;
                OpenLink(target);
                e.Handled = true;
            };
        }
        return control;
    }

    Bitmap? LoadImage(string? url)
    {
        if (LocalPath(url) is not { } path || !ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) || !File.Exists(path))
            return null;
        var written = File.GetLastWriteTimeUtc(path);
        if (_images.TryGetValue(path, out var cached) && cached.Written == written)
            return cached.Image;
        cached.Image?.Dispose();
        Bitmap? bitmap;
        try
        {
            bitmap = new Bitmap(path);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Not an image after all: its text shows instead.
            bitmap = null;
        }
        _images[path] = (written, bitmap);
        return bitmap;
    }

    /// <summary>
    /// The file a link or image points to: beside the document, or for a path starting with / at the root of its
    /// repository, as on GitHub; null for web addresses and #anchors.
    /// </summary>
    internal string? LocalPath(string? url)
    {
        if (_host is null || string.IsNullOrWhiteSpace(url) || url.StartsWith('#'))
            return null;
        var directory = Path.GetDirectoryName(_host.FilePath)!;
        if (!url.StartsWith('/') && Uri.TryCreate(url, UriKind.Absolute, out var absolute))
            return absolute.IsFile ? absolute.LocalPath : null;
        var relative = Uri.UnescapeDataString(url.Split('#', '?')[0]);
        if (relative.Length == 0)
            return null;
        try
        {
            return relative.StartsWith('/')
                ? Path.GetFullPath(Path.Combine(RepositoryRoot(directory) ?? directory, relative.TrimStart('/')))
                : Path.GetFullPath(Path.Combine(directory, relative));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    static string? RepositoryRoot(string directory)
    {
        for (var dir = new DirectoryInfo(directory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git")))
                return dir.FullName;
        return null;
    }

    // ---- Links -------------------------------------------------------------------------------------------------

    /// <summary>A link of the preview: a #heading scrolls to it, a web address opens in the browser, a file opens in VSL.</summary>
    internal void OpenLink(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
            return;
        if (target.StartsWith('#'))
        {
            ScrollTo(Uri.UnescapeDataString(target[1..]));
            return;
        }
        if (!target.StartsWith('/') && Uri.TryCreate(target, UriKind.Absolute, out var uri) && !uri.IsFile)
        {
            if (uri.Scheme is "http" or "https" or "mailto")
                _ = TopLevel.GetTopLevel(this)?.Launcher.LaunchUriAsync(uri);
            return;
        }
        if (LocalPath(target) is { } path && File.Exists(path) && TopLevel.GetTopLevel(this)?.DataContext is MainViewModel vm)
            vm.OpenDocument(path);
    }

    /// <summary>Scrolls a heading (by its GitHub anchor) or a footnote to the top of the preview.</summary>
    internal bool ScrollTo(string anchor)
    {
        if (!_anchors.TryGetValue(anchor, out var control) && !_anchors.TryGetValue(anchor.ToLowerInvariant(), out control))
            return false;
        if (control.TranslatePoint(new Point(0, 0), _panel) is { } point)
            _scroll.Offset = new Vector(0, Math.Clamp(point.Y + _panel.Margin.Top - 8, 0, Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height)));
        return true;
    }

    // ---- Inlines -----------------------------------------------------------------------------------------------

    SelectableTextBlock TextOf(ContainerInline? inlines, double size = BodySize, FontWeight weight = FontWeight.Normal, IBrush? foreground = null)
    {
        var block = new SelectableTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = size,
            FontWeight = weight,
            LineHeight = size * 1.5,
            Inlines = new InlineCollection(),
        };
        if (foreground is not null)
            block.Foreground = foreground;
        var writer = new InlineWriter(this, block.Inlines, size);
        if (inlines is not null)
            writer.Write(inlines, default);
        if (writer.Links.Count > 0)
            MarkdownView.MakeLinksClickable(block, writer.Links);
        return block;
    }

    static string PlainText(ContainerInline container)
    {
        var text = new System.Text.StringBuilder();
        foreach (var inline in container.Descendants())
            if (inline is LiteralInline literal)
                text.Append(literal.Content.ToString());
            else if (inline is CodeInline code)
                text.Append(code.Content);
        return text.ToString();
    }

    readonly record struct Style(bool Bold, bool Italic, bool Strike, bool Code, bool Link);

    /// <summary>Writes Markdig inlines as Avalonia runs, keeping the character ranges of the links (MdLink).</summary>
    sealed class InlineWriter(MarkdownPreview preview, InlineCollection target, double size)
    {
        int _offset;

        public List<MdLink> Links { get; } = [];

        public void Write(ContainerInline container, Style style)
        {
            for (var inline = container.FirstChild; inline is not null; inline = inline.NextSibling)
                Write(inline, style);
        }

        void Write(MdInline inline, Style style)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    Add(literal.Content.ToString(), style);
                    break;
                case CodeInline code:
                    Add(code.Content, style with { Code = true });
                    break;
                case MathInline math:
                    Add(math.Content.ToString(), style with { Code = true });
                    break;
                case LineBreakInline lineBreak when lineBreak.IsHard:
                    target.Add(new LineBreak());
                    _offset++;
                    break;
                case LineBreakInline:
                    Add(" ", style);
                    break;
                case EmphasisInline emphasis:
                    Write(emphasis, emphasis.DelimiterChar switch
                    {
                        '~' when emphasis.DelimiterCount >= 2 => style with { Strike = true },
                        '*' or '_' when emphasis.DelimiterCount >= 2 => style with { Bold = true },
                        '*' or '_' => style with { Italic = true },
                        _ => style,
                    });
                    break;
                case LinkInline { IsImage: true } image:
                    target.Add(new InlineUIContainer(preview.ImageOf(image.Url, PlainText(image), image.Title, null)) { BaselineAlignment = BaselineAlignment.TextBottom });
                    _offset++;
                    break;
                case LinkInline link:
                    Link(link.Url, () => Write(link, style with { Link = true }));
                    break;
                case AutolinkInline auto:
                    Link(auto.IsEmail ? "mailto:" + auto.Url : auto.Url, () => Add(auto.Url, style with { Link = true }));
                    break;
                case FootnoteLink { IsBackLink: false } footnote:
                    Link($"#fn:{footnote.Footnote.Order}", () => Add($"[{footnote.Footnote.Order}]", style with { Link = true }));
                    break;
                case HtmlEntityInline entity:
                    Add(entity.Transcoded.ToString(), style);
                    break;
                case HtmlInline html when LineBreakTag().IsMatch(html.Tag):
                    target.Add(new LineBreak());
                    _offset++;
                    break;
                case HtmlInline html when ImageTag().IsMatch(html.Tag) && Regex.Match(html.Tag, @"\bsrc\s*=\s*[""']?(?<v>[^""'\s>]+)", RegexOptions.IgnoreCase) is { Success: true } src:
                    target.Add(new InlineUIContainer(preview.ImageOf(WebUtility.HtmlDecode(src.Groups["v"].Value), "", null, null)));
                    _offset++;
                    break;
                case TaskList:
                case HtmlInline:
                    break;
                case ContainerInline container:
                    Write(container, style);
                    break;
            }
        }

        void Link(string? url, Action write)
        {
            var start = _offset;
            write();
            if (url is { Length: > 0 } && _offset > start)
                Links.Add(new MdLink(start, _offset, url));
        }

        void Add(string text, Style style)
        {
            if (text.Length == 0)
                return;
            var run = new Run(text);
            if (style.Bold)
                run.FontWeight = FontWeight.SemiBold;
            if (style.Italic)
                run.FontStyle = FontStyle.Italic;
            if (style.Code)
            {
                run.FontFamily = CodeFont;
                run.FontSize = size * 0.88;
                run.Background = InlineCodeBackground;
            }
            if (style.Link)
                run.Foreground = LinkBrush;
            if (style.Strike)
                run.TextDecorations = TextDecorations.Strikethrough;
            target.Add(run);
            _offset += text.Length;
        }
    }

    [GeneratedRegex(@"<img\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ImageTag();

    [GeneratedRegex(@"^<br\s*/?>$", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreakTag();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\balign\s*=\s*[""']?center", RegexOptions.IgnoreCase)]
    private static partial Regex CenterAttribute();
}
