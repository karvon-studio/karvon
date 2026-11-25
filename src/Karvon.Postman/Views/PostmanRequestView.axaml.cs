using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using AvaloniaEdit;
using Karvon.Editor;
using Karvon.Postman.ViewModels;
using Karvon.Ui;
using Karvon.Views;

namespace Karvon.Postman.Views;

/// <summary>A request's view (see <see cref="PostmanRequestDocument"/>): its texts in editors, highlighted; Ctrl+Enter sends.</summary>
public partial class PostmanRequestView : UserControl
{
    readonly (TextEditor Editor, Func<PostmanRequestDocument, string> Get, Action<PostmanRequestDocument, string> Set)[] _texts;
    readonly Dictionary<TextEditor, (string Path, AvaloniaEdit.TextMate.TextMate.Installation Installation)> _grammars = [];
    PostmanRequestDocument? _vm;
    bool _loading;

    public PostmanRequestView()
    {
        InitializeComponent();
        _texts =
        [
            (RawEditor, d => d.Raw, (d, t) => d.Raw = t),
            (QueryEditor, d => d.GraphQuery, (d, t) => d.GraphQuery = t),
            (VariablesEditor, d => d.GraphVariables, (d, t) => d.GraphVariables = t),
            (PreRequestEditor, d => d.PreRequest, (d, t) => d.PreRequest = t),
            (TestsEditor, d => d.Tests, (d, t) => d.Tests = t),
        ];
        foreach (var (editor, _, set) in _texts)
        {
            editor.Options.AllowScrollBelowDocument = false;
            editor.Options.ConvertTabsToSpaces = true;
            editor.Options.IndentationSize = 2;
            editor.TextChanged += (_, _) =>
            {
                if (!_loading && _vm is { } vm)
                    set(vm, editor.Text);
            };
        }
        foreach (var editor in new[] { BodyEditor, HeadersEditor, ConsoleEditor, RequestEditor })
            ExplorerViews.SetUpReadOnly(editor);
        BodyEditor.ShowLineNumbers = true;
        Grammar(PreRequestEditor, "script.js");
        Grammar(TestsEditor, "script.js");
        Grammar(VariablesEditor, "variables.json");
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && PlatformKeys.HasCommand(e.KeyModifiers) && _vm is { } vm)
            {
                vm.SendCommand.Execute(null);
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
    }

    void OnUrlKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _vm is { } vm)
        {
            vm.SendCommand.Execute(null);
            e.Handled = true;
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmChanged;
            _vm.Loaded -= LoadTexts;
        }
        _vm = DataContext as PostmanRequestDocument;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmChanged;
            _vm.Loaded += LoadTexts;
        }
        LoadTexts();
        ShowResult();
    }

    void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PostmanRequestDocument.Result))
            ShowResult();
        else if (e.PropertyName == nameof(PostmanRequestDocument.Language))
            Grammar(RawEditor, RawPath);
    }

    string RawPath => _vm?.Language switch { "json" => "body.json", "xml" => "body.xml", "html" => "body.html", "javascript" => "body.js", _ => "body.txt" };

    void LoadTexts()
    {
        if (_vm is not { } vm)
            return;
        _loading = true;
        foreach (var (editor, get, _) in _texts)
            if (editor.Text != get(vm))
                editor.Text = get(vm);
        _loading = false;
        Grammar(RawEditor, RawPath);
    }

    void ShowResult()
    {
        if (_vm is not { HasResult: true } vm)
            return;
        Grammar(BodyEditor, vm.BodyPath);
        BodyEditor.Text = vm.BodyText;
        HeadersEditor.Text = vm.HeadersText;
        ConsoleEditor.Text = vm.ConsoleText;
        RequestEditor.Text = vm.RequestText;
    }

    /// <summary>The editor highlighted as a file of this name is.</summary>
    void Grammar(TextEditor editor, string path)
    {
        if (_grammars.TryGetValue(editor, out var before))
        {
            if (before.Path == path)
                return;
            before.Installation.Dispose();
            _grammars.Remove(editor);
        }
        if (TextMateSupport.Install(editor, path) is { } installation)
            _grammars[editor] = (path, installation);
    }
}
