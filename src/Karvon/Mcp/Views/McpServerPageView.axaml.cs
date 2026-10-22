using Avalonia.Controls;
using Avalonia.Threading;
using Karvon.Docker.Views;
using Karvon.Mcp.ViewModels;

namespace Karvon.Mcp.Views;

/// <summary>A server's page (see <see cref="McpServerPageViewModel"/>); the log follows what is said.</summary>
public partial class McpServerPageView : UserControl
{
    McpServerPageViewModel? _vm;

    public McpServerPageView()
    {
        InitializeComponent();
        DockerViews.SetUpReadOnly(LogEditor);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
            _vm.Log.Appended -= OnLogAppended;
        _vm = DataContext as McpServerPageViewModel;
        if (_vm is null)
            return;
        _vm.Log.Appended += OnLogAppended;
        LogEditor.Document = _vm.Log.Document;
    }

    void OnLogAppended() => Dispatcher.UIThread.Post(() => LogEditor.ScrollToEnd(), DispatcherPriority.Background);
}
