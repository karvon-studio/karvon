using Avalonia.Controls;
using Avalonia.Threading;
using Karvon.Editor;
using Karvon.Pipelines.ViewModels;
using Karvon.Ui;

namespace Karvon.Pipelines.Views;

/// <summary>A run's page: the selected job's log is an editor, colored as Output, kept at its last line as it grows.</summary>
public partial class PipelinePageView : UserControl
{
    PipelinePageViewModel? _vm;

    public PipelinePageView()
    {
        InitializeComponent();
        ExplorerViews.SetUpReadOnly(LogEditor);
        LogEditor.TextArea.TextView.LineTransformers.Add(new OutputColorizer(() => _vm?.Log));
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
            _vm.Log.Appended -= OnLogAppended;
        _vm = DataContext as PipelinePageViewModel;
        if (_vm is null)
            return;
        _vm.Log.Appended += OnLogAppended;
        LogEditor.Document = _vm.Log.Document;
    }

    void OnLogAppended() => Dispatcher.UIThread.Post(() => LogEditor.ScrollToEnd(), DispatcherPriority.Background);
}
