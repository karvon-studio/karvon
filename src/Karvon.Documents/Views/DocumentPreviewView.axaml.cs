using Avalonia.Controls;
using Avalonia.Input;

namespace Karvon.Documents.Views;

/// <summary>A document's pages: each drawn as it comes into view and let go as it leaves; Ctrl+wheel zooms.</summary>
public partial class DocumentPreviewView : UserControl
{
    public DocumentPreviewView()
    {
        InitializeComponent();
        PagesList.ContainerPrepared += (_, e) =>
        {
            if (DataContext is DocumentPreviewTab tab && e.Index >= 0 && e.Index < tab.Pages.Count)
                tab.Show(tab.Pages[e.Index]);
        };
        PagesList.ContainerClearing += (_, e) =>
        {
            if (DataContext is DocumentPreviewTab tab && e.Container.DataContext is PreviewPage page)
                tab.Hide(page);
        };
        PagesScroll.SizeChanged += (_, e) =>
        {
            if (DataContext is DocumentPreviewTab tab)
                tab.ViewportWidth = e.NewSize.Width;
        };
        DataContextChanged += (_, _) =>
        {
            if (DataContext is DocumentPreviewTab tab && PagesScroll.Bounds.Width > 0)
                tab.ViewportWidth = PagesScroll.Bounds.Width;
        };
        PagesScroll.AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            if (DataContext is not DocumentPreviewTab tab || (e.KeyModifiers & KeyModifiers.Control) == 0)
                return;
            if (e.Delta.Y > 0)
                tab.ZoomInCommand.Execute(null);
            else if (e.Delta.Y < 0)
                tab.ZoomOutCommand.Execute(null);
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }
}
