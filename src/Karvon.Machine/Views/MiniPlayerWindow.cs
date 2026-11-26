using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Karvon.Machine.ViewModels;
using Karvon.Views;

namespace Karvon.Machine.Views;

/// <summary>
/// A video's mini player: a small window over every other, at the screen's corner, that plays on while you work (its page
/// closed or not), shown there alone. Its buttons show under the pointer: play or pause, its line, back to its page (where it
/// plays on), close. It is moved by dragging it, made larger from its corner; Space plays or pauses, a double click goes back
/// to its page. One at a time.
/// </summary>
public sealed class MiniPlayerWindow : Window
{
    static MiniPlayerWindow? _open;

    readonly MediaPage _page;
    readonly Image _picture;
    readonly Border _controls;

    MiniPlayerWindow(MediaPage page)
    {
        _page = page;
        Title = page.Name + " — Karvon";
        Width = 420;
        Height = 236;
        MinWidth = 220;
        MinHeight = 130;
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = true;
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        Background = Brushes.Black;

        _picture = new Image { Name = "Picture", Stretch = Stretch.Uniform };
        _picture.Bind(Image.SourceProperty, new Binding(nameof(MediaPage.Frame)) { Source = page });

        var play = Button("PlayButton", "Start", "Play or Pause (Space)", () => page.PlayPauseCommand.Execute(null));
        ((VsIcon)play.Content!).Bind(VsIcon.KindProperty, new Binding(nameof(MediaPage.PlayIcon)) { Source = page });
        var timeline = new Slider { Name = "Timeline", Minimum = 0, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0) };
        timeline.Bind(Avalonia.Controls.Primitives.RangeBase.MaximumProperty, new Binding(nameof(MediaPage.Duration)) { Source = page });
        timeline.Bind(Avalonia.Controls.Primitives.RangeBase.ValueProperty, new Binding(nameof(MediaPage.Position)) { Source = page, Mode = BindingMode.TwoWay });
        var time = new TextBlock { FontSize = 11, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
        time.Bind(TextBlock.TextProperty, new Binding(nameof(MediaPage.TimeText)) { Source = page });
        var bar = new DockPanel { Children = { Docked(play, Dock.Left), Docked(time, Dock.Right), timeline } };
        _controls = new Border
        {
            Name = "Controls",
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse("#00000000"), 0), new GradientStop(Color.Parse("#CC000000"), 1) },
            },
            Padding = new Thickness(6, 14, 14, 4),
            Child = bar,
            Opacity = 0,
        };
        var corner = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(4),
            Spacing = 2,
            Children =
            {
                Button("BackButton", "Expand", "Back to Its Page", Back),
                Button("CloseButton", "Close", "Close the Mini Player", Close),
            },
        };
        var grip = new Border
        {
            Name = "ResizeGrip",
            Width = 14,
            Height = 14,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.BottomRightCorner),
        };
        grip.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                BeginResizeDrag(WindowEdge.SouthEast, e);
                e.Handled = true;
            }
        };
        var top = new Panel { Children = { corner }, Opacity = 0 };
        Content = new Panel { Children = { _picture, _controls, top, grip } };

        // Its buttons under the pointer, out of the way otherwise.
        PointerEntered += (_, _) => _controls.Opacity = top.Opacity = 1;
        PointerExited += (_, _) => _controls.Opacity = top.Opacity = 0;
        _picture.PointerPressed += (_, e) =>
        {
            if (e.ClickCount == 2)
                Back();
            else if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                BeginMoveDrag(e);
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Space)
            {
                page.PlayPauseCommand.Execute(null);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
                Close();
        };
        page.FrameChanged += Redraw;
        page.IsMini = true;
        Closed += (_, _) =>
        {
            page.FrameChanged -= Redraw;
            page.IsMini = false;
            if (_open == this)
                _open = null;
            // Its page was closed meanwhile: nothing plays it any more.
            if (page.IsPageClosed)
                page.Dispose();
        };
    }

    public MediaPage Page => _page;

    /// <summary>The one that is open, if any.</summary>
    public static MiniPlayerWindow? Current => _open;

    /// <summary>The page's video in the mini player, at the bottom right of the screen <paramref name="owner"/> is on.</summary>
    public static MiniPlayerWindow Open(MediaPage page, Window? owner)
    {
        if (_open is { } open)
        {
            if (open._page == page)
            {
                open.Activate();
                return open;
            }
            open.Close();
        }
        var window = new MiniPlayerWindow(page);
        if ((owner?.Screens.ScreenFromWindow(owner) ?? owner?.Screens.Primary) is { } screen)
        {
            var area = screen.WorkingArea;
            var scale = screen.Scaling;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Position = new PixelPoint(area.Right - (int)(window.Width * scale) - 24, area.Bottom - (int)(window.Height * scale) - 24);
        }
        _open = window;
        window.Show();
        return window;
    }

    void Redraw() => _picture.InvalidateVisual();

    /// <summary>Back to its page: it plays on there, and this closes.</summary>
    void Back()
    {
        _page.ShowPageCommand.Execute(null);
        Close();
    }

    static Button Button(string name, string icon, string tip, Action click)
    {
        var button = new Button
        {
            Name = name,
            Content = new VsIcon { Kind = icon, Width = 14, Height = 14, Foreground = Brushes.White },
            Background = new SolidColorBrush(Color.Parse("#99000000")),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6),
            CornerRadius = new CornerRadius(4),
        };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => click();
        return button;
    }

    static Control Docked(Control control, Dock dock)
    {
        DockPanel.SetDock(control, dock);
        return control;
    }
}
