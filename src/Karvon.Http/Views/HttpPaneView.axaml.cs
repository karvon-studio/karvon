using Avalonia.Controls;
using Karvon.Editor;
using Karvon.Http.ViewModels;
using Karvon.Ui;

namespace Karvon.Http.Views;

/// <summary>The response pane's view: the body highlighted as its content type says, headers and the request as sent read-only.</summary>
public partial class HttpPaneView : UserControl
{
    HttpPaneViewModel? _vm;
    string? _grammar;
    AvaloniaEdit.TextMate.TextMate.Installation? _textMate;

    public HttpPaneView()
    {
        InitializeComponent();
        foreach (var editor in new[] { BodyEditor, HeadersEditor, RequestEditor })
            ExplorerViews.SetUpReadOnly(editor);
        BodyEditor.ShowLineNumbers = true;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
            _vm.PropertyChanged -= OnVmChanged;
        _vm = DataContext as HttpPaneViewModel;
        if (_vm is not null)
            _vm.PropertyChanged += OnVmChanged;
        Show();
    }

    void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HttpPaneViewModel.Exchange))
            Show();
    }

    void Show()
    {
        if (_vm?.Exchange is not { } exchange)
            return;
        if (_grammar != _vm.BodyPath)
        {
            _textMate?.Dispose();
            _textMate = TextMateSupport.Install(BodyEditor, _vm.BodyPath);
            _grammar = _vm.BodyPath;
        }
        BodyEditor.Text = exchange.Error ?? exchange.Body;
        HeadersEditor.Text = _vm.HeadersText;
        RequestEditor.Text = _vm.RequestText;
    }
}
