using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using VSL.Editor;
using VSL.ViewModels;

namespace VSL.Views;

/// <summary>
/// A document's editor and, for a Markdown file, its preview to the right of it or below it with Visual Studio 2022's
/// Markdown toolbar above them (the preview's switch and position). The editor control is the document's own (see
/// <see cref="ControlPresenter"/>): it comes along when the document moves to another tab group or window.
/// </summary>
public sealed class EditorArea : DockPanel
{
    public static readonly StyledProperty<EditorHost?> HostProperty = AvaloniaProperty.Register<EditorArea, EditorHost?>(nameof(Host));

    public static readonly StyledProperty<bool> ShowPreviewProperty = AvaloniaProperty.Register<EditorArea, bool>(nameof(ShowPreview), true);

    public static readonly StyledProperty<bool> PreviewBelowProperty = AvaloniaProperty.Register<EditorArea, bool>(nameof(PreviewBelow));

    readonly ControlPresenter _editor = new();
    readonly GridSplitter _splitter = new() { Background = new SolidColorBrush(Color.Parse("#3F3F46")) };
    readonly MarkdownPreview _preview = new();
    readonly Grid _panes = new();
    readonly Border _toolbar;
    readonly VsIcon _previewIcon = new() { Kind = "Eye" };
    readonly VsIcon _positionIcon = new() { Kind = "PreviewBelow" };
    readonly Button _previewButton;
    readonly Button _positionButton;

    public EditorArea()
    {
        _previewButton = ToolbarButton(_previewIcon, "Preview", () => Vm?.ToggleMarkdownPreview());
        _positionButton = ToolbarButton(_positionIcon, null, () => Vm?.ToggleMarkdownPreviewBelow());
        _toolbar = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#2D2D30")),
            BorderBrush = new SolidColorBrush(Color.Parse("#3F3F46")),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Height = 26,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 2,
                Margin = new Thickness(4, 0),
                Children = { _previewButton, _positionButton },
            },
        };
        SetDock(_toolbar, Dock.Top);
        Children.Add(_toolbar);
        _panes.Children.Add(_editor);
        _panes.Children.Add(_splitter);
        _panes.Children.Add(_preview);
        Children.Add(_panes);
        ArrangePanes();
    }

    public EditorHost? Host
    {
        get => GetValue(HostProperty);
        set => SetValue(HostProperty, value);
    }

    /// <summary>Whether Markdown files show their preview (the toolbar's Preview button; kept in settings).</summary>
    public bool ShowPreview
    {
        get => GetValue(ShowPreviewProperty);
        set => SetValue(ShowPreviewProperty, value);
    }

    /// <summary>Whether the preview is below the editor instead of to its right.</summary>
    public bool PreviewBelow
    {
        get => GetValue(PreviewBelowProperty);
        set => SetValue(PreviewBelowProperty, value);
    }

    internal MarkdownPreview Preview => _preview;

    internal Button PreviewButton => _previewButton;

    internal Button PositionButton => _positionButton;

    MainViewModel? Vm => TopLevel.GetTopLevel(this)?.DataContext as MainViewModel;

    /// <summary>Markdown, by its extension, as VS opens it in the Markdown editor.</summary>
    public static bool IsMarkdown(string? path) =>
        Path.GetExtension(path ?? "").ToLowerInvariant() is ".md" or ".markdown" or ".mdown" or ".mkd";

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == HostProperty || change.Property == ShowPreviewProperty || change.Property == PreviewBelowProperty)
            ArrangePanes();
    }

    static Button ToolbarButton(VsIcon icon, string? text, Action click)
    {
        icon.Width = icon.Height = 14;
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { icon } };
        if (text is not null)
            content.Children.Add(new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button { Classes = { "tb" }, Content = content };
        button.Click += (_, _) => click();
        return button;
    }

    void ArrangePanes()
    {
        var host = Host;
        _editor.Control = host?.View;
        var markdown = IsMarkdown(host?.FilePath);
        var show = markdown && ShowPreview;
        _toolbar.IsVisible = markdown;
        _previewIcon.Kind = ShowPreview ? "Eye" : "EyeOff";
        ToolTip.SetTip(_previewButton, ShowPreview ? "Hide the Markdown preview" : "Show the Markdown preview");
        _positionButton.IsVisible = show;
        _positionIcon.Kind = PreviewBelow ? "PreviewRight" : "PreviewBelow";
        ToolTip.SetTip(_positionButton, PreviewBelow ? "Show the preview to the right" : "Show the preview below");
        _preview.Host = show ? host : null;
        _splitter.IsVisible = _preview.IsVisible = show;

        _panes.RowDefinitions.Clear();
        _panes.ColumnDefinitions.Clear();
        foreach (var control in new Control[] { _editor, _splitter, _preview })
        {
            Grid.SetRow(control, 0);
            Grid.SetColumn(control, 0);
        }
        if (!show)
            return;
        if (PreviewBelow)
        {
            _panes.RowDefinitions = new RowDefinitions("*,4,*");
            _splitter.ResizeDirection = GridResizeDirection.Rows;
            Grid.SetRow(_splitter, 1);
            Grid.SetRow(_preview, 2);
        }
        else
        {
            _panes.ColumnDefinitions = new ColumnDefinitions("*,4,*");
            _splitter.ResizeDirection = GridResizeDirection.Columns;
            Grid.SetColumn(_splitter, 1);
            Grid.SetColumn(_preview, 2);
        }
    }
}
