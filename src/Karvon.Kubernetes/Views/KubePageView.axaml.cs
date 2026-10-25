using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Karvon.Editor;
using Karvon.Kubernetes.ViewModels;
using Karvon.Ui;

namespace Karvon.Kubernetes.Views;

/// <summary>An object's page (see <see cref="KubePageViewModel"/>): its logs an editor colored as Output, kept at its last line; its YAML read-only.</summary>
public partial class KubePageView : UserControl
{
    KubePageViewModel? _vm;

    public KubePageView()
    {
        InitializeComponent();
        ExplorerViews.SetUpReadOnly(LogEditor);
        ExplorerViews.SetUpReadOnly(YamlEditor);
        LogEditor.TextArea.TextView.LineTransformers.Add(new OutputColorizer(() => _vm?.Logs));
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.Logs.Appended -= OnLogAppended;
            _vm.PropertyChanged -= OnVmChanged;
        }
        _vm = DataContext as KubePageViewModel;
        if (_vm is null)
            return;
        _vm.Logs.Appended += OnLogAppended;
        _vm.PropertyChanged += OnVmChanged;
        LogEditor.Document = _vm.Logs.Document;
        YamlEditor.Text = _vm.Yaml;
    }

    void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(KubePageViewModel.Yaml) && _vm is not null)
            YamlEditor.Text = _vm.Yaml;
    }

    void OnLogAppended() => Dispatcher.UIThread.Post(() => LogEditor.ScrollToEnd(), DispatcherPriority.Background);

    void OnPodDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (PodList.SelectedItem is KubeRow row)
            _vm?.OpenPodCommand.Execute(row);
    }
}
