using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Karvon.Docker.ViewModels;
using Karvon.Ui;

namespace Karvon.Docker.Views;

public partial class DockerImageView : UserControl
{
    ImageDetailViewModel? _vm;

    public DockerImageView()
    {
        InitializeComponent();
        ExplorerViews.SetUpJson(InspectEditor);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
            _vm.PropertyChanged -= OnVmChanged;
        _vm = DataContext as ImageDetailViewModel;
        if (_vm is null)
            return;
        _vm.PropertyChanged += OnVmChanged;
        InspectEditor.Text = _vm.InspectJson;
    }

    void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ImageDetailViewModel.InspectJson) && _vm is not null)
            InspectEditor.Text = _vm.InspectJson;
    }

    void OnCopy(object? sender, RoutedEventArgs e) => ExplorerViews.Copy(this, (sender as Control)?.Tag as string);
}
