using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Karvon.Bek.ViewModels;

namespace Karvon.Bek.Views;

/// <summary>
/// The Bek Preview tool window (see <see cref="PreviewViewModel"/>). The screen takes the mouse as the phone takes a finger:
/// a press that does not move is a tap, one that moves scrolls; the wheel scrolls; keys type into the field in focus.
/// </summary>
public partial class PreviewView : UserControl
{
    /// <summary>How far a finger moves, in the page's pixels, before it drags rather than taps: the phone's.</summary>
    const double Slop = 8;

    /// <summary>A step of the wheel, in the page's pixels.</summary>
    const double WheelStep = 48;

    /// <summary>Where the press went down and where it was last; whether it drags.</summary>
    (Point Start, Point Last, bool Dragging)? _press;

    public PreviewView()
    {
        InitializeComponent();
        ScreenImage.PointerPressed += OnPressed;
        ScreenImage.PointerMoved += OnMoved;
        ScreenImage.PointerReleased += OnReleased;
        ScreenImage.PointerWheelChanged += OnWheel;
        ScreenImage.KeyDown += OnKeyDown;
        ScreenImage.TextInput += OnTextInput;
    }

    PreviewViewModel? Preview => DataContext as PreviewViewModel;

    /// <summary>A place on the image as a place on the page: the image is the page's PNG at its own size, scaled by the Viewbox only.</summary>
    Point OnPage(PointerEventArgs e) => e.GetPosition(ScreenImage);

    void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(ScreenImage).Properties.IsLeftButtonPressed)
            return;
        ScreenImage.Focus();
        e.Pointer.Capture(ScreenImage);
        var at = OnPage(e);
        _press = (at, at, false);
        e.Handled = true;
    }

    void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_press is not var (start, last, dragging) || Preview is not { } preview)
            return;
        var at = OnPage(e);
        dragging |= Distance(at, start) > Slop;
        if (dragging)
            _ = preview.ScrollAsync(start.X, start.Y, last.X - at.X, last.Y - at.Y);
        _press = (start, at, dragging);
    }

    void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_press is var (_, _, dragging) && !dragging && Preview is { } preview)
        {
            var at = OnPage(e);
            _ = preview.TapAsync(at.X, at.Y);
        }
        _press = null;
        e.Pointer.Capture(null);
    }

    void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (Preview is not { } preview)
            return;
        var at = OnPage(e);
        _ = preview.ScrollAsync(at.X, at.Y, -e.Delta.X * WheelStep, -e.Delta.Y * WheelStep);
        e.Handled = true;
    }

    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (Preview is not { } preview || e.KeyModifiers is not KeyModifiers.None and not KeyModifiers.Shift)
            return;
        Task? done = e.Key switch
        {
            Key.Escape => preview.ActAsync(new PreviewAction.Back()),
            Key.Back => preview.PressAsync(PreviewKey.Backspace),
            Key.Enter => preview.PressAsync(PreviewKey.Enter),
            _ => null,
        };
        e.Handled = done is not null;
    }

    void OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (Preview is { } preview && e.Text is { Length: > 0 } text && !char.IsControl(text[0]))
        {
            _ = preview.TypeAsync(text);
            e.Handled = true;
        }
    }

    static double Distance(Point a, Point b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
}
