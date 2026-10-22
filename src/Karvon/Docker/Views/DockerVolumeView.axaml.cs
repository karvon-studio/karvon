using System.ComponentModel;
using Avalonia.Controls;
using Karvon.Docker.ViewModels;
using Karvon.Ui;

namespace Karvon.Docker.Views;

public partial class DockerVolumeView : UserControl
{
    VolumeDetailViewModel? _vm;

    public DockerVolumeView()
    {
        InitializeComponent();
        ExplorerViews.SetUpJson(InspectEditor);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
            _vm.PropertyChanged -= OnVmChanged;
        _vm = DataContext as VolumeDetailViewModel;
        if (_vm is null)
            return;
        _vm.PropertyChanged += OnVmChanged;
        InspectEditor.Text = _vm.InspectJson;
    }

    void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VolumeDetailViewModel.InspectJson) && _vm is not null)
            InspectEditor.Text = _vm.InspectJson;
    }
}
