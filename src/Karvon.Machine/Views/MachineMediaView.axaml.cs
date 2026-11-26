using Avalonia.Controls;
using Avalonia.Input;
using Karvon.Machine.ViewModels;

namespace Karvon.Machine.Views;

/// <summary>A video or a sound playing (see <see cref="MediaPage"/>): each new picture drawn; Space plays or pauses, ← and → go 5 seconds.</summary>
public partial class MachineMediaView : UserControl
{
    MediaPage? _page;

    public MachineMediaView()
    {
        InitializeComponent();
        // Its page's pictures and asks, while it is on the screen (a closed page's view no longer listens).
        DataContextChanged += (_, _) => Listen(IsAttachedToVisualTree() ? DataContext as MediaPage : null);
        AttachedToVisualTree += (_, _) => Listen(DataContext as MediaPage);
        DetachedFromVisualTree += (_, _) => Listen(null);
        KeyDown += (_, e) =>
        {
            if (_page is null)
                return;
            switch (e.Key)
            {
                case Key.Space:
                    _page.PlayPauseCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.Right:
                    _page.Position = Math.Min(_page.Duration, _page.Position + 5);
                    e.Handled = true;
                    break;
                case Key.Left:
                    _page.Position = Math.Max(0, _page.Position - 5);
                    e.Handled = true;
                    break;
            }
        };
        AttachedToVisualTree += (_, _) => Focus();
    }

    bool IsAttachedToVisualTree() => TopLevel.GetTopLevel(this) is not null;

    void Listen(MediaPage? page)
    {
        if (_page is not null)
        {
            _page.FrameChanged -= Redraw;
            _page.MiniRequested -= OpenMini;
        }
        _page = page;
        if (_page is not null)
        {
            _page.FrameChanged += Redraw;
            _page.MiniRequested += OpenMini;
        }
    }

    void Redraw() => Picture.InvalidateVisual();

    /// <summary>Back from the mini player: it closes, and the video plays on here.</summary>
    void OnPlayHere(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (MiniPlayerWindow.Current is { } mini && mini.Page == _page)
            mini.Close();
    }

    void OpenMini()
    {
        if (_page is not null)
            MiniPlayerWindow.Open(_page, TopLevel.GetTopLevel(this) as Window);
    }
}
