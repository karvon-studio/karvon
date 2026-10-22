using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Karvon.Docker.ViewModels;
using Karvon.Editor;
using Karvon.Ui;

namespace Karvon.Docker.Views;

/// <summary>
/// A container's page. The logs are an editor over the page's log document, colored as the container wrote them and
/// kept at the last line while it is shown there; inspect is a read-only JSON editor.
/// </summary>
public partial class DockerContainerView : UserControl
{
    ContainerDetailViewModel? _vm;
    bool _scrollPending;

    public DockerContainerView()
    {
        InitializeComponent();
        ExplorerViews.SetUpReadOnly(LogsEditor);
        ExplorerViews.SetUpJson(InspectEditor);
        LogsEditor.TextArea.TextView.LineTransformers.Add(new OutputColorizer(() => _vm?.Logs));
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmChanged;
            _vm.Logs.Appended -= OnLogsAppended;
        }
        _vm = DataContext as ContainerDetailViewModel;
        if (_vm is null)
            return;
        _vm.PropertyChanged += OnVmChanged;
        _vm.Logs.Appended += OnLogsAppended;
        LogsEditor.Document = _vm.Logs.Document;
        LogsEditor.WordWrap = _vm.WrapLogs;
        InspectEditor.Text = _vm.InspectJson;
        ScrollLogsToEnd();
    }

    void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm is null)
            return;
        switch (e.PropertyName)
        {
            case nameof(ContainerDetailViewModel.InspectJson):
                InspectEditor.Text = _vm.InspectJson;
                break;
            case nameof(ContainerDetailViewModel.WrapLogs):
                LogsEditor.WordWrap = _vm.WrapLogs;
                break;
            case nameof(ContainerDetailViewModel.Tab) when _vm.IsLogs:
                ScrollLogsToEnd();
                break;
            case nameof(ContainerDetailViewModel.Tab) when _vm.IsExec:
                Dispatcher.UIThread.Post(() => _vm?.Terminal?.Control.Focus(), DispatcherPriority.Loaded);
                break;
        }
    }

    /// <summary>New lines keep the view at the end, unless the user scrolled up to read.</summary>
    void OnLogsAppended()
    {
        var view = LogsEditor.TextArea.TextView;
        var atEnd = view.VerticalOffset + view.Bounds.Height >= view.DocumentHeight - view.DefaultLineHeight * 3;
        if (atEnd && !_scrollPending)
            ScrollLogsToEnd();
    }

    void ScrollLogsToEnd()
    {
        _scrollPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _scrollPending = false;
            LogsEditor.ScrollToEnd();
        }, DispatcherPriority.Background);
    }

    void OnCopy(object? sender, RoutedEventArgs e) => ExplorerViews.Copy(this, (sender as Control)?.Tag as string);

    void OnCopyLogs(object? sender, RoutedEventArgs e) => ExplorerViews.Copy(this, _vm?.Logs.Document.Text);

    /// <summary>Enter selects the next match after the caret (wrapping round), Shift+Enter the one before.</summary>
    void OnLogSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || LogSearch.Text is not { Length: > 0 } query)
            return;
        e.Handled = true;
        var text = LogsEditor.Document.Text;
        var caret = LogsEditor.SelectionLength > 0 ? LogsEditor.SelectionStart : LogsEditor.CaretOffset;
        var backwards = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var found = backwards
            ? caret > 0 ? text.LastIndexOf(query, caret - 1, StringComparison.OrdinalIgnoreCase) : -1
            : text.IndexOf(query, Math.Min(caret + (LogsEditor.SelectionLength > 0 ? 1 : 0), text.Length), StringComparison.OrdinalIgnoreCase);
        if (found < 0)
            found = backwards ? text.LastIndexOf(query, StringComparison.OrdinalIgnoreCase) : text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (found < 0)
            return;
        LogsEditor.Select(found, query.Length);
        LogsEditor.ScrollTo(LogsEditor.Document.GetLineByOffset(found).LineNumber, 0);
    }
}
