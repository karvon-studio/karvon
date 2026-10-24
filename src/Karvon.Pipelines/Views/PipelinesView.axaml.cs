using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Karvon.Pipelines.ViewModels;

namespace Karvon.Pipelines.Views;

/// <summary>The Pipelines tool window (see <see cref="PipelinesViewModel"/>): a double click or Enter opens a run's page.</summary>
public partial class PipelinesView : UserControl
{
    public PipelinesView() => InitializeComponent();

    PipelinesViewModel? Vm => DataContext as PipelinesViewModel;

    void OnRunDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Control source && source.FindAncestorOfType<Button>(includeSelf: true) is null && source.DataContext is PipelineRow row)
            Vm?.Open(row);
    }

    void OnRunKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && RunList.SelectedItem is PipelineRow row)
        {
            Vm?.Open(row);
            e.Handled = true;
        }
    }
}
