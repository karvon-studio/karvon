using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Karvon.Docker.Views;
using Karvon.Editor;
using Karvon.Servers.ViewModels;

namespace Karvon.Servers.Views;

/// <summary>A machine's page: the logs are an editor kept at its last line; files dropped on the table are uploaded.</summary>
public partial class ServerPageView : UserControl
{
    ServerPageViewModel? _vm;

    public ServerPageView()
    {
        InitializeComponent();
        DockerViews.SetUpReadOnly(LogsEditor);
        LogsEditor.TextArea.TextView.LineTransformers.Add(new OutputColorizer(() => _vm?.Logs));
        FilesScroll.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        FilesScroll.AddHandler(DragDrop.DragLeaveEvent, (_, _) => DropHint.IsVisible = false);
        FilesScroll.AddHandler(DragDrop.DropEvent, OnDrop);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmChanged;
            _vm.Logs.Appended -= OnLogsAppended;
        }
        _vm = DataContext as ServerPageViewModel;
        if (_vm is null)
            return;
        _vm.PropertyChanged += OnVmChanged;
        _vm.Logs.Appended += OnLogsAppended;
        LogsEditor.Document = _vm.Logs.Document;
    }

    void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ServerPageViewModel.Tab) && _vm?.IsTerminal == true)
            Dispatcher.UIThread.Post(() => _vm?.Terminal?.Control.Focus(), DispatcherPriority.Loaded);
    }

    void OnLogsAppended() => Dispatcher.UIThread.Post(() => LogsEditor.ScrollToEnd(), DispatcherPriority.Background);

    void OnPathKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _vm is not null)
        {
            _vm.GoCommand.Execute(null);
            e.Handled = true;
        }
    }

    void OnLogsKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _vm is not null)
        {
            _vm.RefreshLogsCommand.Execute(null);
            e.Handled = true;
        }
    }

    void OnEntryDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is RemoteEntry entry && e.Source is not Button)
            _vm?.OpenEntryCommand.Execute(entry);
    }

    void OnDragOver(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.Contains(DataFormat.File);
        e.DragEffects = files ? DragDropEffects.Copy : DragDropEffects.None;
        DropHint.IsVisible = files;
        e.Handled = true;
    }

    async void OnDrop(object? sender, DragEventArgs e)
    {
        DropHint.IsVisible = false;
        if (_vm is null)
            return;
        var paths = e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().Where(File.Exists).ToList() ?? [];
        e.Handled = true;
        await _vm.UploadAsync(paths);
    }
}
